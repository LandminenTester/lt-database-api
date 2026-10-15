using Ldb.Core.Config;
using Microsoft.Extensions.Options;

namespace Ldb.Api.Buffering;

/// <summary>Runs the schedule of the write-behind buffer and does the final flush when the service stops.</summary>
public sealed class WriteBufferService(WriteBuffer buffer, IOptions<LdbOptions> options, TimeProvider time, ILogger<WriteBufferService> logger) : BackgroundService
{
    readonly WriteBehindOptions o = options.Value.WriteBehind;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!buffer.Enabled) return;
        var times = FlushSchedule.ParseTimes(o.FlushAt);
        var zone = FlushSchedule.Zone(o.TimeZone);
        logger.LogInformation("Write-behind is on: interval={Interval}s at=[{At}] threshold={Threshold} journal={Journal}",
            o.FlushIntervalSeconds, string.Join(",", o.FlushAt), o.FlushThreshold, string.IsNullOrWhiteSpace(o.JournalPath) ? "memory only" : "file");

        var lastCheck = time.GetUtcNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var now = time.GetUtcNow();
            try
            {
                if (FlushSchedule.Due(o, times, zone, buffer.LastFlush, lastCheck, now, buffer.Pending()))
                    await buffer.FlushAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError("Scheduled flush failed: {Type}", ex.GetType().Name); }
            lastCheck = now;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!buffer.Enabled || buffer.Pending() == 0) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, o.ShutdownFlushSeconds)));
        try
        {
            var r = await buffer.FlushAsync(null, cts.Token);
            if (r.Remaining > 0)
                logger.LogWarning("Shutdown: {Remaining} queued write(s) were not applied. {Where}", r.Remaining,
                    string.IsNullOrWhiteSpace(o.JournalPath) ? "They are LOST because no journal is configured." : "They stay in the journal and are applied at the next start.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown flush timed out with {Remaining} write(s) left", buffer.Pending());
        }
    }
}
