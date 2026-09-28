# Bootstrap-dev Guide

Get from a fresh clone to a working `access_token` you can call admin endpoints with.

---

## Prerequisites (one-time per machine)

| Tool | Verify | Install if missing |
|---|---|---|
| .NET 10 SDK | `dotnet --version` starts with `10.0.` | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| Docker + Compose v2 | `docker --version && docker compose version` | Docker Desktop |
| Ports free | `5432`, `4566`, `8082`, `5080` | — |

---

## Step 1 — Bring up the local infra

```bash
docker compose -f docker/docker-compose.yml up -d
```

Wait until `sbqr.postgres` is **healthy**:

```bash
docker compose -f docker/docker-compose.yml ps
# expect: sbqr.postgres ... State: running, Health: healthy
```

This may take 30-60 seconds on first run (Postgres initialises the data volume).

---

## Step 2 — Local configuration: `.env` (or seed user-secrets)

Preferred: copy the repo-root template and fill in your personal AWS
credentials (everything else ships with working dev defaults):

```bash
cp .env.example .env
# edit .env: Storage__AccessKeyId / Storage__SecretAccessKey
```

`.env` is loaded by SBQR.Api in Development only and layered last — it
wins over user-secrets, env vars and `appsettings.json` (see README
"Where each secret lives").

Alternative: seed the per-user secrets store instead:

```bash
./scripts/dev-seed-user-secrets.sh       # bash
.\scripts\dev-seed-user-secrets.ps1      # PowerShell
```

That script populates the same keys the `.env` template carries
(connection strings, JWT key, trust-store URL + cadence, `Crypto:VaultProvider`,
S3 bucket coordinates; personal AWS credentials only via the one-time
`appsettings.Local.json` migration). Verify:

```bash
dotnet user-secrets list --project src/Host/SBQR.Api/SBQR.Api.csproj
```

---

## Step 3 — Generate the platform bootstrap credential

This produces the **client_id**, a **plaintext client_secret** (shown once), and its **Argon2id PHC hash**.

```bash
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret
```

Output looks like:

```
SBQR platform bootstrap credential
----------------------------------

  client_id      : platform-bootstrap   (Auth:Bootstrap:ClientId default)
  client_secret  : 7fK3pV9qW2xR8yT4nMbC1xD5zL0aQ8sJ   ← COPY TO PASSWORD MANAGER NOW

  STORE THE SECRET IN THE OPERATOR VAULT NOW — it is never shown again.

  Auth:Bootstrap:ClientSecretHash (put this in Key Vault / env var
  Auth__Bootstrap__ClientSecretHash): $argon2id$v=19$m=65536,t=3,p=4$...$...   ← COPY THIS
```

Do these two things **right now**:

1. **Paste the `client_secret` into your password manager** (1Password / KeePass / Bitwarden). Record it as `SBQR local-dev / platform-bootstrap`. It will never be shown again.
2. **Copy the `Auth:Bootstrap:ClientSecretHash = …` value** — the entire `$argon2id$v=19$m=…$…$…` string, including the `$` prefix.

---

## Step 4 — Persist the PHC hash into user-secrets

### Bash / Git Bash / WSL

```bash
PHC='$argon2id$v=19$m=65536,t=3,p=4$REPLACE_WITH_YOUR_PHC$REPLACE_WITH_REST'
dotnet user-secrets set "Auth:Bootstrap:ClientSecretHash" "$PHC" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
```

> Single-quote the PHC — it contains `$` which bash would otherwise expand.

### PowerShell

```powershell
$phc = '$argon2id$v=19$m=65536,t=3,p=4$REPLACE_WITH_YOUR_PHC$REPLACE_WITH_REST'
dotnet user-secrets set "Auth:Bootstrap:ClientSecretHash" $phc `
  --project src/Host/SBQR.Api/SBQR.Api.csproj
