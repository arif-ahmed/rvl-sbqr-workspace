# Docker Run Playbook — Local first, then Development (Pre-UAT)

Same Release image everywhere (`dotnet publish -c Release`, `Dockerfile:115`).
Only injected config differs. Local and Development both run
`ASPNETCORE_ENVIRONMENT=Development` (`docs/environments.md`).
mTLS is skipped in both for now (`Mtls:Enabled=false` default).

## 0. Prerequisites

- Docker Desktop running: `docker info --format "{{.ServerVersion}}"`
- Repo root as cwd (has `compose.yml`, `Dockerfile`, `.env`)
- `.env` present: `Copy-Item .env.example .env` on first use
- Hosts entry (so one connection string serves `dotnet run` and containers):
  `127.0.0.1 sbqr.postgres sbqr.rabbitmq`
- Ports free: 8080 (api), 5432 (postgres)

## 1. Build (Release, once)

```powershell
docker build -t sbqr-api:local .
```

For the EC2 push add the arch pin (t3.medium is amd64):

```powershell
docker build --platform linux/amd64 -t <ecr-uri>:<sha> .
docker push <ecr-uri>:<sha>
```

## 2. Database ready (before first API run)

```powershell
pwsh ./scripts/dev-stack-up.ps1
```

This starts `sbqr.postgres` + `sbqr.rabbitmq`, creates `sbqr_app`,
applies `db/migrations/*.sql`. Full loop: `docs/local-dev-stack.md`.

## 3. Local `docker run` (Development mode, ephemeral JWT)

```powershell
docker run --rm -p 8080:8080 --network sbqr-net `
  -e ASPNETCORE_ENVIRONMENT=Development `
  -e ConnectionStrings__sbqr_app="Host=sbqr.postgres;Port=5432;Database=sbqr_app;Username=postgres;Password=postgres;Include Error Detail=true" `
  -e KeyCustody__Vault=Local `
  -e Messaging__Provider=InProcess `
  sbqr-api:local
```

Notes:
- `--network sbqr-net` + `Host=sbqr.postgres` uses compose DNS (`compose.yml:207-210`).
  Host-side postgres instead → `Host=host.docker.internal`.
- No nginx on Windows. Kestrel serves `:8080` directly.
- `.env` inside the container is inert (excluded from image); `-e` is the channel.

## 4. Verify

```powershell
curl http://localhost:8080/health/live    # expect {"status":"live"}, no DB needed
curl http://localhost:8080/health/ready   # expect {"status":"ready"}, needs schema
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

`live` failing = container/image problem. `ready` 503 = DB/schema problem (§6).

## 5. Env vars — where they live

| Runtime | Channel |
|---|---|
| Local compose | repo-root `.env` → compose `${VAR}` substitution (`compose.yml:109-165`) |
| Local `docker run` | `-e KEY=VALUE` flags (this playbook) |
| Local `dotnet run` | same `.env` (parsed by `Program.cs:76-98`) + `dotnet user-secrets` |
| Development EC2 | host-injected only: systemd unit `sbqr-api` reads `/etc/sbqr/sbqr.env` (`chmod 600`), or `docker run --env-file /etc/sbqr/sbqr.env`. No `.env` file on the box |

## 6. Same image on Development EC2 (Pre-UAT)

```powershell
docker run -d --name sbqr-api --restart unless-stopped -p 127.0.0.1:8080:8080 `
  --env-file /etc/sbqr/sbqr.env `
  <ecr-uri>:<sha>
```

`/etc/sbqr/sbqr.env` content (test keys only, never prod keys):

```
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__sbqr_app=Host=<rds-endpoint>;Port=5432;Database=sbqr_app;Username=sbqr_admin;Password=<from SSM /sbqr/dev/db-master-password>;SslMode=Require
Jwt__SigningKey=<shared test key, 32+ chars — stable so logins survive restarts>
KeyCustody__Vault=S3
Storage__Region=ap-southeast-1
Storage__BucketName=rvl-sbqr-key-vault-dev
Storage__VaultFolder=keycustody
Messaging__Provider=InProcess
Bootstrap__Admin__Username=<admin>
Bootstrap__Admin__Password=<12+ chars>
Bootstrap__Admin__Email=<team inbox>
```

nginx (on the host, dev team installs): `proxy_pass http://127.0.0.1:8080`,
Let's Encrypt on 443, port 80 redirect only. Pre-check on the box:

```bash
curl http://127.0.0.1:8080/health/live
```

## 7. Logs — where to look

App writes stdout only (JSON by default, `appsettings.json:11-18`).

| Runtime | Command |
|---|---|
| Compose local | `docker compose logs -f sbqr.api` (capped 50m×10, `compose.yml:190-194`) |
| `docker run` | `docker logs -f sbqr-api` (or container id with `--rm`) |
| `dotnet run` | console (text via `SBQR.Api` launch profile, JSON otherwise) |
| Development EC2 | `docker logs` / `journalctl -u sbqr-api -f` → CloudWatch `/rvl-sbqr/dev/api`, `/rvl-sbqr/dev/nginx` |

## 8. Teardown

```powershell
docker stop sbqr-api; docker rm sbqr-api   # named EC2-style container (skip with --rm)
docker compose down                         # stop stack, keep pgdata/rabbitdata
pwsh ./scripts/dev-stack-down.ps1 -Mode Stop    # same, via script (default)
pwsh ./scripts/dev-stack-down.ps1 -Mode Reset   # + drop volumes (data gone)
```

`--rm` runs need no cleanup. Never use `Reset` on a box holding data you need.
Full tiers: `docs/local-dev-stack.md` §4.

## 9. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `/health/live` refuses | container not on 8080 / wrong network | `docker logs`, confirm `-p 8080:8080 --network sbqr-net` |
| `/health/ready` 503 | schema not applied / wrong host | rerun §2; confirm `Host=sbqr.postgres` in-network, `host.docker.internal` off-network |
| `database "sbqr_app" does not exist` | compose never auto-creates it | `pwsh ./scripts/dev-stack-up.ps1` (creates + migrates) |
| `port is already allocated` | AppHost or second postgres on 5432/8080 | `docker ps`, stop the other stack |
| Login sessions die on restart (local) | ephemeral JWT (no key set) | set `Jwt__SigningKey` in `.env` |
| Container crash-loops on EC2 | missing/invalid env in `sbqr.env` | `docker logs sbqr-api`; compare against §6 key list |
