# lt-database-api

[![CI](https://github.com/LandminenTester/lt-database-api/actions/workflows/ci.yml/badge.svg)](https://github.com/LandminenTester/lt-database-api/actions/workflows/ci.yml)

A small, security-first HTTP API that sits in front of **PostgreSQL**, **MySQL / MariaDB** or **MongoDB** and gives
trusted clients one simple, engine-independent way to:

* **describe a schema declaratively** (tables, columns, types, lengths, defaults, indexes) and have the API create it
  and keep it in sync on every later run, and
* **read, write, update and delete data** through a small, injection-proof query language.

It was written for game-server bundles (the [Replay.RE BlackICE](https://docs.replay.re) sandbox can only talk to the
outside world over HTTP and cannot open database connections), but nothing in it is game specific: any server-side
program that can send an HTTP POST and compute an HMAC can use it.

```
 your program ──HTTPS + signed JSON──▶  lt-database-api  ──▶  PostgreSQL | MySQL | MongoDB
 (trusted server)                       (this project)
```

## Why this exists

| Problem | What this project does |
|---|---|
| The sandbox has no database driver, only `HTTP GET/POST` | One POST endpoint per operation, JSON in and out |
| You do not want three code paths for three databases | One schema format and one query format, translated per engine |
| Exposing a database-backed API is risky | Every request is HMAC-signed, replay-protected, authorized per key and per namespace, and every response is signed too |
| Schemas drift when the program changes | `schema/sync` diffs the declared schema against the last applied one and applies the difference, safely |

## Quick start

### Docker (development)

```bash
git clone https://github.com/LandminenTester/lt-database-api
cd lt-database-api
cp .env.example .env              # then edit it and replace the example secret
docker compose --profile postgres up --build
curl http://localhost:8080/v1/health
```

`--profile` can be `postgres`, `mysql` or `mongodb`. The compose file allows plain HTTP on purpose so you can try things
locally. **Do not expose that port to a network.** For production see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

### Native (Windows or Linux)

Download the archive for your system from the [releases page](../../releases) (single self-contained executable, no .NET
installation needed), or build it yourself:

```bash
dotnet publish src/Ldb.Api -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
# or: -r win-x64
```

Configure it with environment variables or an `appsettings.Production.json` next to the executable, then start `./Ldb.Api`.
It can also run as a Windows service or a systemd unit, see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

### Try a request

`tools/ldb_client.py` is a dependency-free reference client (Python 3.8+). It signs requests exactly like the bundle does:

```bash
export LDB_URL=http://localhost:8080 LDB_KEY_ID=main LDB_SECRET='the-secret-from-your-env-file'
python tools/ldb_client.py schema/sync '{"namespace":"demo","schema":{"tables":[{"name":"players","primaryKey":["id"],"columns":[
  {"name":"id","type":"int64","autoIncrement":true},{"name":"name","type":"string","length":32,"unique":true},{"name":"score","type":"int32","default":0}]}]}}'
python tools/ldb_client.py data/insert '{"namespace":"demo","table":"players","rows":[{"name":"alice","score":10}]}'
python tools/ldb_client.py data/find   '{"namespace":"demo","table":"players","filter":{"field":"score","op":"gte","value":5}}'
```

## Configuration at a glance

Everything lives under the `Ldb` section. Use `appsettings.json` / `appsettings.Production.json` or environment variables
with the prefix `LDB_` and `__` as separator (`LDB_Ldb__Database__Provider=mysql`). Full reference: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#configuration-reference).

```jsonc
{
  "Ldb": {
    "Database": { "Provider": "postgres", "DmlConnectionString": "...", "DdlConnectionString": "..." },
    "Security": {
      "Keys": [ {
        "KeyId": "main",
        "Current": "env:LDB_KEY_MAIN",        // secrets are references, never plain values
        "AllowedNamespaces": [ "mygame" ],
        "AllowedCidrs": [ "203.0.113.10/32" ]  // the game server's address
      } ]
    },
    "Schema": { "DestructiveMode": "Apply" }   // Apply | Quarantine | Deny
  }
}
```

## Documentation

| Document | Contents |
|---|---|
| [docs/API.md](docs/API.md) | Every endpoint, request and response shape, the filter language, error codes |
| [docs/SCHEMA.md](docs/SCHEMA.md) | Portable column types, how they map to each engine, how schema sync decides and what it will not do |
| [docs/SECURITY.md](docs/SECURITY.md) | The signing protocol, threat model, hardening checklist, database privileges, key rotation |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Docker, Windows service, systemd, reverse proxy with TLS, configuration reference, backups, monitoring |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the code is organised and how to add a database engine |
| [docs/BUNDLE-INTEGRATION.md](docs/BUNDLE-INTEGRATION.md) | Writing your own client; using it from a Replay.RE BlackICE bundle (companion bundle: [westwood_database](https://github.com/LandminenTester/westwood_database)) |
| [docs/openapi.yaml](docs/openapi.yaml) | OpenAPI 3.1 description of the endpoints |

## Security in one paragraph

Requests carry a key id, a timestamp, a one-time nonce and an HMAC-SHA256 over the exact payload bytes; the API answers
with an HMAC over the status, the request nonce and the response body. Secrets are 32+ random bytes referenced from the
environment or a file, compared in constant time, and can be rotated without downtime. Keys are limited to namespaces,
operations and (optionally) source networks, with per-key rate limits and a lock-out for repeated failures. The API never
accepts raw SQL or raw Mongo operators, binds every value as a parameter, quotes every identifier and validates both
against the stored schema. Plain HTTP is refused unless explicitly allowed. Details and the honest limits of this design
are in [docs/SECURITY.md](docs/SECURITY.md); please report vulnerabilities as described in [SECURITY.md](SECURITY.md).

## Project status

Tested engines: PostgreSQL 17, MySQL 8.4 and MongoDB 8. The same scenarios run unchanged against all three in containers
(`dotnet test`; the engine tests are skipped automatically if no container runtime is available, and run on every push in CI).
Older PostgreSQL (14+), MySQL 8.0+, MariaDB and MongoDB 6+ use the same features and are expected to work but are not part of
the automated matrix yet.

## License

MIT, see [LICENSE](LICENSE).
