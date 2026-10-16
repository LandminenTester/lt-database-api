# API reference

All operations use the signed envelope described in [SECURITY.md](SECURITY.md#the-signed-envelope). This page documents the
**payload** (the string inside `payload`) of each operation and the **result** (the string inside the response's `payload`).
A machine-readable summary is in [openapi.yaml](openapi.yaml).

## Start with a complete client

An **endpoint** is an HTTP method and path; a **payload** is the operation's data inside the signed message. The JSON snippets below show payloads, not unsigned HTTP request bodies. Use the [reference client](BUNDLE-INTEGRATION.md) or the connector's [explained C# walkthrough](https://blackice.landminentester.de/lt-database/docs/EXAMPLES/) for a first working call. The [glossary](https://blackice.landminentester.de/glossary/) explains short names. `inc` means increment: add to the existing numeric value; `set` replaces it.

## Conventions

* JSON, UTF-8, `camelCase` property names. Unknown properties in a payload are ignored; wrong types are rejected.
* Every operation except `GET /v1/health` is `POST`. (The Replay.RE sandbox only offers `GET` and `POST`.)
* Every payload except `health` carries `namespace`: 1 to 24 characters, `^[a-z][a-z0-9]*(_[a-z0-9]+)*$`.
  Tables and columns follow the same pattern (36 and 48 characters at most). Double underscores are not allowed.
* Success is HTTP 200 with `ok: true`. Failures carry `ok: false`, an HTTP status and a payload
  `{"error":{"code":"...","message":"..."}}`.
* Writes may carry `"durability": "immediate"`. It matters only when the server owner turned on the optional
  [write-behind mode](WRITE-BEHIND.md): such a write runs live even in a buffered namespace.
* Writes may carry `"idempotencyKey": "<string>"`. A second request with the same key and the same payload returns the first
  result without writing again; the same key with a different payload returns `422 idempotency_conflict`. Keys are remembered
  for ten minutes.

## Endpoints

| Method and path | Operation name (for `AllowedOperations`) | Purpose |
|---|---|---|
| `GET /v1/health` | – | `{"status":"ok","time":<unix seconds>}`, no authentication |
| `GET /healthz`, `GET /readyz` | – | Liveness and readiness probes (readiness checks the database) |
| `POST /v1/health` | `health` | Authenticated: provider name and database reachability |
| `POST /v1/schema/sync` | `schema.sync` | Apply a declared schema to a namespace |
| `POST /v1/schema/describe` | `schema.describe` | Return the stored schema |
| `POST /v1/data/insert` | `insert` | Insert rows |
| `POST /v1/data/find` | `find` | Query rows |
| `POST /v1/data/update` | `update` | Change rows |
| `POST /v1/data/upsert` | `upsert` | Insert or update by key |
| `POST /v1/data/delete` | `delete` | Delete rows |
| `POST /v1/data/count` | `count` | Count rows |
| `POST /v1/data/flush` | `flush` | Apply queued writes of a namespace now (only meaningful with [write-behind](WRITE-BEHIND.md)) |

## Schema

### `schema/sync`

```json
{ "namespace": "mygame", "dryRun": false,
  "schema": { "tables": [ {
    "name": "players",
    "primaryKey": ["id"],
    "columns": [
      { "name": "id",    "type": "int64",  "autoIncrement": true },
      { "name": "name",  "type": "string", "length": 32, "unique": true },
      { "name": "score", "type": "int32",  "default": 0 },
      { "name": "bio",   "type": "text",   "nullable": true, "renamedFrom": "about" }
    ],
    "indexes": [ { "name": "by_score", "columns": ["score"], "unique": false } ]
  } ] } }
```

Result:

```json
{ "namespace": "mygame", "hash": "9f2c…", "changed": true, "dryRun": false, "status": "applied",
  "plan": [ { "kind": "createTable", "table": "players", "destructive": false } ] }
```

`status` is `unchanged` (nothing to do), `planned` (dry run), `applied` or `denied` (dry run under `DestructiveMode: Deny`).
Each `plan` entry has `kind`, `table`, optional `column`, `newName`, `index`, `note` and `destructive`.
Types, defaults and the sync rules are described in [SCHEMA.md](SCHEMA.md). The declared schema **is** the schema of the
namespace: tables and columns that are no longer declared are removed (or quarantined, depending on the server setting).

### `schema/describe`

Payload `{ "namespace": "mygame" }`. Result `{ "namespace", "hash", "schema", "updatedAt" }`; `schema` is `null` for a
namespace that was never synced.

## Data

Every data operation needs a synced namespace and a declared table; unknown tables return `404 unknown_table`, unknown
namespaces `404 namespace_unknown`, unknown columns `400 unknown_column`.

### Values

| Column type | JSON value | Returned as |
|---|---|---|
| `int32`, `int64` | number | number |
| `decimal` | number or numeric string | number |
| `float64` | number | number |
| `bool` | `true` / `false` | boolean |
| `string`, `text` | string (length checked) | string |
| `json` | any JSON value | the same JSON value |
| `datetime` | ISO 8601 string, interpreted as UTC | ISO 8601 UTC, e.g. `2026-10-15T12:34:56.7890000Z` |
| `date` | `yyyy-MM-dd` | `yyyy-MM-dd` |
| `uuid` | GUID string | lower-case GUID string |
| `bytes` | base64 string | base64 string |

`null` is accepted for nullable columns.

### `data/insert`

`{ "namespace", "table", "rows": [ {…}, {…} ] }` (at most `Limits:MaxInsertRows`, default 1000). Missing columns use their
default or auto-increment; a missing required column is `400 missing_value`. Result `{ "inserted": 2, "ids": [1, 2] }`
(`ids` hold the primary key of each row when the table has a single-column key).

SQL engines insert all rows in one transaction. MongoDB inserts the rows in order and stops at the first error, so earlier
rows of that request may remain.

### `data/find`

```json
{ "namespace": "mygame", "table": "players",
  "filter": { "field": "score", "op": "gte", "value": 10 },
  "fields": ["name", "score"],
  "sort": [ { "field": "score", "dir": "desc" } ],
  "limit": 50, "offset": 0 }
```

Result `{ "rows": [ {…} ], "hasMore": true }`. `limit` is capped by `Limits:MaxRows` (default 1000); without `sort` the
primary key order is used so paging is stable. `json` columns cannot be sorted; on MongoDB they cannot be filtered either.

### Filters

A filter is an object tree:

* condition `{ "field": "<column>", "op": "<operator>", "value": <value> }`
* `{ "and": [ filter, … ] }`, `{ "or": [ filter, … ] }`, `{ "not": filter }`

| Operator | Meaning | Value |
|---|---|---|
| `eq`, `ne` | equals / differs (`eq` with `null` means IS NULL) | scalar |
| `gt`, `gte`, `lt`, `lte` | comparison | scalar (not null) |
| `in`, `nin` | member of a list / not | list of 1 to 1000 scalars |
| `isNull` | is null (`value: false` means not null) | boolean, default `true` |
| `startsWith`, `contains` | text match, case sensitive, wildcards are escaped | non-empty string, text columns only |

At most 8 levels and 128 conditions. As in SQL, `ne` and the comparisons do not match rows where the column is `NULL`.

### `data/update`

```json
{ "namespace": "mygame", "table": "players",
  "filter": { "field": "name", "op": "eq", "value": "alice" },
  "set": { "bio": "hello" }, "inc": { "score": 5 } }
```

Needs `set` and/or `inc` and a `filter`; `"all": true` replaces the filter and requires the key permission
`AllowUnfilteredWrites`. Primary key columns cannot be changed. Result `{ "affected": 1 }`.

### `data/upsert`

`{ "namespace", "table", "row": {…}, "keys": ["name"] }`. `keys` must match the primary key or a unique index exactly.
Inserts the row, or updates the other columns of the existing one. Result `{ "affected": 1 }`.

### `data/delete`

`{ "namespace", "table", "filter": … }` or `"all": true` (key permission required). Result `{ "affected": n }`.

### `data/count`

`{ "namespace", "table", "filter": … }` → `{ "count": n }`.

### `data/flush`

`{ "namespace": "mygame" }`. Applies the namespace's queued writes now and waits for it. Result
`{ "flushed": n, "deadLettered": m, "remaining": k, "halted": false, "enabled": true }`. Without write-behind it returns
`enabled: false` and does nothing. The key needs the operation `flush`.

### Queued answers (write-behind only)

When the server owner buffers a namespace, `insert`, `update`, `upsert` and `delete` are validated and answered with
`{ "queued": true, "id": "…", "pending": 12 }` instead of the results above (no generated ids, no affected counts). Details,
guarantees and failure handling: [WRITE-BEHIND.md](WRITE-BEHIND.md). `POST /v1/health` then also reports
`"writeBuffer": { "enabled", "pending", "oldestAgeSeconds", "halted" }`.

## Errors

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `invalid_request` | Envelope or payload does not fit the operation |
| 400 | `invalid_identifier` | A namespace, table or column name is not allowed |
| 400 | `invalid_schema` | The declared schema is invalid (the message names the column) |
| 400 | `invalid_filter`, `invalid_sort` | Filter or sort is malformed or uses an unsuitable column |
| 400 | `unknown_column`, `missing_value`, `invalid_value`, `value_too_long`, `filter_required` | Data problems |
| 401 | `unauthorized` | Bad key, signature, timestamp or replayed nonce (always the same answer) |
| 403 | `forbidden` | The key may not use this operation, namespace or unfiltered writes; or `https_required` |
| 404 | `unknown_table`, `namespace_unknown` | Nothing declared yet |
| 409 | `constraint_violation` | Duplicate or missing value, broken reference |
| 409 | `destructive_denied`, `data_does_not_fit`, `unsupported_change`, `sync_in_progress`, `conflict_retry` | Schema sync refused or busy |
| 413 | `payload_too_large`, `too_many_rows` | Limits exceeded |
| 503 | `write_buffer_full`, `write_buffer_unavailable` | Write-behind queue is full or its journal cannot be written; the write was not accepted |
| 503 | `write_buffer_halted` | A prerequisite flush could not drain older writes; immediate write, schema change or consistent read was blocked |
| 422 | `idempotency_conflict` | Idempotency key reused with another payload |
| 429 | `rate_limited` | Per-key, per-address or lock-out limit |
| 500 / 504 | `database_error`, `internal_error`, `database_timeout`, `timeout` | Server side; details are only in the log |
