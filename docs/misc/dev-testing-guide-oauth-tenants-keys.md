<!--
  ============================================================================
  dev-testing-guide-oauth-tenants-keys.md
  ----------------------------------------------------------------------------
  Manual test guide for the OAuth, Tenants, Tenant-Configurations, and
  Crypto-Keys sub-domain businesses.

  SCHEMA CHANGE 2026-09-08: the dated 2026090* migration filenames below
  were never committed — the canonical chain is db/migrations/001-008 (one
  schema per module; see docs/design/database-design.md). Tables are
  schema-qualified: public.tenant_configurations, public.tenants,
  public.crypto_keys (which now also carries valid_from / valid_to /
  rotated_at for the C4 rotation policy). Adjust any copied SQL accordingly.

  Companion to docs/dev-testing-guide.md (InstitutionTrust + BB Trust Store
  mock).  This guide covers the admin-facing lifecycle: bootstrap token →
  register tenant → activate → provision FI configuration → mint tenant token →
  generate / rotate signing keys, plus suspend/reactivate cascades.

  No codebase files were modified to produce this guide — it is a pure
  documentation artifact under docs/.
  ============================================================================
-->

# Developer Manual Test Guide — OAuth · Tenants · Tenant-Configurations · Crypto-Keys

> **Audience** — Backend engineers, QA, and DevOps who need to manually
> exercise the four admin-facing sub-domains end-to-end against a fresh
> PostgreSQL database and a locally-running `SBQR.Api` host.
>
> **Scope**
>
> | # | Sub-domain | Bounded Context | Primary tables | HTTP prefix |
> |---|-----------|-----------------|----------------|-------------|
> | 1 | OAuth | IdentityAccess | `public.tenant_configurations` | `/v1/oauth` |
> | 2 | Tenants | Tenancy | `public.tenants` | `/v1/admin/tenants` |
> | 3 | Tenant-Configurations | IdentityAccess | `public.tenant_configurations` | `/v1/admin/tenants/{id}/tenant-configuration` |
> | 4 | Crypto-Keys | KeyCustody | `public.crypto_keys` | `/v1/crypto-keys` |
>
> **Not in scope** (covered by `docs/dev-testing-guide.md`): InstitutionTrust
> registry sync and the BB Trust Store mock.
>
> **Companion docs**
>
> - `docs/dev-testing-guide.md` — InstitutionTrust + mock stack smoke tests
> - `docs/api-docs-usage-guide.md` — OpenAPI UI access (public + internal-admin)
> - `db/migrations/README.md` — migration naming convention; applied by the external migration tool
> - `CLAUDE.md` — project-wide architecture summary

---

## Table of Contents

