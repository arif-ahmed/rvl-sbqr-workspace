# Institution Onboarding — Implementation Reference

A future-reader's map of the institution-onboarding feature: what it does for the
business, and where every part of it lives in code. Written so you can come back
cold, read ~10 minutes, and jump straight to the code you need.

**Verified against:**

| Repo | Branch | Commit | Date |
|---|---|---|---|
| `rvl-secure-bqr-manager` (backend) | `feature/institution-onboarding` | `6970f4d` | 2026-10-06 |
| `rvl-sbqr-portal` (frontend) | `feature/institution-onboarding-api` | `1c0463b` | 2026-10-05 |

If a path below doesn't resolve, the code moved — the commit SHAs are the fallback
ground truth. Every claim here was read from source, not from a spec.

**Layout:** §1–3 are narrative (read once to get connected). §4–9 are reference
tables (scan on demand). §4 is the spine — most readers come for that one.

---

## 1. The business in one page

Secure Bangla QR is a platform that does two jobs for financial institutions
(FIs, called **tenants** in the code): it **creates signed QR codes** for them,
and it **checks** QR codes for them. Institutions pay per use (that's the
separate metering/billing feature).

**Onboarding is how a new institution joins that platform.** Bangladesh Bank
assigns every FI a six-digit institution code and registers its name in Annex A.
RVL's platform operator takes that regulator-issued identity and turns it into a
working technical tenant: credentials, a signing key, entitlements, and a
trust-directory entry — then switches it live.

| Who | What they are | What they do here |
|---|---|---|
| **Alpha Bank** (`031101`) | A newly registered bank | The institution being onboarded |
| **Operator** (RVL platform admin) | RVL staff who set institutions up | Walks the 5-step wizard |
| **BB** | Bangladesh Bank | Owns the trust store and the Annex A registry |
| **FI backend** | The institution's own server | Later authenticates with the issued credentials |

**One institution, one afternoon.** The operator opens *New institution*, picks
**Alpha Bank** from the directory (never types the name — it comes from the
regulator), fills contact details, says what Alpha may do (generate / validate /
both), issues the API credentials, mints or adopts the signing key, reviews, and
activates. Alpha is now live: its backend can mint QR codes and verify other
banks' codes, signed with a key that other institutions can look up.

**Two business rules that shape everything:**

1. **The institution's identity is fixed at step 1.** The name and code come from
   the regulator's registry, so they can never be typed freely — the picker greys
   out already-registered codes, and the API refuses duplicates with a `409`.
2. **Nothing goes live until it's verifiable.** An institution can only be
   activated once it has an active credential, an active signing key, *and* an
   entry in the trust directory. That last one is why activation is gated rather
   than just a status flip — a signing key nobody can look up produces QRs that
   nobody can verify.

---

## 2. The five steps

The wizard at `/staff/institutions/new` is five steps, in this order. Every step
**saves to the API as it completes**, so closing the tab loses nothing.

| # | Step | What the admin does | Wire code |
|---|---|---|---|
| 1 | **Institution** | Pick from the trust directory / Annex A registry; enter contacts | `PROFILE` |
| 2 | **Configuration** | Choose QR entitlements (generate / validate) | `CONFIGURATION` |
| 3 | **Credentials** | Issue client ID + secret (secret shown **once**) | `CREDENTIALS` |
| 4 | **Signing key** | Generate a key, or paste the institution's own PEM to adopt | `SIGNING_KEY` |
| 5 | **Review** | Confirm everything, then **Activate** | `REVIEW` |

There is also a **rate card** (pricing) — but it is deliberately *not* a wizard
step. It lives in its own surface (`/staff/rates`), surfaces as an advisory
`hasRateCard` flag on the review step, and **does not block activation**.

**Resumability.** The institutions list shows a `3 / 5` progress badge per Pending
institution. "Continue setup" reopens the wizard at the first incomplete step:
`?resume=<tenantId>` → `GET /onboarding` → `currentStep` → open there.
*(Frontend: `onboarding-page.tsx` — `Resume`.)*

---

## 3. Read this first: the reconciler

**This is the one idea you need to understand the whole feature.**

The naive design would store each step's status and trust it. This one doesn't.
`tenant_onboarding_steps` is treated as a **cache, not a source of truth**. On
*every* read of the onboarding view, the server re-derives each step's status
from the **live facts** — does an active credential row exist? an active signing
key? a trust entry? what's the tenant status? — and then writes the result back.

```
GET /onboarding
   ├─ gather live facts   (4 cross-module reads)
   ├─ derive step statuses (pure function)
   └─ upsert drifted rows (idempotent) ──► tenant_onboarding_steps
   └─ return the derived view
```

