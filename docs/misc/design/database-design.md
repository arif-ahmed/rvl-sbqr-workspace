# SBQR Database Design — canonical schema

Status: **agreed 2026-09-08, reconciled with the implemented modules same day.**
The runnable source of truth is [`db/migrations/`](../../db/migrations/README.md)
(applied by the external migration tool — see
[`docs/PERSISTENCE_DECISIONS.md`](../PERSISTENCE_DECISIONS.md) §3 and
[`docs/docker/DEPLOYMENT.md`](../docker/DEPLOYMENT.md)). This document
explains the design; when they disagree, the SQL wins and this doc gets a
PR.

## Two databases

| Database | Contents | Why separate |
|---|---|---|
| `sbqr_app` | All public-side state (this document) | The transactional plane |
| `sbqr_key_vault` | Wrapped (encrypted) private-key material only — **schema script still TBD** | Blast-radius isolation: a compromised app subnet cannot reach the vault server (separate PostgreSQL instance in production; colocated locally). C23 applies to both. |

## One schema per module (`001_module_schemas.sql`)

The physical mirror of the module boundaries: a reviewer who sees
`issuance`-style cross-schema references in a query knows immediately a
module boundary is being crossed. Grants are per schema (C20).

| Schema | Module | Tables |
|---|---|---|
| `tenancy` | Tenancy | `tenants` |
| `identity` | IdentityAccess | `tenant_configurations` (client credentials — relocated from the legacy `api_credentials`) |
| `generation` | QrGeneration | `qr_generations` |
| `verification` | Verification | `qr_validations` |
| `institution_trust` | InstitutionTrust | `institution_registries`, `institution_keys`, `trust_sync_runs` — platform-level, **no tenant_id by design** |
| `key_custody` | KeyCustody | `crypto_keys` |
| `audit` | Audit | `audit_logs` (+ `audit_logs_seq`) |

Cross-schema FKs to `tenancy.tenants` are kept for GA referential integrity
and are documented extraction debt (drop to weak uuid references when a
module is extracted). `audit_logs` has **no** FK to tenants: the audit log
outlives tenants and the audit module must not couple to tenancy.

## Table-by-table decisions

### tenancy.tenants — settled with TL

Columns as agreed with the TL; migrations add only indexes
(`ix_tenants_institution_code` UNIQUE, `ix_tenants_status`,
`ix_tenants_active`). `institution_code` (6 digits, BB-assigned) is the
join key to the trust directory — uniqueness is DB-enforced.

### identity.tenant_configurations

Machine-to-machine client credentials (`client_id` UNIQUE — the auth hot
path resolves client → tenant on every request; `client_secret_hash` is an
Argon2id PHC string, never plaintext), plus the two capability flags
(`is_qr_generation_allowed`, `is_qr_validation_allowed`). Status vocabulary
includes `PENDING_ROTATION` to match `TenantConfigurationConfiguration`.

### generation.qr_generations — append-only

- **No `payload_hash` column** (decision 2026-09-08: integrity rests on the
  QR signature + CRC). The hash is still computed for the API response and
  the audit `resource_id` — never persisted.
- `signature_key_version` points at `key_custody.crypto_keys`
  `(tenant_id, key_id, key_version)` for post-rotation verification and
  blast-radius queries. Weak reference — no FK across the module boundary.
- `idempotency_key` (A9): optional in the API contract
  (`Idempotency-Key` header), tenant-scoped, enforced by the partial unique
  index `(tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL`.
  On `23505` the API returns 409 `DUPLICATE_IDEMPOTENCY_KEY` (the payload
  is never persisted, so the original QR cannot be replayed back).
- `modified_*` / `is_active` dropped: an issuance is an immutable
  financial event.

### verification.qr_validations — append-only, and the C6 replay guard

The draft's `qr_transactions` table is gone: foreign-issued codes can never
reference our generation table, and one row per request now carries both
the request context and its outcome.

- `tenant_id` NOT NULL — the **verifying** tenant (C5), resolved server-side
  from the authenticated credential via `ICurrentTenant`; never from the body.
- `request_id` + `request_timestamp` (C6): client-generated per call; the
  handler validates a ±5 min window (defense-in-depth behind the
  FluentValidation rule). `UNIQUE (tenant_id, request_id)` makes each
  request single-use — a replayed insert raises `23505`, converted into a
  `REQUEST_REPLAYED` / `REPLAY_DETECTED` response; the rejection itself is
  audited (`qr.validation.rejected`), not re-recorded.
- `correlation_id` (A5): server-generated; also embedded in audit metadata.
- `institution_code` nullable — a malformed payload has no extractable
  issuer and the row must still persist.
- `verdict` vocabulary = the implemented `QrVerdict` enum plus
  `REQUEST_STALE` / `REQUEST_REPLAYED`; `trust_source` ∈
  `OWN_CUSTODY | TRUST_DIRECTORY | NONE`; `reason_code` mandatory for every
  non-passing verdict (A11), enforced by CHECK.

### institution_trust.institution_registries / institution_keys

