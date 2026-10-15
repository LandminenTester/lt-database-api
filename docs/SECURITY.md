# Security design

This document describes what the API protects, how, and where the protection ends. Read it before you put the API on a
network.

## Goals and non-goals

The API lets a **trusted server program** use a database it cannot reach directly. It defends against:

* anyone who can reach the HTTP port but does not hold a key secret,
* a network attacker who can read, replay or modify traffic,
* a compromised or buggy *caller* trying to read or damage data outside its namespace or by injecting queries,
* accidents (an update without a filter, a schema change that would destroy data).

It does **not** defend against a caller that holds a valid key and acts inside the permissions of that key, nor against
someone who controls the API host or the database server. Treat key secrets like database passwords.

## The signed envelope

Every operation is `POST /v1/...` with this JSON body (the *envelope*):

```json
{ "v": 1, "kid": "main", "ts": 1760000000, "nonce": "AAAAAAAAAAAAAAAAAAAAAA",
  "payload": "{\"namespace\":\"demo\",\"table\":\"players\"}", "sig": "42zlXLbDTxoL-oJ5eJ3IuG9oGFoByqkaYeBRHtC8x7c" }
```

* `kid` identifies the key. `ts` is the sender's Unix time in seconds. `nonce` is 8 to 64 characters, unique per request
  (16 random bytes, base64url, is recommended).
* `payload` is the operation's JSON **as a string**. The signature covers those exact bytes, so no JSON canonicalisation is
  needed on either side and a proxy re-formatting the envelope cannot break it.
* `sig` is `base64url(HMAC-SHA256(secret, message))` without padding, where `message` is these lines joined by `\n`:

```
v1
<request path, e.g. /v1/data/find>
<kid>
<ts>
<nonce>
<payload>
```

The path is part of the message, so a signature for `count` cannot be reused for `delete`.

The response is an envelope too: `{ "v":1, "ok":true, "status":200, "payload":"<json string>", "sig":"..." }`. Its signature is

```
v1resp
<the request's nonce>
<status>
<payload>
```

A client **must verify the response signature** before it trusts the result. That is what protects against a
man-in-the-middle when the client cannot pin certificates (the Replay.RE sandbox cannot). Errors that happen before the
caller is authenticated (bad envelope, bad signature, rate limit) are unsigned and deliberately uninformative.

### Test vectors

Secret `0123456789abcdef0123456789abcdef` (UTF-8 bytes):

| What | Input | Result |
|---|---|---|
| Request | path `/v1/data/find`, kid `main`, ts `1760000000`, nonce `AAAAAAAAAAAAAAAAAAAAAA`, payload `{"namespace":"demo","table":"players"}` | `42zlXLbDTxoL-oJ5eJ3IuG9oGFoByqkaYeBRHtC8x7c` |
| Response | request nonce `AAAAAAAAAAAAAAAAAAAAAA`, status `200`, payload `{"count":3}` | `Uk6xxTTmVDyXYAuBqMp5aF4vxbPJQH7jjNGM2gs5MH8` |

They were computed with Python's `hmac` module and are asserted in `tests/Ldb.Tests/CoreTests.cs`.

## Checks, in order

1. Per-address lock-out: too many failed attempts in the current minute returns `429`.
2. Body size limit (`MaxBodyBytes`, default 1 MiB) while reading.
3. Envelope shape.
4. Signature against every active secret of the key. Unknown key ids are checked against a dummy secret, so timing does not
   reveal which ids exist. Comparison is constant time.
5. Timestamp within `TimestampToleranceSeconds` (default 30).
6. Source address within the key's `AllowedCidrs` (if set).
7. One-time nonce (remembered for twice the tolerance window; the cache fails closed when full).
8. Any failure in 4 to 7 returns the same `401 unauthorized` and counts towards the lock-out.
9. Authenticated from here on: per-key rate limit, allowed operation, allowed namespace, permission for unfiltered writes.
10. Idempotency key lookup, then the operation.

## What is protected how

| Threat | Defence |
|---|---|
| Guessing or brute-forcing a key | 256-bit secrets enforced by a 32 byte minimum, constant-time compare, uniform errors, per-address lock-out |
| Replay | Timestamp window plus one-time nonce; the nonce is only stored after a valid signature, so unauthenticated traffic cannot fill the cache |
| Tampering with a request | HMAC over path, key id, timestamp, nonce and payload bytes |
| Tampering with or forging a response | Response HMAC bound to the request nonce, status and body |
| Reading traffic | HTTPS required; plain HTTP is refused except from loopback or with `AllowInsecureHttp`. Forwarded headers are only honoured from `TrustedProxies` |
| One caller reading another's data | Keys list `AllowedNamespaces`; physical names are `{namespace}__{table}`; schema sync only touches the namespace's own objects |
| SQL / NoSQL injection | No raw queries exist in the API. Filters and rows are parsed into a fixed grammar, identifiers must match `^[a-z][a-z0-9]*(_[a-z0-9]+)*$` and exist in the stored schema, values are bound parameters, identifiers are quoted per dialect, Mongo documents are built value by value so `$`-keys can never become operators |
| Accidental mass changes | `update` and `delete` require a filter; `all: true` needs the key permission `AllowUnfilteredWrites` |
| Accidental data loss through schema sync | Dry run, `DestructiveMode` (`Apply`, `Quarantine`, `Deny`), narrowing checks before shrinking a column |
| Duplicate writes after a timeout | `idempotencyKey` on writes; a retry returns the first result, re-signed for the new nonce |
| Resource exhaustion | Body, row, filter-depth, list and schema size limits, request timeouts, per-key and per-address rate limits |
| Information leakage | No stack traces, no SQL or driver messages in responses, no payloads or row values in logs, `Server` header removed, `Cache-Control: no-store` |