1.  [Mental Model — How the Four Domains Relate](#1-mental-model)
2.  [Prerequisites](#2-prerequisites)
3.  [Repository Layout — Files You'll Touch (Read-Only)](#3-repository-layout)
4.  [DB Setup From Scratch — "As If No Previous Table Exists"](#4-db-setup-from-scratch)
5.  [Boot the Docker-Compose Stack](#5-boot-the-stack)
6.  [Dev Credentials Reference Table](#6-dev-credentials-reference)
7.  [Happy-Path End-to-End Flow](#7-happy-path-end-to-end-flow)
    - 7.1. [Step 1 — Mint Bootstrap Admin Token (OAuth)](#71-step-1--mint-bootstrap-admin-token-oauth)
    - 7.2. [Step 2 — Register a Tenant (Tenants)](#72-step-2--register-a-tenant-tenants)
    - 7.3. [Step 3 — List + Get Tenant (Tenants)](#73-step-3--list--get-tenant-tenants)
    - 7.4. [Step 4 — Activate Tenant (Tenants)](#74-step-4--activate-tenant-tenants)
    - 7.5. [Step 5 — Provision Tenant Configuration (Tenant-Configurations)](#75-step-5--provision-tenant-configuration-tenant-configurations)
    - 7.6. [Step 6 — Mint Tenant-Scoped Token (OAuth)](#76-step-6--mint-tenant-scoped-token-oauth)
    - 7.7. [Step 7 — Generate a Crypto Key (Crypto-Keys)](#77-step-7--generate-a-crypto-key-crypto-keys)
    - 7.8. [Step 8 — Retrieve + List Crypto Keys (Crypto-Keys)](#78-step-8--retrieve--list-crypto-keys-crypto-keys)
    - 7.9. [Step 9 — Rotate the Signing Key (Crypto-Keys)](#79-step-9--rotate-the-signing-key-crypto-keys)
    - 7.10. [Step 10 — Adopt a Caller-Supplied Key (Crypto-Keys, optional)](#710-step-10--adopt-a-caller-supplied-key-crypto-keys-optional)
    - 7.11. [Step 11 — Suspend → Cascade Verification](#711-step-11--suspend--cascade-verification)
    - 7.12. [Step 12 — Reactivate → Cascade Reversal](#712-step-12--reactivate--cascade-reversal)
8.  [Endpoint Reference (All Four Domains)](#8-endpoint-reference)
9.  [DB Verification Queries](#9-db-verification-queries)
10. [Reset / Cleanup](#10-reset--cleanup)
11. [Automated Tests to Run](#11-automated-tests-to-run)
12. [Troubleshooting FAQ](#12-troubleshooting-faq)

---

## 1. Mental Model — How the Four Domains Relate

```
 ┌──────────────────────────────────────────────────────────────────────┐
 │                 platform-bootstrap (admin credential)                  │
 │                    client_id = platform-bootstrap                     │
 │                    secret    = dev-only-bootstrap-secret             │
 │                              │                                        │
 │                              ▼                                        │
 │          POST /v1/oauth/token  (grant_type=client_credentials)       │
 │                              │                                        │
 │                              ▼                                        │
 │                    JWT  { scope: "admin" }                           │
 │                              │                                        │
 │    ┌────────────────────────┼──────────────────────────────┐          │
 │    ▼                        ▼                              ▼           │
 │  Tenants              Tenant-Configurations            Crypto-Keys  │
 │  (register)           (provision FI creds)             (mint/rotate) │
 │     │                        │                              │          │
 │    POST /tenants              │                              │          │
 │    POST /tenants/{id}/       │                              │          │
 │       activate               │                              │          │
 │    POST /tenants/{id}/       │                              │          │
 │       suspend                │                              │          │
 │    POST /tenants/{id}/       │                              │          │
 │       reactivate             │                              │          │
 │    POST /tenants/{id}/       │                              │          │
 │       terminate              │                              │          │
 │                              │                              │          │
 │    POST /tenants/{id}/tenant-configuration                 │          │
 │     │                          │                            │          │
 │     │  → client_id: "010101-<8hex>"                          │          │
 │     │  → client_secret: <plaintext, shown ONCE>              │          │
 │     │  → row in public.tenant_configurations                  │          │
 │     │                                                            │          │
 │    Tenant now authenticates at:                                    │          │
 │    POST /v1/oauth/token  with provisioned client_id/secret      │          │
 │     │                                                            │          │
 │     ▼ JWT  { scope: "qr:generate qr:validate" }                  │          │
 │    (Used for QR QrGeneration / Verification flows — OUT OF SCOPE here)│          │
 │                                                                  │          │
 │    POST /v1/crypto-keys  { tenantId, mode:"Generate" }            │          │
 │     │                                                              │          │
 │     │  → NEW row in public.crypto_keys                            │          │
 │     │  → KeyVersion 1, Status: Active                             │          │
 │     │  → single-active partial unique index enforced               │          │
 │     │  → AUTO-PUBLISH public key to institution trust store       │          │
 │     │       (via IInstitutionTrustPublisher — same upsert as       │          │
 │     │        POST /v1/admin/institutions).  After a successful    │          │
 │     │        201, qr/validate can resolve the tenant signature    │          │
 │     │        immediately — no second operator call needed.         │          │
 │     │   ← If the trust publish fails the handler returns 409      │          │
 │     │       with crypto_key.trust_publish_failed audit row;       │          │
 │     │       recover with POST /v1/admin/institutions (§7.7a).     │          │
 │     │                                                              │          │
 │     ▼                                                              │          │
 │    GET  /v1/crypto-keys/{tenantId}/active                          │          │
 │    GET  /v1/crypto-keys/{tenantId}                                 │          │
 │    PUT  /v1/crypto-keys/{tenantId}  → rotate (old RETIRED, new v+1)│          │
 │    GET  /v1/crypto-keys                                              │          │
 │                                                                       │          │
 │    ┌─── CASCADE ──────────────────────────────────────────────────────┐      │
 │    │  POST /tenants/{id}/suspend  →                                   │      │
 │    │    1. tenants.status → SUSPENDED                                 │      │
 │    │    2. public.tenant_configurations.status → SUSPENDED  (via     │      │
 │    │       IdentityAccess Contracts seam — ITenantCredentialProvisioner)│     │
 │    │    3. crypto_keys.status → SUSPENDED  (via KeyCustody Contracts   │      │
 │    │       seam — SuspendTenantSigningKeysCommand)                    │      │
 │    │    4. audit_logs row: tenant.suspended                           │      │
 │    │                                                                   │      │
 │    │  POST /tenants/{id}/reactivate →                                  │      │
 │    │    1. tenants.status → ACTIVE                                    │      │
 │    │    2. tenant_configurations.status → ACTIVE  (IdentityAccess)    │      │
 │    │    3. crypto_keys.status → ACTIVE  (KeyCustody)                  │      │
 │    │    4. audit_logs row: tenant.reactivated                        │      │
 │    │                                                                   │      │
 │    │  POST /tenants/{id}/terminate  →                                  │      │
 │    │    1. tenants.is_active = FALSE, status → TERMINATED              │      │
 │    │    2. tenant_configurations cascades to SUSPENDED                 │      │
 │    │    3. crypto_keys cascades to SUSPENDED                          │      │
 │    │    4. audit_logs row: tenant.terminated                           │      │
 │    └─── TERMINATED is a terminal state — reactivate is a no-op 409 ───┘      │
 └──────────────────────────────────────────────────────────────────────────────┘
```

> **Key insight:** The bootstrap token is the only credential that ever carries
> the `admin` scope. Tenant FI configurations (created by provisioning) only ever
> carry `qr:generate` and `qr:validate`. This means **every admin action** in
> this guide uses the bootstrap token — never the tenant token.

---

## 2. Prerequisites

| Tool | Minimum Version | Notes |
|------|----------------|-------|
| Docker Desktop | 4.20+ | Tested on Windows; WSL2 backend |
| Docker Compose v2 | 2.20+ | Invoked as `docker compose` (not `docker-compose`) |
| PowerShell 7 | 7.3+ | For the runbook scripts (`pwsh`) |
| .NET SDK | 10.0 | Only needed if running `dotnet run` instead of Docker |
| `curl` | any | For HTTP calls (Git Bash / WSL / PowerShell) |
| `jq` | 1.7+ | For JSON field extraction in bash one-liners |
| `psql` | 16+ | For direct DB verification (provided by the `sbqr.postgres` container) |

> If you prefer `dotnet run` over Docker, skip to
> [§5 — Boot the Stack (dotnet run alternative)](#5-boot-the-stack).
> The only differences are the API port (`5001` instead of `8080`) and
> the fact that the API assumes the canonical schema has been applied by
> the external migration tool — there is no longer a
> `Database:RunMigrationsOnStartup` config.

---

## 3. Repository Layout — Files You'll Touch (Read-Only)

All paths are relative to the solution root.

```
rvl-secure-bqr-manager/
├── docker/
│   ├── docker-compose.yml              ← compose stack (postgres, api, adminer, mock)
│   ├── .env                           ← dev-only runtime values (non-secret)
│   ├── Dockerfile.api                 ← multi-stage .NET 10 Dockerfile
│   └── postgres/
│       └── init/
│           └── 00-create-databases.sql  ← creates sbqr_app + sbqr_key_vault
├── db/
│   └── migrations/
│       ├── README.md                  ← migration naming convention (applied by the external migration tool)
│       ├── 20260903120000_create_tenants_table.sql
│       ├── 20260903120100_create_api_credentials_table.sql
│       ├── 20260903120200_create_crypto_keys_table.sql
│       ├── 20260903120300_add_chk_argon2id.sql
│       ├── 20260903120400_add_suspended_status.sql
│       ├── 20260903120500_add_public_key_sha256.sql
│       ├── 20260904130000_relocate_to_identity_schema.sql
│       ├── 20260905120000_create_audit_logs_table.sql
│       ├── 20260906120000_create_qr_generations_table.sql
│       ├── 20260907120000_create_qr_verification_tables.sql
│       ├── 20260908120000_create_institution_trust_tables.sql
│       ├── 20260909120000_add_institute_type.sql
│       ├── 20260910120000_drop_tenant_code_require_institution_code.sql
│       └── 20260911120000_replace_api_credentials_with_tenant_configurations.sql
├── src/
│   ├── Host/SBQR.Api/
│   │   ├── Program.cs                 ← host entry point (no migration code)
│   │   ├── appsettings.json           ← the ONLY settings file (single-file convention)
│   │   └── (no Migrations/ directory — schema is owned by the external migration tool)
│   └── Modules/
│       ├── IdentityAccess/...         ← OAuth token issuance + tenant_configurations
│       ├── Tenancy/...                ← Tenant registration + lifecycle
│       └── KeyCustody/...             ← Crypto key generation + rotation
└── docs/
    ├── dev-testing-guide.md           ← existing guide (InstitutionTrust + mock)
    └── dev-testing-guide-oauth-tenants-keys.md ← THIS FILE
```

---

## 4. DB Setup From Scratch — "As If No Previous Table Exists"

> **TL;DR:** Wipe the Postgres volume, start the stack, then apply the 14
> SQL migrations with the external migration tool. This produces the final
> schema with zero assumptions about prior state.

### 4.1 Canonical Approach — Docker Volume Reset + Migration Tool

This is the project's canonical "fresh DB" procedure. It mirrors what
`docker/start-db.ps1` and the CI pipeline do internally:

```bash
# 1. Tear down everything — the -v flag destroys the sbqr.pgdata volume,
#    which means the init script (00-create-databases.sql) re-runs on next
#    boot, recreating sbqr_app + sbqr_key_vault from scratch.
docker compose -f docker/docker-compose.yml down -v

# 2. Start ONLY postgres so the init script can finish before we migrate.
docker compose -f docker/docker-compose.yml up -d sbqr.postgres

# 3. Wait for the healthcheck to report "healthy" (or just sleep).
docker inspect --format '{{.State.Health.Status}}' sbqr.postgres    # → "healthy"

# 4. Run the external migration tool against sbqr_app + sbqr_key_vault.
#    The exact CLI/arguments live in the migration tool's own docs — the
#    canonical scripts are under db/migrations/*.sql and are applied in
#    lexicographic (timestamp) order. Expected 14 scripts.

# 5. Now start the API container (it does NOT apply the schema itself).
docker compose -f docker/docker-compose.yml up -d sbqr.api

# 6. Wait for the healthcheck.
docker inspect --format '{{.State.Health.Status}}' sbqr.api       # → "healthy"
```

> **Idempotency note:** Every migration uses `CREATE TABLE IF NOT EXISTS`,
> `CREATE INDEX IF NOT EXISTS`, etc. Re-running the external migration tool
> against an already-migrated database is a safe no-op — the tool's own
> bookkeeping table tracks which scripts have been applied. See
> `db/migrations/README.md` for full details.

### 4.2 Alternative — One-Shot SQL Script

If you prefer to `psql` the schema directly (bypassing the dbup runner),
here is the consolidated DDL for the **four domain tables only**, in their
final post-migration state:

```sql
-- =====================================================================
-- One-shot schema for the 4 test domains (OAuth, Tenants,
-- Tenant-Configurations, Crypto-Keys).
--
-- Run against a fresh sbqr_app database:
--   docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
--     psql -U postgres -d sbqr_app -f /dev/stdin < schema-oauth-tenants-keys.sql
--
-- This does NOT create institution_registries, qr_generations,
-- qr_verifications, or bb_trust_store_config — those belong to other
-- sub-domains not covered by this guide.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS identity;

-- Migration tool bookkeeping table (so the tool doesn't try to re-apply
-- migrations you've already applied).
-- (The exact schema of this table is determined by the external migration
-- tool you choose; this snippet is the shape the historical dbup-postgresql
-- runner used. If you bypass the external tool and apply DDL directly,
-- you only need to create whatever bookkeeping table your chosen tool
-- expects.)

-- =====================================================================
-- 1. Tenants  (public.tenants — final state after migrations 1 + 13)
-- =====================================================================
CREATE TABLE IF NOT EXISTS public.tenants (
    tenant_id        UUID            PRIMARY KEY,
    institution_name VARCHAR(200)    NOT NULL,
    institution_code VARCHAR(6)      NOT NULL
        CHECK (institution_code ~ '^[0-9]{6}$'),
    status           VARCHAR(20)     NOT NULL
        CHECK (status IN ('PENDING', 'ACTIVE', 'SUSPENDED', 'TERMINATED')),
    created_by       VARCHAR(200)    NULL,
    created_at       TIMESTAMPTZ     NOT NULL DEFAULT now(),
    modified_by      VARCHAR(200)    NULL,
    modified_at      TIMESTAMPTZ     NULL,
    is_active        BOOLEAN         NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS ix_tenants_status             ON public.tenants (status);
CREATE INDEX IF NOT EXISTS ix_tenants_institution_code   ON public.tenants (institution_code);
CREATE INDEX IF NOT EXISTS ix_tenants_active             ON public.tenants (is_active) WHERE is_active = TRUE;

-- =====================================================================
-- 2. Tenant-Configurations (public.tenant_configurations — migration 14)
-- =====================================================================
CREATE TABLE IF NOT EXISTS public.tenant_configurations (
    tenant_configuration_id   UUID            PRIMARY KEY,
    client_id                 VARCHAR(100)    NOT NULL UNIQUE,
    client_secret_hash        TEXT            NOT NULL
        CHECK (client_secret_hash LIKE '$argon2id$%'),
    is_qr_generation_allowed  BOOLEAN         NOT NULL DEFAULT TRUE,
    is_qr_validation_allowed  BOOLEAN         NOT NULL DEFAULT TRUE,
    status                   VARCHAR(20)     NOT NULL
        CHECK (status IN ('ACTIVE', 'SUSPENDED', 'REVOKED', 'EXPIRED', 'PENDING_ROTATION')),
    tenant_id                 UUID            NOT NULL REFERENCES public.tenants (tenant_id) ON DELETE RESTRICT,
    expires_at               TIMESTAMPTZ     NULL,
    last_used_at             TIMESTAMPTZ     NULL,
    created_by               VARCHAR(200)    NULL,
    created_at               TIMESTAMPTZ     NOT NULL DEFAULT now(),
    modified_by              VARCHAR(200)    NULL,
    modified_at              TIMESTAMPTZ     NULL,
    is_active                BOOLEAN         NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS ix_tenant_configurations_client_id  ON public.tenant_configurations (client_id);
CREATE INDEX IF NOT EXISTS ix_tenant_configurations_tenant     ON public.tenant_configurations (tenant_id);
CREATE INDEX IF NOT EXISTS ix_tenant_configurations_status     ON public.tenant_configurations (status);
CREATE INDEX IF NOT EXISTS ix_tenant_configurations_expires_at ON public.tenant_configurations (expires_at) WHERE expires_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_tenant_configurations_active     ON public.tenant_configurations (is_active) WHERE is_active = TRUE;

-- =====================================================================
-- 3. Crypto-Keys (public.crypto_keys — final state after migrations 3 + 6)
-- =====================================================================
CREATE TABLE IF NOT EXISTS public.crypto_keys (
    crypto_key_id          UUID PRIMARY KEY,
    key_id                 VARCHAR(100)  NOT NULL,
    key_version            INTEGER       NOT NULL CHECK (key_version > 0),
    public_key             TEXT          NOT NULL,
    custody_key_reference  VARCHAR(500)  NOT NULL,
    status                 VARCHAR(20)   NOT NULL
        CHECK (status IN ('GENERATING', 'PENDING', 'ACTIVE', 'SUSPENDED',
                          'RETIRING', 'RETIRED', 'REVOKED')),
    tenant_id              UUID          NOT NULL REFERENCES public.tenants (tenant_id) ON DELETE RESTRICT,
    public_key_sha256      CHAR(64)      NOT NULL,
    created_by             VARCHAR(200)  NULL,
    created_at             TIMESTAMPTZ   NOT NULL DEFAULT now(),
    modified_by            VARCHAR(200)  NULL,
    modified_at            TIMESTAMPTZ   NULL,
    is_active              BOOLEAN       NOT NULL DEFAULT TRUE
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_crypto_keys_tenant_keyid_version
    ON public.crypto_keys (tenant_id, key_id, key_version);

CREATE UNIQUE INDEX IF NOT EXISTS uq_crypto_keys_active_tenant
    ON public.crypto_keys (tenant_id)
    WHERE status = 'ACTIVE';

CREATE INDEX IF NOT EXISTS ix_crypto_keys_status  ON public.crypto_keys (status);
CREATE INDEX IF NOT EXISTS ix_crypto_keys_active  ON public.crypto_keys (is_active) WHERE is_active = TRUE;

-- =====================================================================
-- 4. Audit Logs (public.audit_logs — migration 7)
-- =====================================================================
CREATE TABLE IF NOT EXISTS public.audit_logs (
    audit_log_id   UUID PRIMARY KEY,
    event_type     VARCHAR(100)  NOT NULL,
    resource_type  VARCHAR(100)  NULL,
    resource_id    VARCHAR(100)  NULL,
    metadata       TEXT          NULL,
    tenant_id      UUID          NULL REFERENCES public.tenants (tenant_id),
    created_by     VARCHAR(200)  NULL,
    created_at     TIMESTAMPTZ   NOT NULL DEFAULT now(),
    modified_by    VARCHAR(200)  NULL,
    modified_at    TIMESTAMPTZ   NULL,
    is_active      BOOLEAN       NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS ix_audit_logs_event_type   ON public.audit_logs (event_type);
CREATE INDEX IF NOT EXISTS ix_audit_logs_tenant_id    ON public.audit_logs (tenant_id) WHERE tenant_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_audit_logs_created_at   ON public.audit_logs (created_at);
```

> **Use [§4.1 (migration runner)](#41-canonical-approach--docker-volume-reset--migration-runner)
> unless you have a specific reason to run raw SQL.** The runner applies all
> 14 migrations (including tables for QR generations, QR verifications, and
> InstitutionTrust) and is what CI and production use.

---

## 5. Boot the Stack

### Option A — Full Docker Compose (includes API, Adminer, mock trust store)

```bash
docker compose -f docker/docker-compose.yml up --build -d
```

This starts four containers:

| Container | Host Port | Purpose |
|-----------|-----------|---------|
| `sbqr.postgres` | `localhost:5432` | PostgreSQL 16; runs `00-create-databases.sql` on first boot |
| `sbqr.api` | `localhost:8080` | The modular-monolith API (NET 10, listens on `:8080` in-container) |
| `sbqr.adminer` | `localhost:8081` | Database web UI (username: `postgres`, password: `postgres`) |
| `bb-trust-store-mock` | `localhost:8082` | Mock Bangladesh Bank trust store API |

```bash
# Verify everything is healthy:
docker compose -f docker/docker-compose.yml ps
#   Name                  Status              Ports
# ─────────────────────────────────────────────────────────────────
# sbqr.postgres           healthy (starting)  0.0.0.0:5432->5432/tcp
# sbqr.api                healthy (starting)  0.0.0.0:8080->8080/tcp
# sbqr.adminer            healthy (starting)  0.0.0.0:8081->8080/tcp
# bb-trust-store-mock     healthy (starting)  0.0.0.0:8082->8080/tcp

# Quick health check:
curl -s http://localhost:8080/health/live    # → "Healthy"
```

### Option B — `dotnet run` (no Docker, hot-reload enabled)

Requires the .NET 10 SDK and a local PostgreSQL 16 instance on `localhost:5432`:

```bash
# The API does NOT apply the schema; apply it with the external migration
# tool against sbqr_app + sbqr_key_vault before `dotnet run`. API listens
# on :5001.
cd src/Host/SBQR.Api
dotnet run
```

Use `http://localhost:5001` as the base URL instead of `http://localhost:8080`.

> If you use `dotnet run`, the BB Trust Store mock is not available.
> The OAuth / Tenants / Tenant-Configurations / Crypto-Keys flows in this
> guide do **not** call the mock, so this is fine.

---

## 6. Dev Credentials Reference Table

All values below are **dev-only** and defined in `docker/.env.example`,
`docker/docker-compose.yml`, and the seeded user-secrets
(`scripts/dev-seed-user-secrets.ps1` / `.sh`). They are **never** used in
production.

| What | Where | Value |
|------|-------|-------|
| PostgreSQL host | docker-compose | `localhost:5432` |
| PostgreSQL user | docker-compose | `postgres` |
| PostgreSQL password | docker-compose | `postgres` |
| PostgreSQL database | init script | `sbqr_app` *(business data)* |
| PostgreSQL database | init script | `sbqr_key_vault` *(key custody vault)* |
| JWT signing key | docker-compose default | `dev-only-signing-key-change-me-0123456789abcdef` |
| JWT issuer | appsettings.json | `sbqr` |
| JWT audience | appsettings.json | `sbqr-api` |
| Token lifetime | appsettings.json | `10` minutes (600 seconds) |
| Bootstrap client_id | docker-compose default | `platform-bootstrap` |
| Bootstrap client_secret | docker-compose default | `dev-only-bootstrap-secret` |
| Bootstrap scopes | docker-compose default | `admin` |
| Key custody vault provider | `Crypto:VaultProvider` config | `Local` in appsettings.json; seeded to `S3` in dev user-secrets (composes AES-256-GCM vault on S3) |
| Vault KEK (dev) | `KeyCustody:VaultKek` (base64 of 32 bytes) | unset in dev — falls back to the built-in dev KEK; set via user-secrets if you want your own |
| Key store directory | `KeyCustody:KeyStoreDirectory` | Bind-mounted to `./data-keys` in dev |
| OpenAPI internal-admin username | `Docs:InternalAdmin:Username` | default `platform-team` |
| OpenAPI internal-admin password | `Docs:InternalAdmin:PasswordHash` | Argon2id PHC in user-secrets (personal per developer) |

> **No secrets in `.env`**: The `docker/.env` file does **not** override
> `Auth__Bootstrap__ClientSecret` or `Jwt__SigningKey`, so both fall back to
> the `docker-compose.yml` defaults. No code changes are needed.

---

## 7. Happy-Path End-to-End Flow

> All `curl` examples below assume the Docker Compose stack is running and
> the API is at `http://localhost:8080`. The base URL variable is defined once:

```bash
BASE=http://localhost:8080
```

### 7.1 Step 1 — Mint Bootstrap Admin Token (OAuth)

The bootstrap credential is the **only** credential that carries the `admin`
scope. Every admin endpoint in this guide uses the token obtained here.

**Request:**

```bash
curl -s -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=platform-bootstrap" \
  -d "client_secret=dev-only-bootstrap-secret" \
  | jq
```

**Sample payload (form-encoded):**
```
grant_type=client_credentials
client_id=platform-bootstrap
client_secret=dev-only-bootstrap-secret
```

> The OAuth endpoint also accepts JSON (`Content-Type: application/json`)
> with the same three fields.

**Expected response:**
```json
{
    "access_token": "eyJhbGciOiJIUzI1NiIs...",
    "token_type": "Bearer",
    "expires_in": 600
}
```

**Store the token for subsequent steps:**

```bash
TOKEN=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=platform-bootstrap" \
  -d "client_secret=dev-only-bootstrap-secret" \
  | jq -r '.access_token')

echo "Token acquired: ${TOKEN:0:40}..."
```

> ⚠️ **Rate limit:** `POST /v1/oauth/token` is limited to 10 requests per
> minute per IP. If you send more than 10 in 60 seconds you'll get HTTP 429.
> Space your requests accordingly during manual testing.

> 🔍 **JWT decode (optional but recommended):**
> Paste the `access_token` into <https://jwt.io> or decode locally:
> ```bash
> echo "$TOKEN" | cut -d. -f2 | base64 -d 2>/dev/null | jq .
> ```
> Expected payload:
> ```json
> {
>   "sub": "platform-bootstrap",
>   "scope": "admin",
>   "iss": "sbqr",
>   "aud": "sbqr-api",
>   "exp": 1750000000,
>   "iat": 1749996400,
>   "nbf": 1749996400,
>   "jti": "abc123..."
> }
> ```

---

### 7.2 Step 2 — Register a Tenant (Tenants)

**Request:**

```bash
TENANT_RESPONSE=$(curl -s -X POST "$BASE/v1/admin/tenants" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "institutionName": "Example Bank Ltd.",
    "institutionCode": "010101"
  }')

echo "$TENANT_RESPONSE" | jq
```

**Sample payload:**
```json
{
    "institutionName": "Example Bank Ltd.",
    "institutionCode": "010101"
}
```

| Field | Type | Rules |
|-------|------|-------|
| `institutionName` | string | ≤ 200 chars, NOT NULL |
| `institutionCode` | string | Exactly 6 digits (`^[0-9]{6}$`), NOT NULL, UNIQUE |

**Expected response** *(HTTP 201 Created, `Location: /v1/admin/tenants/{tenantId}`)*:
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "institutionName": "Example Bank Ltd.",
    "institutionCode": "010101",
    "status": "Pending",
    "isActive": true
}
```

> The tenant is created in `PENDING` status. It must be activated before its
> configuration can authenticate at the token endpoint.

**Extract the tenant ID:**

```bash
TENANT_ID=$(echo "$TENANT_RESPONSE" | jq -r '.tenantId')
echo "Tenant ID: $TENANT_ID"
```

---

### 7.3 Step 3 — List + Get Tenant (Tenants)

**List all tenants:**

```bash
curl -s "$BASE/v1/admin/tenants?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" \
  | jq
```

**Expected response:**
```json
{
    "items": [
        {
            "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
            "institutionName": "Example Bank Ltd.",
            "institutionCode": "010101",
            "status": "Pending",
            "isActive": true
        }
    ],
    "page": 1,
    "pageSize": 20,
    "totalCount": 1,
    "hasMore": false
}
```

**Filter by status:**

```bash
curl -s "$BASE/v1/admin/tenants?status=Pending" \
  -H "Authorization: Bearer $TOKEN" | jq
```

> `status` values: `Pending`, `Active`, `Suspended`, `Terminated`
> (case-insensitive). An unrecognised value returns HTTP 400.

**Get a single tenant by ID:**

```bash
curl -s "$BASE/v1/admin/tenants/$TENANT_ID" \
  -H "Authorization: Bearer $TOKEN" | jq
```

---

### 7.4 Step 4 — Activate Tenant (Tenants)

```bash
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN" | jq
```

> No request body required.

**Expected response** *(HTTP 200)*:
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status": "Active",
    "activatedAt": "2026-09-11T12:00:01Z"
}
```

> **State machine rule:** `Activate` transitions `Pending → Active` or
> `Suspended → Active`. Calling `Activate` on an already-`Active` tenant
> returns HTTP 409 (`ErrorCode.InvariantViolation`).

---

### 7.5 Step 5 — Provision Tenant Configuration (Tenant-Configurations)

This endpoint creates a new row in `public.tenant_configurations` with an
Argon2id-hashed client secret. The **plaintext** `clientSecret` is returned
exactly once in the response — it is never persisted or re-displayed.

```bash
CRED_RESPONSE=$(curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN" | jq -r '.')
echo "$CRED_RESPONSE" | jq
```

> No request body required. The server generates the `client_id` as
> `{institution_code}-{8 random hex chars}` (e.g., `010101-7c1b4d88`).

**Expected response** *(HTTP 200)*:
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "credentialId": "c2a8f000-4d5e-6f70-8192-0a1b2c3d4e5f",
    "clientId": "010101-7c1b4d88",
    "clientSecret": "K7vQ9wR2pX4yB6mN8cL3jT5sY0dF1hG3aU7iE9o",
    "expiresAt": "2027-09-11T12:00:00Z"
}
```

| Field | Description |
|-------|-------------|
| `tenantId` | Echoes the route parameter |
| `credentialId` | UUID of the `tenant_configurations` row |
| `clientId` | Shaped `{institution_code}-{8hex}`; used at the token endpoint |
| `clientSecret` | Plaintext secret — **shown once only** |
| `expiresAt` | Credential expiry (ISO-8601); defaults to 1 year from creation |

**Extract the tenant credential:**

```bash
CLIENT_ID=$(echo "$CRED_RESPONSE" | jq -r '.clientId')
CLIENT_SECRET=$(echo "$CRED_RESPONSE" | jq -r '.clientSecret')
echo "Client ID: $CLIENT_ID"
echo "Client Secret: $CLIENT_SECRET"   # only visible here — store it!
```

> **Important:** If you lose the `clientSecret`, you must suspend the old
> credential and provision a new one. The old hash is **not** retrievable.

---

### 7.6 Step 6 — Mint Tenant-Scoped Token (OAuth)

Now that the tenant has an ACTIVE credential row, authenticate at the token
endpoint using the provisioned `clientId` / `clientSecret`:

```bash
TENANT_TOKEN=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  | jq -r '.access_token')

echo "Tenant token acquired: ${TENANT_TOKEN:0:40}..."
```

**Expected response** *(same shape as the bootstrap token)*:
```json
{
    "access_token": "eyJhbGciOiJIUzI1NiIs...",
    "token_type": "Bearer",
    "expires_in": 600
}
```

**Decode the tenant JWT to verify scopes:**

```bash
echo "$TENANT_TOKEN" | cut -d. -f2 | base64 -d 2>/dev/null | jq .
```

Expected payload:
```json
{
    "sub": "client:010101-7c1b4d88",
    "scope": "qr:generate qr:validate",
    "tenant_id": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "inst": "010101",
    "iss": "sbqr",
    "aud": "sbqr-api",
    "exp": 1750000000,
    "iat": 1749996400,
    "nbf": 1749996400,
    "jti": "def456..."
}
```

> **Scope comparison:**
> - Bootstrap token: `scope = "admin"` → can call `/v1/admin/*` and `/v1/crypto-keys`
> - Tenant token: `scope = "qr:generate qr:validate"` → can call QR QrGeneration &
>   Verification endpoints (out of scope for this guide)

> ⚠️ Remember the **10 req/min rate limit** on `/v1/oauth/token`. You've just
> used 2 of your 10 requests in this minute.

---

### 7.7 Step 7 — Generate a Crypto Key (Crypto-Keys)

The tenant's first signing key. The server generates a fresh Ed25519 keypair
using the configured `KeyCustody:ActiveProvider` (PlainFile in dev). The
public key is returned; the private key is stored in the vault and never
leaves the server.

```bash
KEY_RESPONSE=$(curl -s -X POST "$BASE/v1/crypto-keys" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"tenantId\": \"$TENANT_ID\",
    \"mode\": \"Generate\"
  }" | jq -r '.')

echo "$KEY_RESPONSE" | jq
```

**Sample payload:**
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "mode": "Generate"
}
```

> `mode` is case-insensitive. Valid values: `"Generate"` or `"Adopt"`
> (see [§7.10](#710-step-10--adopt-a-caller-supplied-key-crypto-keys-optional)).

**Expected response** *(HTTP 201 Created)*:
```json
{
    "cryptoKeyId": "d3b8f000-7a8b-9c0d-1e2f-3a4b5c6d7e8f",
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "keyId": "platform-key-001",
    "keyVersion": 1,
    "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
    "status": "Active",
    "isActive": true,
    "createdAt": "2026-09-11T12:00:02Z"
}
```

> **Single-active rule:** If a tenant already has an `ACTIVE` key, `POST`
> returns HTTP 409 (`ErrorCode.InvariantViolation`). Use `PUT` (rotate)
> instead.

#### 7.7a Auto-publish side-effect (trust directory)

A successful `POST /v1/crypto-keys` also publishes the freshly minted public
key into the **institution trust directory** as part of the same operation
(`InstitutionTrust` module). After the 201 returns, `qr/validate` can resolve
the tenant's signature immediately — **no second `POST /v1/admin/institutions`
call is required** for a happy-path onboarding.

The publish step uses the same `InstitutionUpsertService.UpsertAsync` flow as
the manual admin endpoint, so the wire behaviour and the audit row are
identical:

| Audit row `event_type` | Emitted by |
|------------------------|-----------|
| `institution.trust.key.published` (actor `system:crypto-create`) | The crypto-create auto-publish path |
| `institution.trust.key.published` (actor `<admin subject>`) | The manual `POST /v1/admin/institutions` path |

To verify the auto-publish from the DB side after Step 7:

```bash
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app -c "
    SELECT institution_code, key_version, status, created_by
    FROM public.institution_keys
    WHERE institution_code = '010101';
  "
# Expect: one ACTIVE row at key_version=1, created_by = 'system:crypto-create'
```

> **Failure mode (rare).** If the trust publish step fails (network glitch,
> InstitutionTrust DbContext down, validation reject) the handler emits a
> `crypto_key.trust_publish_failed` audit row containing the tenant ID,
> public-key SHA-256 and the failure reason, then returns HTTP 409 with
> `ErrorCode.InvariantViolation`. The `crypto_keys` row is **preserved** so
> the operator can recover with one additional manual call:
>
> ```bash
> curl -X POST "$BASE/v1/admin/institutions" \
>   -H "Authorization: Bearer $TOKEN" \
>   -H "Content-Type: application/json" \
>   -d '{
>         "institutionCode": "010101",
>         "institutionName": "Example Bank Ltd.",
>         "publicKeyPem": "<paste the publicKeyPem from the failed POST /v1/crypto-keys response>"
>       }'
> ```
>
> This matches the existing operational model — until this change, the
> operator already had to call `POST /v1/admin/institutions` after every
> `POST /v1/crypto-keys`, so the failure recovery is the same one-call fix.

---

### 7.8 Step 8 — Retrieve + List Crypto Keys (Crypto-Keys)

**Get the tenant's active key:**

```bash
curl -s "$BASE/v1/crypto-keys/$TENANT_ID/active" \
  -H "Authorization: Bearer $TOKEN" | jq
```

**Expected response** *(HTTP 200)*:
```json
{
    "cryptoKeyId": "d3b8f000-7a8b-9c0d-1e2f-3a4b5c6d7e8f",
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "keyId": "platform-key-001",
    "keyVersion": 1,
    "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
    "status": "Active",
    "isActive": true,
    "createdAt": "2026-09-11T12:00:02Z"
}
```

**List all key versions for the tenant**
(includes retired keys — not filtered by soft-delete):

```bash
curl -s "$BASE/v1/crypto-keys/$TENANT_ID?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | jq
```

**Expected response:**
```json
{
    "items": [
        {
            "cryptoKeyId": "d3b8f000-7a8b-9c0d-1e2f-3a4b5c6d7e8f",
            "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
            "keyId": "platform-key-001",
            "keyVersion": 1,
            "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
            "status": "Active",
            "isActive": true,
            "createdAt": "2026-09-11T12:00:02Z"
        }
    ],
    "page": 1,
    "pageSize": 20,
    "totalCount": 1,
    "hasMore": false
}
```

**List across all tenants:**

```bash
curl -s "$BASE/v1/crypto-keys?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | jq
```

> `GET /v1/crypto-keys/{tenantId}` returns the **full version history**
> (active + retired + suspended), using `IgnoreQueryFilters()` at the EF
> layer to bypass the `is_active = TRUE` soft-delete filter.
> `GET /v1/crypto-keys/{tenantId}/active` returns only the current
> `ACTIVE` key.

---

### 7.9 Step 9 — Rotate the Signing Key (Crypto-Keys)

Rotation retires the current ACTIVE key (sets `status = RETIRED`,
`is_active = FALSE`) and immediately mints a new server-generated key at
`key_version + 1`.

```bash
ROTATE_RESPONSE=$(curl -s -X PUT "$BASE/v1/crypto-keys/$TENANT_ID" \
  -H "Authorization: Bearer $TOKEN" | jq -r '.')

echo "$ROTATE_RESPONSE" | jq
```

> No request body required.

**Expected response** *(HTTP 200)*:
```json
{
    "cryptoKeyId": "e4c9f000-8b9c-0d1e-2f3a-4b5c6d7e8f9a",
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "keyId": "platform-key-001",
    "keyVersion": 2,
    "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
    "status": "Active",
    "isActive": true,
    "createdAt": "2026-09-11T12:00:03Z"
}
```

Note that `keyVersion` is now `2` (incremented from `1`) and the `cryptoKeyId`
is a new UUID. The old key (version 1) is still in the DB but now has
`status = "Retired"` and `isActive = false`.

**Verify the version history:**

```bash
curl -s "$BASE/v1/crypto-keys/$TENANT_ID?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | jq '.items[] | {keyVersion, status, isActive}'
```

Expected:
```json
{"keyVersion": 1, "status": "Retired", "isActive": false}
{"keyVersion": 2, "status": "Active", "isActive": true}
```

> **Atomicity note:** Rotation uses two sequential `SaveChanges` calls —
> the old key is retired and committed **first**, then the new key is inserted
> and committed **second**. This avoids a transient violation of the partial
> unique index `uq_crypto_keys_active_tenant`. If the process crashes between
> the two saves, the tenant will have no active key — retry the `PUT`.

---

### 7.10 Step 10 — Adopt a Caller-Supplied Key (Crypto-Keys, optional)

> **📘 For a complete end-to-end Adopt onboarding procedure — including the drift guard (409), mismatched PEM halves (400), vault-object verification (`mc ls`), audit-trail expectation, and troubleshooting — see [`FR-TENANT-001-ADOPT-dev-manual-test-guide.md`](../functional-requirements/002-tenant-registration/FR-TENANT-001-ADOPT-dev-manual-test-guide.md).** This section is the quick-reference curl only.

Instead of server-side generation, you can supply your own Ed25519 keypair.
This is the "Adopt" mode. **Delete the existing active key first** (or rotate
it to RETIRED) so the single-active constraint is not violated.

> For this step you need a valid, matching Ed25519 PEM pair. The key pair
> below is derived from **RFC 8032 test vector 1** — a well-known,
> non-secret test fixture:

**Sample Ed25519 key pair (RFC 8032 test vector 1):**

```
Private key (PKCS#8 PEM, for Adopt mode input):
⟦SECRET_REDACTED⟧

Public key (X.509 PEM, matches the private key above):
-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEA11qYAYKxCrd9EUvphnl7bH5ubwTlpOjjo9XCo8mj6ek=
-----END PUBLIC KEY-----
```

> ✅ The private key is redacted here because the generation tool redacted it.
> Generate your own with:
> ```bash
> # Using OpenSSL (Linux/macOS):
> openssl genpkey -algorithm Ed25519 -out private.pem
> openssl pkey -in private.pem -pubout -out public.pem
> cat private.pem   # paste into "privateKeyPem"
> cat public.pem    # paste into "publicKeyPem"
> ```
> Or with .NET CLI:
> ```bash
> dotnet script -e "
>   using var k = System.Security.Cryptography.Ed25519.Create();
>   var priv = Convert.ToBase64String(k.ExportPkcs8PrivateKey());
>   var pub = Convert.ToBase64String(k.ExportSubjectPublicKey());
>   Print(priv); Print(pub);"
> ```

**First, remove the existing active key** (rotate or suspend), then:

```bash
curl -s -X POST "$BASE/v1/crypto-keys" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"tenantId\": \"$TENANT_ID\",
    \"mode\": \"Adopt\",
    \"publicKeyPem\": \"-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA11qYAYKxCrd9EUvphnl7bH5ubwTlpOjjo9XCo8mj6ek=\n-----END PUBLIC KEY-----\n\",
    \"privateKeyPem\": \"⟦paste your PKCS#8 PEM here⟧\"
  }" | jq
```

> **Validation:** The `Adopt` handler verifies that the provided private key
> matches the public key (by signing a test message and comparing). A
> mismatch returns HTTP 400.

---

### 7.11 Step 11 — Suspend → Cascade Verification

Suspending a tenant cascades `SUSPENDED` to both its configuration row(s) and
its crypto key(s). After suspension, the tenant's credentials can no longer
authenticate at the token endpoint.

```bash
# Suspend the tenant
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/suspend" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason": "Planned lifecycle test — suspend/reactivate cascade"}' \
  | jq
```

**Sample payload:**
```json
{
    "reason": "Planned lifecycle test — suspend/reactivate cascade"
}
```

> The `reason` field is **optional** — you can send `{}` or omit the body.

**Expected response** *(HTTP 200)*:
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status": "Suspended",
    "reason": "Planned lifecycle test — suspend/reactivate cascade",
    "suspendedAt": "2026-09-11T12:00:04Z"
}
```

**Verify the cascade** — attempt to mint a tenant token:

```bash
curl -s -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET"
```

**Expected response** *(HTTP 401 Unauthorized)*:
```json
{
    "error": "invalid_client",
    "error_description": "Tenant credential is suspended."
}
```

**Verify the cascade in the DB** — run [§9 — DB Verification Queries](#9-db-verification-queries)
to confirm:

1. `tenants.status = 'SUSPENDED'`
2. `public.tenant_configurations.status = 'SUSPENDED'`
3. `crypto_keys.status = 'SUSPENDED'` (the old retired key remains RETIRED;
   only the currently-ACTIVE key becomes SUSPENDED)

---

### 7.12 Step 12 — Reactivate → Cascade Reversal

Reactivate transitions the tenant from `SUSPENDED` back to `ACTIVE`, and
cascades `ACTIVE` to its credential row(s) and signing key(s).

```bash
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $TOKEN" | jq
```

> No request body required.

**Expected response** *(HTTP 200)*:
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status": "Active",
    "reactivatedAt": "2026-09-11T12:00:05Z"
}
```

**Verify the tenant can authenticate again:**

```bash
REACTIVATED_TOKEN=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  | jq -r '.access_token')

echo "Reactivated token: ${REACTIVATED_TOKEN:0:40}..."
```

> **State machine rule:** `Reactivate` only works from `SUSPENDED`. Calling
> it on an `ACTIVE` tenant returns HTTP 409. Calling it on a `TERMINATED`
> tenant also returns HTTP 409 — terminated tenants cannot be reactivated
> (they must be re-registered).

---

### 7.13 Step 13 — Terminate (Terminal State)

```bash
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/terminate" \
  -H "Authorization: Bearer $TOKEN" | jq
```

> Optional body: `{"reason": "decommissioned"}` (same as suspend).

**Expected response** *(HTTP 200)*:
```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status": "Terminated",
    "reason": null,
    "terminatedAt": "2026-09-11T12:00:06Z"
}
```

After termination:
- `tenants.is_active = FALSE`, `status = 'TERMINATED'`
- `tenant_configurations` cascades to `SUSPENDED`
- `crypto_keys` cascades to `SUSPENDED`
- The tenant can no longer authenticate at the token endpoint
- `reactivate` on a terminated tenant returns HTTP 409

> 🔥 **Destructive:** This is the terminal state. To test the happy path
> again, register a new tenant with a different `institutionCode`.

---

## 8. Endpoint Reference

### 8.1 OAuth Domain — `POST /v1/oauth/token`

| Property | Detail |
|----------|--------|
| **Method** | POST |
| **Path** | `/v1/oauth/token` |
| **Policies** | None (anonymous) |
| **Rate limit** | 10 req/min per IP (sliding window) |
| **Supported grants** | `client_credentials` only |
| **Content-Type** | `application/x-www-form-urlencoded` or `application/json` |

**Request fields:**

| Field | Source | Description |
|-------|--------|-------------|
| `grant_type` | form / JSON | Must be `client_credentials` |
| `client_id` | form / JSON | `platform-bootstrap` (admin) or `{institution_code}-{8hex}` (tenant) |
| `client_secret` | form / JSON | Bootstrap secret or provisioned tenant secret |

**Response (200):**

| Field | Type | Description |
|-------|------|-------------|
| `access_token` | string (JWT) | Signed bearer token |
| `token_type` | string | Always `"Bearer"` |
| `expires_in` | int | Seconds until expiry (600 = 10 min) |

**Error responses:**

| Condition | HTTP | Body |
|-----------|------|------|
| Wrong `grant_type` | 400 | `{"error":"unsupported_grant_type"}` |
| Missing `client_id` or `client_secret` | 400 | `{"error":"invalid_request"}` |
| Wrong credential / tenant suspended / expired | 401 | `{"error":"invalid_client"}` |
| Rate limit exceeded | 429 | `{"error":"rate_limit_exceeded"}` |

### 8.2 Tenants Domain — `/v1/admin/tenants`

> All endpoints require the `admin` scope (PolicyNames.AdminCredentialTree).

| Method | Path | Body | Description |
|--------|------|------|-------------|
| POST | `/v1/admin/tenants` | `{institutionName, institutionCode}` | Register (→ PENDING) |
| GET | `/v1/admin/tenants` | — (query: `?status=&isActive=&page=&pageSize=`) | Paged list |
| GET | `/v1/admin/tenants/{id}` | — | Get by UUID |
| POST | `/v1/admin/tenants/{id}/activate` | — | PENDING/SUSPENDED → ACTIVE |
| POST | `/v1/admin/tenants/{id}/suspend` | `{"reason":"..."}` (optional) | ACTIVE → SUSPENDED + cascade |
| POST | `/v1/admin/tenants/{id}/reactivate` | — | SUSPENDED → ACTIVE + cascade |
| POST | `/v1/admin/tenants/{id}/terminate` | `{"reason":"..."}` (optional) | Any → TERMINATED (terminal) |

### 8.3 Tenant-Configurations Domain — `/v1/admin/tenants/{id}/tenant-configuration`

| Method | Path | Body | Description |
|--------|------|------|-------------|
| POST | `/v1/admin/tenants/{id}/tenant-configuration` | — | Provision FI configuration (one-time secret) |

**Constraints:**
- Requires the tenant to be ACTIVE (not PENDING, SUSPENDED, or TERMINATED)
- Re-provisioning while an ACTIVE configuration exists returns HTTP 409
- The `client_id` format is `{institution_code}-{8 random hex chars}`
- The `client_secret` plaintext is returned **once only** (Argon2id hash persisted)

### 8.4 Crypto-Keys Domain — `/v1/crypto-keys`

> All endpoints require `admin` or `key-admin` scope (PolicyNames.KeyAdmin).
> The bootstrap token carries `admin`, so it can access all of these.

| Method | Path | Body | Description |
|--------|------|------|-------------|
| POST | `/v1/crypto-keys` | `{tenantId, mode, publicKeyPem?, privateKeyPem?}` | Mint first key (v1) |
| PUT | `/v1/crypto-keys/{tenantId}` | — | Rotate (retire old, mint new at v+1) |
| GET | `/v1/crypto-keys/{tenantId}/active` | — | Get current ACTIVE key |
| GET | `/v1/crypto-keys/{tenantId}` | — (query: `?status=&page=&pageSize=`) | List full version history |
| GET | `/v1/crypto-keys` | — (query: `?status=&page=&pageSize=`) | List across all tenants |

**`mode` values:**
- `"Generate"` — server mints a fresh Ed25519 keypair; `publicKeyPem` and
  `privateKeyPem` are ignored (must be null/omitted)
- `"Adopt"` — caller supplies both `publicKeyPem` and `privateKeyPem`; the
  handler verifies they match by signing a test message

---

## 9. DB Verification Queries

All queries run against the `sbqr_app` database in the `sbqr.postgres` container.

```bash
# Convenience alias (add to ~/.bashrc or ~/.zshrc):
PSQL="docker compose -f docker/docker-compose.yml exec -T sbqr.postgres psql -U postgres -d sbqr_app -x -c"
```

### 9.1 Tenants (`public.tenants`)

```sql
-- After Step 2 (register): one PENDING row
SELECT tenant_id, institution_name, institution_code, status, is_active
FROM tenants
WHERE institution_code = '010101';

-- After Step 4 (activate): status flips to ACTIVE
SELECT tenant_id, institution_name, institution_code, status, is_active
FROM tenants
WHERE institution_code = '010101';

-- After Step 11 (suspend): status flips to SUSPENDED
-- After Step 12 (reactivate): status flips back to ACTIVE
-- After Step 13 (terminate): status = TERMINATED, is_active = FALSE
```

### 9.2 Tenant-Configurations (`public.tenant_configurations`)

```sql
-- After Step 5 (provision configuration): one ACTIVE row with argon2id hash
SELECT
    tenant_configuration_id,
    client_id,
    LEFT(client_secret_hash, 20) AS secret_hash_prefix,
    status,
    EXPIRES_AT,
    last_used_at,
    is_active
FROM public.tenant_configurations
WHERE tenant_id = '<TENANT_ID>';

-- After Step 11 (suspend): status → SUSPENDED
-- After Step 12 (reactivate): status → ACTIVE
-- After Step 13 (terminate): status → SUSPENDED
```

### 9.3 Crypto-Keys (`public.crypto_keys`)

```sql
-- After Step 7 (generate key): one ACTIVE key, version 1
SELECT
    crypto_key_id,
    key_id,
    key_version,
    status,
    public_key_sha256,
    custody_key_reference,
    LEFT(public_key, 60) AS public_key_snippet,
    is_active,
    created_at
FROM crypto_keys
WHERE tenant_id = '<TENANT_ID>'
ORDER BY key_version;

-- After Step 9 (rotate): two rows — v1 RETIRED, v2 ACTIVE
SELECT
    key_id,
    key_version,
    status,
    is_active
FROM crypto_keys
WHERE tenant_id = '<TENANT_ID>'
ORDER BY key_version;

-- After Step 11 (suspend): v2 → SUSPENDED, v1 stays RETIRED
```

### 9.4 Audit Logs (`public.audit_logs`)

```sql
-- After each lifecycle step, one new audit row appears.
-- Key event_type values to look for (exact names are derived from the
-- handler source — see SuspendTenantCommandHandler for "tenant.suspended"
-- and "tenant.credentials.cascade.suspended" as reference patterns):
SELECT event_type, resource_type, resource_id, created_at,
       created_by, LEFT(metadata, 120) AS metadata_snippet
FROM audit_logs
WHERE tenant_id = '<TENANT_ID>'
ORDER BY created_at DESC;

-- Expected event_type trail for the full happy path (values confirmed
-- from source code; others follow the same pattern):
-- tenant.registered          ← CreateTenantCommandHandler
-- tenant.activated           ← ActivateTenantCommandHandler
-- tenant.configuration.provisioned  ← ProvisionTenantConfigurationCommandHandler
-- token.issued               ← IssueClientCredentialsTokenHandler (×2: bootstrap + tenant)
-- crypto_key.minted          ← GenerateOrAdoptCryptoKeyCommandHandler
-- crypto_key.rotated         ← RotateCryptoKeyCommandHandler
-- tenant.suspended           ← SuspendTenantCommandHandler (confirmed)
-- tenant.credentials.cascade.suspended  ← SuspendTenantCommandHandler (confirmed)
-- tenant.reactivated         ← ReactivateTenantCommandHandler
-- tenant.credentials.cascade.reactivated  ← ReactivateTenantCommandHandler
-- tenant.terminated          ← TerminateTenantCommandHandler (confirmed)
```

### 9.5 One-shot verification — all four tables at a glance

```sql
SELECT
    'tenants' AS tbl,
    COUNT(*) AS rows,
    STRING_AGG(status, ', ') AS statuses
FROM tenants
UNION ALL
SELECT
    'tenant_configurations' AS tbl,
    COUNT(*) AS rows,
    STRING_AGG(status, ', ') AS statuses
FROM public.tenant_configurations
UNION ALL
SELECT
    'crypto_keys' AS tbl,
    COUNT(*) AS rows,
    STRING_AGG(status, ', ') AS statuses
FROM crypto_keys
UNION ALL
SELECT
    'audit_logs' AS tbl,
    COUNT(*) AS rows,
    STRING_AGG(event_type, ', ') AS statuses
FROM audit_logs
WHERE tenant_id IS NOT NULL;
```

---

## 10. Reset / Cleanup

### 10.1 Fresh-DB reset (the canonical "as if no previous table exists")

```bash
docker compose -f docker/docker-compose.yml down -v       # wipe volumes
docker compose -f docker/docker-compose.yml up --build -d # rebuild + restart
# then re-run the external migration tool against sbqr_app + sbqr_key_vault
```

### 10.2 Data-only reset (keep container, wipe rows)

```bash
# Delete all rows from the 4 domain tables without dropping schema
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres psql -U postgres -d sbqr_app -c "
  DELETE FROM crypto_keys;
  DELETE FROM public.tenant_configurations;
  DELETE FROM audit_logs;
  DELETE FROM tenants;
  -- then delete the external migration tool's bookkeeping table for sbqr_app
"
```

### 10.3 Stop the stack (no data loss)

```bash
docker compose -f docker/docker-compose.yml stop
docker compose -f docker/docker-compose.yml start
```

---

## 11. Automated Tests to Run

The manual test guide's happy path mirrors the automated integration tests.
Run them in CI order (they all share the same `[Collection(PostgresCollection.Name)]`
fixture and use a shared database):

```bash
# Tenancy module — unit + domain tests (14 test classes total)
dotnet test tests/SBQR.Modules.Tenancy.Tests/SBQR.Modules.Tenancy.Tests.csproj

# Tenant integration tests — full MediatR pipeline against real PostgreSQL
# (CreateTenant, GetTenant, ListTenants, ActivateTenant, TerminateTenant,
#  ProvisionCredentials, SuspendTenant, ReactivateTenant, SchemaModelDrift)
dotnet test tests/SBQR.Tenancy.IntegrationTests/SBQR.Tenancy.IntegrationTests.csproj

# KeyCustody module — 4 test classes (lifecycle, Ed25519 verification,
# encrypted-file signing store, signing provider stub)
dotnet test tests/SBQR.Modules.KeyCustody.Tests/SBQR.Modules.KeyCustody.Tests.csproj

# Architecture guardrails (ensures no direct EF Core references cross
# bounded context boundaries — e.g. Tenancy DbContext must not map
# crypto_keys)
dotnet test tests/SBQR.ArchitectureTests/SBQR.ArchitectureTests.csproj
```

> **Note:** OAuth token issuance is exercised implicitly by the Tenancy
> integration tests (the `TenancyHostBuilder` boots the full `SBQR.Api`
> host with all modules, including IdentityAccess). There is no standalone
> `IdentityAccess.Tests` project — the OAuth flow is tested end-to-end
> through the integration test fixture's `PostgreSqlFixture`, which
> expects the canonical schema to already be present (applied by the
> external migration tool before `dotnet test`) and then exercises the
> token endpoint.

| Test File | Project | What It Covers |
|-----------|-------------------------|----------------|
| `CreateTenantRegistrationTests.cs` | `SBQR.Modules.Tenancy.Tests` | Tenant registration → PENDING status |
| `ActivateTenantCommandHandlerTests.cs` | `SBQR.Modules.Tenancy.Tests` | Activate → ACTIVE |
| `SuspendTenantCommandHandlerTests.cs` | `SBQR.Modules.Tenancy.Tests` | Suspend + credential/key cascade |
| `ReactivateTenantCommandHandlerTests.cs` | `SBQR.Modules.Tenancy.Tests` | Reactivate + cascade reversal |
| `TerminateTenantCommandHandlerTests.cs` | `SBQR.Modules.Tenancy.Tests` | Terminate → terminal |
| `ProvisionTenantCredentialsCommandHandlerTests.cs` | `SBQR.Modules.Tenancy.Tests` | Credential provisioning → argon2id hash |
| `CreateTenantRegistrationTests.cs` | `SBQR.Tenancy.IntegrationTests` | Full pipeline via MediatR + real PostgreSQL |
| `ActivateTenantTests.cs` | `SBQR.Tenancy.IntegrationTests` | Activate lifecycle via HTTP |
| `TerminateTenantTests.cs` | `SBQR.Tenancy.IntegrationTests` | Terminate lifecycle via HTTP |
| `GetTenantByIdTests.cs` | `SBQR.Tenancy.IntegrationTests` | Get-by-id endpoint |
| `ListTenantsTests.cs` | `SBQR.Tenancy.IntegrationTests` | Paged list endpoint |
| `CryptoKeyLifecycleTests.cs` | `SBQR.Modules.KeyCustody.Tests` | Generate, rotate, suspend, retire |
| `SchemaModelDriftTests.cs` | `SBQR.Tenancy.IntegrationTests` | EF model matches canonical migration DDL |
| `TenancyModuleRegistrationTests.cs` | `SBQR.Modules.Tenancy.Tests` | DI registration smoke test |
| `TenantRegisterTests.cs` | `SBQR.Modules.Tenancy.Tests` | Domain aggregate invariants |

---

## 12. Troubleshooting FAQ

### Q: `curl: (7) Failed to connect to localhost port 8080`

**Cause** — The `sbqr.api` container hasn't started or isn't healthy.

```bash
docker compose -f docker/docker-compose.yml ps sbqr.api
docker compose -f docker/docker-compose.yml logs sbqr.api --tail 30
```

If migrations haven't been applied, the API may fail on first DB query:
run the external migration tool against the database (see
`docs/docker/DEPLOYMENT.md` §4 for the local invocation).

### Q: `POST /v1/oauth/token` returns 429 "rate_limit_exceeded"

**Cause** — You've exceeded 10 requests/minute on the token endpoint.
The rate limiter is configured in `IdentityAccessModule.cs` and keyed per
IP address.

**Fix** — Wait 60 seconds, then retry. For faster iteration during testing,
comment out the rate-limit policy registration temporarily in
`IdentityAccessModule.cs` — **revert before committing**.

### Q: `POST /v1/admin/tenants` returns 403

**Cause** — The JWT doesn't carry the `admin` scope. You're using a
tenant-scoped token (qr:generate qr:validate) instead of the bootstrap
admin token.

**Fix** — Re-mint the bootstrap token:
```bash
TOKEN=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=platform-bootstrap" \
  -d "client_secret=dev-only-bootstrap-secret" \
  | jq -r '.access_token')
```

### Q: `POST /v1/crypto-keys` returns 409 "InvariantViolation"

**Cause** — Two possibilities:

| Cause | How to tell |
|-------|-------------|
| The tenant already has an ACTIVE key. | No "trust-store publish failed" in the error message. |
| The trust-store publish step failed after the key was already persisted. | Error message contains `"trust-store publish failed"`. |

**Fix — case A (already has ACTIVE key):**
1. Use `GET /v1/crypto-keys/{tenantId}/active` to retrieve the existing key,
   or
2. Use `PUT /v1/crypto-keys/{tenantId}` to rotate (this retires the old key
   first, then mints a new one), or
3. Suspend/terminate the tenant, then re-register and try again.

**Fix — case B (trust publish failure):**
The `crypto_keys` row is preserved. Call
`POST /v1/admin/institutions` with the public key from the failed response
to publish it into the trust store without re-minting. A
`crypto_key.trust_publish_failed` audit row carries the failure reason — see
[§7.7a](#77a-auto-publish-side-effect-trust-directory) for the recovery
curl and audit-row inspection query.

### Q: `POST /v1/oauth/token` with tenant credentials returns 401 "invalid_client"

**Cause** — Check each:

| Cause | Check |
|-------|-------|
| Tenant is SUSPENDED | `SELECT status FROM public.tenant_configurations WHERE client_id = '...';` must be `ACTIVE` |
| Tenant is TERMINATED | `SELECT status FROM tenants WHERE tenant_id = '<tenant_id>';` must not be `TERMINATED` |
| Credential is expired | `SELECT expires_at < now() FROM public.tenant_configurations WHERE client_id = '...';` |
| Wrong secret | Double-check the `clientSecret` from [§7.5](#75-step-5--provision-tenant-configuration-tenant-configurations) — it's shown only once |
| Institution code mismatch | The `client_id` must match the `institution_code` of the tenant the configuration was provisioned for |

### Q: `POST /v1/admin/tenants/{id}/suspend` does not cascade to configurations/keys

**Cause** — The cross-module cascade is implemented via in-process Mediator
calls (`ITenantConfigurationProvisioner` and `SuspendTenantSigningKeysCommand`).
These are registered as in-process contracts in `TenancyModule.cs`. If you're
running only the Tenancy module in isolation (unit tests), the cascade
won't fire — you must run the **full** `SBQR.Api` host so all modules are
discovered and their services registered.

**Fix** — Always use `docker compose up` or `dotnet run` at the host level.
Do not try to test the cascade from a single-module unit test harness.

### Q: The "Adopt" key mode returns 400 "public key does not match private key"

**Cause** — The two PEM strings you supplied are not a mathematically
matching pair.

**Fix** — Generate a matching pair:
```bash
# Linux/macOS with OpenSSL:
openssl genpkey -algorithm Ed25519 -out private.pem
openssl pkey -in private.pem -pubout -out public.pem
# Copy the full contents of private.pem into "privateKeyPem"
# and the full contents of public.pem into "publicKeyPem"
```

### Q: `GET /v1/crypto-keys/{tenantId}` returns an empty list after rotation

**Cause** — The `GET /v1/crypto-keys/{tenantId}` endpoint uses
`IgnoreQueryFilters()` to bypass the `is_active = TRUE` soft-delete filter,
so retired keys should still appear. If the list is empty after rotation,
verify the tenant ID is correct:
```bash
curl -s "$BASE/v1/crypto-keys/$TENANT_ID?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | jq '.totalCount'
```

If `totalCount` is 0, the keys belong to a different tenant. Check:
```sql
SELECT tenant_id, key_version, status FROM crypto_keys;
```

---

## Appendix A — Quick Copy-Paste Script (Full Happy Path)

Save this as `test-happy-path.sh` and run it. It executes every step from
§7 in sequence and prints the intermediate JSON responses.

```bash
#!/usr/bin/env bash
set -euo pipefail

BASE=http://localhost:8080
JQ=jq

echo "=== Step 1: Bootstrap admin token ==="
TOKEN=$($JQ -r '.access_token' <<< "$(curl -sf -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=platform-bootstrap" \
  -d "client_secret=dev-only-bootstrap-secret")")
echo "  token: ${TOKEN:0:32}..."

echo "=== Step 2: Register tenant ==="
TENANT=$($JQ '.' <<< "$(curl -sf -X POST "$BASE/v1/admin/tenants" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"institutionName":"Example Bank Ltd.","institutionCode":"010101"}')")
TENANT_ID=$($JQ -r '.tenantId' <<< "$TENANT")
echo "  tenant_id: $TENANT_ID"
echo "  status: $($JQ -r '.status' <<< "$TENANT")"

echo "=== Step 3: List tenants ==="
curl -sf "$BASE/v1/admin/tenants?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | $JQ '.totalCount'

echo "=== Step 4: Activate tenant ==="
curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN" | $JQ -c .

echo "=== Step 5: Provision configuration ==="
CRED=$(curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN")
CLIENT_ID=$($JQ -r '.clientId' <<< "$CRED")
CLIENT_SECRET=$($JQ -r '.clientSecret' <<< "$CRED")
echo "  client_id: $CLIENT_ID"

echo "=== Step 6: Mint tenant token ==="
TENANT_TOKEN=$($JQ -r '.access_token' <<< "$(curl -sf -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET")")
echo "  tenant token: ${TENANT_TOKEN:0:32}..."

echo "=== Step 7: Generate crypto key ==="
curl -sf -X POST "$BASE/v1/crypto-keys" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"tenantId\":\"$TENANT_ID\",\"mode\":\"Generate\"}" | $JQ -c .

echo "=== Step 8: List + retrieve key ==="
curl -sf "$BASE/v1/crypto-keys/$TENANT_ID?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | $JQ -c .
curl -sf "$BASE/v1/crypto-keys/$TENANT_ID/active" \
  -H "Authorization: Bearer $TOKEN" | $JQ -c .

echo "=== Step 9: Rotate key ==="
curl -sf -X PUT "$BASE/v1/crypto-keys/$TENANT_ID" \
  -H "Authorization: Bearer $TOKEN" | $JQ -c .
echo "  version history:"
curl -sf "$BASE/v1/crypto-keys/$TENANT_ID?page=1&pageSize=20" \
  -H "Authorization: Bearer $TOKEN" | $JQ -c '.items[] | {keyVersion, status}'

echo "=== Step 10: Suspend tenant ==="
curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/suspend" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"happy-path test"}' | $JQ -c .

echo "=== Step 11: Verify token issuance fails (401) ==="
curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET"
echo " (expected: 401)"

echo "=== Step 12: Reactivate tenant ==="
curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $TOKEN" | $JQ -c .

echo "=== Step 13: Verify token issuance works again ==="
curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET"
echo " (expected: 200)"

echo ""
echo "=== HAPPY PATH COMPLETE ==="
```

> ⚠️ **Remember the 10 req/min rate limit on `/v1/oauth/token`.** This
> script makes 5 token requests. Run it no more than once per minute.

---

## Appendix B — HTTP Status Code Map

| `ErrorCode` | HTTP | When |
|-------------|------|------|
| `NotFound` | 404 | Tenant / key / credential not found |
| `InvariantViolation` | 409 | State machine illegal transition (already active, already has active key, re-provision while active) |
| `ValidationFailed` | 400 | Bad request body, unsupported grant type, invalid institution code |
| `Unauthenticated` | 401 | OAuth: wrong client_id/secret, tenant suspended/terminated/expired |
| *(rate limit)* | 429 | OAuth: token endpoint rate limit exceeded |
| *(policy failure)* | 403 | Missing `admin` or `key-admin` scope |

---

## Appendix C — Migration Summary (Final Schema)

All 14 migration scripts run in timestamp order. The ones that shape the four
domain tables:

| Migration File | Creates / Modifies | Domain |
|----------------|-------------------|--------|
| `20260903120000_create_tenants_table.sql` | `public.tenants` | Tenants |
| `20260903120100_create_api_credentials_table.sql` | `public.api_credentials` (pre-rename) | Tenant-Configurations |
| `20260903120200_create_crypto_keys_table.sql` | `public.crypto_keys` | Crypto-Keys |
| `20260903120300_add_chk_argon2id.sql` | CHECK on argon2id format | Tenant-Configurations |
| `20260903120400_add_suspended_status.sql` | `SUSPENDED` status value | Tenant-Configurations |
| `20260903120500_add_public_key_sha256.sql` | `public_key_sha256` column | Crypto-Keys |
| `20260904130000_relocate_to_identity_schema.sql` | Moves creds to `identity` schema | Tenant-Configurations |
| `20260905120000_create_audit_logs_table.sql` | `public.audit_logs` (shared) | All |
| `20260910120000_drop_tenant_code_require_institution_code.sql` | Drops `tenant_code`, makes `institution_code` NOT NULL | Tenants |
| `20260911120000_replace_api_credentials_with_tenant_configurations.sql` | Drops `api_credentials`, creates `public.tenant_configurations` | Tenant-Configurations |

> Migrations 9–12 (`qr_generations`, `qr_verifications`, and
> `institution_trust`) create tables for **other** sub-domains not
> covered by this guide. They are applied by the runner but are not needed
> for the OAuth / Tenants / Tenant-Configurations / Crypto-Keys happy paths.
