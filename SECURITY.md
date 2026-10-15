# Security policy

## Reporting a vulnerability

Please do **not** open a public issue for security problems. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository. Include the version, the configuration involved (without secrets)
and steps to reproduce. You will get an answer within a few days; fixes are released as patch versions and credited in the
release notes unless you prefer otherwise.

## Supported versions

Only the latest release receives security fixes while the project is below 1.0.

## Scope

In scope: authentication and authorization bypass, replay, injection into SQL or MongoDB queries, schema-sync escaping its
namespace, information disclosure in responses or logs, denial of service through a single request.

Out of scope: problems that require a leaked key secret or database credentials, a misconfigured reverse proxy, running with
`AllowInsecureHttp` on an untrusted network, and the security of the database server itself.

The design and its limits are described in [docs/SECURITY.md](docs/SECURITY.md).
