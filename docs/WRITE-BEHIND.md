# Write-behind mode (optional)

By default the API is **live**: every `insert`, `update`, `upsert` and `delete` runs against the database before the answer is
sent, so the result is exact (generated ids, affected rows) and the data is current the moment the call returns.

**Write-behind** is an optional mode for servers that produce many small writes. The API checks each write, records it in a
journal, answers "queued" at once, and applies the queued writes to the database later, in order, in batches. You trade
immediacy for fewer database round trips and for resilience against short database outages.

Nothing changes unless the server owner turns it on, and the owner decides which namespaces and operations are affected.
Schema changes and reads are never delayed.

## When to use it, and when not

Good fit:

* Frequent small updates whose exact result nobody waits for (stats, last-seen, counters, preferences, logs).
* The database is remote or slow and a game tick must not wait for it.
* You want writes to survive a short database outage without error handling in every caller.

Poor fit:

* You need the generated id or the affected row count of a write. A queued write cannot return them.
* A later step must see the data immediately (use `FlushBeforeRead`, or send that one write with `"durability":"immediate"`).
* Money-like data where a rejected write must be reported to the player. Rejections happen at flush time (see below).

## Turning it on

```json
{
  "Ldb": {
    "WriteBehind": {
      "Enabled": true,
      "Namespaces": [ "mygame" ],
      "Operations": [ "insert", "update", "upsert", "delete" ],
      "FlushIntervalSeconds": 30,
      "FlushAt": [ "04:00" ],
      "TimeZone": "Europe/Berlin",
      "FlushThreshold": 1000,
      "JournalPath": "/data/write-behind.journal"
    }
  }
}
```

Environment variables work as everywhere: `LDB_Ldb__WriteBehind__Enabled=true`, `LDB_Ldb__WriteBehind__FlushAt__0=04:00`, …

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. Off means everything is live. |
| `Namespaces` | `[]` | Namespaces whose writes are queued. Empty means all. |
| `Operations` | all four | Which of `insert`, `update`, `upsert`, `delete` are queued. |
| `FlushIntervalSeconds` | `30` | Flush every N seconds while writes are waiting. `0` disables interval flushing. |
| `FlushAt` | `[]` | Flush at these times of day (`"HH:mm"`, 24 h), for example `["04:00","16:00"]`. |
| `TimeZone` | `UTC` | Time zone id for `FlushAt` (IANA such as `Europe/Berlin`, or a Windows id). |
| `FlushThreshold` | `1000` | Flush early when this many writes are waiting. `0` disables. |
| `MaxQueuedOperations` | `50000` | Hard limit. A full buffer answers `503 write_buffer_full`. |
| `MaxQueuedBytes` | `67108864` | Hard limit on the size of queued payloads. |
| `JournalPath` | empty | File that keeps queued writes across restarts and crashes. Empty keeps them in memory only. |
| `JournalFsync` | `true` | Force every entry to disk before it is acknowledged. |
| `OnError` | `Skip` | `Skip`: a write the database refuses for good goes to the dead-letter file. `Stop`: the flush stops at it. |
| `MaxRetries` | `5` | Retries with back-off for temporary database errors before a flush pauses and tries again later. |
| `FlushBeforeRead` | `false` | Apply a namespace's queued writes before a `find` or `count` on it. |
| `ShutdownFlushSeconds` | `30` | How long a graceful shutdown waits for the final flush. |

An invalid `FlushAt` or `TimeZone` stops the service at start-up with a clear message.

## What the caller sees

A queued write is answered with a signed `200`:

```json
{ "queued": true, "id": "9f1c0c9e5b4a4d3f8f3b…", "pending": 12 }
```

* `id` is a receipt for logs; `pending` is the queue length after this write.
* There is **no** `ids` list for an insert and no `affected` count for update, upsert and delete.
  Tables with `autoIncrement` keys give no ids back, so use client-generated keys (`uuid`, a natural key) when you need to refer
  to the row afterwards.
* The write has been **validated** against the stored schema before it is accepted: unknown table or column, missing required
  column, wrong JSON kind, a too long string, an update without a filter, a primary key change and similar mistakes are refused
  at once with `400`/`404`, exactly like in live mode. Only what the database itself decides (a unique value that already
  exists, for example) can still fail later.
* `"durability":"immediate"` in the payload sends one write live, even in a buffered namespace. Older queued writes of that
  namespace are applied first, so the order is kept.
