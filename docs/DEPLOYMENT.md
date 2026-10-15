# Deployment

## Prerequisites

* A PostgreSQL, MySQL/MariaDB or MongoDB server that the API host can reach (and nobody else).
* Two database accounts as described in [SECURITY.md](SECURITY.md#database-privileges); scripts in [`docker/grants`](../docker/grants).
* A TLS certificate, or a reverse proxy that provides one. Plain HTTP is only for loopback and local testing.
* A random secret per caller: `openssl rand -base64 48`.

## Configuration reference

Settings come from `appsettings.json`, `appsettings.<Environment>.json` and environment variables named
`LDB_Ldb__<Section>__<Name>` (for lists: `…__Keys__0__KeyId`). The environment wins.

| Setting | Default | Meaning |
|---|---|---|
| `Database:Provider` | `postgres` | `postgres`, `mysql` (also MariaDB) or `mongodb` |
| `Database:DmlConnectionString` | – | Connection string / URL for data access (required) |
| `Database:DdlConnectionString` | data connection | Used only by schema sync |
| `Database:MongoDatabase` | `ldb` | MongoDB database name |
| `Database:CommandTimeoutSeconds` | `15` | Per statement; operations are cancelled after four times this |
| `Security:AllowInsecureHttp` | `false` | Accept plain HTTP from non-loopback addresses (local testing only) |
| `Security:TimestampToleranceSeconds` | `30` | Allowed clock difference |
| `Security:MaxBodyBytes` | `1048576` | Request size limit |
| `Security:FailedAuthPerMinute` | `10` | Failed attempts per address and minute before lock-out |
| `Security:TrustedProxies` | `[]` | Proxy addresses whose `X-Forwarded-*` headers are trusted |
| `Security:Keys[n]:KeyId` | – | Letters, digits, `-`, `_`, `.` (max 64) |
| `Security:Keys[n]:Current` / `Next` | – | Secret references `env:NAME` or `file:/path` (≥ 32 bytes) |
| `Security:Keys[n]:AllowedNamespaces` | – | Required. `["*"]` allows all |
| `Security:Keys[n]:AllowedOperations` | all | e.g. `["find","count","insert"]` |
| `Security:Keys[n]:AllowedCidrs` | any | e.g. `["203.0.113.10/32","10.0.0.0/8"]` |
| `Security:Keys[n]:RequestsPerMinute` | `600` | Per key |
| `Security:Keys[n]:AllowUnfilteredWrites` | `false` | Permit `all: true` on update and delete |
| `Schema:DestructiveMode` | `Apply` | `Apply`, `Quarantine`, `Deny` |
| `Schema:MaxTables` | `64` | Tables per namespace |
| `Limits:MaxRows` | `1000` | Rows per `find` |
| `Limits:MaxInsertRows` | `1000` | Rows per `insert` |

Connection string examples:

```
postgres: Host=db.internal;Port=5432;Database=ldb;Username=ldb_data;Password=…;SSL Mode=Require
mysql:    Server=db.internal;Port=3306;Database=ldb;User ID=ldb_data;Password=…;SslMode=Required
mongodb:  mongodb://ldb_data:…@db.internal:27017/ldb?authSource=ldb&tls=true
```

Because connection strings contain passwords, prefer supplying them through the environment or an `appsettings.Production.json`
with restricted permissions rather than committing them anywhere.

## Docker

The image (`docker/Dockerfile`) is multi-stage, runs as a non-root user on a chiseled base image and has no shell.

```bash
cp .env.example .env && $EDITOR .env
docker compose --profile postgres up -d --build      # or mysql / mongodb
```

The compose file starts the chosen database next to the API for **local testing** and allows plain HTTP. For production run
the image against your own database and put TLS in front:

```bash
docker run -d --name ldb --restart unless-stopped --read-only --cap-drop ALL \
  -p 127.0.0.1:8080:8080 \
  -e LDB_Ldb__Database__Provider=postgres \
  -e LDB_Ldb__Database__DmlConnectionString='Host=…' \
  -e LDB_Ldb__Security__Keys__0__KeyId=main \
  -e LDB_Ldb__Security__Keys__0__Current=file:/run/secrets/ldb_main \
  -e LDB_Ldb__Security__Keys__0__AllowedNamespaces__0=mygame \
  -e LDB_Ldb__Security__TrustedProxies__0=172.17.0.1 \
  -v /etc/ldb/main.secret:/run/secrets/ldb_main:ro \
  ghcr.io/landminentester/lt-database-api:latest
```

## Reverse proxy with TLS

Terminate TLS in a proxy, forward to `127.0.0.1:8080`, and list the proxy under `Security:TrustedProxies` so the API sees the
caller's real address and `X-Forwarded-Proto: https`.

Caddy (automatic certificates):

```
db.example.com {
    request_body { max_size 2MB }
    reverse_proxy 127.0.0.1:8080
}
```

nginx:

```
server {
    listen 443 ssl http2;
    server_name db.example.com;
    ssl_certificate     /etc/ssl/db.example.com.pem;
    ssl_certificate_key /etc/ssl/db.example.com.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    client_max_body_size 2m;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Without a proxy, give Kestrel a certificate:

```json
{ "Kestrel": { "Endpoints": { "Https": { "Url": "https://*:8443", "Certificate": { "Path": "/etc/ldb/cert.pfx", "Password": "…" } } } } }
```

## Linux (systemd)

```bash
sudo useradd --system --home /opt/ldb --shell /usr/sbin/nologin ldb
sudo install -d -o root -g ldb -m 750 /opt/ldb /etc/ldb
sudo tar -xzf lt-database-api-linux-x64.tar.gz -C /opt/ldb
sudo install -m 640 -o root -g ldb ldb.env /etc/ldb/ldb.env      # LDB_… variables, one per line
```

`/etc/systemd/system/ldb.service`:

```ini
[Unit]
Description=lt-database-api
After=network-online.target

[Service]
Type=notify
User=ldb
WorkingDirectory=/opt/ldb
ExecStart=/opt/ldb/Ldb.Api
EnvironmentFile=/etc/ldb/ldb.env
Environment=ASPNETCORE_URLS=http://127.0.0.1:8080
Restart=on-failure
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
CapabilityBoundingSet=
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX

[Install]
WantedBy=multi-user.target
```

`sudo systemctl enable --now ldb`

## Windows (service)

```powershell
Expand-Archive lt-database-api-win-x64.zip -DestinationPath C:\ldb
# Settings: machine-wide environment variables, or C:\ldb\appsettings.Production.json readable only by the service account
New-Service -Name ldb -BinaryPathName 'C:\ldb\Ldb.Api.exe' -DisplayName 'lt-database-api' -StartupType Automatic
Start-Service ldb
```

Put the TLS certificate in Kestrel's configuration (above) or run a proxy such as IIS ARR, Caddy or nginx in front. Restrict
the file ACLs of the folder and secret files to the service account and administrators.

## Operations

* **Probes:** `GET /healthz` (process is up), `GET /readyz` (database reachable, returns 503 otherwise).
* **Logs:** JSON lines on stdout. Look for `audit` lines per request.
* **Backups:** back up the database. The API holds no state except caches. Structure is also recoverable from the caller's
  own declaration, but data is not.
* **Upgrades:** replace the binary or image and restart. Schema bookkeeping tables are forward compatible.
* **Rotating a secret:** see [SECURITY.md](SECURITY.md#key-management).
* **Several instances:** possible, but nonce and idempotency caches are per instance; use sticky routing per key.
