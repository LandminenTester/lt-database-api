# Contributing

Thank you for helping. Small, focused pull requests are easiest to review.

## Setup

* .NET 10 SDK.
* Docker (or another container runtime) for the engine tests. Without it, `dotnet test` still runs the unit and pipeline tests
  and skips the rest; `LDB_SKIP_DOCKER=1` skips them explicitly.

```bash
dotnet build          # warnings are errors
dotnet test
```

## Rules of the house

* **Security first.** Anything that touches authentication, identifiers, queries or logging needs a test that fails without
  the change. Never log payloads or values, never put driver or SQL text in a response.
* **No raw queries.** New capabilities are added to the filter grammar or the schema model, not as pass-through strings.
* **Same behaviour on every engine.** New data or schema behaviour goes into `ProviderContract` so PostgreSQL, MySQL and
  MongoDB are tested identically. If an engine cannot do something, say so in `docs/SCHEMA.md` and make the test opt out explicitly.
* **Document it.** Update `docs/API.md` or `docs/SCHEMA.md` with the change, and `docs/openapi.yaml` when endpoints change.
* Keep comments for the *why*. Public behaviour is described in the docs, not in code comments.

## Reporting bugs

Open an issue with the version, engine, a minimal request and the response `code`. For security problems follow
[SECURITY.md](SECURITY.md) instead.