* The permission `AllowUnfilteredWrites` is checked when the write is accepted, as in live mode.

## When queued writes are applied

A flush applies waiting writes **in the order they were accepted**. It runs

* every `FlushIntervalSeconds` while something is waiting,
* at each time listed in `FlushAt` (in `TimeZone`),
* when `FlushThreshold` writes are waiting,
* when a client calls `POST /v1/data/flush` with `{"namespace":"mygame"}` (needs the operation `flush`; the answer is
  `{"flushed":n,"deadLettered":m,"remaining":k,"halted":false,"enabled":true}`),
* before a schema change of that namespace, because the writes were checked against the old structure,
* before a read of that namespace if `FlushBeforeRead` is on,
* before an immediate write of that namespace,
* when the service shuts down gracefully (up to `ShutdownFlushSeconds`).

Consecutive inserts into the same table are merged into one database call on SQL providers (up to `Limits:MaxInsertRows`
rows). MongoDB keeps separate queued requests separate: ordered bulk inserts may partially succeed, so splitting and
replaying a failed merged batch could repeat already-applied writes. An individual multi-row MongoDB request can still
partially succeed on a database error.

For **fixed times only**, set `FlushIntervalSeconds: 0` and `FlushThreshold: 0`. Otherwise those triggers can apply writes
before the configured clock time. Namespace requests are serialized while buffering is enabled so concurrent writes
cannot enter between a schema change's drain and the actual schema change.

If a required flush halts, schema changes, immediate writes and `FlushBeforeRead` reads return `503 write_buffer_halted`
instead of overtaking pending writes. Fix the failing write/database and retry the flush. The explicit flush endpoint
itself returns its signed report; inspect `halted` and `remaining`.

Reads (`find`, `count`) always see only what has been applied. That is the point of the mode; turn on `FlushBeforeRead` if a
namespace needs read-your-writes.

## When things go wrong

| Situation | What happens |
|---|---|
| The database is down or times out | The flush retries with back-off (`MaxRetries`), then pauses. Nothing is lost; the next trigger tries again. `health` reports `halted: true` while paused. |
| The database refuses a write for good (constraint, bad value) | With `OnError: Skip` the write is appended to `<JournalPath>.dead` with its error code and the flush continues. With `Stop` it stays at the head of the queue and the flush halts until you fix or remove it. |
| A merged insert is refused | It is split and applied write by write, so only the offending write is rejected. |
| The buffer is full | New writes get `503 write_buffer_full` (or send them with `durability: immediate`). |
| The journal cannot be written (disk full) | The write is **not** accepted: `503 write_buffer_unavailable`. |
| The process crashes or is killed | With a journal: queued writes are loaded at the next start and applied. A write that the database had confirmed but the journal not yet marked can be applied twice, so make writes idempotent where it matters (`upsert`, `set` instead of `inc`, unique keys). Without a journal: queued writes are lost. |
| Graceful shutdown | A final flush runs. Anything left stays in the journal; without a journal a warning names how many writes were lost. |

The delivery guarantee is **at least once**.

## Operating it

* **Journal and dead-letter files contain the payloads** (row values). Protect them like the database: restrictive file
  permissions, a data volume, backups if the data matters. In Docker mount a volume at `/data`.
* **Watch** `ldb_writebehind_pending` (gauge), `ldb_writebehind_applied_total` and `ldb_writebehind_dead_lettered_total` (meter
  `Ldb.Api`), the `write-behind flush …` log lines, and the authenticated `health` result:
  `"writeBuffer":{"enabled":true,"pending":12,"oldestAgeSeconds":3,"halted":false}`.
* **Alert** on a growing `pending`, on `halted: true` and on any dead-lettered write.
* **Dead letters** are JSON lines `{"id","op","ns","p","code","message","t"}`. Fix the cause, then re-send the payload `p`
  with a normal write.
* **Several API instances** each keep their own queue and journal. Ordering is per instance.
* **Maintenance:** a flush before you stop the service is automatic. For a planned database migration call `flush` for each
  namespace first and wait for `remaining: 0`.

## Using it from the lt-database bundle

Nothing changes for callers. `Db.Insert/Update/Upsert/Delete` return a result whose `Json` is the queued receipt when the server
owner buffers that namespace: check it with `Db.IsQueued(result)`. `Db.Flush(ns, done)` forces a flush, and every write helper
accepts `immediate: true`. See the lt-database documentation.
