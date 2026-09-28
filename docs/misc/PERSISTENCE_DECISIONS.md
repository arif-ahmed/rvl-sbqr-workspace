# Persistence Decisions

Numbered so code comments can cite a section (§3 is the one most files
reference). Companion document: [`docs/design/database-design.md`](design/database-design.md).

## §1 — Two databases

`sbqr_app` (public side) and `sbqr_key_vault` (wrapped private-key material
only) are separate databases — separate PostgreSQL instances in production,
colocated in local docker. Blast-radius isolation for key custody (C17/C23).

## §2 — One schema per module

`tenancy`, `identity`, `generation`, `verification`, `institution_trust`,
`key_custody`, `audit` (created by
`db/migrations/001_module_schemas.sql`). Every EF `ToTable` call declares
its schema; grants are per schema (C20). Cross-schema FKs to
`tenancy.tenants` are documented extraction debt.

## §3 — External raw-SQL migrations only; EF Core code-first is forbidden

The canonical schema lives in `db/migrations/*.sql` and is applied by an
external migration tool / DBA — never by the application, never by
`Database.Migrate()`. The runtime DB user has **no DDL** privileges (C20).
Consequences:

- Every `DbContext` in every module NEVER migrates.
- The EF model and the SQL scripts are kept in lockstep by the schema-drift
  test (`SchemaModelDriftTests`) — it compares the EF-declared columns and
  indexes against the live database and fails with a structural diff on
  drift. Index names in migrations therefore mirror the EF `HasDatabaseName`
  declarations.
- Integration-test fixtures apply the `db/migrations` chain to their
  ephemeral Testcontainers — the fixtures play the external tool; the
  application binary still contains no migration runner.

## §4 — Append-only business events

`qr_generations`, `qr_validations` and `audit_logs` are immutable facts: no
`modified_*`/`is_active` columns on the first two; the audit table is
physically immutable (trigger + INSERT-only grants for the runtime role).

## §5 — Audit hash chain (C8)

The chain lives within `audit.audit_logs` (`sequence`, `previous_hash`,
`entry_hash`) — no chain-head table. Writes: per-scope advisory lock →
reserve sequence + read head → canonical-JSON SHA-256 → insert. Full
protocol in `db/migrations/007_audit.sql` (the SQL comment and
`AuditLogger` must stay in sync).

## §6 — UUID v7 for hot tables

Application-generated time-ordered UUIDs (`Guid.CreateVersion7()`) for
`qr_generations`, `qr_validations`, `audit_logs` PKs.

## §7 — Test isolation

Respawn (DELETE-only, FK-safe) resets the six operational schemas between
tests; the `audit` schema is never reset (append-only). Integration
fixtures own their ephemeral PostgreSQL 16 containers.
