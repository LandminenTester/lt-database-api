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

BlackICE server components run in a sandbox. The facts that shape this API (from the SDK 1.1.0 index and
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
wraps all of this: other server bundles call its exports (`Sync`, `Insert`, `Find`,
`Update`, `Upsert`, `Delete`, `Count`, `Flush`) and never see a key, a URL or an HTTP response. It has no client component, so players have
nothing to call. See that repository's README for installation and the export reference.

Quick setup of the pair:

1. Run this API with a key `gameserver` limited to your namespaces and to the game server's address.
2. In the bundle's `config.toml` allow the API host under `[Networking]`.
3. Put the secret into the bundle's `vfs/` folder as described in the bundle README and set the API URL and key id.
4. From your own bundle, declare your tables once at start-up with `Sync` and use the other exports at runtime.

The [public bundle guide](https://blackice.landminentester.de/lt-database/docs/USAGE/) includes compiled C# examples.
When write-behind is enabled on the API, check `Db.IsQueued(result)` before expecting generated IDs or row counts.
`Db.Flush(ns, done)` waits for a flush; every write helper accepts `immediate: true`. Check flush `halted` and `remaining`
before depending on the queued writes. See [Write-behind](WRITE-BEHIND.md).
