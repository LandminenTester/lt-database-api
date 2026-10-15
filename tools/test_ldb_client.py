"""Protocol regressions for the dependency-free reference client."""
import json
import unittest
from unittest.mock import patch, MagicMock

import ldb_client


class ResponseVerificationTests(unittest.TestCase):
    def call(self, body, status=200):
        response = MagicMock()
        response.status = status
        response.read.return_value = json.dumps(body).encode()
        response.__enter__.return_value = response
        with patch("urllib.request.urlopen", return_value=response), patch("secrets.token_bytes", return_value=b"a" * 16):
            return ldb_client.call("https://example.com", "main", "secret", "health", "{}")

    def test_unsigned_success_is_rejected(self):
        with self.assertRaisesRegex(SystemExit, "unsigned"):
            self.call({"status": 200, "payload": "{}"})

    def test_http_status_must_match_the_signed_envelope(self):
        with self.assertRaisesRegex(SystemExit, "status"):
            self.call({"status": 201, "payload": "{}", "sig": "irrelevant"})

    def test_forged_signature_is_rejected(self):
        with self.assertRaisesRegex(SystemExit, "signature"):
            self.call({"status": 200, "payload": "{}", "sig": "forged"})

    def test_correctly_signed_response_is_accepted(self):
        nonce = ldb_client.b64url(b"a" * 16)
        sig = ldb_client.mac(b"secret", "\n".join(["v1resp", nonce, "200", "{}"]))
        self.assertEqual(200, self.call({"status": 200, "payload": "{}", "sig": sig})[0])


if __name__ == "__main__":
    unittest.main()
