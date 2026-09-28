# FR-TENANT-001 — Jira Story & Task Breakdown

Reference spec: [FR-TENANT-001 — Institute (Tenant) Self-Contained Registration](FR-TENANT-001-tenant-registration.md)

## Story

**Title:** Platform admin can register a new institute in a single atomic API call

> **As a** platform operator onboarding a new institute (tenant),
> **I want** a single `POST /v1/admin/tenants:register` call that creates the tenant, provisions the FI client credential, mints the Ed25519 signing key, and auto-publishes the public key to the trust directory,
> **so that** I can take a brand-new institute from "no row in the DB" to "able to mint verifiable QR codes" in one round-trip — instead of the current four-call sequence (`register` → `activate` → `tenant-configuration` → `crypto-keys`).

**Spec:** [FR-TENANT-001](FR-TENANT-001-tenant-registration.md)

**Background.** Today every institute requires four consecutive API
calls (`docs/new-tenant-onboard-guide.md` Steps 2–5), each with its own
rate-limit budget, its own audit row, and — most painfully — its own
window for *partial* state to be visible to a second operator. If the
network fails between Step 4 and Step 5, the tenant exists but has no
signing key and cannot be verified by `qr/validate`. The four-call
sequence is also the dominant source of `new-tenant-onboard-guide.md`
support tickets: "I ran the four calls, but `validate` returns `false`".

**Scope.** This story:

- Adds `POST /v1/admin/tenants:register` to the existing
  `TenantsController`.
- Wires a new MediatR saga
  (`RegisterTenantCommand` / `RegisterTenantCommandHandler`) that
  orchestrates the four already-implemented child command handlers in
  one DB transaction.
- Adds an idempotency policy for the `institutionCode` retry case
  (AC8).
- Deprecates (returns `410` for 6 months) the bare
  `POST /v1/admin/tenants` endpoint; scope-shrinks
  `…/{id}/activate` to `Suspended` tenants only.
- Updates `docs/new-tenant-onboard-guide.md` to delete Steps 2–5 and
  collapse the Appendix B audit table.
- Adds the FR's acceptance-criteria test coverage.

It does **not** change FR-AUTH-001 (admin token mint), the rotation
flow (`PUT /v1/crypto-keys/{tenantId}`), the manual recovery call
(`POST /v1/admin/institutions`), or the suspend/reactivate lifecycle.

## Acceptance Criteria (= FR-TENANT-001 §11)

1. Happy-path single call returns `201` with `tenantId`,
   `credentialId`, `clientId`, `clientSecret`, `cryptoKeyId`,
   `keyVersion=1`, `publicKeyPem`, `status=Active`,
   `trustRegistry.autoPublished=true`.
2. Returned `clientId`/`clientSecret` mint a tenant-scoped JWT
   end-to-end without any further setup call.
3. Tenant is `Active` immediately after the response.
4. Trust registry has the public key at
   `key_version=1, status=ACTIVE`.
5. Audit chain emits the four expected rows in order, all with the
   admin JWT's `sub` as `actor_id`.
6. Duplicate `institutionCode` retry → `409 InvariantViolation`, no
   row duplication.
7. Tenant-scoped token → `403 Forbidden`.
8. Same body + `Idempotency-Key` header twice → second call returns
   the original `201` verbatim, no extra audit rows.
9. Trust-publish DB outage → `201` with
   `trustRegistry.autoPublished=false` and `trustPublishError`,
   `crypto_key.trust_publish_failed` audit row emitted.
10. Validation rejections → `400 ValidationFailed`, no half-created
    rows.
11. Forced failure inside key-mint → full transaction rollback, no
    rows in any of the four tables.
12. Rate-limit policy enforced: 11th call within a minute from one
    IP → `429`.

## Subtasks