**Start here:** `src/Modules/Tenancy/SBQR.Modules.Tenancy.Application/Onboarding/OnboardingReconcilerLogic.cs`

That file is **296 lines, zero I/O, zero time, zero actor strings** — the entire
rule set as a pure static class. It's the highest-value file in the feature and
it's fully unit-tested. Read `ResolveStatus` and you understand every step.

The orchestration half (which does the I/O) is `OnboardingReconciler.cs` in the
same folder: it gathers four facts, calls the pure logic, upserts drift.

### The rule table

Each cell is a guard in `OnboardingReconcilerLogic.ResolveStatus`:

| Step | Reported `COMPLETED` when | Otherwise |
|---|---|---|
| `PROFILE` | **Always** — hardcoded, no row needed | — |
| `CONFIGURATION` | stored row is `COMPLETED`, **or** an active credential exists | `IN_PROGRESS` if stored row was; else `NOT_STARTED` |
| `CREDENTIALS` | an active credential exists | `IN_PROGRESS` if stored row was `COMPLETED` **but** no credential (drift); else stored, else `NOT_STARTED` |
| `SIGNING_KEY` | an active signing key exists | stored row, else `NOT_STARTED` |
| `REVIEW` | tenant status is `Active` | stored row, else `NOT_STARTED` |

Note the deliberate **live-fact shortcuts**: because the provisioner stamps
entitlements *before* minting the credential, "an active credential exists" is
proof that configuration was saved — so the row is never required.

---

## 4. Action → API mapping

The spine of this document. One row per admin action, read left to right:
what the operator clicks → the frontend hook and component → the endpoint →
the backend handler → what it writes → which step it advances.

Frontend paths are relative to `rvl-sbqr-portal`, backend paths to
`rvl-secure-bqr-manager`. Both hooks below live in
`src/surfaces/staff/institutions/api/hooks.ts` unless noted.

### The map: admin action → hook → endpoint → handler → effect

| # | Admin action | Hook (frontend) | Wizard component | Endpoint | Handler (backend) | DB effect | Step row |
|---|---|---|---|---|---|---|---|
| 1 | Register institution | `useRegisterTenant` | `InstitutionStep` | `POST /v1/admin/tenants` | `CreateTenantCommandHandler` | insert `tenants` (`Pending`) | `PROFILE` (implicit) |
| 2 | Save QR entitlements | `useSaveConfiguration` | `ConfigurationStep` | `PATCH /v1/admin/tenants/{id}/configuration` | `SaveTenantConfigurationCommandHandler` | update `tenants.is_qr_*_allowed` | `CONFIGURATION` |
| 3 | Issue client ID + secret | `useProvisionCredentials` | `CredentialsStep` | `POST /v1/admin/tenants/{id}/tenant-configuration` | `ProvisionTenantConfigurationCommandHandler` | insert `tenant_api_clients`; secret hashed, **never stored** | `CREDENTIALS` |
| 4 | Mint / adopt signing key | `useCreateSigningKey` | `KeyStep` | `POST /v1/crypto-keys` | `GenerateOrAdoptCryptoKeyCommandHandler` | insert `crypto_keys`; **async** `institution_keys` via RabbitMQ | `SIGNING_KEY` |
| 5 | Set rate card *(separate surface)* | `rates/api/hooks.ts` | — | `POST /v1/admin/billing/rate-cards` | Billing module | `billing_rate_cards` | — (not a step) |
| 6 | **Activate** | `useTenantAction({ action: 'activate' })` | `ReviewStep` | `POST /v1/admin/tenants/{id}/activate` | `ActivateTenantCommandHandler` | `tenants.status` → `Active` | `REVIEW` |
| — | Resume / refresh progress | `useOnboarding` | all steps | `GET /v1/admin/tenants/{id}/onboarding` | `GetTenantOnboardingQueryHandler` | **writes** `tenant_onboarding_steps` | all (reconciled) |

Request/response shapes for every row: `institutions/api/types.ts` (frontend) and
`Tenancy.Api/Contracts/` (backend). Both sides cite this document as the contract.

**Authorisation:** all `/v1/admin/tenants` endpoints require
`PolicyNames.AdminCredentialTree`; the crypto-key endpoint requires
`PolicyNames.KeyAdmin`. Both declared in
`src/SharedKernel/SBQR.SharedKernel/Application/PolicyNames.cs`.

**Cache behaviour:** every write hook's `onSuccess` calls `refresh()`, which
invalidates the tenant list + the single tenant + the onboarding view — so the
screen you land on next never renders stale progress.