```

Verify (should now show 5 entries):

```bash
dotnet user-secrets list --project src/Host/SBQR.Api/SBQR.Api.csproj
```

---

## Step 5 — Apply the database migrations

```bash
for f in db/migrations/*.sql; do
  echo "applying $f"
  docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
    psql -U postgres -d sbqr_app -v ON_ERROR_STOP=1 -f - < "$f" \
    || { echo "FAILED: $f"; break; }
done
```

Verify with Adminer at <http://localhost:8081> (server: `db`, user: `postgres`, password: `postgres`, database: `sbqr_app`).

---

## Step 6 — Start the API

```bash
dotnet run --project src/Host/SBQR.Api --urls http://localhost:5080
```

Wait for the line `Now listening on: http://localhost:5080`.

Leave this terminal running. Open a **second terminal** for the next steps.

---

## Step 7 — Exchange the client-credentials token

Set the plaintext secret from Step 3 in your environment, then call `/v1/oauth/token`:

### Bash

```bash
export CLIENT_SECRET='7fK3pV9qW2xR8yT4nMbC1xD5zL0aQ8sJ'

curl -s -X POST http://localhost:5080/v1/oauth/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret="$CLIENT_SECRET"
```

### PowerShell

```powershell
$env:CLIENT_SECRET = '7fK3pV9qW2xR8yT4nMbC1xD5zL0aQ8sJ'

$resp = Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5080/v1/oauth/token" `
  -ContentType "application/x-www-form-urlencoded" `
  -Body @{
    grant_type    = "client_credentials"
    client_id     = "platform-bootstrap"
    client_secret = $env:CLIENT_SECRET
  }

$resp.access_token   # eyJhbGciOi...
$resp.expires_in     # 600
```

### Expected response (both shells)

```json
{
  "access_token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "token_type": "Bearer",
  "expires_in": 600
}
```

The token has **`scope: admin`**, `sub: platform:bootstrap-admin`, **10-minute TTL**, no tenant binding.

---

## Step 8 — Use the token against an admin endpoint

```bash
TOKEN='eyJhbGciOi...'   # paste from Step 7

# Smoke test — should NOT be 500. 401 means missing token, 200/201 means auth works.
curl -i http://localhost:5080/v1/admin/tenants -H "Authorization: Bearer $TOKEN"

# Register a tenant (institute)
curl -X POST http://localhost:5080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"code":"DEMO","name":"Demo Institute","country":"DE"}'
```

If you get a non-`5xx` response, you're authenticated. The list will be empty until you register a tenant.

---

## Step 9 — (Optional) Import into Postman

The repo ships with `.postman/BQR-Dev.postman_environment.json`:

1. In Postman → **Environments** → **Import** → pick that file.
2. Fill in `clientId` = `platform-bootstrap`, `clientSecret` = the plaintext from Step 3, `baseUrl` = `http://localhost:5080`.
3. Import `.postman/BQR-public.postman_collection.json` and run `POST /v1/oauth/token` — the test script will auto-populate `accessToken` from the response.

---

## Verification checklist

- [ ] `docker compose -f docker/docker-compose.yml ps` → `sbqr.postgres` **healthy**
- [ ] `.env` exists at the repo root (or `dotnet user-secrets list ...` shows the seeded keys)
- [ ] Adminer at <http://localhost:8081> shows tables in `sbqr_app`
- [ ] `curl -fsS http://localhost:5080/health` → **200**
- [ ] `/v1/oauth/token` returns `access_token`
- [ ] `/v1/admin/tenants` with the Bearer token → not `500`

---

## Common pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| `/v1/oauth/token` always returns `401 invalid_client` | `Auth:Bootstrap:ClientSecretHash` not set, or wrong PHC | Re-run Step 3 + Step 4 carefully |
| `/v1/oauth/token` returns `500` | Migrations not applied | Run Step 5 |
| `dotnet user-secrets set …` silently drops characters | Forgot to single-quote the PHC; bash ate the `$` | Single-quote it, re-run |
| Token exchange shows `expires_in: 0` | API didn't start cleanly | Check the API terminal for stack trace |
| Port `5080` already in use | Another process bound it | `lsof -ti:5080 \| xargs kill` (bash) or `Get-NetTCPConnection -LocalPort 5080` (PS) |
| Shell history leaked the plaintext secret | Used the PHC line as `HASH=` then re-typed `client_secret=…` plainly | Prefix the line with a space on bash, or `Clear-History` on PS, then re-save the secret in your vault |

---

## Reset / re-bootstrap

To start over from scratch:

```bash
# 1. Stop the API (Ctrl+C in its terminal)

# 2. Tear down infra + volumes
docker compose -f docker/docker-compose.yml down -v --remove-orphans

# 3. Clear user-secrets
dotnet user-secrets clear --project src/Host/SBQR.Api/SBQR.Api.csproj

# 4. Go back to Step 1
```

---

## Why this is dev-only

- `dotnet user-secrets` lives in `~/.microsoft/usersecrets/` (per-user, gitignored).
- The plaintext `client_secret` lives only in your password manager.
- The Argon2id PHC is the *only* form the API ever sees — even if the user-secrets JSON leaks, the secret can't be reversed.
- For staging / production, secrets come from environment variables / Azure Key Vault (see `README.md` §7 and `AGENTS.md` non-negotiable constraints C3 + C9).

---

## Quick recap (the entire flow, condensed)

```bash
# Bring up infra
docker compose -f docker/docker-compose.yml up -d
sleep 30    # give Postgres time to become healthy

# Seed non-secret keys
./scripts/dev-seed-user-secrets.sh

# Generate bootstrap credential (SAVE THE client_secret!)
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret

# → copy the PHC value, then:
PHC='$argon2id$...'
dotnet user-secrets set "Auth:Bootstrap:ClientSecretHash" "$PHC" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj

# Apply migrations
for f in db/migrations/*.sql; do
  docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
    psql -U postgres -d sbqr_app -v ON_ERROR_STOP=1 -f - < "$f"
done

# Start API (in a separate terminal)
dotnet run --project src/Host/SBQR.Api --urls http://localhost:5080

# Get a token
export CLIENT_SECRET='<paste the plaintext client_secret from earlier>'
curl -s -X POST http://localhost:5080/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret="$CLIENT_SECRET"
# → access_token in the JSON response
```

That's it — paste the `access_token` into `Authorization: Bearer …` and you can hit every admin endpoint.
