# Developer Testing Guide — `BbTrustStoreMock.Api` + `InstitutionTrust`

> **⚠ SCHEMA CHANGE 2026-09-08 — some SQL in this guide is stale.**
> The canonical schema is now [`db/migrations/`](../db/migrations/README.md)
> (one PostgreSQL schema per module — see
> [`docs/design/database-design.md`](design/database-design.md)). Tables are
> schema-qualified: `public.institution_registries`,
> `public.institution_keys` (now with `key_version`, `source`,
> `status`, `valid_from/valid_to/revoked_at`, `synced_at`),
> `public.trust_sync_runs`. Query `public.audit_logs` (not
> `public.audit_logs`) — it is append-only and hash-chained.

This guide walks a developer through everything they need to **run, observe,
and test** the BB Trust-Store mock and the `InstitutionTrust` sub-domain
locally, including the full flow from "an institute registers" to "the
public key lands in the local trust directory".

---

## Table of contents

1. [Mental model](#1-mental-model)
2. [Repository layout](#2-repository-layout)
3. [Prerequisites](#3-prerequisites)
4. [Approach A — Run the full stack with docker-compose (recommended)](#4-approach-a--run-the-full-stack-with-docker-compose-recommended)
5. [Approach B — Run pieces individually with `dotnet run`](#5-approach-b--run-pieces-individually-with-dotnet-run)
6. [The end-to-end happy path](#6-the-end-to-end-happy-path-step-by-step)
7. [Failure scenarios worth exercising by hand](#7-failure-scenarios-worth-exercising-by-hand)
8. [Automated test suite](#8-automated-test-suite)
9. [Inspecting state in PostgreSQL](#9-inspecting-state-in-postgresql)
10. [Useful log lines & OpenAPI surfaces](#10-useful-log-lines--openapi-surfaces)
11. [Reset / cleanup](#11-reset--cleanup)
12. [Troubleshooting](#12-troubleshooting)

---

## 1. Mental model

Two independent processes communicate over HTTP. They are deployed
separately, and each owns its OWN database — SBQR.Api writes
`sbqr_app`, the mock persists its uploaded keys in the disposable
`bb_trust_store_mock` database (same dev Postgres container, zero shared
tables; the mock's DB is dropped when the real BB trust store lands).

```
┌──────────────────────────────┐                    ┌──────────────────────────────┐
│   BbTrustStoreMock.Api       │  HTTP GET          │   SBQR.Api  (src/Host)       │
│   src/Host/BbTrustStoreMock  │ /trust-store/      │                              │
│                              │   institutions     │   InstitutionTrustModule     │
│   ─ bb_trust_store_mock DB ─►├───────────────────►│     │                        │
│   ─ Refuses to run in ───    │                    │     ▼                        │
│     Production ──────────────┤                    │   ITrustStoreClient         │
└──────────────────────────────┘                    │     └─ HttpTrustStoreClient  │
            ▲                                       │                              │
            │  HTTP PUT (spec-shaped upload)        │   DailyTrustSyncService     │
            │  /trust-store/institutions/{id}/      │     └─► InstitutionUpsertService
            │    public-key                         │
            │                                       │          (folds the per-tick │
            │                                       │           upsert loop inline)│
            │                                       │                └─► EF Core → PostgreSQL
            │                                       └──────────────────────────────┘
```

Three entry points matter for testing:

| Entry point | Who calls it | What it proves |
|---|---|---|
| `PUT /trust-store/institutions/{id}/public-key` on the **mock** | Developer / test code | "Upload an institution's public key into the mock trust store" (spec Annex B/C shape) |
| `POST /v1/admin/institutions` on **SBQR.Api** | Operator (manual fallback) | "Manually upsert an institution + key into the local trust directory" |
| `GET /trust-store/institutions` on the **mock** | `HttpTrustStoreClient` (every `DailyTrustSyncService` tick) | "Here's everything the mock currently believes" |

Everything else (`GET /trust-store/institutions/{id}/public-key` on the mock,
`GET .../public-key` query on the host, the external migration tool, OpenAPI
docs, `/health/*`) is either symmetrical, observability, or plumbing.

---

## 2. Repository layout (only the parts you need)

```
src/
  Host/BbTrustStoreMock.Api/        ← the mock (separate deployable)
    Program.cs                       ← refuses to run in Production; EnsureCreates the schema
    TrustStoreRepository.cs          ← upsert/revoke/read semantics (idempotent uploads)
    Persistence/TrustStoreDbContext.cs ← EF Core over bb_trust_store_mock
    Validation/SeedInstitutionRequestValidator.cs ← A6-style upload rules (Ed25519 SPKI PEM only)
    Models.cs                        ← DTOs (Institution, Key)
    Controllers/
      TrustStoreController.cs        ← GET endpoints (BB-shaped)
      AdminController.cs             ← POST revoke (mock-only control surface)

  Host/SBQR.Api/                     ← the platform host
    Program.cs                       ← wires modules + middleware

  Modules/InstitutionTrust/
    SBQR.Modules.InstitutionTrust.Api/         ← InstitutionTrustModule (composition root)
      Controllers/InstitutionsController.cs    ← POST /v1/admin/institutions
    SBQR.Modules.InstitutionTrust.Application/ ← write/read logic
      Services/InstitutionUpsertService.cs     ← THE one write path
      Services/DailyTrustSyncService.cs        ← singleton hosted service; PeriodicTimer (24h default)
      Commands/UpsertInstitutionCommand.cs     ← MediatR command
      Queries/GetInstitutionPublicKeyQueryHandler.cs
    SBQR.Modules.InstitutionTrust.Infrastructure/
      TrustStore/ITrustStoreClient.cs          ← THE port
      TrustStore/HttpTrustStoreClient.cs       ← THE only adapter today
      TrustStore/TrustStoreOptions.cs
      Persistence/InstitutionTrustDbContext.cs

tests/
  SBQR.BbTrustStoreMock.Tests/              ← in-process WebApplicationFactory
  SBQR.Modules.InstitutionTrust.Tests/      ← pure unit, stubbed HttpMessageHandler
  SBQR.InstitutionTrust.IntegrationTests/   ← PostgreSQL (Testcontainers) + fake ITrustStoreClient

db/migrations/
  20260905120000_create_institution_trust_tables.sql

docker/
  docker-compose.yml                       ← spins up postgres + mock + api + adminer
  Dockerfile.api
  Dockerfile.bb-trust-store-mock
  postgres/init/00-create-databases.sql
  .env.example
```

---

## 3. Prerequisites

| Tool | Version | Why |
|---|---|---|
| .NET SDK | 10.0 (see `global.json`) | build / run / test |
| Docker Desktop / Docker Engine | any recent | the full-stack path |
| `curl` + `jq` | any | manual API poking |
| A POSIX shell or PowerShell | any | examples below use POSIX; PowerShell equivalents are noted |

Confirm dotnet:
```bash
dotnet --version        # should match the rollForward in global.json
```

Confirm docker:
```bash
docker --version
docker compose version
```

---

## 4. Approach A — Run the full stack with docker-compose (recommended)

This is the path that exercises the **whole loop end to end**.

### 4.1 Boot

From repo root:

```bash
docker compose -f docker/docker-compose.yml up --build -d
```

This brings up four containers:

| Container | Host port | Purpose |
|---|---|---|
| `sbqr.postgres` | `5432` | one Postgres 16 with two DBs (`sbqr_app`, `sbqr_key_vault`) |
| `sbqr.bb-trust-store-mock` | `8082` | the BB trust-store mock |
| `sbqr.api` | `8080` | the SBQR platform |
| `sbqr.adminer` | `8081` | web UI for ad-hoc DB inspection |

### 4.2 Confirm everything is healthy

```bash
docker compose -f docker/docker-compose.yml ps

# Mock trust store
curl -sS http://localhost:8082/health/live || true
# (mock has no /health, but the empty list is a good liveness signal)
curl -sS http://localhost:8082/trust-store/institutions
# → []

# SBQR.Api
curl -sS http://localhost:8080/health/live
# → {"status":"live"}
curl -sS http://localhost:8080/health/ready
# → {"status":"ready"}

# Adminer
# Open http://localhost:8081 in a browser
#   System: PostgreSQL
#   Server: sbqr.postgres
#   Username: postgres
#   Password: postgres
#   Database: sbqr_app
```

### 4.3 Verify `TrustStore:BaseUrl` was actually injected

The SBQR.Api container **must** see the mock's DNS name, not `localhost`
(it runs in the `sbqr-net` Docker network):

```bash
docker compose -f docker/docker-compose.yml exec sbqr.api env | grep -i truststore
# TrustStore__BaseUrl=http://bb-trust-store-mock:8080
# TrustStore__SyncIntervalHours=24
```

### 4.4 Shorten the sync interval for faster manual testing

The default is 24 hours. Override per-run (clamped to a minimum of 1 hour):

```bash
docker compose -f docker/docker-compose.yml up -d \
  --force-recreate \
  -e TrustStore__SyncIntervalHours=1 \
  sbqr.api
```

Now the sync ticks every hour.

### 4.5 Apply DB migrations (one-time, or after a schema change)

The API container does NOT apply the schema. Run the external migration
tool against `sbqr_app` and `sbqr_key_vault` (see
`docs/docker/DEPLOYMENT.md` §4) before starting the API. You should see
one migration line apply:
- `20260905120000_create_institution_trust_tables.sql`
- (plus the audit migration `20260904120000_create_audit_logs_table.sql`,
  which is what `DailyTrustSyncService` writes per-run summary rows to)

There is no longer a `Database:RunMigrationsOnStartup` setting — the API
image is migration-free by design.

then re-up:

```bash
docker compose -f docker/docker-compose.yml up -d --force-recreate sbqr.api
```

---

## 5. Approach B — Run pieces individually with `dotnet run`

Useful when you want to step through the mock with a debugger, or when you
don't want to drag docker in.

### 5.1 Start PostgreSQL only

Either:

- **Docker:** `docker compose -f docker/docker-compose.yml up -d sbqr.postgres`
- **Local install:** run `docker/start-db.ps1` (Windows) or its equivalent
  on Linux, then make sure `sbqr_app` and `sbqr_key_vault` exist.

### 5.2 Run the mock

The mock needs its database: start the compose Postgres (step 5.1) — on an
older volume `docker/start-db.ps1` creates `bb_trust_store_mock` for you if
it's missing (the init script itself only runs on a fresh volume). Seed the
mock's user-secrets connection string once via
`scripts/dev-seed-user-secrets.ps1` (or `.sh`), then:

```bash
dotnet run --project src/Host/BbTrustStoreMock.Api
# → Now listening on: http://localhost:8082  (launchSettings; same port the
#   compose service publishes, so TrustStore:BaseUrl is identical either way)
```

Verify:
```bash
curl -sS http://localhost:8082/trust-store/institutions
# → []
```

### 5.3 Run the SBQR.Api host

In another shell:
```bash
dotnet run --project src/Host/SBQR.Api
```

For the API to find the mock, the host's `TrustStore:BaseUrl` must point
to wherever the mock is actually listening. The seed script
(`scripts/dev-seed-user-secrets.ps1` / `.sh`) already sets it — along with
the dev sync cadence (`SyncOnStartup: true`, `SyncIntervalMinutes: 1`) —
into user-secrets, which layer on top of `appsettings.json` in
Development. To change any of them:

```bash
dotnet user-secrets set "TrustStore:BaseUrl" "http://localhost:8082" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
```

Or use an environment variable:

```bash
export TrustStore__BaseUrl=http://localhost:5000
export TrustStore__SyncIntervalHours=1
dotnet run --project src/Host/SBQR.Api
```

---

## 6. The end-to-end happy path, step by step

The four steps below correspond to the four operations in the design doc
("institute registers → key pair created → key uploaded to trust store →
local DB synced"). Each step lists what to observe.

> **Note:** The mock simulates "BB already accepted the institution and its
> key". In production, "institute registers" and "key pair created" happen
> inside the institute's environment (and only the **public** key travels
> to BB). Here, you upload the public key via
> `PUT /trust-store/institutions/{id}/public-key` on the mock (the
> spec-shaped "share your public key with BB" route), then watch
> `DailyTrustSyncService` pull that record down into the local trust
> directory.

### Step 1 — Generate a key pair (offline)

You only need a public-key PEM to seed the mock. The simplest way to get a
real Ed25519 SPKI PEM is `openssl`:

```bash
openssl genpkey -algorithm ed25519 -out /tmp/inst.key
openssl pkey -in /tmp/inst.key -pubout -out /tmp/inst.pub
cat /tmp/inst.pub
```

This gives you a block that starts with `-----BEGIN PUBLIC KEY-----`. Save
it in a shell variable so the curl body stays readable:

```bash
PUBKEY=$(cat /tmp/inst.pub)
```

### Step 2 — Upload the public key into the **mock** trust store

```bash
curl -sS -i -X PUT http://localhost:8082/trust-store/institutions/031008/public-key \
  -H 'Content-Type: application/json' \
  -d "$(jq -n \
        --arg name 'Example Bank' \
        --arg pem "$PUBKEY" \
        '{institutionName:$name, publicKeyPem:$pem}')"
```

Expected response:

```
HTTP/1.1 201 Created
```

Verify the mock can serve it back:

```bash
# Full list
curl -sS http://localhost:8082/trust-store/institutions | jq

# Single lookup
curl -sS http://localhost:8082/trust-store/institutions/031008/public-key | jq
```

Both should show one institution with `status: "ACTIVE"`, `keyVersion: 1`,
and the SHA-256 of the PEM you posted.

### Step 3 — Wait for (or trigger) the sync tick

If you shortened `SyncIntervalHours` to `1` (Approach A §4.4) or `1`
(Approach B §5.3), wait up to one hour.

In the SBQR.Api container, watch for the log lines:

```bash
docker compose -f docker/docker-compose.yml logs -f sbqr.api | grep -iE "trust-sync|6101|6105|6107"
```

Or look at the raw logs:

```bash
docker compose -f docker/docker-compose.yml logs sbqr.api
```

A successful tick produces **no errors**; on the next tick boundary the
host emits a `institution.trust.sync.completed` row in `audit_logs`
(see Step 4b).

### Step 4 — Verify the local DB has the key

Pick one of three ways.

**(a) Adminer:** open `http://localhost:8081` → log in with
`postgres/postgres`, database `sbqr_app`. Run:

```sql
SELECT * FROM institution_registries;
SELECT institution_code, key_version, status, public_key_sha256
FROM   institution_keys
ORDER  BY institution_code, key_version;

-- Per-run summary emitted by DailyTrustSyncService
SELECT created_at, actor_id, resource_id, metadata
FROM   audit_logs
WHERE  event_type = 'institution.trust.sync.completed'
ORDER  BY created_at DESC
LIMIT  5;
```

You should see:
- one row in `institution_registries`: `(031008, "Example Bank", ACTIVE)`
- one row in `institution_keys`: `(031008, 1, ACTIVE, <sha256>)`
- one row in `audit_logs` with `event_type = 'institution.trust.sync.completed'`,
  `actor_id = 'system:trust-sync'`, `resource_type = 'trust-sync'`, and
  `metadata ->> 'institutions_synced' = '1'`. Failures show up as
  `metadata ->> 'error'` populated.

**(b) psql in the running container:**

```bash
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "SELECT institution_code, institution_name, status FROM institution_registries;"
```

**(c) The dev-only route manifest** (proves the controller is live):

```bash
curl -sS http://localhost:8080/admin/_routes | jq '.routes[] | select(.Pattern | contains("institutions"))'
```

### Step 5 — Test the read path (cross-module contract)

The cross-module read path is `GetInstitutionPublicKeyQuery` (MediatR). It
is wired in via DI; the simplest way to exercise it from a developer's
machine is to call it from a one-off test program or from
`SBQR.Modules.Verification.Tests`. The integration test
`HttpTrustStoreClientTests` exercises the upstream side; the read side is
exercised in `InstitutionUpsertServiceTests`.

For a manual smoke, add an instrumentation endpoint **only in dev** (not
shown here), or run a small console harness against the real DbContext —
the read query is a single EF Core query, so any quick repro will reveal
its shape.

The contract callers should rely on:

```csharp
public sealed record InstitutionPublicKeyView(
    string InstitutionCode,   // "031008"
    int    KeyVersion,        // 1
    string PublicKeyPem,      // SPKI PEM block
    string Status);           // "ACTIVE"
```

`null` means "institution not in directory → verifier fails closed".

### Step 6 — Revocation propagation

This is the most important guarantee in the system: a revocation **must**
propagate. The mock seeds `ACTIVE`, the host retires the local key, and no
replacement is published.

```bash
# Revoke on the mock
curl -sS -i -X POST http://localhost:8082/admin/institutions/031008/keys/revoke
# → 204 No Content

# Confirm the mock still lists it (with status=REVOKED, not dropped)
curl -sS http://localhost:8082/trust-store/institutions | jq '.[] | select(.institutionId=="031008")'
# → status: "ACTIVE", activeKey.status: "REVOKED"

# Confirm the single-lookup 404s
curl -sS -i http://localhost:8082/trust-store/institutions/031008/public-key | head -1
# → HTTP/1.1 404 Not Found

# Wait for the next tick (≤ SyncIntervalHours)
sleep 3601

# Verify in the DB
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "SELECT institution_code, key_version, status FROM institution_keys;"
```

Expected:
- The `031008` key that was `ACTIVE` is now `RETIRED`.
- **No new row was inserted** — the registry still has 1 key for `031008`,
  not 2.
- `institution_registries` is **untouched** — revocation retires the key,
  not the institution.
- The latest `institution.trust.sync.completed` row in `audit_logs` is
  accompanied by an `institution.trust.key.revoked` row (revocation is
  not a failure).

### Step 7 — Manual upsert fallback (proves the two paths coexist)

```bash
# Hit SBQR.Api's own /v1/admin/institutions with an institution the mock does NOT know about
PUBKEY2=$(openssl genpkey -algorithm ed25519 | openssl pkey -pubout)

curl -sS -i -X POST http://localhost:8080/v1/admin/institutions \
  -H 'Content-Type: application/json' \
  -d "$(jq -n \
        --arg id '031999' \
        --arg name 'Manual Bank' \
        --arg pem "$PUBKEY2" \
        '{institutionCode:$id, institutionName:$name, publicKeyPem:$pem}')"
```

This endpoint is gated by `[Authorize(Policy = PolicyNames.AdminCredentialTree)]`
so you'll need a valid admin JWT. For dev convenience, mint one through
the platform bootstrap client (see the README and the `--generate-bootstrap-secret`
CLI in `SBQR.Api/Program.cs`):

```bash
# 1) Generate a bootstrap secret (one-shot, prints once)
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret

# 2) Exchange it for an admin-scoped token at POST /v1/oauth/token
curl -sS -X POST http://localhost:5080/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret=<the-secret-you-just-saved>
```

(Exact URLs and headers depend on the `Jwt__*` configuration in your
user-secrets / environment — see the README.)

Then verify:

```sql
SELECT institution_code, institution_name, status
FROM   institution_registries
WHERE  institution_code = '031999';
```

After the next sync tick, the row remains (the mock only knows about
`031008`, so `031999` is left untouched — proves the non-destructive sync).

---

## 7. Failure scenarios worth exercising by hand

These map directly to the negative-path tests in
`tests/SBQR.InstitutionTrust.IntegrationTests/DailyTrustSyncServiceTests.cs`.

### 7.1 Trust store unreachable

```bash
docker compose -f docker/docker-compose.yml stop bb-trust-store-mock
# Wait for one sync tick…
docker compose -f docker/docker-compose.yml logs sbqr.api | grep -i "trust store fetch"
# → "Trust store fetch failed; leaving existing directory data untouched." (EventId 6101)

docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "SELECT created_at, metadata FROM audit_logs WHERE event_type = 'institution.trust.sync.completed' ORDER BY created_at DESC LIMIT 1;"
# → metadata LIKE '%error%' (with the connection error message) and institutions_synced = 0
```

Then restart the mock and watch the next tick recover:
```bash
docker compose -f docker/docker-compose.yml start bb-trust-store-mock
```

### 7.2 Mock returns a malformed record

You can't easily inject malformed JSON into the mock (its DTOs are typed
and validated), so exercise this via the integration tests:

```bash
dotnet test tests/SBQR.InstitutionTrust.IntegrationTests/ \
  --filter "FullyQualifiedName~DailyTrustSyncServiceTests.An_invalid_record_is_skipped"
```

This proves a seven-digit institution code from the trust store is
rejected without taking down the batch.

### 7.3 Misconfigured `TrustStore:BaseUrl`

```bash
docker compose -f docker/docker-compose.yml up -d \
  --force-recreate -e TrustStore__BaseUrl= sbqr.api
# → container refuses to start; exits with the
#    "TrustStore:BaseUrl is required" error from ValidateOnStart.
```

```bash
docker compose -f docker/docker-compose.yml up -d \
  --force-recreate -e TrustStore__BaseUrl=not-a-url sbqr.api
# → same: "TrustStore:BaseUrl must be an absolute URI"
```

### 7.4 Production guard on the mock itself

```bash
dotnet run --project src/Host/BbTrustStoreMock.Api --environment Production
# → unhandled InvalidOperationException at startup; the mock refuses to run.
```

This guards against accidentally pointing a production deployment at the
mock via a leftover config.

---

## 8. Automated test suite

Three test projects cover the system from three angles.

```bash
# 1. Mock's own controllers — WebApplicationFactory<Program>
dotnet test tests/SBQR.BbTrustStoreMock.Tests/

# 2. HttpTrustStoreClient — pure unit, stubbed HttpMessageHandler
dotnet test tests/SBQR.Modules.InstitutionTrust.Tests/

# 3. DailyTrustSyncService + InstitutionUpsertService — PostgreSQL Testcontainers + NSubstitute fake
dotnet test tests/SBQR.InstitutionTrust.IntegrationTests/
```

### 8.1 What each project proves

| Project | Angle | Key tests |
|---|---|---|
| `SBQR.BbTrustStoreMock.Tests` | The mock serves the shape `HttpTrustStoreClient` expects | `Seeding_then_listing_shows_the_institution_with_its_active_key`, `Revoking_the_active_key_makes_the_single_lookup_404`, `Revoking_the_active_key_still_lists_the_institution_with_a_revoked_key` |
| `SBQR.Modules.InstitutionTrust.Tests` | The client deserializes correctly and is robust against a record with no key | `FetchAllAsync_maps_the_trust_store_list_response`, `FetchAllAsync_skips_a_record_with_no_key_instead_of_failing_the_batch` |
| `SBQR.InstitutionTrust.IntegrationTests` | `DailyTrustSyncService` upserts, isolates failures, propagates revocation, emits per-run audit rows | `A_successful_fetch_upserts_every_institution...`, `A_failed_fetch_leaves_existing_data_untouched...`, `An_invalid_record_is_skipped...`, `A_database_level_failure_does_not_poison_the_rest_of_the_batch`, `A_revoked_key_retires_the_local_active_key...` |

### 8.2 Running one specific test

```bash
dotnet test tests/SBQR.InstitutionTrust.IntegrationTests/ \
  --filter "FullyQualifiedName~DailyTrustSyncServiceTests.A_revoked_key_retires"
```

### 8.3 The Testcontainers quirk

`SBQR.InstitutionTrust.IntegrationTests` spins up an ephemeral
`postgres:16-alpine` container per test session and applies the SQL
migrations from `db/migrations/` on it. Tests are **serialized** via
`[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]`
to keep Respawn's checkpoints safe. Don't enable parallelization for
this assembly — it will corrupt test isolation.

---

## 9. Inspecting state in PostgreSQL

### 9.1 The three tables that matter

```sql
-- 1. The local trust directory (the platform's mirror of the trust store)
SELECT * FROM institution_registries;
SELECT * FROM institution_keys;

-- 2. The audit trail of every sync tick (one row per DailyTrustSyncService run)
SELECT created_at, actor_id, resource_id, metadata
FROM   audit_logs
WHERE  event_type = 'institution.trust.sync.completed'
ORDER  BY created_at DESC
LIMIT  10;

-- 3. The "at-most-one ACTIVE key per institution" invariant
SELECT institution_code, COUNT(*) AS active_keys
FROM   institution_keys
WHERE  status = 'ACTIVE' AND is_active = TRUE
GROUP  BY institution_code
HAVING COUNT(*) > 1;
-- Should always return zero rows.
```

### 9.2 Helpful join

```sql
SELECT r.institution_code,
       r.institution_name,
       r.status              AS registry_status,
       k.key_version,
       k.status              AS key_status,
       k.public_key_sha256,
       k.valid_from,
       k.valid_to
FROM   institution_registries r
LEFT   JOIN institution_keys k USING (institution_code)
ORDER  BY r.institution_code, k.key_version DESC;
```

### 9.3 Quick schema reset (dev only)

```bash
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "TRUNCATE institution_registries, institution_keys RESTART IDENTITY CASCADE;"
```

(Use the `Respawn` library in tests, not `TRUNCATE`, when iterating on
test data — but for ad-hoc poking during dev, TRUNCATE is fine.)

---

## 10. Useful log lines & OpenAPI surfaces

### 10.1 EventIds in `DailyTrustSyncService` and friends

| EventId | Level | Meaning |
|---|---|---|
| 6101 | Warning | Trust store fetch failed (existing data untouched) |
| 6102 | Warning | Per-institution sync failure |
| 6103 | Error | Unhandled error during the whole tick |
| 6104 | Warning | A specific institution was skipped (invalid shape) |
| 6105 | Information | Local ACTIVE key was retired (trust store reported REVOKED) |
| 6107 | Information | Trust-sync run finished (synced/failed counts) |
| 6111 | Warning | Trust store record arrived without a key object — skipped |

Filter by EventId:
```bash
docker compose -f docker/docker-compose.yml logs sbqr.api \
  | grep -E "EventId ?[=:].*(6101|6105|6111)"
```

### 10.2 OpenAPI documents

The host publishes two OpenAPI documents:

| URL | Document | Auth |
|---|---|---|
| `http://localhost:8080/openapi/v1.public.json` | Public API (QR generate/validate, v1/oauth/token, health) | Anonymous |
| `http://localhost:5080/openapi/v1.internal-admin.json` | Internal admin (`/v1/admin/institutions`, `/v1/admin/...`) | HTTP Basic (`Docs__InternalAdmin__Username`/`PasswordHash`) |
| `http://localhost:5080/docs/public` | Scalar UI for public doc | Anonymous |
| `http://localhost:5080/docs/internal-admin` | Scalar UI for internal doc | HTTP Basic |

For dev, the internal-admin password hash is set in user-secrets
(`Docs:InternalAdmin:PasswordHash`, an Argon2id PHC — see the README
secrets section for the generate command).

---

## 11. Reset / cleanup

```bash
# Stop everything, keep data volumes
docker compose -f docker/docker-compose.yml down

# Stop everything, wipe data + mock state
docker compose -f docker/docker-compose.yml down -v

# Just wipe the mock's UPLOADED keys (they persist across restarts — that is
# the point of its bb_trust_store_mock database)
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d bb_trust_store_mock -c \
  "TRUNCATE institution_keys, institutions RESTART IDENTITY CASCADE;"

# Just wipe the local DB tables (dev only)
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "TRUNCATE institution_registries, institution_keys RESTART IDENTITY CASCADE;"
```

---

## 12. Troubleshooting

### "I started everything but `/trust-store/institutions` 502s from SBQR.Api"
The `TrustStore:BaseUrl` env var didn't reach the container.
```bash
docker compose -f docker/docker-compose.yml logs sbqr.api | grep -i truststore
```
You should see:
```
TrustStore__BaseUrl=http://bb-trust-store-mock:8080
TrustStore__SyncIntervalHours=...
```
If `BaseUrl` is empty or `localhost`, fix `docker/.env` or the compose file
and restart the API container.

### "The DB tables don't exist"
You forgot to run the external migration tool against the database. See
`docs/docker/DEPLOYMENT.md` §4 for the local/staging/production invocation
of the external migration tool.

### "Sync tick never produces a SUCCEEDED row"
Check the latest `institution.trust.sync.completed` row in `audit_logs`:
```bash
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "SELECT created_at, metadata FROM audit_logs WHERE event_type = 'institution.trust.sync.completed' ORDER BY created_at DESC LIMIT 1;"
```
- `metadata` contains an `error` key → the fetch failed. The connection
  error message is in `error`; see EventId 6101 in the logs.
- `metadata` lists institution codes in `failures` → one or more records
  were malformed. Look for EventId 6104 and the offending institution code.

### "The mock refuses to start"
```bash
dotnet run --project src/Host/BbTrustStoreMock.Api --environment Production
# → InvalidOperationException: BbTrustStoreMock.Api is a dev/test-only mock...
```
Either run it in `Development` (the default) or accept that this guard
exists for a reason.

### "Integration tests time out"
The Testcontainers fixture needs Docker. Make sure Docker Desktop is
running before `dotnet test`. On Linux without root, also ensure your user
is in the `docker` group.

### "Revoked institution still has an ACTIVE key in the local DB"
Either:
- A sync tick hasn't run since you revoked. Trigger one (shorten the
  interval, or call `DailyTrustSyncService.RunOnceAsync` directly in a test).
- You're querying without `.IgnoreQueryFilters()` — the EF Core
  `HasQueryFilter(r => r.IsActive)` filter hides soft-deleted rows. The
  ACTIVE column on the row is the source of truth, not the soft-delete
  flag. To see everything:
  ```sql
  SELECT * FROM institution_keys WHERE institution_code = '031008';
  -- ignore is_active; check status instead.
  ```

### "I want to verify the abstraction really is the only seam"
```bash
# 1. Only one production implementation of ITrustStoreClient
grep -rn "ITrustStoreClient" src/Modules/InstitutionTrust/

# 2. Only InstitutionUpsertService writes
grep -rn "Registries.Add\|Keys.Add" src/Modules/InstitutionTrust/

# 3. No module outside InstitutionTrust references trust-store URLs
grep -rn "trust-store\|BbTrustStoreMock\|TrustStore:BaseUrl" src/Modules/ \
  --include="*.cs" | grep -v InstitutionTrust

# 4. The only place the host's hostname leaks is config / docs / tests
grep -rn "bb-trust-store-mock\|localhost:8082" src/ docker/
```
If any of the above returns something unexpected, the abstraction has
leaked — fix it before adding new features.