### Reads and support calls

Not onboarding steps, but the calls the surrounding screens make.

| Purpose | Hook / caller | Endpoint | Notes |
|---|---|---|---|
| Institutions table | `useInstitutionList` | `GET /v1/admin/tenants?page=&pageSize=` | `pageSize` max 100; portal pages through all of them |
| Single institution | `useInstitution` | `GET /v1/admin/tenants/{id}` | Powers the detail page + wizard resume |
| Trust directory picker | `useInstitutionDirectory` | `GET /v1/admin/institutions` | **Only** lists institutions that already published a key — so the portal overlays it onto the static Annex A registry |
| Signing key state | — | `GET /v1/crypto-keys/{tenantId}/active` | `404` when none |
| Rate cards | `rates/api/hooks.ts` | `GET /v1/admin/billing/rate-cards?tenantId=` | Fanned out per Active institution |
| Suspend / reactivate / terminate | `useTenantAction` | `POST /v1/admin/tenants/{id}/{action}` | These **do** cascade to credentials + keys (activate does not) |

### Error handling worth knowing

- **Activation refusal is a `409` with a `blockers[]` array** in the problem-details
  body, not a generic error. The portal reads it via `ApiError.blockers`
  (`shared/api/client.ts`) and renders each blocker on the review step.
- **`409` on step 3 means "already done", not "failed"** — `useProvisionCredentials`
  catches it and returns `null`, so a retry after a dropped connection moves on
  quietly instead of showing an error.
- The client secret is returned **exactly once**, and the mutation uses
  `gcTime: 0` so the secret is dropped from cache as soon as nothing observes it.

---

## 5. Data model

Five tables are involved; only one was added for onboarding.

| Table | Module | Onboarding role |
|---|---|---|
| `tenants` | Tenancy | The tenant itself. `status`, `institution_code` (unique), the two `is_qr_*_allowed` entitlements |
| `tenant_onboarding_steps` | Tenancy | **New.** One row per `(tenant_id, step_code)` — the resumability cache |
| `tenant_api_clients` | IdentityAccess | The issued credential. At most **one** `ACTIVE` row per tenant (partial unique index) |
| `crypto_keys` | KeyCustody | The signing key. Versioned, `ACTIVE`/`SUSPENDED`/`RETIRED`/`REVOKED` |
| `institution_keys` | InstitutionTrust | Platform-level trust directory, keyed by `institution_code` (no FK — joined by lookup) |

Full column-level reference: `rvl-secure-bqr-manager/docs/design/database-design.md`
(§2.3 is the onboarding table, §3.1 credentials, §6.1 trust directory).

### `tenant_onboarding_steps` — why a child table

```sql
PRIMARY KEY (tenant_id, step_code)   -- also the upsert key, which is what
                                     -- makes reconcile-on-read idempotent
```

Not columns on `tenants`, because the step list is expected to grow (billing
contact, IP allow-list) and each step carries its own `completed_at` /
`completed_by` / `last_error`. A 1:N child table keeps `tenants` narrow.
`ON DELETE RESTRICT` on the FK — step history is never cascaded away.

**Step-code and status vocabularies are enforced by DB `CHECK` constraints**
(`013_tenant_onboarding.sql`), not just in C#, so a typo in the API cannot insert
an unknown step.

### Migration & drift test

Migrations are hand-written SQL in `db/migrations/`, **not** EF-generated. The
schema is owned by SQL; EF must match, and
`SBQR.ArchitectureTests/SchemaModelDriftTests` fails the build if it drifts.
Adding a column or index means: edit the SQL, then make the EF model match, then
let the drift test confirm you did both.

---

## 6. Backend map

`rvl-secure-bqr-manager` is a **modular monolith**: 11 modules, each split into 5
projects (`Api` / `Application` / `Domain` / `Infrastructure` / `Contracts`),
assembled by `src/Host/SBQR.Api`. Onboarding lives entirely in **Tenancy**; the
work it touches in other modules is reached through **`*.Contracts` seams**
(MediatR query interfaces + narrow DTOs), never by reaching into another
module's DbContext.

| Concern | Path (relative to `src/Modules/Tenancy/`) |
|---|---|
| HTTP surface | `SBQR.Modules.Tenancy.Api/Controllers/TenantsController.cs` |
| Wire DTOs | `SBQR.Modules.Tenancy.Api/Contracts/` |
| **Pure rule engine** | `SBQR.Modules.Tenancy.Application/Onboarding/OnboardingReconcilerLogic.cs` |
| **Orchestration (I/O)** | `SBQR.Modules.Tenancy.Application/Onboarding/OnboardingReconciler.cs` |
| Response view records | `SBQR.Modules.Tenancy.Application/Onboarding/OnboardingView.cs` |
| Step aggregate | `SBQR.Modules.Tenancy.Domain/Aggregates/TenantOnboardingStep.cs` |
| Queries | `SBQR.Modules.Tenancy.Application/Queries/GetTenantOnboarding/` |

