# Architecture

```
src/
  Ldb.Core                    contracts, no I/O
    Protocol/                 envelope records, HMAC signing
    Schema/                   schema model, validator/normaliser, hasher, differ
    Query/                    filter syntax tree + parser, request and result records
    Config/LdbOptions.cs      all settings
    IDatabaseProvider.cs      what an engine has to implement
  Ldb.Providers.Sql           shared SQL machinery
    SqlDialect                engine differences: quoting, types, DDL, upsert
    SqlQueryBuilder           filter tree -> parameterised WHERE
    SqlValues                 JSON <-> typed driver values
    SqlProviderBase           schema sync + data operations written once
  Ldb.Providers.Postgres      dialect + connection + advisory lock
  Ldb.Providers.MySql         dialect + connection + GET_LOCK
  Ldb.Providers.Mongo         complete provider (collections, $jsonSchema, lock documents)
  Ldb.Api                     ASP.NET Core minimal API host
    RequestPipeline           authentication, authorisation, limits, signing
    Operations                the list of operations and their payload handling
    Security/                 key registry, secret resolver, nonce / rate / idempotency caches
tests/Ldb.Tests               unit, pipeline and provider-contract tests
```

## Request flow

`Program.cs` maps one route per entry of `Operations.All`. Every route calls `RequestPipeline.HandleAsync`, which reads the
body (bounded), verifies the envelope, runs the operation against the configured `IDatabaseProvider` and signs the answer.
Providers receive only validated, typed requests. They never see credentials, and the pipeline never sees SQL.

The provider is chosen once at start-up from `Ldb:Database:Provider`.

## Design decisions

* **Declarative schema + stored previous declaration.** Diffing two declarations is deterministic and engine independent.
  The database is consulted only to repair or skip steps, which avoids reverse-mapping every native type back to a portable one.
* **No raw queries, ever.** The filter grammar is small on purpose. It is what makes the API safe to expose to semi-trusted callers.
* **The signature covers the payload as bytes.** No canonical JSON, nothing a proxy can reformat.
* **Everything stateful is in memory** (nonces, idempotency, rate counters). That keeps the service a single binary with no
  extra dependency; the cost is documented in [SECURITY.md](SECURITY.md#limits-you-should-know-about).
* **One contract test suite for all engines.** `ProviderContract` holds the scenarios; `PostgresContractTests`,
  `MySqlContractTests` and `MongoContractTests` only start a container. A new engine is accepted when it passes the same tests.

## Adding a database engine

1. For a SQL engine: derive from `SqlDialect` (quoting, types, DDL, upsert, introspection queries) and `SqlProviderBase`
   (connection factory, lock). For anything else implement `IDatabaseProvider` directly, like `MongoProvider`.
2. Register it in the `switch` in `Program.cs`.
3. Add a `…ContractTests` class that starts the engine in a container.
4. Document the type mapping in [SCHEMA.md](SCHEMA.md).

## Building and testing

```bash
dotnet build                       # warnings are errors
dotnet test                        # unit + pipeline tests always; engine tests need a container runtime
LDB_SKIP_DOCKER=1 dotnet test      # skip the engine tests explicitly
```
