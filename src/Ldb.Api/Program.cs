using System.Net;
using System.Threading.RateLimiting;
using Ldb.Api;
using Ldb.Api.Buffering;
using Ldb.Api.Security;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Providers.MySql;
using Ldb.Providers.Postgres;
using Ldb.Providers.Mongo;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows service or a systemd unit when started by one; plain console otherwise.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

builder.Configuration.AddEnvironmentVariables(prefix: "LDB_");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = false; o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "; });

var section = builder.Configuration.GetSection(LdbOptions.Section);
builder.Services.Configure<LdbOptions>(section);
var opts = section.Get<LdbOptions>() ?? new LdbOptions();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = opts.Security.MaxBodyBytes;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    k.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(240, TimeSpan.FromSeconds(10));
    k.Limits.MaxConcurrentConnections = 1000;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new KeyRegistry(opts.Security));
builder.Services.AddSingleton(new NonceCache(TimeSpan.FromSeconds(opts.Security.TimestampToleranceSeconds * 2 + 5)));
builder.Services.AddSingleton(new IdempotencyCache(TimeSpan.FromMinutes(10)));
builder.Services.AddKeyedSingleton("failures", new MinuteCounter());
builder.Services.AddKeyedSingleton("keyrates", new MinuteCounter());
builder.Services.AddSingleton<IDatabaseProvider>(sp =>
{
    var db = sp.GetRequiredService<IOptions<LdbOptions>>().Value.Database;
    if (string.IsNullOrWhiteSpace(db.DmlConnectionString)) throw new InvalidOperationException("Ldb:Database:DmlConnectionString is empty.");
    var logs = sp.GetRequiredService<ILoggerFactory>();
    return db.Provider.ToLowerInvariant() switch
    {
        "postgres" or "postgresql" => new PostgresProvider(db, logs.CreateLogger<PostgresProvider>()),
        "mysql" or "mariadb" => new MySqlProvider(db, logs.CreateLogger<MySqlProvider>()),
        "mongodb" or "mongo" => new MongoProvider(db, logs.CreateLogger<MongoProvider>()),
        _ => throw new InvalidOperationException($"Unknown Ldb:Database:Provider '{db.Provider}'. Use postgres, mysql or mongodb."),
    };
});
builder.Services.AddSingleton<IWriteJournal>(sp =>
{
    var wb = sp.GetRequiredService<IOptions<LdbOptions>>().Value.WriteBehind;
    return wb.Enabled && !string.IsNullOrWhiteSpace(wb.JournalPath) ? new FileWriteJournal(wb.JournalPath, wb.JournalFsync) : new MemoryWriteJournal();
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<LdbOptions>>().Value;
    // Fail at start-up, not at the first scheduled flush, if the schedule is misconfigured.
    if (o.WriteBehind.Enabled) { FlushSchedule.ParseTimes(o.WriteBehind.FlushAt); FlushSchedule.Zone(o.WriteBehind.TimeZone); }
    var buffer = new WriteBuffer(sp.GetRequiredService<IDatabaseProvider>(), o.WriteBehind, o.Limits, sp.GetRequiredService<IWriteJournal>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<WriteBuffer>>());
    if (o.WriteBehind.Enabled) buffer.Recover();
    return buffer;
});
builder.Services.AddHostedService<WriteBufferService>();
builder.Services.AddSingleton(sp => new RequestPipeline(
    sp.GetRequiredService<IOptions<LdbOptions>>(), sp.GetRequiredService<IDatabaseProvider>(), sp.GetRequiredService<KeyRegistry>(),
    sp.GetRequiredService<NonceCache>(), sp.GetRequiredKeyedService<MinuteCounter>("failures"), sp.GetRequiredKeyedService<MinuteCounter>("keyrates"),
    sp.GetRequiredService<IdempotencyCache>(), sp.GetRequiredService<WriteBuffer>(), sp.GetRequiredService<ILogger<RequestPipeline>>(), sp.GetRequiredService<TimeProvider>()));

// Per-address limit for everything, including unauthenticated calls.
builder.Services.AddRateLimiter(r =>
{
    r.RejectionStatusCode = 429;
    r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

if (opts.Security.TrustedProxies.Count > 0)
{
    var fh = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    fh.KnownProxies.Clear();
    foreach (var p in opts.Security.TrustedProxies) fh.KnownProxies.Add(IPAddress.Parse(p));
    app.UseForwardedHeaders(fh);
}

app.UseRateLimiter();

// Security headers and transport rule.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (ctx.Request.IsHttps) ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    else if (!opts.Security.AllowInsecureHttp && !(ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = new { code = "https_required", message = "Use HTTPS." } });
        return;
    }
    await next();
});

var pipeline = app.Services.GetRequiredService<RequestPipeline>();
foreach (var op in Operations.All)
    app.MapPost(op.Path, (Func<HttpContext, Task<IResult>>)(ctx => pipeline.HandleAsync(ctx, op)));

// Unauthenticated probes: only a status and the server time. The time lets clients with an unreliable clock correct their timestamps.
app.MapGet("/v1/health", (TimeProvider t) => Results.Json(new { status = "ok", time = t.GetUtcNow().ToUnixTimeSeconds() }));
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", async (IDatabaseProvider p, CancellationToken ct) => await p.PingAsync(ct) ? Results.Text("ready") : Results.StatusCode(503));

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    _ = Task.Run(async () =>
    {
        try
        {
            var provider = app.Services.GetRequiredService<IDatabaseProvider>();
            await provider.InitializeAsync(CancellationToken.None);
            logger.LogInformation("Database provider {Provider} ready, {Keys} API key(s) loaded", provider.Name, app.Services.GetRequiredService<KeyRegistry>().Count);
            if (opts.Security.AllowInsecureHttp) logger.LogWarning("AllowInsecureHttp is on: traffic is not encrypted. Use this only on a trusted network.");
        }
        catch (Exception ex)
        {
            logger.LogCritical("Database initialisation failed: {Type}. Check the connection settings.", ex.GetType().Name);
        }
    });
});

app.Run();

public partial class Program;
