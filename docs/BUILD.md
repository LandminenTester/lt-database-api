# Build, test and release

## Toolchain and source

Install the .NET 10 SDK and Git. Install a Linux-container-capable Docker runtime for the integration tests.
Runtime installations from the release archives do not need the SDK.

```sh
git clone https://github.com/LandminenTester/lt-database-api
cd lt-database-api
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

The test project includes protocol/security, request-pipeline, schema/query validation, scheduling, journal recovery,
write-behind and provider contract tests. PostgreSQL 17, MySQL 8.4 and MongoDB 8 containers start automatically.
Check the final counts: without Docker those engine tests are **skipped**, not passed. `LDB_SKIP_DOCKER=1` explicitly
selects the unit-only mode. Linux CI runs the full matrix; Windows CI runs unit tests.

## Local development

Configure settings as in [Deployment](DEPLOYMENT.md), with development credentials restricted to disposable namespaces.
For all three engines at once use `docker compose -f docker-compose.all.yml up -d --build` after configuring `.env`.
Run the [Python reference client](../tools/ldb_client.py) against each loopback API endpoint for signed requests.
Never commit `.env`, production settings, secrets, journals or database contents.

## Publish a native application

```sh
dotnet publish src/Ldb.Api -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None -o out/linux-x64
```

Use `linux-arm64` or `win-x64` for the other release targets. Package the whole publish directory, including
`appsettings.json`, plus `README.md` and `LICENSE`. The file name is `Ldb.Api` (`Ldb.Api.exe` on Windows).
Keep configuration and journal files in persistent, protected locations outside replaceable release directories.

## Docker

```sh
docker build -f docker/Dockerfile -t lt-database-api:local .
```

The image runs as an unprivileged user. Follow [Deployment](DEPLOYMENT.md) for keys, read-only filesystem settings,
networking and a writable `/data` volume when write-behind uses a journal. Do not bake secrets into an image.

## Release process

1. Commit the reviewed source and documentation. Require a green CI including the Linux database tests.
2. Choose a new semantic version; verify that the tag does not already exist.
3. Push `v<version>` to the repository. `.github/workflows/release.yml` runs the tests, publishes the three native
   archives and `SHA256SUMS.txt`, and builds the versioned GHCR image plus `latest`.
4. Inspect both release jobs and verify the archive contents and checksums. A tag alone is not a finished release.
5. Update the shared documentation with the docs repository's sync workflow and review its source-revision diff.

## Maintaining the buffer

Keep public payloads and [OpenAPI](openapi.yaml) in sync. Add regression tests for ordering, journal behavior and
error paths when changing write-behind. A halted prerequisite flush must block schema changes, immediate writes and
reads configured for consistency. Namespace requests are serialized while buffering is enabled; background flushes
also serialize through the flush lock. Validate against the refreshed schema after synchronization.

MongoDB bulk inserts may partially succeed, so separate queued insert requests are not merged on that provider.
An individual multi-row MongoDB insert can still partially commit on error. Crash recovery remains **at least once**;
use stable unique keys and idempotent updates where retries matter. See [Write-behind](WRITE-BEHIND.md).