## Limits you should know about

* **Shared secret, not public-key.** HMAC needs the secret on both sides. Anyone who can read the secret from the game
  server can act as that key. Keep it out of anything that is sent to players, and give the file restrictive permissions.
* **No caller identity inside the game server.** Every bundle on the same game server shares the key the connector uses.
  Separation between bundles is by namespace and is enforced by the connector's own allow-list, which assumes the server
  owner trusts the bundles they install. For hard separation use one key per game server and restrict it to that server's
  namespaces and address.
* **Replay protection is per instance.** The nonce and idempotency caches live in memory. Running several API instances
  behind a load balancer needs sticky routing or a shared cache (not included). A restart forgets nonces, but the
  timestamp window (30 s by default) bounds what can be replayed.
* **Clock skew.** The caller's clock must be within the tolerance. `GET /v1/health` returns the server time so a caller
  with an unreliable clock can compute an offset.
* **Not a firewall.** Rate limits are per address and per key and live in memory. Put the service behind a reverse proxy
  or firewall that restricts who can connect at all.
* **MySQL DDL is not transactional.** A failed schema sync can stop half way. The next sync repairs what is missing
  (see [SCHEMA.md](SCHEMA.md)), but take backups.

## Database privileges

Use two database accounts. The data account never needs structure changes; the schema account is only used by `schema/sync`.
Ready-to-run scripts are in [`docker/grants`](../docker/grants).

| Engine | Data account (`DmlConnectionString`) | Schema account (`DdlConnectionString`) |
|---|---|---|
| PostgreSQL | `SELECT, INSERT, UPDATE, DELETE` on the schema's tables, `USAGE` on sequences | `CREATE` on the schema and ownership of the tables it creates |
| MySQL / MariaDB | `SELECT, INSERT, UPDATE, DELETE` | `CREATE, ALTER, DROP, INDEX, REFERENCES` plus the data privileges, `LOCK TABLES` is not needed |
| MongoDB | `readWrite` on the database | `dbAdmin` and `readWrite` on the database |

The data account must also be able to read the bookkeeping table `_ldb_schema` (it is created by the schema account).
If you give both connection strings the same account, everything still works, only with broader privileges.

## Key management

* Generate secrets with `openssl rand -base64 48` (or `python -c "import secrets;print(secrets.token_urlsafe(48))"`).
* Reference them as `env:NAME` or `file:/run/secrets/name`. Plain values in configuration are refused, secrets shorter
  than 32 bytes stop the service from starting.
* **Rotation without downtime:** put the new secret in `Next`, restart the API, switch the caller to the new secret, then
  move it to `Current` and clear `Next`. While both are set, either one is accepted and the response is signed with the
  one the caller used.
* Use one key id per caller and limit it with `AllowedNamespaces`, `AllowedOperations` and `AllowedCidrs`.

## Transport

Terminate TLS in Kestrel or, more commonly, in a reverse proxy (see [DEPLOYMENT.md](DEPLOYMENT.md)). Use TLS 1.2 or newer.
When a proxy terminates TLS, list its address under `Security:TrustedProxies` so `X-Forwarded-For` and
`X-Forwarded-Proto` are trusted from it and from nobody else. The service sends `Strict-Transport-Security` on HTTPS.
mTLS between proxy and clients is possible on the proxy; it is optional because the Replay.RE sandbox cannot present a
client certificate.

## Write-behind files

With the optional [write-behind mode](WRITE-BEHIND.md) and a `JournalPath`, queued writes are stored in that file, and writes the
database refused for good in `<JournalPath>.dead`. Both contain the row values of the requests. They are as sensitive as the
database itself: keep them on a volume only the service account can read, include them in your backup policy if the data
matters, and delete the dead-letter file once handled. Anyone who can write to the journal can inject writes that will be
applied to the database, so it must not be writable by anyone else. Queued writes are validated when accepted and again by the
database when applied; permissions of the key (`AllowedNamespaces`, `AllowUnfilteredWrites`) are checked when accepted.

## Logging and monitoring

Logs are JSON on stdout. Each request produces one `audit` line with key id, address, operation, namespace, status,
duration and request id, never payloads or values. Counters `ldb_requests_total` and `ldb_auth_failures_total` are exposed
through `System.Diagnostics.Metrics` (meter `Ldb.Api`); attach OpenTelemetry or `dotnet-counters` to read them. Alert on
bursts of `401`/`429` and on `5xx`.

## Hardening checklist

- [ ] HTTPS only, `AllowInsecureHttp` is `false`
- [ ] Keys use `AllowedCidrs` for the game server's address
- [ ] Secrets come from files or the environment with restricted permissions, and are rotated now and then
- [ ] Separate data and schema database accounts, with the privileges above
- [ ] The database is not reachable from the internet; only the API host can connect
- [ ] Container runs as non-root with a read-only file system (the supplied image does)
- [ ] Backups of the database exist and a restore has been tested
- [ ] `DestructiveMode` is a conscious choice (`Quarantine` is the reversible one)
- [ ] Logs are collected and alerts exist for repeated `401`/`429`
