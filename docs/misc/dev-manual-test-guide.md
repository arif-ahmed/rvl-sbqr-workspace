# Developer Manual Test Guide — OAuth · Tenants · Tenant Configurations · Crypto Keys · QR Generation · QR Validation

> **⚠ SCHEMA CHANGE 2026-09-08 — some SQL in this guide is stale.**
> The canonical schema is now [`db/migrations/`](../db/migrations/README.md)
> (one PostgreSQL schema per module — see
> [`docs/design/database-design.md`](design/database-design.md)). When
> copying SQL from this guide, apply these deltas:
> `qr_transactions` no longer exists (one `public.qr_validations` row
> per request, keyed by `(tenant_id, request_id)`); `payload_hash` is no
> longer a persisted column of `qr_generations` (it lives only in API
> responses and `audit_logs.resource_id`); tables are schema-qualified
> (`public.tenants`, `public.tenant_configurations`,
> `public.qr_generations`, `public.qr_validations`,
> `institution_trust.*`, `public.crypto_keys`, `public.audit_logs`);
> the dated `2026090*` migration filenames referenced below were never
> committed — use the `001`–`008` scripts in `db/migrations/`; and
> `POST /v1/qr/validate` now requires `requestId` + `requestTimestamp`
> (C6 replay guard).

This guide walks a developer through **manually testing the six sub-domain
businesses** of the identity/onboarding + QR-generation + QR-verification
side of SBQR:

| # | Sub-domain | Owning module | HTTP surface |
|---|---|---|---|
| 1 | OAuth (token issuance) | `IdentityAccess` | `POST /v1/oauth/token` |
| 2 | Tenants | `Tenancy` | `/v1/admin/tenants` route tree |
| 3 | Tenant configurations (client credentials) | `IdentityAccess` (row) + `Tenancy` (endpoint) | `POST /v1/admin/tenants/{id}/tenant-configuration` |
| 4 | Crypto keys (signing keys) | `KeyCustody` | `/v1/crypto-keys` route tree |
| 5 | QR generation | `QrGeneration` | `POST /v1/qr/generate/static`, `POST /v1/qr/generate/dynamic` |
| 6 | QR validation (round-trip) | `Verification` | `POST /v1/qr/validate` |

It covers: the **DB scripts to run from scratch** (as if no previous table
exists), **sample payloads** for every happy path, the **expected responses**,
**failure-scenario payloads and status codes**, and the **SQL to verify
state** after each step.