The mirrored BB registry directory (one row per participating institution)
plus its versioned public keys — the single source of truth for ALL public
keys used at verification. Platform-level: no tenant_id.

- `institution_keys` carries `key_version` (UNIQUE per institution) and
  `public_key_sha256` — kept after the 2026-09-08 "avoid" decision was
  revisited: the implemented sync computes/returns the fingerprint and
  audit entries carry it. New sync ⇒ next version; the previous ACTIVE key
  flips to `RETIRED` (revocation is only ever explicit).
- Added trust-window columns (C4/C16): `source` (`REGISTRY` | `LOCAL` —
  `LOCAL` reserved for our own tenants' keys published by KeyCustody, and
  protects them from being clobbered by the registry sync), `status`,
  `valid_from`, `valid_to`, `revoked_at`, `synced_at`.

### key_custody.crypto_keys

The signing-key catalog following the implemented lifecycle
(`GENERATING → PENDING → ACTIVE → SUSPENDED/RETIRING → RETIRED`, `REVOKED`).
At most one ACTIVE key per tenant (partial unique index) — the activation
gate the generation path relies on.

- `custody_key_reference` is the opaque vault/HSM handle
  (`tenant:<guid>:institution:<6>:<keyId>:v<n>`). The wrapped private
  bytes live only in `sbqr_key_vault`.
- **Deferred delta (decision 2026-09-08):** `public_key` /
  `public_key_sha256` were to be REMOVED (read public keys from
  `institution_keys`, extra caution for key safety). Removal requires the
  coordinated KeyCustody→InstitutionTrust publish flow (Generate/Adopt
  publishing the public PEM as `institution_keys.source = 'LOCAL'`); until
  that refactor lands, both columns stay because the implemented
  factories, domain events, custody providers, API summaries and tests
  consume them. See "Deferred deltas" below.
- Added (C4): `valid_from`, `valid_to`, `rotated_at` — the ≤90-day rotation
  policy computes `valid_to` at activation; alerting checks it.

### audit.audit_logs — append-only, hash-chained, monthly partitions

C8 tamper evidence with **no second table**: `sequence`,
`previous_hash`, `entry_hash` live on the row itself.

Write protocol (implemented in
`SBQR.Modules.Audit.Infrastructure.AuditLogger`, documented at the top of
`db/migrations/007_audit.sql`): per-tenant (or `SYSTEM`) transaction-scoped
advisory lock → reserve `audit_logs_seq` + read the chain head →
`entry_hash` = SHA-256 over canonical JSON (snake_case keys sorted, no
whitespace, metadata as its masked string, ISO-8601 UTC timestamps) →
insert with explicit `created_at`/`sequence`. Immutability is physical: a
`BEFORE UPDATE OR DELETE OR TRUNCATE` trigger refuses mutation, and the
runtime role holds INSERT/SELECT only. The C8 tamper test = a verification
job that walks each scope by `sequence` recomputing every hash.

Partitioned monthly on `created_at` through 2027-08 with **no DEFAULT
partition** (agreed): an out-of-range insert fails loudly — fail-closed per
A12 — so partition creation is a monitored, blocking ops duty (runbook in
`db/migrations/README.md`).

### institution_trust.trust_sync_runs

Operational history of the registry sync (status CHECK, per-outcome
counters, `triggered_by`, `error_message`).

## Deferred deltas (tracked, not forgotten)

1. **KeyCustody publish flow** — strip `public_key` / `public_key_sha256`
   from `crypto_keys` once Generate/Adopt publishes the public PEM into
   `institution_keys (source='LOCAL')`; touches the aggregate, domain
   events, custody providers, API summaries and their tests.
2. **Audit failure semantics** — `AuditLogger` currently logs-and-swallows
   write failures (epic-9 outbox will revisit). A12's strict reading
   ("audit sink unavailable → reject") is NOT yet the runtime behavior.
3. **Audit correlation propagation** — the correlation id is generated
   inside `AuditLogger`; producers embed their own in metadata (the
   verification handler does). A first-class `AuditEntry.CorrelationId`
   would make it uniform.
4. **RLS** — see the future-consideration section in
   `db/migrations/README.md`: database-enforced tenant isolation layered
   under the app-level filters (C5); requires GUC plumbing + full
   integration-test pass, scheduled post-GA.
5. **`sbqr_key_vault` schema** — the wrapped private-key table still needs
   its own migration script, grants and restore drill (C23).

## Cross-cutting rules

- **UTC only** (A10): everything is `timestamptz`; `Asia/Dhaka` exists at
  presentation only.
- **UUID v7** for hot/append-only PKs (`Guid.CreateVersion7()`) —
  time-ordered ids keep B-tree inserts localized (the 72-hour soak would
  expose random-v4 scatter otherwise).
- **Index names mirror the EF declarations** so the schema-drift test
  (`tests/SBQR.Tenancy.IntegrationTests/Schema/SchemaModelDriftTests.cs`)
  compares equal; the drift readers walk all module schemas.
- **No EF Core migrations, ever** — external raw SQL only
  (PERSISTENCE_DECISIONS §3).