### The four cross-module seams the reconciler reads

`OnboardingReconciler.ReconcileAsync` gathers its facts through these — this is
the clearest picture of how the modules stay decoupled:

| Fact | Seam | Source table |
|---|---|---|
| `HasActiveCredential` | `ITenantApiClientProvisioner` (IdentityAccess) | `tenant_api_clients` |
| `HasActiveSigningKey` | `HasActiveSigningKeyQuery` (KeyCustody) | `crypto_keys` |
| `HasTrustEntry` | `GetInstitutionPublicKeyQuery` (InstitutionTrust) | `institution_keys` |
| `HasRateCard` | `HasRateCardQuery` (Billing) | `billing_rate_cards` |

Plus a fifth read for display: `GetSigningKeyQuery`, which returns the key's
`keyId` / version / status for the view.

### Tests to read as executable spec

| Suite | What it pins down |
|---|---|
| `SBQR.Modules.Tenancy.Tests` | The reconciler rule table, step state machine, activation blockers |
| `SBQR.Tenancy.IntegrationTests` | Real Postgres — EF migrations, drift, transaction behaviour |
| `SBQR.ArchitectureTests` | Layering rules + `SchemaModelDriftTests` |

---

## 7. Frontend map

`rvl-sbqr-portal` is **one React SPA with two surfaces**: `src/surfaces/staff`
(RVL admin/finance) and `src/surfaces/fi` (the institutions themselves).
Surfaces are lazy-loaded and must never import across each other; shared code
lives in `src/shared`.

| Concern | Path |
|---|---|
| Wizard shell + resume logic | `src/surfaces/staff/onboarding/onboarding-page.tsx` |
| The 5 step components | `src/surfaces/staff/onboarding/steps.tsx` |
| Directory picker + Annex A fallback | `src/surfaces/staff/onboarding/use-institution-directory.ts`, `institution-picker.tsx` |
| One-time secret dialog | `src/surfaces/staff/onboarding/secret-dialog.tsx` |
| **All API hooks (action → endpoint)** | `src/surfaces/staff/institutions/api/hooks.ts` |
| Wire types (the contract) | `src/surfaces/staff/institutions/api/types.ts` |
| DTO → UI mappers | `src/surfaces/staff/institutions/api/mappers.ts` |
| Authenticated fetch seam | `src/shared/api/client.ts` |

**The pattern to imitate:** wire DTOs in `api/types.ts` → mappers in `mappers.ts` →
UI models in `../types.ts` → hooks in `api/hooks.ts`. The wizard components stay
presentational: `onboarding-page.tsx` owns the requests and hands each step
`busy` / `error` / `onSubmit`. A new onboarding step should add a hook + a step
component and touch nothing else.

**Auth:** `shared/api/client.ts` runs an OAuth 2.1 client-credentials grant,
keeps the token **in memory only** (a page refresh signs you out), re-mints 30s
before expiry, and retries once on `401`. All requests are same-origin `/v1/...`
(Vite proxy in dev).

---

## 8. Things that will confuse you

These are the real traps. Each one is correct-by-design but looks like a bug
until you know why.

**1. A `GET` that writes to the database.**
`GET /onboarding` commits step-row upserts on every read
(`GetTenantOnboardingQueryHandler` calls `_uow.SaveChangesAsync`). It's safe —
the composite PK makes the upsert idempotent, and the transaction is a handful
of rows — but it means the endpoint is not a pure read. This is deliberate:
it's what keeps the cache honest.

**2. The stored status is not the reported status.**
Never read `tenant_onboarding_steps` and assume it. A row can say `COMPLETED`
while the live facts say otherwise — the reconciler **downgrades** it to
`IN_PROGRESS` on read. The canonical example: credentials were revoked after
the wizard completed step 3, so the stored row still says `COMPLETED` but the
view reports `IN_PROGRESS`.

**3. Two different vocabularies on the wire, in the same payload.**
Tenant status is **PascalCase** (`Pending`, `Active`) because it comes from
`tenant.Status.ToString()`. Step codes and step statuses are **UPPER_SNAKE**
(`SIGNING_KEY`, `COMPLETED`) because they go through explicit `ToWire()`
conversions. Easy to miss. The portal's `toStatus()` in `mappers.ts` handles it.

