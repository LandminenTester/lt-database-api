# Writing a client, and using the API from a Replay.RE bundle

## Any client

A client needs three things: an HTTP POST, HMAC-SHA256 and a way to make a unique nonce.

1. Build the operation payload as a JSON string.
2. Choose `ts` (Unix seconds, within 30 s of the server) and a unique `nonce`.
3. `sig = base64url(HMAC-SHA256(secret, "v1\n" + path + "\n" + kid + "\n" + ts + "\n" + nonce + "\n" + payload))`.
4. POST `{ "v":1, "kid", "ts", "nonce", "payload", "sig" }` to `https://host/v1/...`.
5. Parse the response envelope, compute `HMAC(secret, "v1resp\n" + nonce + "\n" + status + "\n" + payload)`, compare it with
   `sig` in constant time and **reject the answer if it differs**.
6. Parse `payload` as JSON.

[`tools/ldb_client.py`](../tools/ldb_client.py) is a complete, 80-line example. Test your signing against the vectors in
[SECURITY.md](SECURITY.md#test-vectors).

If the client's clock may be wrong, call `GET /v1/health` first and use the returned `time` to compute an offset.

For idempotent writes, send a stable `idempotencyKey` in the payload and re-send the same request after a timeout; you get
the original result instead of a second write.

## Replay.RE BlackICE bundles

BlackICE server components run in a sandbox. The facts that shape this API (from the SDK 1.1.0 index; SDK 1.3.0 added `re.sql`, see
[below](#api-or-resql-which-one), and
[docs.replay.re](https://docs.replay.re/ugc/authoring/script-api/)):

* The only outbound channel is `re.net.Http.Get(url, headers?, timeoutMs)` and `re.net.Http.Post(url, body, headers?, timeoutMs)`.
  No `PUT`, `PATCH` or `DELETE`, which is why every operation here is a `POST`.
* Calls return a `Future<HttpResponse>` and can be awaited. `HttpResponse` has `Status`, `Error`, `Ok`, `Text()` and `Header(name)`.
* Destinations must be listed in the bundle's `config.toml`:

  ```toml
  [Networking]
  allowed = ["db.example.com"]
  ```

* A bundle's `vfs/` folder is a private, plain-file area on the server and is never streamed to clients. It is where the connector
  bundle reads its key secret from. File permissions on the game server are the only protection, so lock them down.

The companion bundle [**lt_database**](https://github.com/LandminenTester/lt-database) (folder `lt_database`)
wraps all of this: other server bundles use its C# helper methods (`Sync`, `Insert`, `Find`,
`Update`, `Upsert`, `Delete`, `Count`, `Flush`), which call the `Call` and `TakeResult` exports and never see a key, a URL or an HTTP response. It has no client component, so players have
nothing to call. See that repository's README for installation and the export reference.

### API or `re.sql`: which one?

Since version 0.3.0 the **lt_database** bundle can also talk to PostgreSQL or SQLite directly through the host's `re.sql` provider
(`"backend": "sql"` in its `vfs/database.json`, SDK 1.3.0 or newer) without this API. Consumers see the same exports, requests, results and
error codes, and the schema hash and the receipt fingerprint are identical, so a database created through this API can be taken over by
the `sql` backend and back (never both at once on the same namespaces).

Use **this API** when
several game servers share one database, you use MySQL, MariaDB or MongoDB, you want write-behind, or the game server host must not hold
database credentials (the bundle only holds a key limited to its namespaces and address).
Use **`re.sql`** when one game server is enough, you want nothing else to run (SQLite is a file), or you are developing and testing.
The connector's guide [Choosing a backend and settings](https://blackice.landminentester.de/lt-database/docs/USE-CASES/) compares both with
use cases, and [SQL backend](https://blackice.landminentester.de/lt-database/docs/SQL-BACKEND/) lists every difference.

Quick setup of the pair:

1. Run this API with a key `gameserver` limited to your namespaces and to the game server's address.
2. In the bundle's `config.toml` allow the API host under `[Networking]`.
3. Put the secret into the bundle's `vfs/` folder as described in the bundle README and set the API URL and key id.
4. From your own bundle, declare your tables once at start-up with `Sync` and use the other exports at runtime.

The [public bundle guide](https://blackice.landminentester.de/lt-database/docs/USAGE/) includes compiled C# examples.
When write-behind is enabled on the API, check `Db.IsQueued(result)` before expecting generated IDs or row counts.
`Db.Flush(ns, done)` waits for a flush; every write helper accepts `immediate: true`. Check flush `halted` and `remaining`
before depending on the queued writes. See [Write-behind](WRITE-BEHIND.md).
