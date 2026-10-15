#!/usr/bin/env python3
"""Reference client for lt-database-api (Python 3.8+, standard library only).

    export LDB_URL=https://db.example.com LDB_KEY_ID=main LDB_SECRET='<the key secret>'
    python ldb_client.py data/find '{"namespace":"demo","table":"players"}'

The first argument is the endpoint below /v1 (for example schema/sync, data/insert, health), the second the JSON payload.
It signs the request, sends it, verifies the response signature and prints the result payload.
This file is also the clearest specification of the protocol in docs/SECURITY.md.
"""
import base64
import hashlib
import hmac
import json
import os
import secrets
import sys
import time
import urllib.error
import urllib.request


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def mac(secret: bytes, message: str) -> str:
    return b64url(hmac.new(secret, message.encode("utf-8"), hashlib.sha256).digest())


def call(base_url: str, key_id: str, secret: str, endpoint: str, payload: str, offset: int = 0):
    path = "/v1/" + endpoint.strip("/")
    ts = int(time.time()) + offset
    nonce = b64url(secrets.token_bytes(16))
    message = "\n".join(["v1", path, key_id, str(ts), nonce, payload])
    envelope = {"v": 1, "kid": key_id, "ts": ts, "nonce": nonce, "payload": payload, "sig": mac(secret.encode(), message)}
    request = urllib.request.Request(
        base_url.rstrip("/") + path,
        data=json.dumps(envelope).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:  # the API answers errors with a JSON envelope too
        status, body = error.code, error.read()
    answer = json.loads(body)
    if answer.get("status") != status:
        raise SystemExit("The response status does not match HTTP. Do not trust this answer.")

    signed = answer.get("sig")
    if signed:
        expected = mac(secret.encode(), "\n".join(["v1resp", nonce, str(answer["status"]), answer["payload"]]))
        if not hmac.compare_digest(expected, signed):
            raise SystemExit("The response signature is wrong. Do not trust this answer.")
    elif status < 400:
        raise SystemExit("The successful response is unsigned. Do not trust this answer.")
    else:
        print("(unsigned error - the request was not authenticated)", file=sys.stderr)
    return status, answer


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    url, key_id, secret = (os.environ.get(n) for n in ("LDB_URL", "LDB_KEY_ID", "LDB_SECRET"))
    if not (url and key_id and secret):
        print("Set LDB_URL, LDB_KEY_ID and LDB_SECRET.", file=sys.stderr)
        return 2
    payload = sys.argv[2]
    json.loads(payload)  # fail early on malformed JSON
    status, answer = call(url, key_id, secret, sys.argv[1], payload)
    print(json.dumps(json.loads(answer["payload"]), indent=2))
    return 0 if status == 200 else 1


if __name__ == "__main__":
    raise SystemExit(main())