| # | Subtask | Type | Constraints |
|---|---|---|---|
| 1 | `RegisterTenantCommand` + `RegisterTenantCommandValidator` records (FluentValidation rules: `institutionName` 1–200, `institutionCode` regex `^[0-9]{6}$`, `credentialExpiry` optional, must be ≤ now+5y, must be future). | Code | A6 |
| 2 | `RegisterTenantCommandHandler` — MediatR saga that opens one transaction, calls the four child handlers in order, and assembles the aggregate response. Re-uses `CreateTenantCommandHandler`, `ActivateTenantCommandHandler`, `ProvisionTenantConfigurationCommandHandler`, `GenerateOrAdoptCryptoKeyCommandHandler` via the existing Contracts seams (`ITenantAdmissionDirectory`, `IInstitutionTrustPublisher`). | Code | C15 |
| 3 | Add `POST /v1/admin/tenants:register` to `TenantsController` with `[Authorize(Policy = PolicyNames.AdminWrite)]` and the existing `RateLimitPolicyNames.AdminWrite` decoration. Add the `[ProducesResponseType]` annotations for 201 / 400 / 403 / 409 / 429. | Code | C15, C7 |
| 4 | Add a new `RegisterTenantResponse` DTO that combines the four child responses. **Hard-pin** the shape with a new `RegisterTenantResponseShapeTests` (the same way `CryptoKeySummaryShapeTests` pins the key DTO). | Code | C19 |
| 5 | Idempotency: `Idempotency-Key` header support on the new endpoint — store the response body keyed by `Idempotency-Key + institutionCode` for 24h, return the stored body on a retry. | Code | C7 |
| 6 | Deprecate `POST /v1/admin/tenants` → return `410 Gone` with `errorCode = EndpointRetired`; keep the contract for 6 months then remove in v2. | Code | C22 |
| 7 | Scope-shrink `POST /v1/admin/tenants/{id}:activate` to `Suspended` only — add a check in `ActivateTenantCommandHandler` that rejects activation on a `Pending` row with `409 InvariantViolation` (this codifies what the saga now does implicitly). | Code | C15 |
| 8 | Atomicity tests: a forced failure in step 4 leaves no rows; audit log gains only an error-class entry. | Tests | A1, DoD |
| 9 | Auto-publish failure surface test: stub `IInstitutionTrustPublisher.UpsertAsync` to throw → response is `201` with `trustRegistry.autoPublished=false`, audit row present. | Tests | DoD |
| 10 | All 12 acceptance criteria tests: unit (validator, saga happy path with mocked child handlers), integration (full stack with Postgres, idempotency replay, rate limit). | Tests | A1, DoD |
| 11 | Update `docs/new-tenant-onboard-guide.md` — delete Steps 2–5, replace with the one-call flow, replace Appendix B (5 audit rows in one call). Add a link to this FR. | Docs | C22 |
| 12 | Update `docs/dev-testing-guide-oauth-tenants-keys.md` §7 (rotation) to call out that rotation (`PUT /v1/crypto-keys/{tenantId}`) is unchanged and that this FR only touches the greenfield path. | Docs | C22 |
| 13 | Migration review: confirm no schema changes are needed; this is an orchestration-layer change. Update the `migration` notes in the docs. | Docs | A4 |
| 14 | OpenAPI doc grouping: this endpoint flows into `v1.public` (it is an admin verb that the operator's runbook calls), not `internal` — confirm the absence of `[ApiExplorerSettings(IgnoreApi = true)]` on the new action. | Docs | superpowers spec 2026-09-04 |

## Definition of Done

- All 12 acceptance criteria automated in
  `tests/SBQR.Tenancy.IntegrationTests/RegisterTenant/` (integration)
  and `tests/SBQR.Modules.Tenancy.Tests/Application/RegisterTenantValidatorTests.cs`
  (unit).
- PR description names the touched constraint ids: **A1, A4, A6, C7,
  C15, C19, C22**, plus the failure-case test ids.
- `docs/new-tenant-onboard-guide.md` reflects the one-call reality and
  links to this FR.
- Architecture tests still pass — the saga lives in the Tenancy
  Application layer and only consumes Contracts seams (no direct
  access to IdentityAccess/KeyCustody/InstitutionTrust aggregates or
  DbContexts).
- Idempotency table is exercised in the integration suite (24-hour
  retention verified by a clock-faked replay test).

## Sizing & Sequencing

Two increments suggested:

- **Increment 1** — Tasks 1, 2, 3, 4, 6, 7 (handler + endpoint +
  deprecation). Validates the happy path (AC1, 3, 5, 6) and the
  deprecation message (AC7).
- **Increment 2** — Tasks 5, 8, 9, 10, 11, 12, 13, 14 (idempotency,
  atomicity failure tests, auto-publish failure surface, docs).

Sizing: ~5–7 story points total (depends on the idempotency table —
if it can lean on an existing Redis-backed cache, the increment is
smaller).

## Notes for Jira

- **Source of truth** — paste FR §11 into the story description.
- **Linked issues** — search for any open ticket that mentions
  "onboarding steps", "tenant registration", "first-time setup"; link
  them as *blocks*.
- **Stakeholders** — Platform ops, Security (for BR5 — plaintext
  clientSecret only ever in the response body, never logs).
- **Out-of-scope reminders** in the PR description: rotation, Adopt
  mode, bulk import, human-driven UI.
- **Migration safety** — no schema changes; purely orchestration. No
  data-backfill required. The deprecation of `POST /v1/admin/tenants`
  is the only externally-visible contract change.

## Open Questions

- **Q1.** Should `RegisterTenantCommandHandler` open its transaction
  at the Tenancy Application layer or at the Host-level
  `IUnitOfWork`? Recommendation: Tenancy Application (matches the
  existing `CreateTenantCommandHandler` boundary; one
  `BeginTransactionAsync` at the start of the saga).
- **Q2.** Idempotency cache backing store: in-memory
  (`IMemoryCache`) is enough for the 24h window if we accept the
  process-restart-loss trade-off. Recommendation: start with
  `IMemoryCache`; move to Redis when we have a session-aware deploy.
- **Q3.** Do we keep `POST /v1/admin/tenants` for 6 months or only one
  release cycle? Recommendation: **6 months** — gives the operator
  runbook time to roll forward, matches our previous
  v1→v2 deprecation window.