**4. The wizard CHECKs are enforced by the database alone.**
`tenant_onboarding_steps.step_code` / `.status` have `CHECK` whitelists in
migration 013 that nothing in CI compares against the C# enums — the drift test
deliberately skips CHECK constraints (`EfModelSchemaReader.Read` returns an
empty list) because EF Core 10's `IReadOnlyCheckConstraint` API is unstable.
This is exactly how the removed `CERTIFICATE` step and unused `SKIPPED` status
lingered in the SQL after being deleted from `OnboardingStepCode` /
`OnboardingStepStatus`. Both were corrected in place on 2026-10-06, but the
structural gap remains: **if you add a step or a status, the enum and the CHECK
must be changed by hand, or the database will silently accept a value the code
can never produce.**

**5. `TRUST_ENTRY_MISSING` is a blocker with no wizard step.**
The trust directory is populated **asynchronously** — step 4's handler publishes
the public key through `IInstitutionTrustPublisher` over RabbitMQ. So
step 4 → Activate is inherently racy: the key exists locally, but the directory
row may not have landed yet. The reconciler is what makes this converge —
re-read `GET /onboarding` after the event propagates. There's no retry or
polling on the portal side.

**6. Two separate activation gates, with slightly different rules.**
- `OnboardingReconcilerLogic.BuildBlockers` — **four** blockers: credential,
  configuration, signing key, trust entry. This is what the review step shows.
- `ActivateTenantCommandHandler.EvaluateReadinessAsync` — **three** flags:
  credential, signing key, trust entry. Configuration isn't checked, on the
  argument that an active credential implies configuration was already stamped.

The handler calls `BuildBlockers` to format its `409`, so the list is built in
one place — but the *gates* differ. Don't assume the two lists are identical.

**7. `PROFILE` is hardcoded `COMPLETED`.**
`ResolveStatus` returns `Completed` unconditionally for `PROFILE`, with no row
required. Registering the tenant is what makes it true.

**8. `Activate` doesn't cascade; `Suspend` does.**
`Suspend` / `Reactivate` / `Terminate` cascade onto credentials and signing
keys. `Activate` deliberately does **not** — it's a state-machine entry, not a
suspend-reversal. Credential and key state are governed by their own endpoints.

---

## 9. Known gaps

Factual state of the branches as read on 2026-10-06. No editorialising.

| # | Gap | Where |
|---|---|---|
| 1 | ~~Migration 013 whitelists the removed `CERTIFICATE` step and an unused `SKIPPED` status~~ — **fixed** in place on 2026-10-06; 013 now declares 5 steps / 4 statuses | `db/migrations/013_tenant_onboarding.sql` |
| 2 | Trust entry lands asynchronously → step 4 → Activate is racy; no retry on the portal side | `GenerateOrAdoptCryptoKeyCommandHandler` → `IInstitutionTrustPublisher` |
| 3 | Signing-key **rotation** is a stub — the handler returns a failure | `CryptoKeysController.cs` (`PUT {tenantId}`) |
| 4 | No re-display of an issued secret. If the connection drops at step 3, the admin must re-provision and revoke the old credential by hand | `TenantsController.ProvisionConfigurationAsync` |
| 5 | `last_error` column and the `FAILED` step status are modelled end-to-end but nothing writes them yet — there's no transition to `FAILED` in v1 | `TenantOnboardingStep`; `database-design.md` §2.3 |
| 6 | The drift test does **not** compare CHECK constraints, so the two whitelists in 013 are enforced by the database alone | `EfModelSchemaReader.Read` returns an empty CHECK list |

---

## 10. Related documents

| Document | Where | Why read it |
|---|---|---|
| Full DB schema (all 11 modules) | `rvl-secure-bqr-manager/docs/design/database-design.md` | Column-level truth |
| BB P2P spec (source of the domain rules) | `rvl-secure-bqr-manager/docs/bb-banglaqr-p2p-specification.md` | Why institution codes and trust keys work the way they do |
| Metering & billing guide | `docs/features/metering-billing/dotnet-dev-onboarding-guide.md` | The rate card + statement side of the same institution |
| Portal conventions | `rvl-sbqr-portal/AGENTS.md` | Surface separation, auth rules, no-secrets rules |
| Backend charter | `rvl-secure-bqr-manager/AGENTS.md` | Spec-first rules for the domain |
| Onboarding migration | `rvl-secure-bqr-manager/db/migrations/013_tenant_onboarding.sql` | The step table (see gap #1) |