> **Sub-domain 5 (QR generation) has a known blocker as of this branch: it
> cannot be exercised via real HTTP calls yet** — see [§9](#9-sub-domain-5--qr-generation)
> for why, and use `dotnet test tests/SBQR.Qr.IntegrationTests` to actually
> exercise that pipeline today.

> Shell examples use Git Bash (POSIX) on Windows. PowerShell users: the `curl`
> examples map 1:1 to `curl.exe` (Windows ships it) or `Invoke-RestMethod`.

---

## Table of contents

1. [Mental model & suggested test order](#1-mental-model--suggested-test-order)
2. [Prerequisites](#2-prerequisites)
3. [Fresh database setup — the scripts to run](#3-fresh-database-setup--the-scripts-to-run)
4. [Start the API & smoke checks](#4-start-the-api--smoke-checks)
   - 4.1 [Running with the S3 vault provider (LocalStack)](#41-running-with-the-s3-vault-provider-localstack)
5. [Sub-domain 1 — OAuth](#5-sub-domain-1--oauth)
6. [Sub-domain 2 — Tenants](#6-sub-domain-2--tenants)
7. [Sub-domain 3 — Tenant configurations](#7-sub-domain-3--tenant-configurations)
8. [Sub-domain 4 — Crypto keys](#8-sub-domain-4--crypto-keys)
9. [Sub-domain 5 — QR generation](#9-sub-domain-5--qr-generation)
10. [Sub-domain 6 — QR validation](#10-sub-domain-6--qr-validation)
11. [Worked example — ACME Bank (institution_code `031008`)](#11-worked-example--acme-bank-institution_code-031008)
12. [End-to-end golden journey](#12-end-to-end-golden-journey)
13. [Audit-trail expectations](#13-audit-trail-expectations)
14. [Reset / cleanup](#14-reset--cleanup)
15. [Troubleshooting](#15-troubleshooting)

---

## 1. Mental model & suggested test order

The four sub-domains are **sequentially dependent** — test them in this order:

```
                    ┌────────────────────────────────────────────────┐
                    │  platform-bootstrap client (config-held)       │
                    │  client_id / client_secret live in appsettings │
                    └───────────────┬────────────────────────────────┘
                                    │ POST /v1/oauth/token
                                    ▼
     (1) OAuth ──────► admin-scoped JWT (scope=admin)
                                    │
                                    ▼
     (2) Tenants ────► POST /v1/admin/tenants            (row in public.tenants)
                    │ POST /v1/admin/tenants/{id}/activate
                                    │
                                    ▼
     (3) Tenant ─────► POST /v1/admin/tenants/{id}/tenant-configuration
         configurations   (row in public.tenant_configurations;
          (credentials)    clientSecret shown EXACTLY ONCE)
                                    │
                                    ▼
     (1) OAuth ──────► tenant-scoped JWT (scope=qr:generate,qr:validate
        (2nd leg)        + tenant_id claim) — same /v1/oauth/token
                                    │
                                    ▼
     (4) Crypto keys ► POST /v1/crypto-keys (Generate | Adopt), PUT rotate
                        (row in public.crypto_keys; private half sealed
                         into the local key vault on disk)
                                    │
                                    ▼
     (5) QR generation ► POST /v1/qr/generate/static or /qr/generate/dynamic
         [BLOCKED — §9]   (needs the tenant token + an ACTIVE key) → row in public.qr_generations
                          (payload_hash only; raw QR returned once)
```

Cross-domain rule worth knowing while testing: **suspending a tenant
cascades** — it suspends the tenant's configuration row (making
`/v1/oauth/token` return `401 invalid_client`) **and** suspends its ACTIVE
signing key. Reactivating cascades both back.

### Endpoint quick reference

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /v1/oauth/token` | anonymous (the secret IS the auth) | Exchange client credentials for a JWT |
| `POST /v1/admin/tenants` | `scope=admin` | Register tenant (starts `Pending`) |
| `GET /v1/admin/tenants` | `scope=admin` | Paged tenant list |
| `GET /v1/admin/tenants/{id}` | `scope=admin` | Tenant by id |
| `POST /v1/admin/tenants/{id}/activate` | `scope=admin` | `Pending → Active` |
| `POST /v1/admin/tenants/{id}/suspend` | `scope=admin` | `Active/Pending → Suspended` (+ cascades) |
| `POST /v1/admin/tenants/{id}/reactivate` | `scope=admin` | `Suspended → Active` (+ cascades back) |
| `POST /v1/admin/tenants/{id}/terminate` | `scope=admin` | `→ Terminated` (one-way, + cascades) |
| `POST /v1/admin/tenants/{id}/tenant-configuration` | `scope=admin` | Provision one-time client configuration |
| `POST /v1/crypto-keys` | `scope=admin` or `key-admin` | Mint first signing key (`Generate`/`Adopt`) |
| `GET /v1/crypto-keys/{tenantId}/active` | `scope=admin` or `key-admin` | Active key for tenant |
| `PUT /v1/crypto-keys/{tenantId}` | `scope=admin` or `key-admin` | Rotate (retire old, mint new) |
| `GET /v1/crypto-keys/{tenantId}` | `scope=admin` or `key-admin` | Key history for tenant (paged) |
| `GET /v1/crypto-keys` | `scope=admin` or `key-admin` | Keys across all tenants (paged) |
| `POST /v1/qr/generate/static` | `scope=qr:generate` (tenant token) | Build + sign one **static** QR payload (Tag 01 = "11") — **blocked, see §9** |
| `POST /v1/qr/generate/dynamic` | `scope=qr:generate` (tenant token) | Build + sign one **dynamic** QR payload (Tag 01 = "12") — **blocked, see §9** |
| `POST /v1/qr/validate` | `scope=qr:validate` (tenant token) | Verify one raw QR payload; returns a `verdict` plus decoded identity (see §10) |

> **URL versioning is mandatory**: every route above lives under `/v1/`.
> A request without the segment (`POST /admin/tenants`) or with an undeclared
> version (`/v2/...`) returns a plain **404**. Successful responses carry an
> `api-supported-versions: 1.0` header.

---

## 2. Prerequisites

| Tool | Version | Why |
|---|---|---|
| .NET SDK | 10.0 (per `global.json`) | build / run |
| Docker Desktop | any recent | PostgreSQL (`sbqr.postgres`) |
| `curl` + `jq` | any | manual API poking |
| `openssl` | any (Git Bash ships it) | Ed25519 PEMs for the `Adopt` key mode |
| Git Bash or PowerShell 7 | any | examples below use Git Bash |

```bash
dotnet --version      # matches global.json rollForward
docker --version
```

Base URL used throughout this guide:

| How you run the API | Base URL |
|---|---|
| `dotnet run --project src/Host/SBQR.Api` (Development — default) | `http://localhost:5001` |
| `docker compose -f docker/docker-compose.yml up` (`sbqr.api`) | `http://localhost:8080` |

The examples use `BASE=http://localhost:5001`; export it once and everything
copies verbatim:

```bash
BASE=http://localhost:5001
PSQL="docker compose -f docker/docker-compose.yml exec -T sbqr.postgres psql -U postgres -d sbqr_app"
```

---

## 3. Fresh database setup — the scripts to run

Goal: a database **as if no previous table existed**. There are two layers of
scripts, both already in the repo — nothing to author:

1. **`docker/postgres/init/00-create-databases.sql`** — runs automatically the
   first time the Postgres container starts on a **fresh volume**; creates the
   two logical databases `sbqr_app` and `sbqr_key_vault`.
2. **`db/migrations/*.sql`** — the canonical schema, applied by the external
   migration tool (see `docs/docker/DEPLOYMENT.md` §4). The `SBQR.Api` host
   does **not** apply migrations itself — there is no `--migrate` flag, no
   auto-migrate-on-startup option. Apply the schema with the external tool
   before the API can serve any endpoint that touches the DB.

### 3.1 Wipe any previous state and start Postgres

```bash
# from the repo root

# (a) nuke everything, including the data volume → "as if no previous table exists"
docker compose -f docker/docker-compose.yml down -v

# (b) start ONLY postgres (creates sbqr_app + sbqr_key_vault on the fresh volume)
pwsh docker/start-db.ps1          # Windows one-liner (waits for healthy)
# — or —
docker compose -f docker/docker-compose.yml up -d sbqr.postgres
```

`start-db.ps1` finishes with `sbqr.postgres: healthy` and prints the
connection info (`localhost:5432`, `postgres`/`postgres`).

### 3.2 Apply the migrations (the actual DB scripts)

Run the external migration tool against the running Postgres. The tool's
exact CLI/arguments live in its own docs; the typical invocation is
something like:

```bash
# example — actual command depends on the chosen migration tool
sbqr-migrate \
    --connection-string "Host=localhost;Port=5432;Database=sbqr_app;Username=postgres;Password=postgres" \
    --migrations-dir db/migrations
sbqr-migrate \
    --connection-string "Host=localhost;Port=5432;Database=sbqr_key_vault;Username=postgres;Password=postgres" \
    --migrations-dir db/migrations
```

The tool applies every `db/migrations/*.sql` in timestamp order against the
target database and records what ran in its own bookkeeping table (consult
the tool's docs for the table name and column shape). On a fresh database
you should see **14 scripts** apply cleanly across the two databases.

### 3.3 Which migrations land which sub-domain's tables

All 14 run as one forward-only chain, including tables owned by other
modules. The ones this guide cares about:

| Sub-domain | Table(s) | Migration(s) |
|---|---|---|
| Tenants | `public.tenants` | `20260903120000_create_tenants_table.sql`, `20260910120000_drop_tenant_code_require_institution_code.sql` |
| Tenant configurations | `public.tenant_configurations` | `20260911120000_replace_api_credentials_with_tenant_configurations.sql` (supersedes the four legacy `api_credentials` files, which stay for history) |
| Crypto keys | `public.crypto_keys` | `20260903120200_create_crypto_keys_table.sql`, `20260903120500_add_public_key_sha256_to_crypto_keys.sql` |
| OAuth (evidence trail) | `public.audit_logs` | `20260904120000_create_audit_logs_table.sql` |
| (other modules, come along for the ride) | `institution_registries`, `institution_keys`, `qr_generations`, `qr_transactions`, `qr_validations` | the remaining migrations |

### 3.4 Verify the schema landed

```bash
$PSQL -c "SELECT table_schema, table_name FROM information_schema.tables
           WHERE table_schema IN ('public','identity')
           ORDER BY 1,2;"
```

Expected (minimum, for this guide):

```
identity | tenant_configurations
public   | audit_logs
public   | crypto_keys
public   | institution_keys
public   | institution_registries
public   | qr_generations
public   | qr_transactions
public   | qr_validations
public   | tenants
```

Confirm the migration tool's bookkeeping table also reports 14 applied
migrations (table name and column shape depend on the tool — consult its
own docs).

### 3.5 Quick re-reset without wiping the volume

Between test cycles you can reset to "no tables" state surgically (drops
the migration tool's bookkeeping too, so the next migrate run re-applies
the whole chain):

```bash
$PSQL -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;
          DROP SCHEMA IF EXISTS identity CASCADE;"
# then re-run the external migration tool from §3.2
```

Optional hygiene: the key vault on disk (see §8.6) survives a DB reset.
Old sealed blobs are harmless (new keys get new handles), but for a truly
clean slate delete `%LOCALAPPDATA%\sbqr\key-vault` (Windows) or
`/var/lib/sbqr/key-vault` (Linux).

---

## 4. Start the API & smoke checks

The default `dotnet run` launch uses the **file-based** vault provider — both
the KeyCustody signing-key store (`Crypto:VaultProvider=Local`) and the
signing pipeline (`KeyCustody:ActiveProvider=PlainFile`) write and read from
disk under `%LOCALAPPDATA%\sbqr\key-vault`. That is the right choice for
running tests against the host with zero external dependencies. **If you want
the S3-backed vault** (LocalStack with operator-readable object keys like
`keys/<institutionCode>_v<n>_private.pem`, AES-256-GCM sealed with
`SBQRKEY1` magic header, SSE-S3 AES256 server-side), use the alternative
launch in [§4.1](#41-running-with-the-s3-vault-provider-localstack).

```bash
dotnet run --project src/Host/SBQR.Api
# Now listening on: http://localhost:5001   (Development via launchSettings.json)
```

Smoke checks (all anonymous):

```bash
curl -sS $BASE/health/live     # → {"status":"live"}
curl -sS $BASE/health/ready    # → {"status":"ready"}

# dev-only route manifest — ground truth for "is my endpoint actually mapped?"
curl -sS $BASE/admin/_routes | jq '.routes[] | select(.pattern | test("oauth|tenants|crypto-keys"))'

# OpenAPI (public document; the token endpoint is documented here)
#   $BASE/openapi/v1.public.json   + Scalar UI at $BASE/docs/public
```

> **Expected log noise (harmless for this guide):** Development points
> `TrustStore:BaseUrl` at `http://localhost:5002`. Without the BB trust-store
> mock running you'll see periodic `Trust store fetch failed` warnings and
> `institution.trust.sync.completed` audit rows carrying an `error` — that's
> the InstitutionTrust module, not one of our four sub-domains. Ignore it.

### 4.1 Running with the S3 vault provider (LocalStack)

The KeyCustody vault has two providers selected at startup by configuration:

| Config key | Default (file vault) | S3 variant |
|---|---|---|
| `Crypto:VaultProvider` | `Local` (Phase-1 AES-256-GCM file blobs under `KeyCustody:KeyStoreDirectory`) | `S3` (LocalStack; reads `Storage:*` for endpoint/credentials/bucket) |
| `KeyCustody:ActiveProvider` | `PlainFile` (dev-only; Production refuses it) | `PemVault` (resolves the tenant's ACTIVE `crypto_keys` row and signs via the configured vault) |

Switching the two to `S3` / `PemVault` makes `POST /v1/crypto-keys`
(`Generate`/`Adopt`) and the rotation `PUT` write **operator-readable S3
objects** at `keys/<institutionCode>_v<n>_private.pem` under the `sbqr-dev`
bucket, with `SBQRKEY1`-magic AES-256-GCM blobs sealed by `KeyCustody:VaultKek`
and SSE-S3 `AES256` applied by LocalStack. The DB row in `public.crypto_keys`
still carries the opaque `custody_key_reference` handle; the bucket objects
are the actual sealed material.

#### 4.1.1 Prerequisites

The same LocalStack container the [LocalStack S3 dev guide](dev-localstack-s3-guide.md)
sets up. Quick check before launching the host:

```bash
docker ps --filter "name=sbqr.localstack" --format "{{.Names}}: {{.Status}}"
# → sbqr.localstack: Up X hours (healthy)

curl -sS http://localhost:4566/_localstack/health | jq -r '.services.s3'
# → "running"

# bucket exists (created by sbqr.s3-init on first boot)
curl -sS "http://localhost:4566/sbqr-dev/?list-type=2" | head -1
# → <?xml version='1.0' encoding='utf-8'?><ListBucketResult ...><Name>sbqr-dev</Name>...
```

If LocalStack is not up yet, follow [§3 of the LocalStack guide](dev-localstack-s3-guide.md#3-one-time-setup)
to bring it up; the bucket is created automatically by the `sbqr.s3-init`
one-shot.

#### 4.1.2 Launch via the `S3-LocalStack` profile

A second launch profile lives next to the default in
`src/Host/SBQR.Api/Properties/launchSettings.json`:

```jsonc
// src/Host/SBQR.Api/Properties/launchSettings.json
{
  "profiles": {
    "SBQR.Api":        { /* default — file vault, plain-file signer */ },
    "S3-LocalStack":   { /* flips Crypto:VaultProvider + KeyCustody:ActiveProvider */ }
  }
}
```

The `S3-LocalStack` profile sets the two vault-related env vars
(`Crypto__VaultProvider`, `KeyCustody__ActiveProvider`) and points the
InstitutionTrust mock at the running `sbqr.bb-trust-store-mock` container.
Everything else stays on `appsettings.json` defaults plus the seeded
user-secrets (same DB, same Kestrel port 5001). The
`ConnectionStrings:sbqr_app` entry already in user-secrets is reused
as-is — no need to redeclare it here.

To launch:

```bash
# from the repo root
dotnet run --project src/Host/SBQR.Api --launch-profile S3-LocalStack
# → Now listening on: http://localhost:5001
```

The `--launch-profile` flag (or the short form `--profile`) is the standard
.NET way to pick one of multiple `launchSettings.json` profiles — same
mechanism your IDE uses when you switch the dropdown in the Run
configuration.

Wait a couple of seconds for Kestrel to bind, then verify:

```bash
curl -sS -o /dev/null -w "%{http_code}\n" $BASE/health/live     # → 200
curl -sS -o /dev/null -w "%{http_code}\n" $BASE/health/ready    # → 200
```

> **Profiles vs. shell env vars.** Profiles are the right place for "what
> env vars does this launch need?" — they live in source control, the IDE
> knows about them, and they can't be lost between terminal sessions. Set
> shell env vars only when you need a one-off override (e.g. flip a single
> key for a debug session). For the S3-vs-file decision, profile > shell.

#### 4.1.3 Adding the profile to your IDE

`launchSettings.json` is consumed natively by Visual Studio, Rider, and
VS Code (via the C# Dev Kit / C# extension). The `S3-LocalStack` profile
shows up by name in the Run/Debug dropdown alongside the default — pick
it once, then Run or Debug as usual. No IDE-specific configuration needed.

#### 4.1.4 Verify the S3 path after a §8.1 generate

After you mint a signing key (§8.1), the bucket should contain exactly **one
new object per (institution, version)** pair, with no flat mirror:

```bash
curl -sS "http://localhost:4566/sbqr-dev/?list-type=2&prefix=keys%2F" \
  | grep -oE '<Key>[^<]+</Key>'
# → <Key>keys/100101_v1_private.pem</Key>
```

The object's first eight bytes are the `SBQRKEY1` magic header and its SSE
header on the way back is `AES256`:

```bash
# Fetch the head of the encrypted blob (LocalStack returns the bytes inline)
curl -sS "http://localhost:4566/sbqr-dev/keys/100101_v1_private.pem" -o /tmp/100101_v1.pem
head -c 8 /tmp/100101_v1.pem ; echo        # → SBQRKEY1

# SSE header check via the metadata endpoint (s3api head-object equivalent)
curl -sS -I "http://localhost:4566/sbqr-dev/keys/100101_v1_private.pem" \
  | grep -i 'x-amz-server-side-encryption'
# → x-amz-server-side-encryption: AES256
```

DB row carries the same version encoded in the handle:

```bash
$PSQL -c "SELECT tenant_id, key_id, key_version,
                  left(custody_key_reference, 80) AS handle_prefix
           FROM crypto_keys WHERE tenant_id='$TENANT_A';"
# → tenant_id=... key_version=1 handle_prefix=tenant:...:institution:100101:sbqr-signing:v1
```

And rotation (`PUT /v1/crypto-keys/{tenantId}`) should produce a **second,
new** object and leave the first untouched (no overwrite):

```bash
# after §8.3
curl -sS "http://localhost:4566/sbqr-dev/?list-type=2&prefix=keys%2F100101%2F" \
  | grep -oE '<Key>[^<]+</Key>' | sort
# → keys/100101_v1_private.pem
# → keys/100101_v2_private.pem
```

#### 4.1.5 When the bucket stays empty

Symptom: `POST /v1/crypto-keys` returns `201 Created` and the DB row lands,
but `curl "http://localhost:4566/sbqr-dev/?list-type=2&prefix=keys%2F"`
shows zero keys under `keys/`. Two things to check:

1. **Did the host actually launch under the `S3-LocalStack` profile?** A
   `dotnet run` without `--launch-profile` picks the **first** profile in
   `launchSettings.json` (alphabetical), which is `S3-LocalStack` here — so
   a bare `dotnet run` will actually work. But if you launched
   `SBQR.Api.exe` directly (the prebuilt binary), env vars from
   `launchSettings.json` do **not** apply; the exe inherits only the
   ambient shell environment. Either re-launch via `dotnet run
   --launch-profile S3-LocalStack`, or set `Crypto__VaultProvider=S3` and
   `KeyCustody__ActiveProvider=PemVault` in the same shell before invoking
   the exe. Telltale: the file vault grew a new file at the same time
   (`%LOCALAPPDATA%\sbqr\key-vault`).
2. **Is the host's stderr clean?** A failing S3 PUT (network, SSE mismatch,
   bucket missing) throws `ObjectStorageException` from `S3ObjectStorage`
   and propagates back as a `500` — not a silent miss. Tail the host's
   console output (the IDE Debug panel, or the terminal that ran
   `dotnet run`).

---

## 5. Sub-domain 1 — OAuth

**Endpoint:** `POST /v1/oauth/token` — anonymous, rate-limited to
**10 requests/minute per IP** (429 with an empty body beyond that — pace
yourself when re-running this guide).

Accepts `application/x-www-form-urlencoded` (the RFC 6749 encoding; what
`curl -d` sends) **or** JSON. Exactly three parameters — there is **no
`scope` parameter**; scopes are decided server-side by which credential you
present.

### 5.1 Happy path A — the platform bootstrap (admin) token

The bootstrap client is "client zero": held in **configuration**, not the
database, so it survives DB resets. Dev credentials (public dev values from
`docker-compose.yml` defaults):

- `client_id=platform-bootstrap`
- `client_secret=dev-only-bootstrap-secret`

```bash
curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret=dev-only-bootstrap-secret | jq
```

**Expected — `200 OK`:**

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiJ9....",
  "tokenType": "Bearer",
  "expiresIn": 600
}
```

> **Field-casing note:** the wire format is **camelCase** (`accessToken`),
> because the response record carries no explicit JSON names and the host
> uses default ASP.NET Core serialization. Some older prose in
> `docs/api-docs-usage-guide.md` and code comments shows RFC-style
> `access_token`/`expires_in` — that's a known doc/code discrepancy; trust
> the live response above.

JSON variant (equivalent):

```bash
curl -sS -X POST $BASE/v1/oauth/token \
  -H 'Content-Type: application/json' \
  -d '{"grant_type":"client_credentials","client_id":"platform-bootstrap","client_secret":"dev-only-bootstrap-secret"}'
```

Capture it for every later section (tokens expire after **10 minutes** —
re-mint if your 401s start mid-session):

```bash
TOKEN=$(curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret=dev-only-bootstrap-secret | jq -r .accessToken)
```

### 5.2 Inspect the token (optional but recommended)

```bash
payload=$(echo "$TOKEN" | awk -F. '{print $2}')
case $((${#payload} % 4)) in 2) payload="$payload==";; 3) payload="$payload=";; esac
echo "$payload" | tr '_-' '/+' | base64 -d | jq
```

Bootstrap-token claims you should see:

| Claim | Value |
|---|---|
| `sub` | `platform:bootstrap-admin` |
| `scope` | `["admin"]` (JSON array) |
| `iss` / `aud` | `sbqr` / `sbqr-api` |
| `exp` − `iat` | 600 seconds |
| `tenant_id` | **absent** (platform-level principal) |

### 5.3 Happy path B — tenant credential token

Deferred to [§7.4](#74-happy-path--tenant-leg-of-oauth): it needs a tenant +
a provisioned credential first. The short version: same endpoint, the
`clientId`/`clientSecret` pair from §7, yielding `scope =
["qr:generate","qr:validate"]` plus a `tenant_id` claim.

### 5.4 DB / audit evidence for a successful mint

Every successful token issuance (both legs) writes an `auth.token.issued`
row:

```bash
$PSQL -c "SELECT event_type, resource_type, resource_id, tenant_id, created_by, created_at
           FROM audit_logs WHERE event_type='auth.token.issued'
           ORDER BY created_at DESC LIMIT 5;"
```

Bootstrap leg → `resource_type='PlatformBootstrapClient'`, `tenant_id` NULL.
Tenant leg → `resource_type='TenantConfiguration'`, `tenant_id` set, and the
tenant's row in `public.tenant_configurations` gets `last_used_at` stamped.

### 5.5 Error quick-reference (for orientation, not required testing)

| Case | Status | Body |
|---|---|---|
| Missing/empty fields | 400 | `{"error":"invalid_request"}` |
| `grant_type=password` etc. | 400 | `{"error":"unsupported_grant_type"}` |
| Unknown client / wrong secret / suspended tenant or credential | 401 | `{"error":"invalid_client"}` (identical body for all — no oracle) |
| > 10 hits/min from your IP | 429 | empty |
| No `/v1/` segment, or `/v2/...` | 404 | — |

---

## 6. Sub-domain 2 — Tenants

**Route tree:** `/v1/admin/tenants` — every action requires
`Authorization: Bearer $TOKEN` with `scope=admin` (the bootstrap token from
§5.1 works).

We'll use three tenants: **A** (primary — gets a credential and signing
keys), **B** (lifecycle dummy), **C** (for the key-`Adopt` mode later).

### 6.1 Register a tenant (happy path)

```bash
curl -sS -i -X POST $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"institutionName":"Example Bank","institutionCode":"100101"}'
```

**Expected — `201 Created`** with `Location: /v1/admin/tenants/{guid}` and:

```json
{
  "tenantId": "3f9b2c1e-....",
  "institutionName": "Example Bank",
  "institutionCode": "100101",
  "status": "Pending",
  "isActive": true
}
```

Save the id:

```bash
TENANT_A=$(curl -sS -X POST $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"Example Bank","institutionCode":"100101"}' | jq -r .tenantId)
```

Register the other two the same way (or via the copy-paste form):

```bash
TENANT_B=$(curl -sS -X POST $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"Lifecycle Test Bank","institutionCode":"100202"}' | jq -r .tenantId)

TENANT_C=$(curl -sS -X POST $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"Adopt Test Bank","institutionCode":"100303"}' | jq -r .tenantId)
```

> Field rules (validator-enforced → 400 on breach): `institutionName`
> non-empty, ≤ 200 chars; `institutionCode` exactly six digits
> (`^[0-9]{6}$`). A **duplicate** `institutionCode` is not a validation error
> — it surfaces as **409** `Tenant invariant violated`.

### 6.2 Get by id

```bash
curl -sS $BASE/v1/admin/tenants/$TENANT_A -H "Authorization: Bearer $TOKEN" | jq
```

**Expected — `200`** with the same shape as §6.1 (`status:"Pending"`).

### 6.3 List (paged, filterable)

```bash
curl -sS "$BASE/v1/admin/tenants?status=Pending&page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | jq
```

**Expected — `200`:**

```json
{
  "items": [ { "tenantId": "...", "institutionName": "Example Bank", "institutionCode": "100101", "status": "Pending", "isActive": true }, ... ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 3,
  "hasMore": false
}
```

Query params (all optional): `status` (case-insensitive
`Pending|Active|Suspended|Terminated`; anything else → 400), `isActive`
(bool), `page` (≥1, default 1), `pageSize` (1–100, default 20).

### 6.4 Activate (Pending → Active)

```bash
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_A/activate \
  -H "Authorization: Bearer $TOKEN" | jq
```

**Expected — `200`** with `"status":"Active"`. (No cascade: activation
touches only the tenant row.) Do the same for `TENANT_C` so it's `Active`.

### 6.5 Lifecycle on the dummy tenant (B)

```bash
# suspend (optional JSON body: {"reason":"<= 500 chars, lands in audit metadata"})
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_B/suspend \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"routine compliance hold"}' | jq
# → 200, "status":"Suspended"

# reactivate (Suspended → Active, cascades children back)
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_B/reactivate \
  -H "Authorization: Bearer $TOKEN" | jq
# → 200, "status":"Active"

# terminate (one-way; sets isActive=false too)
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_B/terminate \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"offboarded"}' | jq
# → 200, "status":"Terminated","isActive":false
```

Re-running any transition onto its current state (e.g. suspend on
Suspended) → **409**; transitions out of `Terminated` → **409**; unknown id →
**404**.

### 6.6 DB evidence

```bash
$PSQL -c "SELECT tenant_id, institution_name, institution_code, status, is_active, created_by, created_at
           FROM tenants ORDER BY created_at;"
```

Expected: A `ACTIVE`, B `TERMINATED` (is_active=false), C `ACTIVE`. The
lifecycle events wrote audit rows (`tenant.suspended`,
`tenant.reactivated`, `tenant.terminated` — see §13). Registration itself
deliberately writes **no** audit_logs row; its trail rides on the row's own
audit columns (`created_by` etc.).

---

## 7. Sub-domain 3 — Tenant configurations

One endpoint mints the credential; everything else about
"tenant-configurations" is the `public.tenant_configurations` **table**
plus its behaviour through the OAuth endpoint and tenant cascades.

### 7.1 Provision the client configuration (happy path)

```bash
curl -sS -i -X POST $BASE/v1/admin/tenants/$TENANT_A/tenant-configuration \
  -H "Authorization: Bearer $TOKEN"
```

**Expected — `201 Created`** with `Location: /v1/admin/tenants/{guid}`:

```json
{
  "tenantId": "3f9b2c1e-...",
  "credentialId": "8c1d9f02-...",
  "clientId": "100101-7f3a91c2",
  "clientSecret": "<43-char base64url string>",
  "expiresAt": "2027-09-05T09:15:00Z"
}
```

Notes:

- `clientId` format is `{institution_code}-{8 hex}`.
- **`clientSecret` is displayed exactly once.** Only its Argon2id hash is
  stored; there is no re-display endpoint. Copy it into a shell variable now:

```bash
CREDS=$(curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_A/tenant-configuration \
  -H "Authorization: Bearer $TOKEN")
CLIENT_ID=$(echo "$CREDS" | jq -r .clientId)
CLIENT_SECRET=$(echo "$CREDS" | jq -r .clientSecret)
```

- `expiresAt` is one year out. Admission rules: `Pending` and `Active`
  tenants may provision; `Suspended`/`Terminated` → 409. Provisioning a
  **second** configuration while one is active → **409** (rotation is a planned
  future flow — none exists today).

### 7.2 DB evidence — the row that actually backs OAuth

```bash
$PSQL -c "SELECT client_id,
                 left(client_secret_hash, 20) || '…' AS hash_prefix,
                 is_qr_generation_allowed, is_qr_validation_allowed,
                 status, expires_at, last_used_at, tenant_id
          FROM public.tenant_configurations;"
```

Expected: one row, `hash_prefix` starting `$argon2id$…`, both capability
flags `true`, `status='ACTIVE'`, `last_used_at` NULL (until first token).

Sanity-check the schema guarantees:

```sql
-- hash format is CHECK-enforced
SELECT client_secret_hash LIKE '$argon2id$%' FROM public.tenant_configurations;  -- t
```

### 7.3 Cascade behaviour (suspend / reactivate the tenant)

```bash
# Suspend tenant A → credential row suspends
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_A/suspend \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"test cascade"}' >/dev/null

$PSQL -c "SELECT status FROM public.tenant_configurations;"
# → SUSPENDED

# A suspended credential can no longer mint tokens (see 7.4 for the call):
#   → 401 {"error":"invalid_client"}

# Reactivate → credential reinstates
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_A/reactivate \
  -H "Authorization: Bearer $TOKEN" >/dev/null

$PSQL -c "SELECT status FROM public.tenant_configurations;"
# → ACTIVE
```

### 7.4 Happy path — tenant leg of OAuth

With `CLIENT_ID`/`CLIENT_SECRET` from §7.1 (and tenant A back in
`Active`/credential `ACTIVE`):

```bash
TENANT_TOKEN=$(curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=$CLIENT_ID \
  -d client_secret=$CLIENT_SECRET | jq -r .accessToken)
```

**Expected — `200`** with the same body shape as §5.1. Decode it (helper in
§5.2) and check the claims:

| Claim | Value |
|---|---|
| `sub` | `client:100101-7f3a91c2` (i.e. `client:{client_id}`) |
| `scope` | `["qr:generate","qr:validate"]` — never `admin` |
| `tenant_id` | tenant A's guid (lowercase) |

Then:

```bash
# last_used_at gets stamped by the mint
$PSQL -c "SELECT client_id, last_used_at FROM public.tenant_configurations;"

# the tenant token CANNOT reach the admin surface (policy needs scope=admin)
curl -sS -o /dev/null -w '%{http_code}\n' $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TENANT_TOKEN"
# → 403
```

If you re-check while the tenant (or credential) is `SUSPENDED`, the same
token call returns **401 `{"error":"invalid_client"}`** — that's §7.3's
money shot.

---

## 8. Sub-domain 4 — Crypto keys

**Route tree:** `/v1/crypto-keys` — requires `scope=admin` **or**
`scope=key-admin` (the bootstrap token works). Private key material never
crosses the API; it is sealed (AES-256-GCM) into the local key vault on disk.

### 8.1 Generate the first signing key (happy path)

```bash
curl -sS -i -X POST $BASE/v1/crypto-keys \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"tenantId\":\"$TENANT_A\",\"mode\":\"Generate\"}"
```

**Expected — `201 Created`** with
`Location: /v1/crypto-keys/{tenantId}/active`:

```json
{
  "cryptoKeyId": "b6e4f0aa-...",
  "tenantId": "3f9b2c1e-...",
  "keyId": "sbqr-signing",
  "keyVersion": 1,
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
  "status": "ACTIVE",
  "isActive": true,
  "createdAt": "2026-09-05T09:30:12Z"
}
```

(`status` is UPPERCASE here — note the contrast with tenants' PascalCase.)
A second `Generate` for the same tenant while an ACTIVE key exists →
**409** (use rotate). Unknown `tenantId` → **404**. Unknown `mode` → 400.

### 8.2 Get the active key

```bash
curl -sS $BASE/v1/crypto-keys/$TENANT_A/active \
  -H "Authorization: Bearer $TOKEN" | jq '{keyId, keyVersion, status}'
# → {"keyId":"sbqr-signing","keyVersion":1,"status":"ACTIVE"}
```

### 8.3 Rotate (retire v1, mint v2)

```bash
curl -sS -X PUT $BASE/v1/crypto-keys/$TENANT_A \
  -H "Authorization: Bearer $TOKEN" | jq '{keyVersion, status}'
# → {"keyVersion":2,"status":"ACTIVE"}
```

Rotation always **server-generates** the new key (no Adopt-on-rotate). Then
inspect the full history:

```bash
curl -sS "$BASE/v1/crypto-keys/$TENANT_A?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | jq '.items[] | {keyVersion, status, createdAt}'
# → v2 ACTIVE, v1 RETIRED   (same paged envelope as tenants: items/page/pageSize/totalCount/hasMore)
```

Filterable: `?status=Retired` etc. (case-insensitive; allowed values
`Generating, Pending, Active, Suspended, Retiring, Retired, Revoked`).
`GET /v1/crypto-keys` (no tenantId) lists across all tenants.

### 8.4 Adopt an externally generated key (happy path)

For tenant C, bring your own Ed25519 pair (public = SPKI PEM, private =
PKCS#8 PEM — this is exactly what `openssl genpkey/pkey` emits):

```bash
openssl genpkey -algorithm ed25519 -out /tmp/adopt.key
openssl pkey -in /tmp/adopt.key -pubout -out /tmp/adopt.pub

curl -sS -i -X POST $BASE/v1/crypto-keys \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "$(jq -n --arg tid "$TENANT_C" \
              --arg pub "$(cat /tmp/adopt.pub)" \
              --arg priv "$(cat /tmp/adopt.key)" \
              '{tenantId:$tid, mode:"Adopt", publicKeyPem:$pub, privateKeyPem:$priv}')"
```

**Expected — `201`** with the same `CryptoKeySummary` shape; `publicKeyPem`
matches your `/tmp/adopt.pub`, `status:"ACTIVE"`. A mismatched
public/private pair → 400 (PEM consistency is validated server-side).

### 8.5 DB evidence + invariants

```bash
$PSQL -c "SELECT tenant_id, key_id, key_version, status, is_active, created_at
           FROM crypto_keys ORDER BY tenant_id, key_version;"

# The "exactly one ACTIVE signing key per tenant" invariant (partial unique index)
$PSQL -c "SELECT tenant_id, count(*) FROM crypto_keys
           WHERE status='ACTIVE' GROUP BY tenant_id HAVING count(*) > 1;"
# → (0 rows) — always
```

Also note: the `custody_key_reference` column holds the opaque handle to the
sealed private blob; **no** audit_logs row is written for generate / adopt /
rotate (the table's own audit columns carry the trail).

### 8.6 The local key vault (where the private halves live)

Where the sealed blobs actually land depends on which vault provider the
host was started with ([§4.1](#41-running-with-the-s3-vault-provider-localstack)):

- **Default dev launch** (`Crypto:VaultProvider=Local`,
  `KeyCustody:ActiveProvider=PlainFile`) — each private key is sealed into
  its own file under:
  - Windows: `%LOCALAPPDATA%\sbqr\key-vault`
  - Linux: `/var/lib/sbqr/key-vault`

  ```bash
  ls -la "$LOCALAPPDATA/sbqr/key-vault" 2>/dev/null || ls -l /var/lib/sbqr/key-vault
  # → one file per key; name = hex sha256 of the custody handle;
  #   file starts with the ASCII magic "SBQRKEY1"
  head -c 8 <some-file> ; echo   # → SBQRKEY1
  ```

- **S3 vault launch** (`Crypto:VaultProvider=S3`,
  `KeyCustody:ActiveProvider=PemVault`, run via `dotnet run
  --launch-profile S3-LocalStack`) — each
  private key is sealed into the LocalStack `sbqr-dev` bucket at
  `keys/<institutionCode>_v<n>_private.pem` (one immutable object per
  `(institution, version)`, no flat mirror). SSE-S3 `AES256` is applied by
  LocalStack; the application-layer wrapping is still AES-256-GCM with the
  `SBQRKEY1` magic header. See [§4.1.4](#414-verify-the-s3-path-after-a-81-generate)
  for verification commands.

Files / objects appear right after §8.1/§8.3/§8.4 succeed. Under both
backends the dev KEK is a deterministic fallback (Production refuses this
and requires `KeyCustody:VaultKek`).

### 8.7 Cascade behaviour (keys follow the tenant)

```bash
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_A/suspend \
  -H "Authorization: Bearer $TOKEN" >/dev/null
$PSQL -c "SELECT key_version, status FROM crypto_keys WHERE tenant_id='$TENANT_A' AND status='ACTIVE';"
# → (0 rows) — the ACTIVE key is now SUSPENDED

curl -sS -X POST $BASE/v1/admin/tenants/$TENANT_A/reactivate \
  -H "Authorization: Bearer $TOKEN" >/dev/null
$PSQL -c "SELECT key_version, status FROM crypto_keys WHERE tenant_id='$TENANT_A' AND status='ACTIVE';"
# → v2 ACTIVE again
```

(Suspending/reinstating keys happens **only** through these tenant-level
cascades — there is no standalone suspend-key endpoint today, and `Revoked`
is only reachable via the InstitutionTrust sync, out of scope here.)

---

## 9. Sub-domain 5 — QR generation

**Endpoints:** `POST /v1/qr/generate/static` and `POST /v1/qr/generate/dynamic` —
require `Authorization: Bearer <token>` with `scope=qr:generate` (the **tenant**
token from [§7.4](#74-happy-path--tenant-leg-of-oauth), never the
bootstrap/admin token). Owning module: `QrGeneration`
(`SBQR.Modules.QrGeneration.*`).

The two endpoints mirror the BanglaQR P2P spec's two QR modes (Tag 01
"11" = static / "12" = dynamic). The split is deliberate:

- **Static QR** encodes only the recipient identity — the transaction amount
  is decided by the payer at scan time and is therefore **not** carried in
  the payload (Tag 54 is absent). `transactionAmount` is rejected on this
  endpoint (400).
- **Dynamic QR** carries the transaction amount in Tag 54 — that field is
  **required** on this endpoint (400 when missing or non-numeric).

Both endpoints share the same error contract and return the same response
shape (`GenerateQrResponse`).

> ### Tenant context — how the bearer token reaches the handler
>
> The QR-generation handler reads its tenant identity from
> `ICurrentTenant.TenantId` (line 123 of
> `GenerateQrCommandHandler.cs`). The host wires
> `JwtClaimCurrentTenant`
> (`src/Host/SBQR.Api/Infrastructure/JwtClaimCurrentTenant.cs`) — a
> `Scoped` resolver that pulls the `tenant_id` claim out of the
> validated `HttpContext.User` on every DI resolution.
>
> Concretely, this means:
>
> - **Bootstrap / admin tokens** carry no `tenant_id` claim (the issuer
>   deliberately omits it for the platform path). The resolver returns
>   `Guid.Empty` for those, and the QR handler rejects them with the
>   `401 {"error":3,"message":"QR generation requires an authenticated tenant request."}`
>   body — same response it has always produced for a missing tenant.
> - **Tenant FI tokens** minted by `IssueClientCredentialsTokenCommand`
>   carry `tenant_id` (see `JwtAccessTokenIssuer.cs:74-77`). The
>   resolver parses it case-insensitively, and the handler proceeds
>   with that tenant's institution identity.
> - **Anonymous / health / OAuth-token paths** never resolve a tenant
>   — the resolver falls back to `Guid.Empty`, which those endpoints
>   do not consume.
>
> The legacy `NoopCurrentTenant` (always `Guid.Empty`) is kept in the
> tree as a documented dev-only fallback but is no longer wired into
> DI. An architecture test
> (`CurrentTenant_implementations_must_only_live_in_Host_Infrastructure`,
> in `tests/SBQR.ArchitectureTests`) prevents a future module from
> shipping its own resolver and silently bypassing the JWT path.

### 9.1 Prerequisites

Reuse tenant A's state from §§6–8: `Active` tenant status, a provisioned
tenant configuration ([§7.1](#71-provision-the-client-configuration-happy-path))
with its `TENANT_TOKEN` ([§7.4](#74-happy-path--tenant-leg-of-oauth), scope
`qr:generate,qr:validate`), and an ACTIVE signing key
([§8.1](#81-generate-the-first-signing-key-happy-path)/[§8.3](#83-rotate-retire-v1-mint-v2)).

### 9.2 Happy path A — static QR

`POST /v1/qr/generate/static` — Tag 01 = "11". No `transactionAmount` field
(it's rejected if sent).

```bash
curl -sS -i -X POST $BASE/v1/qr/generate/static \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
        "recipientName": "Arif Mahmood",
        "recipientCity": "Dhaka",
        "recipientPan": "01711111111"
      }'
```

**Expected — `201 Created`** (once tenant resolution ships), with
`Location: /v1/qr/generate/static` and:

```json
{
  "qrPayload": "0002010102115926...520448005303050...5802BD5910Arif Mahmood6006Dhaka...6304A1B2",
  "payloadHash": "9f2c4e7b1a...e1",
  "qrType": "STATIC",
  "signatureKeyVersion": 2
}
```

Tag `01` in `qrPayload` reads `11` (static Point of Initiation Method,
Table 3A). The QR string is returned **exactly once** and is never
persisted — only `payloadHash` is (load-bearing assertion L3, see §9.5).

### 9.3 Happy path B — dynamic QR (amount + optional fields)

`POST /v1/qr/generate/dynamic` — Tag 01 = "12". `transactionAmount` is
required (digits with an optional single decimal point, ≤ 13 bytes).

```bash
curl -sS -i -X POST $BASE/v1/qr/generate/dynamic \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-dynamic-001' \
  -d '{
        "transactionAmount": "150.00",
        "recipientName": "Arif Mahmood",
        "recipientCity": "Dhaka",
        "recipientPan": "01711111111",
        "postalCode": "1207",
        "customerLabel": "FT",
        "purposeOfTransaction": "Invoice #4821"
      }'
```

**Expected — `201 Created`**, same response shape, `"qrType":"DYNAMIC"`, Tag
`01` reads `12`. The `Idempotency-Key` header is optional but recommended
for client-side retry safety (see §9.4 for what happens on reuse).

> Field rules (FluentValidation → 400 on breach), applied per endpoint:
> `recipientName` non-empty ≤ 25 bytes UTF-8; `recipientCity` non-empty ≤
> 15 bytes; `recipientPan` non-empty ≤ 19 bytes; `postalCode` ≤ 10 bytes
> when present; `customerLabel` and `purposeOfTransaction` ≤ 25 bytes when
> present; `Idempotency-Key` ≤ 100 chars when present.
> Dynamic adds `transactionAmount` required, ≤ 13 bytes, digits with an
> optional single decimal point. Static has no `transactionAmount` field at
> all. The institution identity (Tag 26) always comes from the calling
> tenant's own `institutionCode` — never from the request body.

### 9.4 Failure scenarios

The status codes and bodies below apply to **both** endpoints unless
explicitly qualified.

| # | Case | How to trigger | Status | Body |
|---|---|---|---|---|
| a | Mandatory field missing/empty | Omit `recipientName` (or send `""`) on either endpoint | 400 | `{"error":1,"message":"RecipientName: 'Recipient Name' must not be empty."}` |
| b | Field exceeds byte cap | `recipientCity` > 15 UTF-8 bytes on either endpoint | 400 | `{"error":1,"message":"RecipientCity: The length of 'Recipient City' must be 15 characters or fewer..."}` |
| c | Static endpoint receives `transactionAmount` | Send `{"transactionAmount":"100.00", ...}` to `/qr/generate/static` | 400 | `{"error":1,"message":"Static QR does not accept 'transactionAmount'; use POST /v1/qr/generate/dynamic instead."}` |
| d | Dynamic endpoint missing `transactionAmount` | Omit `transactionAmount` from a `/qr/generate/dynamic` body | 400 | `{"error":1,"message":"TransactionAmount: 'Transaction Amount' must not be empty."}` |
| e | Tenant has no ACTIVE signing key | Call as a freshly-registered/activated tenant that never ran §8.1 (key status `NONE`), or a tenant whose key is `SUSPENDED` (e.g. right after §8.7's tenant-suspend cascade, using a `TENANT_TOKEN` minted *before* the suspend — the JWT itself is still valid until it expires) | 422 | `{"error":"KEY_NOT_ACTIVE","message":"KEY_NOT_ACTIVE: tenant 100101 key status is 'NONE'."}` |
| f | Duplicate `Idempotency-Key` for the same tenant | Repeat §9.3's exact call (same header value) | 409 | `{"error":"DUPLICATE_IDEMPOTENCY_KEY","message":"DUPLICATE_IDEMPOTENCY_KEY: this Idempotency-Key was already used by this tenant."}` |
| g | Wrong token audience (admin/bootstrap token instead of tenant token) | Use `$TOKEN` from §5.1 instead of `$TENANT_TOKEN` | 403 | empty (policy requires `scope=qr:generate`; the bootstrap token carries `scope=admin`) |
| h | No bearer token at all | Omit the `Authorization` header | 401 | empty (challenge from the auth middleware, before the handler runs) |
| i | Missing/undeclared API version | `POST /qr/generate/static` or `POST /v2/qr/generate/static` | 404 | — (same URL-versioning rule as every other endpoint, §1) |

> **Enum-casing note** (mirrors §5.1's `accessToken` callout): cases (a)/(b)/(c)/(d)
> serialize `error` as the **raw numeric `ErrorCode`** (`1` = `ValidationFailed`)
> because no `JsonStringEnumConverter` is registered — this is the generic
> fallback branch in `QrGenerationController`. Cases (e)/(f) special-case
> their `error` field as a literal string (`"KEY_NOT_ACTIVE"`,
> `"DUPLICATE_IDEMPOTENCY_KEY"`) before the generic branch runs. Don't expect
> consistent casing across every error shape on this endpoint.

### 9.5 DB evidence

```bash
$PSQL -c "SELECT qr_generation_id, tenant_id, qr_type, payload_hash,
                 signature_key_version, idempotency_key, created_at
          FROM qr_generations ORDER BY created_at;"
```

Expected: one row per successful `201`. **No payload column exists** —
`payload_hash` (SHA-256 hex of the finalized QR string) is the only trace
kept, by design (assertion L3; relaxing it is an ADR-level change). The
`(tenant_id, idempotency_key)` pair is unique via a partial index
(`ix_qr_generations_tenant_idempotency_unique`, `WHERE idempotency_key IS
NOT NULL`) — that's what backs failure case (d).

Every successful generation also writes an audit row (`qr.generated`,
`resource_type='qr_generation'`, `resource_id`=the payload hash) — see §13.
Failed generation attempts are **not** audited, same convention as OAuth
(§5.5).

---

## 10. Sub-domain 6 — QR validation

**Endpoint:** `POST /v1/qr/validate` — bearer-token auth (`scope=qr:validate`,
tenant or admin JWT both work as long as the scope is present), one JSON
field in the body, eight fields on the response. Owning module: `Verification`
(`SBQR.Modules.Verification.*`). Listed in the **public** OpenAPI document
(`/openapi/v1.public.json`, Scalar UI at `/docs/public`) because it is the
shape a payer app would call — but it is not anonymous.

### 10.1 What this endpoint does

You paste a raw BanglaQR P2P payload string (the same one you got back from
`POST /v1/qr/generate/static|dynamic` in §9, or one produced by any other
compliant issuer) into the body. The server runs the **reverse** of the
generation pipeline:

1. Decode the TLV, verify the CRC (`6304`).
2. Extract the issuer's institution code from Tag 26.
3. Resolve the issuer's Ed25519 public key — first from this platform's own
   `crypto_keys` (`trustSource = "OWN_CUSTODY"`), falling back to the BB
   trust directory (`trustSource = "TRUST_DIRECTORY"`) if the issuer is not
   one of our tenants.
4. Verify the embedded signature (Tag 63/64 chain).
5. Return a single `verdict` plus the decoded identity fields the payer app
   reviews.

The endpoint is **fail-closed by construction**: every indeterminate or
failed step returns a recorded rejection verdict (`INVALID_SIGNATURE`,
`STRUCTURAL_INVALID`, `KEY_*`). There is no configuration flag anywhere
that turns a failed verification into an acceptance.

### 10.2 Mental model — generating and validating is one round-trip

The validation endpoint is the second half of the issuance pipeline you
walked through in §§6–9. If you have already run §9 (or §11's ACME worked
example) end-to-end, you already have everything you need:

```
 ┌─────────────────────────────────────────────┐   ┌──────────────────────────┐
 │  POST /v1/qr/generate/static                │   │  POST /v1/qr/validate    │
 │  Bearer: <TENANT_TOKEN>                     │   │  Bearer: <TENANT_TOKEN>  │
 │  ────────► qrPayload (returned exactly once)│──►│  ──► verdict + identity  │
 └─────────────────────────────────────────────┘   └──────────────────────────┘
       generation pipeline                            verification pipeline
       (QrGeneration)                                 (Verification)
```

You can validate any QR that is BanglaQR P2P compliant — your own freshly
generated one, a static QR you generated yesterday and pasted into a
mobile app, or one generated by an external institution you have previously
registered into the trust directory (see `POST /v1/admin/institutions`).

### 10.3 Prerequisites

| What | Where it comes from |
|---|---|
| API running on `BASE` | [§4](#4-start-the-api--smoke-checks) |
| A bearer token with `scope=qr:validate` | [§5.3](#53-happy-path-b--tenant-credential-token) (tenant leg — what the §9 generation used) **or** the bootstrap/admin token does **not** carry this scope, so it will 403 here; mint a tenant token first |
| A QR payload string to validate | One you just minted in [§9](#9-sub-domain-5--qr-generation), a fixture from `QrRoundTripTests`, or a hand-rolled BanglaQR string. The field is a single string ≤ 2048 chars; nothing else on the body |

### 10.4 Setting it up in Postman

**One-time setup** (do this once per Postman workspace, then reuse the
collection forever):

1. **Create an environment.** Top-right → gear → *Manage Environments* →
   *Add*. Name it `SBQR Local`. Add two variables:
   - `baseUrl` → `http://localhost:5001`
   - `tenantToken` → *(leave the **Initial** and **Current** values blank
     for now; you fill it in step 3 below)*

2. **Get the tenant token** in Postman itself — no shell needed:
   - *Add request* → method **POST**, URL
     `{{baseUrl}}/v1/oauth/token`
   - **Body** tab → `x-www-form-urlencoded` (Postman's first option on
     that tab). Add three key/value rows:

     | KEY | VALUE |
     |---|---|
     | `grant_type` | `client_credentials` |
     | `client_id` | `100101-7f3a91c2` *(your tenant's `clientId` from §7.1)* |
     | `client_secret` | `<the 43-char secret you copied in §7.1>` |

   - **Tests** tab → add this one-liner so Postman captures the token for
     later requests:

     ```javascript
     pm.environment.set("tenantToken", pm.response.json().accessToken);
     ```

   - *Send*. Expect **200 OK** with the `accessToken` field.

3. **Open a second request.** Method **POST**, URL
   `{{baseUrl}}/v1/qr/validate`. You are now ready for §§10.5–10.8.

> If your tenant was registered with `institutionCode=031008`, the tenant
> `clientId` is `031008-7f3a91c2` (the institution prefix preserves leading
> zeros). If the response from `/v1/oauth/token` is **401
> `{"error":"invalid_client"}`**, re-run [§7.3](#73-cascade-behaviour-suspend--reactivate-the-tenant)
> — the credential was probably left in `SUSPENDED` from a previous test.

### 10.5 Happy path A — round-trip your own freshly generated QR

The cleanest test of `/v1/qr/validate` is to mint a QR through
`/v1/qr/generate/static` and feed the response right back in. This proves
**your** signing key, **your** TLV builder, and **your** verifier all line
up on the live wire.

**Request to `/v1/qr/generate/static`** (so you have something to validate):

| Tab | Field | Value |
|---|---|---|
| **Authorization** | Type | Bearer Token |
| | Token | `{{tenantToken}}` |
| **Headers** | `Content-Type` | `application/json` |
| | `Idempotency-Key` | `<any uuid, e.g. from {{$guid}}>` *(optional)* |
| **Body** *(raw, JSON)* | | see below |

```json
{
  "recipientName": "Arif Mahmood",
  "recipientCity": "Dhaka",
  "recipientPan":  "01711111111",
  "postalCode":    "1212",
  "customerLabel": "ACME Bank",
  "purposeOfTransaction": "utility"
}
```

Send it. Expect **201 Created** with a body like:

```json
{
  "qrPayload": "000201010211293000391203100810082Arif Mahmood6006Dhaka...6304A1B2",
  "payloadHash": "9f2c4e7b1a2f3e4d5c6b7a8d9e0f1234c5b6a7d8e9f0123456789abcdef01234",
  "qrType": "STATIC",
  "signatureKeyVersion": 1
}
```

Copy the `qrPayload` value into your clipboard (or use Postman's *Set
Environment Variable* snippet to capture it automatically — see the helper
at the end of §10.4):

```javascript
// Tests tab on the /generate/static request
pm.environment.set("lastQrPayload", pm.response.json().qrPayload);
```

**Now validate it.** Open the second request from §10.4 step 3
(`POST /v1/qr/validate`):

| Tab | Field | Value |
|---|---|---|
| **Authorization** | Type | Bearer Token |
| | Token | `{{tenantToken}}` |
| **Headers** | `Content-Type` | `application/json` |
| **Body** *(raw, JSON)* | | see below |

```json
{
  "qrPayload": "{{lastQrPayload}}"
}
```

> Postman detail: paste the raw QR string directly. It will look like a wall
> of digits/letters — no JSON-escaping is needed because the entire value is
> a single JSON string. Don't try to re-format it; the codec treats the
> string as opaque bytes and will recompute the CRC anyway.

Send it. Expect **200 OK** with:

```json
{
  "verdict":            "VALID",
  "trustSource":        "OWN_CUSTODY",
  "reasonCode":         null,
  "institutionCode":    "100101",
  "payloadHash":        "9f2c4e7b1a2f3e4d5c6b7a8d9e0f1234c5b6a7d8e9f0123456789abcdef01234",
  "recipientName":      "Arif Mahmood",
  "recipientPan":       "01711111111",
  "qrClassification":   "P2P"
}
```

Field-by-field:

| Field | Value here | Why |
|---|---|---|
| `verdict` | `"VALID"` | Ed25519 signature verified against **this** tenant's ACTIVE key (`OWN_CUSTODY` path) |
| `trustSource` | `"OWN_CUSTODY"` | Issuer is one of our tenants; key was loaded from `crypto_keys`, not the BB trust directory |
| `reasonCode` | `null` | Only populated on rejection verdicts |
| `institutionCode` | `"100101"` | Tag 26 decoded; matches the `institutionCode` of the tenant that minted the token |
| `payloadHash` | (same hex string) | SHA-256 of `qrPayload`; should be **byte-for-byte** identical to the one in the generation response |
| `recipientName`, `recipientPan` | (echoed from generation) | Decoded from Tag 59 / 62; what the payer app surfaces to the user for confirmation |
| `qrClassification` | `"P2P"` | Tag 52 = 4829 — the BanglaQR P2P flow we build for |

**Cross-check:** the `payloadHash` in the validate response is the same hex
string you got back from `/v1/qr/generate/static`. If they differ, the
QR string was mutated in transit (Postman normalization, paste-with-newlines,
etc.) — re-mint and re-validate without touching the value in between.

### 10.6 Happy path B — round-trip a dynamic QR (Tag 01 = "12")

Same call shape as §10.5, but on the dynamic endpoint. The dynamic payload
also carries Tag 54 with the transaction amount; both static and dynamic
payloads validate identically through this endpoint — the validator doesn't
branch on Tag 01.

```bash
# In Postman, duplicate the /generate/static request from §10.5 and rename it.
```

| Tab | Change |
|---|---|
| **URL** | `{{baseUrl}}/v1/qr/generate/dynamic` |
| **Body** | (add `transactionAmount` — required on this endpoint) |

```json
{
  "transactionAmount": "150.00",
  "recipientName": "Arif Mahmood",
  "recipientCity": "Dhaka",
  "recipientPan":  "01711111111",
  "postalCode":    "1212",
  "customerLabel": "ACME Bank",
  "purposeOfTransaction": "Invoice #4821"
}
```

Capture the new `qrPayload` and send it through the **same** validate
request from §10.5. Expected response is the same shape:

```json
{
  "verdict":            "VALID",
  "trustSource":        "OWN_CUSTODY",
  "reasonCode":         null,
  "institutionCode":    "100101",
  "payloadHash":        "<64-char hex>",
  "recipientName":      "Arif Mahmood",
  "recipientPan":       "01711111111",
  "qrClassification":   "P2P"
}
```

> The amount field (`150.00`) is **not** echoed back on the validate
> response — it isn't decoded into any field on `ValidateQrResponse`. That's
> by design: the verifier proves the issuer signed the payload, but does
> not re-validate the amount semantics. The payer's app is responsible for
> displaying the amount to the user for confirmation before executing the
> transfer.

### 10.7 Happy path C — validate an external issuer's QR (trust directory)

Once a QR has been generated by an **external** institution (one not
registered as a tenant in `tenants`), the validator falls back to the
trust directory. To exercise this path in Postman you need:

1. An institution + Ed25519 keypair registered into the trust directory
   (`POST /v1/admin/institutions` — out of scope for this guide, see the
   smoke script in `scripts/smoke/`).
2. A QR minted with **that** institution's private key (the integration
   test `tests/SBQR.Qr.IntegrationTests/QrRoundTripTests.cs` does this in
   its step 6 and is the easiest way to get a fixture string).
3. The **same** `{{tenantToken}}` you used for §10.5 — your token does
   *not* need to match the issuer. Validation is server-to-server, and the
   issuer is determined by the QR's Tag 26, not the caller's claims.

Feed that payload into the §10.5 validate request. Expected response:

```json
{
  "verdict":            "VALID",
  "trustSource":        "TRUST_DIRECTORY",
  "reasonCode":         null,
  "institutionCode":    "000909",
  "payloadHash":        "<64-char hex>",
  "recipientName":      "<from the external issuer>",
  "recipientPan":       "<from the external issuer>",
  "qrClassification":   "P2P"
}
```

The only differences from §10.5 are `trustSource` (`TRUST_DIRECTORY`
instead of `OWN_CUSTODY`) and `institutionCode` (the external issuer's
code instead of yours). Everything else follows the same shape.

### 10.8 Failure scenarios (Postman-friendly table)

The status codes below are what Postman shows in the status bar; the body
is what's in the **Body** tab. **Every** rejection verdict returns **200 OK
with a non-`VALID` verdict** — only request-shape problems return 4xx.

| # | How to trigger in Postman | Status | `verdict` | `reasonCode` | `trustSource` |
|---|---|---|---|---|---|
| a | **Empty body** (omit `qrPayload` or send `{"qrPayload":""}`) | **400** | — | — | — *(FluentValidation: `NotEmpty`)* |
| b | **Too long** (set `qrPayload` to a 3000-char string) | **400** | — | — | — *(FluentValidation: `MaximumLength(2048)`)* |
| c | **Malformed TLV / CRC mismatch** — flip the last char of a valid payload | **200** | `STRUCTURAL_INVALID` | `CRC_MISMATCH` | `null` |
| d | **Missing signature** — take a valid payload, strip the trailing `6304XXXX` + signature tag | **200** | `INVALID_SIGNATURE` | `SIGNATURE_MISMATCH` *(or `MISSING_SIGNATURE`)* | `null` |
| e | **Wrong key** — sign a fresh QR with a private key the server doesn't know | **200** | `INVALID_SIGNATURE` | `SIGNATURE_MISMATCH` | `null` |
| f | **Issuer unknown** — QR with an institution code that exists in no `tenants` row and no `institutions` row | **200** | `KEY_NOT_FOUND` | `TRUST_DIRECTORY_MISS` *(or `CUSTODY_KEY_NOT_FOUND`)* | `null` |
| g | **Issuer key suspended** — sign with your tenant's private key, then suspend the tenant (§6.5); the next validate returns | **200** | `KEY_SUSPENDED` | `CUSTODY_KEY_SUSPENDED` | `null` |
| h | **Non-P2P QR** — feed in a payload whose Tag 52 is anything other than `4829` (e.g. a merchant-presented-mode QR) | **200** | `NON_P2P` | `null` | `null` *(informational — not an error)* |
| i | **No bearer** — clear the Authorization tab | **401** | — | — | — *(auth middleware challenge)* |
| j | **Wrong scope** — send a token whose `scope` claim is only `admin` (the bootstrap token from §5.1) | **403** | — | — | — *(policy check: needs `qr:validate`)* |
| k | **Wrong path** — POST `/v1/qr/valid8` (typo) or `POST /v2/qr/validate` | **404** | — | — | — *(URL versioning — see §1)* |

Body samples (one row, all the same shape):

Case (c) — `STRUCTURAL_INVALID` (CRC mismatch):
```json
{
  "verdict":            "STRUCTURAL_INVALID",
  "trustSource":        null,
  "reasonCode":         "CRC_MISMATCH",
  "institutionCode":    "100101",
  "payloadHash":        "<SHA-256 of the (now-corrupt) qrPayload>",
  "recipientName":      null,
  "recipientPan":       null,
  "qrClassification":   "UNKNOWN"
}
```

Case (f) — `KEY_NOT_FOUND` (issuer not in custody or trust directory):
```json
{
  "verdict":            "KEY_NOT_FOUND",
  "trustSource":        null,
  "reasonCode":         "TRUST_DIRECTORY_MISS",
  "institutionCode":    "777777",
  "payloadHash":        "<SHA-256 of the qrPayload>",
  "recipientName":      null,
  "recipientPan":       null,
  "qrClassification":   "P2P"
}
```

Case (h) — `NON_P2P` (informational, not an error):
```json
{
  "verdict":            "NON_P2P",
  "trustSource":        null,
  "reasonCode":         null,
  "institutionCode":    "100101",
  "payloadHash":        "<SHA-256 of the qrPayload>",
  "recipientName":      "<decoded>",
  "recipientPan":       "<decoded>",
  "qrClassification":   "NON_P2P"
}
```

> **Why a `Rejection` is a `200 OK`** — the verdict *is* the answer. A
> payer app branching on `verdict` should never have to inspect an HTTP
> status code to distinguish "this QR is bad" from "my request was bad".
> `4xx` is reserved for **request-shape problems** (empty payload, wrong
> scope, no bearer) that mean the validator didn't even get to look at
> the QR.

### 10.9 Field reference card (bookmark this for the team)

| Field | Type | Always present? | Legal values |
|---|---|---|---|
| `verdict` | string | yes | `VALID` · `INVALID_SIGNATURE` · `STRUCTURAL_INVALID` · `KEY_NOT_FOUND` · `KEY_SUSPENDED` · `KEY_REVOKED` · `KEY_NOT_ACTIVE` · `NON_P2P` |
| `trustSource` | string? | yes, but `null` on every non-`VALID` verdict | `OWN_CUSTODY` · `TRUST_DIRECTORY` · `null` |
| `reasonCode` | string? | yes, but `null` on `VALID` and `NON_P2P` | codec-stable string (`CRC_MISMATCH`, `SIGNATURE_MISMATCH`, `MISSING_SIGNATURE`, `CUSTODY_KEY_NOT_FOUND`, `TRUST_DIRECTORY_MISS`, `CUSTODY_KEY_SUSPENDED`, …) |
| `institutionCode` | string? | yes when Tag 26 decodes | 6-digit code (leading zeros preserved) · `null` if the QR is structurally unparseable |
| `payloadHash` | string | yes | 64-char lowercase hex SHA-256 of the raw `qrPayload` |
| `recipientName` | string? | yes when Tag 59 decodes | ≤25-byte UTF-8 name · `null` if not present |
| `recipientPan` | string? | yes when Tag 62's PAN sub-tag decodes | phone or PAN · `null` if not present |
| `qrClassification` | string | yes | `P2P` (Tag 52 = 4829) · `NON_P2P` (Tag 52 ≠ 4829) · `UNKNOWN` (structural failure before classification) |

> **Wire-format note.** Field names are camelCase (ASP.NET Core default
> `System.Text.Json` policy); the `verdict` / `trustSource` /
> `qrClassification` strings are the enum's `.ToString()` literal, not
> the typical `Accept` / `Reject` / `Static` / `Dynamic` vocabulary you
> might expect. The earlier §11.1 example in this guide used the
> `Accept` / `Static` vocabulary — that was a stale draft; the live wire
> speaks `VALID` / `P2P` (this section is the corrected version).

### 10.10 DB / audit evidence

Every successful validation (including rejections — this is one endpoint
where `Rejection` verdicts **do** leave an audit row) writes one
`qr.validated` row:

```bash
$PSQL -c "SELECT event_type, resource_type, left(resource_id, 16) || '…' AS resource_prefix,
                 tenant_id, verdict, trust_source, reason_code, created_at
          FROM audit_logs
          WHERE event_type='qr.validated'
          ORDER BY created_at DESC LIMIT 5;"
```

The `resource_id` is the **payload hash** of the validated QR — the same
hex string that lives in the response, and the same hex string you got back
from `/v1/qr/generate/{static,dynamic}` if you are round-tripping your own
QR. Cross-checking:

```bash
# All generations and validations of one QR share the same payload_hash:
$PSQL -c "SELECT event_type, created_at, resource_id
          FROM audit_logs
          WHERE resource_id = '<paste the 64-char hex payload_hash here>'
          ORDER BY created_at;"
# → qr.generated  <-- one row from §10.5/§10.6
# → qr.validated  <-- one row from this section
```

Failed validations (400 / 401 / 403 — request-shape problems, §10.8 rows
a/b/i/j/k) are **not** audited, same convention as generation (§9.5) and
OAuth (§5.5).

### 10.11 Postman collection — one-click import

The fastest way to onboard a new dev is to share the Postman collection as
JSON. Drop this into a file named `sbqr-qr-validation.postman_collection.json`
and *Import* it into Postman:

```json
{
  "info": {
    "name": "SBQR — QR Validation",
    "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
  },
  "item": [
    {
      "name": "1. Mint tenant token (OAuth)",
      "request": {
        "method": "POST",
        "header": [{ "key": "Content-Type", "value": "application/x-www-form-urlencoded" }],
        "url": { "raw": "{{baseUrl}}/v1/oauth/token", "host": ["{{baseUrl}}"], "path": ["v1", "oauth", "token"] },
        "body": {
          "mode": "urlencoded",
          "urlencoded": [
            { "key": "grant_type",     "value": "client_credentials" },
            { "key": "client_id",      "value": "100101-7f3a91c2" },
            { "key": "client_secret",  "value": "<paste from §7.1>" }
          ]
        }
      },
      "event": [{
        "listen": "test",
        "script": {
          "exec": [
            "pm.environment.set('tenantToken', pm.response.json().accessToken);"
          ]
        }
      }]
    },
    {
      "name": "2. Generate static QR (capture qrPayload)",
      "request": {
        "method": "POST",
        "header": [
          { "key": "Authorization", "value": "Bearer {{tenantToken}}" },
          { "key": "Content-Type",  "value": "application/json" },
          { "key": "Idempotency-Key", "value": "{{$guid}}" }
        ],
        "url": { "raw": "{{baseUrl}}/v1/qr/generate/static", "host": ["{{baseUrl}}"], "path": ["v1", "qr", "generate", "static"] },
        "body": {
          "mode": "raw",
          "raw": "{\n  \"recipientName\": \"Arif Mahmood\",\n  \"recipientCity\": \"Dhaka\",\n  \"recipientPan\":  \"01711111111\",\n  \"postalCode\":    \"1212\",\n  \"customerLabel\": \"ACME Bank\",\n  \"purposeOfTransaction\": \"utility\"\n}"
        }
      },
      "event": [{
        "listen": "test",
        "script": {
          "exec": [
            "pm.environment.set('lastQrPayload', pm.response.json().qrPayload);",
            "console.log('captured payloadHash=', pm.response.json().payloadHash);"
          ]
        }
      }]
    },
    {
      "name": "3. Validate the QR (round-trip)",
      "request": {
        "method": "POST",
        "header": [
          { "key": "Authorization", "value": "Bearer {{tenantToken}}" },
          { "key": "Content-Type",  "value": "application/json" }
        ],
        "url": { "raw": "{{baseUrl}}/v1/qr/validate", "host": ["{{baseUrl}}"], "path": ["v1", "qr", "validate"] },
        "body": {
          "mode": "raw",
          "raw": "{\n  \"qrPayload\": \"{{lastQrPayload}}\"\n}"
        }
      }
    },
    {
      "name": "4. Validate with tampered payload (expect STRUCTURAL_INVALID)",
      "request": {
        "method": "POST",
        "header": [
          { "key": "Authorization", "value": "Bearer {{tenantToken}}" },
          { "key": "Content-Type",  "value": "application/json" }
        ],
        "url": { "raw": "{{baseUrl}}/v1/qr/validate", "host": ["{{baseUrl}}"], "path": ["v1", "qr", "validate"] },
        "body": {
          "mode": "raw",
          "raw": "{\n  \"qrPayload\": \"{{lastQrPayloadInvalid}}\"\n}"
        }
      }
    }
  ]
}
```

The four requests correspond to §§10.5 / 10.8(c) / 10.8(d–f) etc. The
"Tests" scripts on requests 1 and 2 populate the environment variables
`tenantToken` and `lastQrPayload` automatically — open the *Runner*,
select this collection, and step through it once to populate the
environment.

### 10.12 Where this fits in the bigger picture

`/v1/qr/validate` is the only endpoint a payer mobile app talks to for the
verification side. It is listed in the **public** OpenAPI document because
that document is the one you'd hand to a third-party app developer —
but the endpoint still requires the same `qr:validate`-scoped JWT that the
tenant leg of OAuth mints. The auth is server-to-server; the public-doc
listing is for SDK / schema discoverability, not for unauthenticated access.

The full round-trip (§§10.5 + §10.6 + §10.7) is what `QrRoundTripTests`
exercises in-process. If the integration test passes and the Postman calls
fail, the gap is almost always one of:

| Symptom | Likely cause |
|---|---|
| `verdict` is `INVALID_SIGNATURE` on a freshly minted QR | the `qrPayload` value picked up whitespace / newlines on the Postman clipboard; re-mint and copy without trailing newline |
| `verdict` is `KEY_NOT_FOUND` on your own freshly minted QR | the tenant you're validating as is in `SUSPENDED` status (§6.5), so the key lookup returns `KEY_SUSPENDED` → `KEY_NOT_FOUND`; reactivate the tenant |
| `qrClassification` is `"UNKNOWN"` instead of `"P2P"` | the QR failed structural parse before Tag 52 could be read; usually means a CRC mismatch — see §10.8(c) |
| `payloadHash` differs between generation and validation | the payload string was mutated between calls; treat it as fully opaque and pass through Postman variables only |

---

## 11. Worked example — ACME Bank (institution_code `031008`)

The §6–§9 examples use generic tenants (Example Bank / `100101`, etc.).
This section walks **one** concrete end-to-end scenario — **ACME Bank,
institution code `031008`** — with the exact request bodies, response
bodies, and DB queries you can paste into a shell and replay. The
institution code is leading-zero sensitive: `031008` is **not** the same
as `31008` and the validator will reject any value that isn't exactly
six digits (`^[0-9]{6}$`).

### 11.1 What this example proves

- A new tenant can be registered, activated, credentialed, and key-minted
  end-to-end over real HTTP.
- The QR payload that the issuance pipeline produces for ACME Bank encodes
  the institution identity from the **tenant row** (Tag 26 =
  `031008` → `institutionType=03`, `institutionId=1008`), never from the
  request body.
- The same payload round-trips through verification (`verdict=Accept`,
  `institutionCode=031008`, `payloadHash` stable).
- A row lands in `qr_generations` with `qr_type='STATIC'` and the
  SHA-256 of the finalized payload in `payload_hash`.

### 11.2 Prerequisites

| Prerequisite | How to satisfy |
|---|---|
| Postgres up and migrations applied | `pwsh docker/start-db.ps1`, then run the external migration tool against `sbqr_app` + `sbqr_key_vault` (§3.2) |
| API listening on `http://localhost:5001` | `dotnet run --project src/Host/SBQR.Api` |
| `BASE` exported | `export BASE=http://localhost:5001` (Git Bash) or `$env:BASE='http://localhost:5001'` (PowerShell) |
| `curl`, `jq` | already on PATH |
| A fresh DB | Use the wipe+remigrate sequence from §3.1 / §3.5, or omit previously-registered institution codes (the unique index makes `031008` collide on a re-run) |

### 11.3 Step 1 — bootstrap admin token

```bash
TOKEN=$(curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret=dev-only-bootstrap-secret | jq -r .accessToken)
echo "$TOKEN" | awk -F. '{print $2}' | tr '_-' '/+' \
  | { read p; case $((${#p} % 4)) in 2) p="${p}==";; 3) p="${p}=";; esac; echo "$p"; } \
  | base64 -d 2>/dev/null | jq '.scope'
# → ["admin"]
```

### 11.4 Step 2 — register ACME Bank

**Request body** (camelCase, exact wire format):

```json
{
  "institutionName": "ACME Bank",
  "institutionCode": "031008"
}
```

```bash
TENANT=$(curl -sS -X POST $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"institutionName":"ACME Bank","institutionCode":"031008"}' \
  | jq -r .tenantId)
echo "TENANT=$TENANT"
# → TENANT=3f9b2c1e-....           (a fresh guid every time)
```

**Expected response — `201 Created`:**

```json
{
  "tenantId": "3f9b2c1e-4a2b-4c9d-8e9f-0a1b2c3d4e5f",
  "institutionName": "ACME Bank",
  "institutionCode": "031008",
  "status": "Pending",
  "isActive": true
}
```

If the `031008` row already exists from a previous run you'll get
**`409 Tenant invariant violated`** — that's the unique index on
`tenants.institution_code` doing its job. Either use a different
6-digit code or wipe (§3.5 / §14).

### 11.5 Step 3 — activate ACME Bank

```bash
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT/activate \
  -H "Authorization: Bearer $TOKEN" | jq
```

**Expected — `200 OK`:**

```json
{
  "tenantId": "3f9b2c1e-...",
  "institutionName": "ACME Bank",
  "institutionCode": "031008",
  "status": "Active",
  "isActive": true
}
```

DB confirmation:

```bash
$PSQL -c "SELECT institution_name, institution_code, status, is_active
          FROM tenants WHERE institution_code='031008';"
# → ACME Bank | 031008 | ACTIVE | t
```

### 11.6 Step 4 — provision ACME's OAuth configuration

```bash
CREDS=$(curl -sS -X POST $BASE/v1/admin/tenants/$TENANT/tenant-configuration \
  -H "Authorization: Bearer $TOKEN")
CLIENT_ID=$(echo "$CREDS" | jq -r .clientId)
CLIENT_SECRET=$(echo "$CREDS" | jq -r .clientSecret)
echo "clientId=$CLIENT_ID"
echo "clientSecret=$CLIENT_SECRET"   # copy NOW — shown exactly once
```

**Expected — `201 Created`:**

```json
{
  "tenantId": "3f9b2c1e-...",
  "credentialId": "8c1d9f02-...",
  "clientId": "031008-7f3a91c2",
  "clientSecret": "<43-char base64url string>",
  "expiresAt": "2027-09-06T09:15:00Z"
}
```

> Note `clientId` is `031008-7f3a91c2` — the **leading zero survives**
> in the institution prefix. A re-run with the same tenant id will get a
> **409** (one active configuration per tenant). The Argon2id hash of
> `clientSecret` is the only thing persisted.

### 11.7 Step 5 — mint ACME's tenant JWT

```bash
TENANT_TOKEN=$(curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=$CLIENT_ID \
  -d client_secret=$CLIENT_SECRET | jq -r .accessToken)
```

**Expected — `200 OK`**, body:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiJ9...",
  "tokenType":   "Bearer",
  "expiresIn":   600
}
```

Decoded claims (use the helper in §5.2):

| Claim | Value for ACME Bank |
|---|---|
| `sub` | `client:031008-7f3a91c2` |
| `scope` | `["qr:generate","qr:validate"]` — **never** `admin` |
| `tenant_id` | the lowercase guid of the `031008` tenant |
| `iss` / `aud` | `sbqr` / `sbqr-api` |

### 11.8 Step 6 — mint ACME's ACTIVE signing key

```bash
curl -sS -X POST $BASE/v1/crypto-keys \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"tenantId\":\"$TENANT\",\"mode\":\"Generate\"}" | jq
```

**Expected — `201 Created`:**

```json
{
  "cryptoKeyId": "b6e4f0aa-...",
  "tenantId": "3f9b2c1e-...",
  "keyId": "sbqr-signing",
  "keyVersion": 1,
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
  "status": "ACTIVE",
  "isActive": true,
  "createdAt": "2026-09-06T09:30:12Z"
}
```

The private half is sealed (AES-256-GCM with the `SBQRKEY1` magic
header, keyed off the deterministic dev KEK) into the local vault —
**never** on the wire, **never** in the DB. See §8.6 for where the
sealed blob lands on disk.

DB confirmation:

```bash
$PSQL -c "SELECT tenant_id, key_id, key_version, status, is_active
          FROM crypto_keys WHERE tenant_id='$TENANT';"
# → ACME Bank now has v1 ACTIVE.
```

### 11.9 Step 7 — generate a static QR for ACME Bank

> The host's `JwtClaimCurrentTenant` reads `tenant_id` from the
> `TENANT_TOKEN` minted in §11.7, so this curl is the live path —
> no test seam required. If you ever see `401 {"error":3,...}`
> here, the token you're using is the bootstrap admin one (which
> legitimately carries no `tenant_id`), not ACME's tenant token.

**Request body** (camelCase, exact wire format) — this is what a
real ACME Bank static-QR call looks like:

```json
{
  "recipientName": "Arif Mahmood",
  "recipientCity": "Dhaka",
  "recipientPan":  "01711111111",
  "postalCode":    "1212",
  "customerLabel": "ACME Bank",
  "purposeOfTransaction": "utility"
}
```

The corresponding curl:

```bash
IDEM=$(uuidgen | tr 'A-Z' 'a-z')    # optional, but recommended for retry safety

curl -sS -i -X POST $BASE/v1/qr/generate/static \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $IDEM" \
  -d '{
        "recipientName": "Arif Mahmood",
        "recipientCity": "Dhaka",
        "recipientPan":  "01711111111",
        "postalCode":    "1212",
        "customerLabel": "ACME Bank",
        "purposeOfTransaction": "utility"
      }'
```

**Expected — `201 Created`** with `Location: /v1/qr/generate/static`
and the following body. The payload-hash and the QR-string are the
values that the working integration test produces today
(`QrRoundTripTests.Register_issue_and_verify_the_full_round_trip`,
which already uses ACME Bank / `031008` as its fixture):

```json
{
  "qrPayload": "00020101021129300039...031008010802Arif Mahmood6006Dhaka6304A1B2",
  "payloadHash": "<64-char lowercase hex SHA-256 of qrPayload>",
  "qrType": "STATIC",
  "signatureKeyVersion": 1
}
```

Tag `01` reads `11` (static Point of Initiation Method). Tag `29`
embeds the institution prefix `031008` (id `031008`, type `03`,
composed inside the codec from the tenant row — never from the request
body). The QR string is returned **exactly once** and is never
persisted; only `payload_hash` lands in `qr_generations`.

### 11.10 Step 8 — generate a dynamic QR for ACME Bank

The dynamic endpoint adds the required `transactionAmount`:

```json
{
  "transactionAmount": "150.00",
  "recipientName": "Arif Mahmood",
  "recipientCity": "Dhaka",
  "recipientPan":  "01711111111",
  "postalCode":    "1212",
  "customerLabel": "ACME Bank",
  "purposeOfTransaction": "Invoice #4821"
}
```

```bash
curl -sS -i -X POST $BASE/v1/qr/generate/dynamic \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $IDEM-dyn" \
  -d '{
        "transactionAmount": "150.00",
        "recipientName": "Arif Mahmood",
        "recipientCity": "Dhaka",
        "recipientPan":  "01711111111",
        "postalCode":    "1212",
        "customerLabel": "ACME Bank",
        "purposeOfTransaction": "Invoice #4821"
      }'
```

**Expected — `201 Created`** with `"qrType":"DYNAMIC"`, Tag `01`
reading `12`, and Tag `54` carrying the amount `150.00`.

### 11.11 Step 9 — validate the QR (round-trip)

Paste the `qrPayload` returned by §11.9's `POST /v1/qr/generate/static`
into:

```bash
QR="<paste qrPayload from §11.9>"

curl -sS -X POST $BASE/v1/qr/validate \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"qrPayload\":\"$QR\"}" | jq
```

**Expected — `200 OK`** with:

```json
{
  "verdict": "Accept",
  "trustSource": "OWN_CUSTODY",
  "reasonCode": null,
  "institutionCode": "031008",
  "payloadHash": "<matches generation's payloadHash byte-for-byte>",
  "recipientName": "Arif Mahmood",
  "recipientPan": "01711111111",
  "qrClassification": "Static"
}
```

`trustSource: "OWN_CUSTODY"` is the path for ACME Bank's own
signatures — the verification module resolves the signing key from the
own tenant's `crypto_keys` row. `TRUST_DIRECTORY` is the path used
when the issuer is **not** one of our tenants (see QrRoundTripTests
step 6).

> Verdict semantics: a **`Reject`** verdict still returns **`200 OK`**
> (§11 of the QR validation controller XML doc: "the verdict IS the
> answer"). Only `400`/`401`/`403` indicate actual request failures.

### 11.12 Step 10 — DB evidence (ACME Bank only)

```bash
# 1. The tenant row.
$PSQL -c "SELECT institution_name, institution_code, status, is_active
          FROM tenants WHERE institution_code='031008';"

# 2. The credential row (Argon2id hash only — secret is not re-readable).
$PSQL -c "SELECT client_id,
                 left(client_secret_hash, 20) || '…' AS hash_prefix,
                 status, last_used_at, expires_at
          FROM public.tenant_configurations
          WHERE tenant_id='$TENANT';"

# 3. The signing key (public half only — private is sealed on disk).
$PSQL -c "SELECT key_id, key_version, status, is_active,
                 left(public_key, 40) || '…' AS pubkey_prefix
          FROM crypto_keys WHERE tenant_id='$TENANT';"

# 4. The generation row (payload_hash only — raw QR is NOT persisted).
$PSQL -c "SELECT qr_generation_id, qr_type, signature_key_version,
                 idempotency_key, created_at
          FROM qr_generations WHERE tenant_id='$TENANT';"

# 5. The audit trail.
$PSQL -c "SELECT event_type, resource_type, left(resource_id, 16) || '…' AS resource_prefix
          FROM audit_logs
          WHERE tenant_id='$TENANT'
             OR resource_id IN (SELECT payload_hash FROM qr_generations WHERE tenant_id='$TENANT')
          ORDER BY created_at;"
```

Expected event types for a clean ACME run: `auth.token.issued`
(bootstrap leg + tenant leg), `auth.client_credentials.issued`,
`tenant.configuration.provisioned`, `tenant.activated`,
`institution.trust.key.published`, `crypto_key.trust_publish_failed`
(if the BB mock is down — harmless), `qr.generated`. See §13 for the
full taxonomy.

### 11.13 Verifying the end-to-end path

Two equivalent ways to run the ACME scenario — pick whichever fits
your loop:

**A. Over real HTTP, using the reusable smoke script.**

```bash
# in one terminal: docker + api
pwsh docker/start-db.ps1
dotnet run --project src/Host/SBQR.Api

# in another:
pwsh scripts/smoke/test-acme-qr-generation.ps1
```

The script walks §§11.3–11.9, hits `POST /v1/qr/generate/static` with
the live JWT, then round-trips the returned `qrPayload` through
`POST /v1/qr/validate`, and asserts every step. Re-runnable — uses the
`-WipeDb` flag to drop the schema first if you want a clean slate.

**B. Over the integration test seam (in-process, no HTTP).**

```bash
dotnet test tests/SBQR.Qr.IntegrationTests/SBQR.Qr.IntegrationTests.csproj \
  --filter "FullyQualifiedName~QrRoundTripTests" -v normal
```

`QrRoundTripTests` uses ACME Bank / `031008` as its fixture tenant and
walks the same pipeline on real Postgres + real Ed25519 crypto. It
asserts (paraphrased; see the test file for the exact strings):

| # | Assertion |
|---|---|
| 1 | `tenant.IsSuccess` after `CreateTenantCommand("ACME Bank","031008")` |
| 2 | `minted.IsSuccess` after `GenerateOrAdoptCryptoKeyCommand(Mode=Generate)` |
| 3 | `generated.IsSuccess` after `GenerateStaticQrCommand(...)` and `qrPayload.StartsWith("0002")` |
| 4 | `verdict=VALID`, `trustSource=OWN_CUSTODY`, `institutionCode="031008"` |
| 5 | Externally-signed QR with ACME tenant's **own** key vs trust directory path (`verdict=VALID`, `trustSource=TRUST_DIRECTORY`) |
| 6 | Wrong key → `verdict=INVALID_SIGNATURE`, `reasonCode="SIGNATURE_MISMATCH"` |
| 7 | Tampered payload → `verdict=STRUCTURAL_INVALID`, `reasonCode="CRC_MISMATCH"` |
| 8 | Unknown issuer → `verdict=KEY_NOT_FOUND` |
| 9 | Both `qr.generated` and `qr.validated` audit rows present |

**Negative cases (fail-closed by construction).**

| Case | Status | Body |
|---|---|---|
| Bearer is the bootstrap/admin token (no `tenant_id` claim) | 401 | `{"error":3,"message":"QR generation requires an authenticated tenant request."}` |
| Bearer missing entirely | 401 | empty (challenge from the auth middleware, before the handler runs) |
| `tenant_id` claim present but the tenant doesn't exist in `tenants` | 404 | `{"error":4,"message":"Calling tenant does not exist or has no institution code registered."}` |
| `tenant_id` claim malformed (not a Guid) | 401 | same as the bootstrap path — the resolver returns `Guid.Empty` on parse failure |

### 11.14 Mapping curl → request → response for §11.9 (annotated)

```
POST /v1/qr/generate/static HTTP/1.1              ← path: /v1/{module}/...
Host:                  localhost:5001              ← Development Kestrel
Authorization:         Bearer <TENANT_TOKEN>      ← scope=qr:generate
Content-Type:          application/json           ← camelCase JSON body
Idempotency-Key:       <uuid>                     ← optional, ≤100 chars
                                                      unique per (tenant, key)

{                                                ← GenerateStaticQrRequest
  "recipientName": "Arif Mahmood",               ←   required, ≤25 bytes UTF-8
  "recipientCity": "Dhaka",                       ←   required, ≤15 bytes
  "recipientPan":  "01711111111",                 ←   required, ≤19 bytes
  "postalCode":    "1212",                       ←   optional, ≤10 bytes
  "customerLabel": "ACME Bank",                   ←   optional, ≤25 bytes
  "purposeOfTransaction": "utility"              ←   optional, ≤25 bytes
}                                                  (transactionAmount rejected)

HTTP/1.1 201 Created                             ← tenant_id claim from JWT
                                                      is resolved into
                                                      ICurrentTenant by
                                                      JwtClaimCurrentTenant
                                                      (Program.cs:299)
Location: /v1/qr/generate/static
Content-Type: application/json

{                                                ← GenerateQrResponse
  "qrPayload":            "000201010211…6304A1B2",←  BanglaQR P2P string;
                                                      Tag 01="11"=static;
                                                      Tag 29 encodes "031008"
  "payloadHash":          "9f2c4e7b…e1",          ←  SHA-256 hex, only persistent
  "qrType":               "STATIC",               ←  PascalCase row enum mirror
  "signatureKeyVersion":  1                       ←  mirrors crypto_keys row
}
```

---

## 12. End-to-end golden journey

Everything above as one continuous happy-path script (Git Bash, fresh DB).
Mind the **10 token-requests/minute** limiter if you re-run it in a loop.

```bash
BASE=http://localhost:5001
PSQL="docker compose -f docker/docker-compose.yml exec -T sbqr.postgres psql -U postgres -d sbqr_app"

# 0. fresh DB (see §3) and API running (see §4), then:
TOKEN=$(curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret=dev-only-bootstrap-secret | jq -r .accessToken)

# 1. register + activate tenant
TENANT=$(curl -sS -X POST $BASE/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"Golden Journey Bank","institutionCode":"100404"}' | jq -r .tenantId)
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT/activate -H "Authorization: Bearer $TOKEN" >/dev/null

# 2. provision configuration (secret shown once!)
CREDS=$(curl -sS -X POST $BASE/v1/admin/tenants/$TENANT/tenant-configuration -H "Authorization: Bearer $TOKEN")

# 3. tenant leg of OAuth
TENANT_TOKEN=$(curl -sS -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=$(echo "$CREDS" | jq -r .clientId) \
  -d client_secret=$(echo "$CREDS" | jq -r .clientSecret) | jq -r .accessToken)

# 4. mint + rotate signing key
curl -sS -X POST $BASE/v1/crypto-keys -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"tenantId\":\"$TENANT\",\"mode\":\"Generate\"}" | jq '{keyVersion,status}'   # v1 ACTIVE
curl -sS -X PUT $BASE/v1/crypto-keys/$TENANT -H "Authorization: Bearer $TOKEN" \
  | jq '{keyVersion,status}'                                                        # v2 ACTIVE

# 5. suspend → tenant token dies, key suspends; reactivate → everything returns
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT/suspend -H "Authorization: Bearer $TOKEN" >/dev/null
curl -sS -o /dev/null -w 'suspended-credential-token → %{http_code}\n' \
  -X POST $BASE/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=$(echo "$CREDS" | jq -r .clientId) \
  -d client_secret=$(echo "$CREDS" | jq -r .clientSecret)     # → 401
curl -sS -X POST $BASE/v1/admin/tenants/$TENANT/reactivate -H "Authorization: Bearer $TOKEN" >/dev/null

echo "TENANT=$TENANT"
$PSQL -c "SELECT status FROM tenants WHERE tenant_id='$TENANT';"                                   # ACTIVE
$PSQL -c "SELECT status FROM public.tenant_configurations WHERE tenant_id='$TENANT';"            # ACTIVE
$PSQL -c "SELECT key_version, status FROM crypto_keys WHERE tenant_id='$TENANT' ORDER BY 1;"       # 2 ACTIVE / 1 RETIRED
```

---

## 13. Audit-trail expectations

After running §§5–9, `audit_logs` should contain these `event_type` values
(and **only** these — for orientation):

| `event_type` | Written when |
|---|---|
| `auth.token.issued` | every successful `/v1/oauth/token` (bootstrap **and** tenant legs) |
| `auth.client_credentials.issued` | `POST /admin/tenants/{id}/tenant-configuration` (IdentityAccess side) |
| `tenant.configuration.provisioned` | same endpoint (Tenancy side; metadata carries `client_id`, never the secret) |
| `tenant.activated` / `tenant.suspended` / `tenant.reactivated` / `tenant.terminated` | tenant lifecycle transitions |
| `tenant.credentials.cascade.suspended` / `tenant.credentials.cascade.reinstated` | configuration cascade on suspend/reactivate/terminate |
| `tenant_configuration.suspended` / `tenant_configuration.reactivated` | IdentityAccess view of the same cascade |
| `crypto_key.suspended` / `crypto_key.reinstated` | signing-key cascade on tenant suspend/reactivate |
| `institution.trust.key.published` | every successful `POST /v1/crypto-keys` (auto-publish into the trust directory) **and** the manual `POST /v1/admin/institutions` path |
| `crypto_key.trust_publish_failed` | `POST /v1/crypto-keys` succeeded locally but the trust-directory publish failed; the key row is preserved (operator recovers with `POST /v1/admin/institutions`) |
| `qr.generated` | every successful `POST /v1/qr/generate/static` or `POST /v1/qr/generate/dynamic` (§9); `resource_id` is the payload hash |
| `qr.validated` | every successful `POST /v1/qr/validate` (§10), **including rejection verdicts**; `resource_id` is the validated payload's hash, `verdict`/`trustSource`/`reasonCode` live in the metadata |

Deliberately **absent**: tenant **registration**, and crypto-key
**generate/adopt/rotate** (their trail lives in the rows' own audit columns).
Failed token attempts, failed QR-generation attempts, and request-shape
failures on `/v1/qr/validate` (400/401/403 — empty payload, wrong scope,
no bearer) are not audited. Rejection verdicts on `/v1/qr/validate` *are*
audited because they are normal 200 responses.
InstitutionTrust sync rows may also appear (see §4 note) — unrelated.

```bash
$PSQL -c "SELECT event_type, count(*) FROM audit_logs
           GROUP BY event_type ORDER BY event_type;"
```

---

## 14. Reset / cleanup

```bash
# tables only (fast, repeatable) → then re-run the external migration tool
$PSQL -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;
          DROP SCHEMA IF EXISTS identity CASCADE;"
# then re-run the external migration tool from §3.2

# everything, including the databases and their volumes
docker compose -f docker/docker-compose.yml down -v
pwsh docker/start-db.ps1
# then re-run the external migration tool from §3.2

# stop for the day (keep data)
docker compose -f docker/docker-compose.yml stop
```

Remember: the **bootstrap credential survives resets** (config-held);
**tenant configurations do not** (rows in `public.tenant_configurations`).
Sealed vault files survive too (§8.6) — delete the directory for a clean slate.

---

## 15. Troubleshooting

### "404 on every call even though the API is up"
Missing the `/v1/` URL segment (e.g. `POST /admin/tenants`). Versioning is
URL-segment-only; unversioned or undeclared versions 404. Check
`GET /admin/_routes` (dev) for the live patterns.

### "401 with a perfectly good token"
Tokens expire after **10 minutes** — re-mint (§5.1). Also check you're not
sending a *tenant* token to an *admin* endpoint (that's 403, not 401).

### "429 with an empty body from /v1/oauth/token"
The per-IP fixed-window limiter (10/min). Wait a minute or space out token
calls; only the token endpoint is limited.

### "The token response has accessToken, not access_token"
Correct — camelCase is the live wire format (§5.1 note). Docs showing
snake_case are stale on this point.

### "409 on POST /admin/tenants"
Duplicate `institutionCode` (unique per the `ix_tenants_institution_code`
index). Use a different 6-digit code or reset the DB (§14).

### "409 on POST /admin/tenants/{id}/tenant-configuration"
Either the tenant is `Suspended`/`Terminated`, or an active configuration
already exists (one per tenant; rotation not implemented).

### "409 on POST /crypto-keys"
The tenant already has an ACTIVE key — rotate via `PUT /v1/crypto-keys/{tenantId}`.

### "POST /v1/crypto-keys returns 201 but the S3 bucket has no key"
The host didn't pick up the S3 vault provider. The most common cause is
launching the prebuilt `SBQR.Api.exe` directly — `launchSettings.json`
profiles only apply to `dotnet run` and IDE launches. Stop the host
(`Stop-Process -Name SBQR.Api -Force`) and re-launch via `dotnet run
--launch-profile S3-LocalStack` (§4.1.2). Telltale: the **file** vault at
`%LOCALAPPDATA%\sbqr\key-vault` grew a new file at the same time — that
proves the running host was on `Crypto:VaultProvider=Local`. See
[§4.1.5](#415-when-the-bucket-stays-empty) for the full diagnostic.

### "409 on POST /v1/crypto-keys with 'trust-store publish failed'"
The crypto-key row was created (vault blob committed) but the trust-directory
publish failed. The audit log has a `crypto_key.trust_publish_failed` row
with the reason. Recover with one manual call:

```bash
curl -X POST http://localhost:5080/v1/admin/institutions \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
        \"institutionCode\": \"010101\",
        \"institutionName\": \"<name>\",
        \"publicKeyPem\": \"<pem from GET /v1/crypto-keys/{tenantId}/active>\"
      }"
```

The same `409` body explains this in its `error` field.

### "POST /v1/qr/generate/{static,dynamic} returns 401 with a fresh tenant token"
The token you're sending is the **bootstrap / admin** token from §5.1,
not ACME's tenant token from §7.4. Bootstrap tokens deliberately carry
no `tenant_id` claim; the resolver returns `Guid.Empty`; the handler
rejects with `401 {"error":3,"message":"QR generation requires an authenticated tenant request."}`.
Re-mint the tenant token from the `clientId`/`clientSecret` returned by
§7.1's `POST /v1/admin/tenants/{id}/tenant-configuration`. Decode the
token (§5.2) and confirm `scope` contains `qr:generate` and the `tenant_id`
claim is the tenant you intend.

### "422 KEY_NOT_ACTIVE on POST /v1/qr/generate/{static,dynamic}"
The calling tenant has no ACTIVE signing key — mint one first (§8.1), or
if it was suspended via a tenant-suspend cascade (§8.7), reactivate the
tenant (§6.5) to bring the key back to ACTIVE.

### "409 DUPLICATE_IDEMPOTENCY_KEY on POST /v1/qr/generate/{static,dynamic}"
The `Idempotency-Key` header value was already used by this tenant
(unique per `(tenant_id, idempotency_key)`, §9.5). Use a new value, or omit
the header if you don't need replay protection for that call.

### "400 'Static QR does not accept transactionAmount' on /qr/generate/static"
You sent `transactionAmount` on the static endpoint. Static QRs must not
encode the amount (the payer decides it at scan time) — use
`/v1/qr/generate/dynamic` instead.

### "400 'TransactionAmount must not be empty' on /qr/generate/dynamic"
`transactionAmount` is required on the dynamic endpoint. Either provide it
or use `/v1/qr/generate/static` if you don't want to bake an amount into
the QR.

### "Tables don't exist / relation does not exist"
Migrations didn't run. Re-run the external migration tool (§3.2) and
confirm the tool's bookkeeping table reports 14 applied migrations (§3.4).
Check the `ConnectionStrings:sbqr_app` in your user-secrets
(`dotnet user-secrets list --project src/Host/SBQR.Api/SBQR.Api.csproj`)
actually points at your Postgres (`localhost:5432`, `postgres/postgres`).

### "Connection refused on localhost:5432"
Postgres container isn't up: `pwsh docker/start-db.ps1` (or
`docker compose -f docker/docker-compose.yml up -d sbqr.postgres`), then
re-run the external migration tool (§3.2).

### "Periodic 'Trust store fetch failed' warnings"
InstitutionTrust's daily sync pointing at a mock that isn't running —
harmless for this guide (§4 note). Start the mock or ignore.

### "Wrong port"
`dotnet run` (Development) → `5001` (from `Properties/launchSettings.json`);
docker `sbqr.api` → `8080` (from `ASPNETCORE_URLS` in `docker/Dockerfile.api`,
overridable via `Kestrel__Endpoints__Http__Url` in `docker/.env`). If you run
the published DLL directly without any of those, the host falls back to Kestrel's
own default (`http://localhost:5000`) — set `ASPNETCORE_URLS` to pin a port.
