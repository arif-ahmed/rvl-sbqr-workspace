# Institution Onboarding — Implementation Reference

Where every part of institution onboarding lives in code. Read §1–2 once to get
connected; §3 is the spine (one subsection per wizard step); the rest is
reference.

**Verified against:**

| Repo | Branch | Commit |
|---|---|---|
| `rvl-secure-bqr-manager` (backend) | `feature/institution-onboarding` | `6970f4d` |
| `rvl-sbqr-portal` (frontend) | `feature/institution-onboarding-api` | `1c0463b` |

Both branches have since moved; treat the SHAs as provenance, not as current
HEAD. Every claim here was read from source, not from a spec.

---

## 1. What this feature does

Secure Bangla QR **creates** signed QR codes for financial institutions (FIs,
called **tenants** in code) and **verifies** theirs. Onboarding is how a new FI
joins: Bangladesh Bank assigns it a six-digit institution code and registers its
name in Annex A, and an RVL operator turns that regulator-issued identity into a
working technical tenant — credentials, a signing key, entitlements, a
trust-directory entry — then switches it live.

Two business rules shape everything:

1. **Identity is fixed at step 1.** Name and code come from the regulator's
   registry, so they are picked, never typed. Registered codes are greyed out;
   the API refuses duplicates with a `409`.
2. **Nothing goes live until it's verifiable.** Activation requires an active
   credential, an active signing key, *and* a trust-directory entry. A signing
   key nobody can look up produces QRs nobody can verify.

The wizard lives at `/staff/institutions/new`. Every step **saves as it
completes**, so closing the tab loses nothing.

| # | Step | The admin does | Wire code |
|---|---|---|---|
| 1 | Institution | Pick from the directory; enter contacts | `PROFILE` |
| 2 | Configuration | Choose QR entitlements (generate / validate) | `CONFIGURATION` |
| 3 | Credentials | Issue client ID + secret — secret shown **once** | `CREDENTIALS` |
| 4 | Signing key | Generate a key, or adopt the institution's own PEM | `SIGNING_KEY` |
| 5 | Review | Confirm, then **Activate** | `REVIEW` |

**Rate cards are not a step.** Pricing lives on its own `/staff/rates` surface in
the Billing module; onboarding never writes one. It reads exactly one rate-card
thing: the advisory `hasRateCard` flag, so step 5 can warn that usage won't be
billed yet. See `docs/features/metering-billing/dotnet-dev-onboarding-guide.md`.

---

## 2. The one idea: the reconciler

**Read this and you understand the whole feature.**

The naive design stores each step's status and trusts it. This one doesn't.
`tenant_onboarding_steps` is a **cache, not a source of truth**. On every read of
the onboarding view the server re-derives each step's status from the **live
facts**, then writes the result back:

```
GET /onboarding
   ├─ gather live facts    (4 cross-module reads)
   ├─ derive step statuses (pure function)
   ├─ upsert drifted rows ──► tenant_onboarding_steps
   └─ return the derived view
```

Two files, deliberately split. `OnboardingReconcilerLogic.cs` is **pure** — 296
lines, zero I/O, zero time, zero actor strings, fully unit-tested. Read its
`ResolveStatus` and you know every step. `OnboardingReconciler.cs` next to it
does the I/O: gathers the facts, calls the pure logic, upserts drift.

### The rule table

Each cell is a guard in `ResolveStatus`:

| Step | Reported `COMPLETED` when | Otherwise |
|---|---|---|
| `PROFILE` | **Always** — hardcoded, no row needed | — |
| `CONFIGURATION` | stored row is `COMPLETED`, **or** an active credential exists | `IN_PROGRESS` if stored row was; else `NOT_STARTED` |
| `CREDENTIALS` | an active credential exists | `IN_PROGRESS` if stored row was `COMPLETED` **but** no credential (drift); else stored, else `NOT_STARTED` |
| `SIGNING_KEY` | an active signing key exists | stored row, else `NOT_STARTED` |
| `REVIEW` | tenant status is `Active` | stored row, else `NOT_STARTED` |

The live facts come from four cross-module seams, which is the clearest picture
of how the modules stay decoupled:

| Fact | Seam | Source table |
|---|---|---|
| `HasActiveCredential` | `ITenantApiClientProvisioner` (IdentityAccess) | `tenant_api_clients` |
| `HasActiveSigningKey` | `HasActiveSigningKeyQuery` (KeyCustody) | `crypto_keys` |
| `HasTrustEntry` | `GetInstitutionPublicKeyQuery` (InstitutionTrust) | `institution_keys` |
| `HasRateCard` | `HasRateCardQuery` (Billing) | `billing_rate_cards` |

Plus a fifth read for display: `GetSigningKeyQuery`, returning `keyId` / version
/ status.

### Three things that look like bugs

- **A `GET` that writes.** `GET /onboarding` commits step-row upserts on every
  read. It's safe — the composite PK makes the upsert idempotent — and it's
  deliberate: it's what keeps the cache honest.
- **The stored status is not the reported status.** Never read
  `tenant_onboarding_steps` and assume it. A row can say `COMPLETED` while the
  live facts say otherwise, and the reconciler *downgrades* it to `IN_PROGRESS`.
  Canonical case: credentials were revoked after the wizard finished step 3, so
  the row still says `COMPLETED` but the view reports `IN_PROGRESS`.
- **Two vocabularies in the same payload.** Tenant status is **PascalCase**
  (`Pending`, `Active`) from `tenant.Status.ToString()`. Step codes and statuses
  are **UPPER_SNAKE** (`SIGNING_KEY`, `COMPLETED`) via explicit `ToWire()`
  conversions. The portal's `toStatus()` in `mappers.ts` reconciles it.

---

## 3. Step-by-step: action → API mapping

The spine. Every step below is the same shape: what the admin does, the UI chain,
the call, the handler, when the reconciler calls it done, and the one thing that
bites.

Paths are relative to `rvl-sbqr-portal` and `rvl-secure-bqr-manager`. All hooks
live in `src/surfaces/staff/institutions/api/hooks.ts`. Steps 1–3 and 5 are
Tenancy (`PolicyNames.AdminCredentialTree`); step 4 is KeyCustody
(`PolicyNames.KeyAdmin`).

### At a glance

| Step | Frontend | Backend | Database | Sub-domain |
|---|---|---|---|---|
| 1 `PROFILE` | `InstitutionStep` → `useRegisterTenant` | `POST /v1/admin/tenants` → `CreateTenantCommandHandler` | insert `tenants` (`Pending`); upsert step row | Tenancy |
| 2 `CONFIGURATION` | `ConfigurationStep` → `useSaveConfiguration` | `PATCH /v1/admin/tenants/{id}/configuration` → `SaveTenantConfigurationCommandHandler` | update `tenants.is_qr_*_allowed`; upsert step row | Tenancy |
| 3 `CREDENTIALS` | `CredentialsStep` + `SecretDialog` → `useProvisionCredentials` | `POST /v1/admin/tenants/{id}/tenant-configuration` → `ProvisionTenantConfigurationCommandHandler` | update `tenants` entitlements; insert `tenant_api_clients` (secret hashed); upsert step rows | Tenancy → **IdentityAccess** |
| 4 `SIGNING_KEY` | `KeyStep` → `useCreateSigningKey` | `POST /v1/crypto-keys` → `GenerateOrAdoptCryptoKeyCommandHandler` | insert `crypto_keys` + key material in the vault; upsert `institution_keys` | KeyCustody → **InstitutionTrust** |
| 5 `REVIEW` | `ReviewStep` → `useTenantAction` | `POST /v1/admin/tenants/{id}/activate` → `ActivateTenantCommandHandler` | update `tenants.status` → `Active` | Tenancy |
| *resume* | `useOnboarding` | `GET /v1/admin/tenants/{id}/onboarding` → `OnboardingReconciler` | **writes** step rows on drift; reads `tenant_api_clients`, `crypto_keys`, `institution_keys`, `billing_rate_cards` | Tenancy, reading 4 seams |

Bolded sub-domains are cross-module: onboarding owns the orchestration, but the
table itself belongs to another module, reached through a `*.Contracts` seam.

**Who writes the step row is not uniform**, and it's the thing most likely to
confuse you when a step looks "half done":

- Steps 1 and 2 stamp their own row inside the command handler, in the same
  transaction as the tenant write.
- Step 3 stamps `CREDENTIALS` and `CONFIGURATION` via a best-effort
  `TryReconcileAsync` after minting.
- **Steps 4 and 5 write no step row at all.** Step 4's handler is in KeyCustody
  and cannot reach Tenancy's tables; step 5's handler has no reconciler injected.
  Their rows appear on the *next* `GET /onboarding`, derived from the live facts.
  That's the cache design doing its job — but it means the stepper can lag one
  read behind the write.

### 3.1 Institution — `PROFILE`

Pick the institution from the directory; enter contact details.

- **UI.** `InstitutionStep` (`onboarding/steps.tsx`) → `register()`
  (`onboarding-page.tsx`) → `useRegisterTenant`. The zod-validated form keeps
  name/type/code in one `InstitutionPicker`, so they can't be typed
  independently.
- **Call.** `POST /v1/admin/tenants`, body from `toRegisterRequest()`
  (`institutions/api/mappers.ts`) — it concatenates the 6-digit code from
  `type` + `institutionId` and omits empty optional fields.
- **Handler.** `TenantsController.RegisterAsync` →
  `CreateTenantCommandHandler`: inserts `tenants` as `Pending`, upserts the
  `PROFILE` step row, one transaction; audits `tenant.registered`.
- **Done when.** Always — `PROFILE` is hardcoded `COMPLETED`, so no row is
  required. Registering the tenant is what makes it true.
- **Bites.** `institution_code` is DB-unique, so a duplicate is a `409`, not a
  validation error. On resume `register()` is a **no-op advance** (a `tenantId`
  already exists) and the identity fields render locked.

**Where the picker's entries come from:** `useInstitutionDirectory`
(`onboarding/use-institution-directory.ts`) overlays
`GET /v1/admin/institutions` on the static Annex A registry, falling back to the
registry alone when the API is unreachable — onboarding never hard-blocks on the
network. The directory only lists institutions that already published a key, so
it can never offer a new one on its own; the overlay is what makes it work.

### 3.2 Configuration — `CONFIGURATION`

Turn on what the institution may do: QR generation (`qr:generate`), QR
validation (`qr:validate`), or both.

- **UI.** `ConfigurationStep` → `saveConfig()` → `useSaveConfiguration`.
  Continue is disabled when both are off — a credential with neither scope would
  be unusable.
- **Call.** `PATCH /v1/admin/tenants/{id}/configuration`, body
  `{ isQrGenerationAllowed, isQrValidationAllowed }`.
- **Handler.** `TenantsController.SaveConfigurationAsync` →
  `SaveTenantConfigurationCommandHandler`. Guards in order: `404` unknown
  tenant; `409` if not `Pending` (the wizard is only legal pre-activation);
  `409` if an **active credential exists** — changing the flags would drift the
  credential row's `scopes` away from the tenant's entitlements, and rotation is
  the correct flow. Then `Tenant.SetQrEntitlements` (itself rejecting "neither
  flag true" → `400`), the step row, the tenant update, one commit; audits
  `tenant.onboarding.step_completed`.
- **Done when.** The stored row is `COMPLETED` **or** an active credential
  exists. The second clause is the live-fact shortcut: step 3 stamps these same
  entitlements itself, so this row is never strictly required.
- **Bites.** The UI locks the switches once `onboarding.credential` exists, and
  `saveConfig()` short-circuits past the call. `MarkCompleted` keeps the original
  timestamp on a re-mark, so a second admin tab sees the first save's
  `completedAt`.

### 3.3 Credentials — `CREDENTIALS`

Issue the client ID + secret the institution's gateway will use. The secret is
shown **once**, in a modal.

- **UI.** `CredentialsStep` → `issueCredentials()` → `useProvisionCredentials`.
  The step restates the step-2 scope so the admin can go back before issuing.
  On success the pair goes to `SecretDialog`
  (`onboarding/secret-dialog.tsx`); `dismissSecret()` also calls
  `provision.reset()` so the secret isn't held for the rest of the screen's life.
- **Call.** `POST /v1/admin/tenants/{id}/tenant-configuration`, body **the same
  two capability flags** — that's what scopes the issued credential. The
  endpoint name is misleading: it provisions a *configuration*, not a credential.
- **Handler.** `TenantsController.ProvisionConfigurationAsync` →
  `ProvisionTenantConfigurationCommandHandler`. Guards: `404` unknown tenant;
  `409` for `Suspended`/`Terminated` (the token endpoint re-checks admission at
  mint time, so provisioning one they'd reject is refused up front); `409` if an
  active client already exists. Then it **stamps the entitlements and commits
  them first**, then mints through `ITenantApiClientProvisioner` — IdentityAccess
  owns `tenant_api_clients`, the Argon2id hash and its own audit row. The
  plaintext secret crosses the boundary exactly once (result → controller →
  response body) and is never logged; the Tenancy breadcrumb
  (`tenant.configuration.provisioned`) records only the `client_id`. A
  best-effort reconcile stamps the step rows immediately.
- **Done when.** An active credential exists.
- **Bites.** A `409` here means **"already done", not "failed"** — the hook
  catches it and resolves `null`, so a retry after a dropped connection moves on
  quietly with a toast. The mutation uses `gcTime: 0`, dropping the secret from
  cache as soon as nothing observes it. There is no way to re-display an issued
  secret.

### 3.4 Signing key — `SIGNING_KEY`

Generate a new Ed25519 key pair, or adopt the institution's own by pasting its
private-key PEM. Required — no signing key means no activation.

- **UI.** `KeyStep` → `useCreateSigningKey`. If a key already exists the step
  short-circuits to a "Signing key active" card. Otherwise a `Generate`/`Adopt`
  radiogroup; `Adopt` reveals a PEM textarea validated by `pemSchema`. The
  pasted PEM is sent once then cleared from local state — not kept, stored or
  logged.
- **Call.** `POST /v1/crypto-keys`, body `{ tenantId, mode, privateKeyPem? }`.
  **The only step outside Tenancy**: `CryptoKeysController.CreateAsync`.
- **Handler.** `GenerateOrAdoptCryptoKeyCommandHandler`. Guards: `404` unknown
  tenant; `409` if an `ACTIVE` key already exists (rotate via `PUT` instead).
  `Adopt` adds a **drift guard, before any side-effect**: the canonical SPKI
  public PEM is derived from the supplied private half and its SHA-256 compared
  against the active `public.institution_keys` row → `409`
  `crypto_key.adopt.no_trust_row` when there's no trust row, `409`
  `crypto_key.adopt.drift_rejected` on mismatch. Adopt never auto-publishes a
  trust row to satisfy itself; pre-seed it via
  `POST /v1/admin/institutions`. On success: insert `crypto_keys` + the vault
  blob, commit, audit `crypto_key.minted`/`adopted`, then **auto-publish** the
  public key to the trust directory via `IInstitutionTrustPublisher`.
- **Done when.** An active signing key exists.
- **Bites.** The publish is **mandatory and synchronous** —
  `InstitutionTrustPublisher` calls `InstitutionUpsertService` in-process, not
  through a queue. But KeyCustody and InstitutionTrust own separate
  `DbContext`s, so there's no shared transaction: the `crypto_keys` row commits
  *first*. If the publish then throws, the handler returns a failure with
  `crypto_key.trust_publish_failed` and **keeps** the key row (no compensating
  delete) — so re-minting would just hit the "already has an ACTIVE signing key"
  `409`. Recover with one manual `POST /v1/admin/institutions`. Between the
  commit and the publish there's a window where a key exists but the directory
  row doesn't, which is what `TRUST_ENTRY_MISSING` reports; the reconciler closes
  it once the publish is repaired. There's no retry or polling on the portal side.

### 3.5 Review and activate — `REVIEW`

Check capabilities / credentials / signing key, see what blocks activation,
activate. "Finish later" exits as `Pending`.

- **UI.** `ReviewStep` → `activateInstitution()` → `useTenantAction({
  action: 'activate' })` — the same hook as the lifecycle actions. Activate is
  disabled unless `onboarding.canActivate`. A refused activation's `blockers[]`
  is kept in local state (`refusal`) because it's fresher than the last
  onboarding read.
- **Call.** `POST /v1/admin/tenants/{id}/activate`.
- **Handler.** `TenantsController.ActivateAsync` →
  `ActivateTenantCommandHandler`. `EvaluateReadinessAsync` reads **three** flags
  — credential, signing key, trust entry — then `Tenant.Activate` drives the
  state machine (raising `TenantActivated`). A thrown
  `InvalidOperationException` becomes a `409` whose problem-details body carries
  `blockers[]`, built by the reconciler's own `BuildBlockers` so the `409` and
  this screen always show the same list. One audit row (`tenant.activated`).
- **Done when.** Tenant status is `Active`. There's no row for "review was
  viewed".
- **Bites.** **Two gates, different rules.** `BuildBlockers` emits **four**
  blockers (credential, configuration, signing key, trust entry);
  `EvaluateReadinessAsync` checks **three** — configuration isn't checked, on
  the argument that an active credential implies it was already stamped. Don't
  assume the two lists are identical. And **Activate does not cascade** onto
  credentials or keys, unlike Suspend/Reactivate/Terminate: it's a state-machine
  entry, not a suspend-reversal.

`409` means different things at different steps — duplicate code, non-Pending
tenant, existing credential, existing key, failed activation gate all arrive as
`409 InvariantViolation`. Read the message; don't pattern-match the status.

### 3.6 Resumability

The one read that drives all five steps — not a step itself.

- **UI.** `useOnboarding(id)` → `GET /v1/admin/tenants/{id}/onboarding`.
  `?resume=<tenantId>` opens the wizard at `OnboardingView.currentStep`
  (`onboarding-page.tsx` — `Resume`); the institutions list shows a `3 / 5`
  badge per Pending institution.
- **Backend.** `GetTenantOnboardingQueryHandler` → `OnboardingReconciler` →
  `OnboardingReconcilerLogic.Reconcile`.
- **Returns.** Five step rows, `currentStep`, `blockers[]`, `canActivate`,
  `hasRateCard`, plus the configuration / credential / signing-key snapshots.
- **Cache behaviour.** Every write hook's `onSuccess` calls `refresh()`,
  invalidating the tenant list + the single tenant + the onboarding view, so the
  screen you land on next never renders stale progress.

### 3.7 Supporting reads

Not onboarding steps, but the calls the surrounding screens make.

| Purpose | Hook / caller | Endpoint | Notes |
|---|---|---|---|
| Institutions table | `useInstitutionList` | `GET /v1/admin/tenants?page=&pageSize=` | `pageSize` max 100; portal pages through all of them |
| Single institution | `useInstitution` | `GET /v1/admin/tenants/{id}` | Powers the detail page + wizard resume |
| Trust directory | `useInstitutionDirectory` | `GET /v1/admin/institutions` | Only lists institutions that already published a key — see §3.1 |
| Signing key state | — | `GET /v1/crypto-keys/{tenantId}/active` | `404` when none |
| Rate cards | `rates/api/hooks.ts` | `GET /v1/admin/billing/rate-cards?tenantId=` | Fanned out per Active institution — **billing surface** |
| Suspend / reactivate / terminate | `useTenantAction` | `POST /v1/admin/tenants/{id}/{action}` | These **do** cascade to credentials + keys (activate does not) |

---

## 4. Data model

Five tables are involved; only one was added for onboarding.

| Table | Module | Onboarding role |
|---|---|---|
| `tenants` | Tenancy | The tenant. `status`, `institution_code` (unique), the two `is_qr_*_allowed` entitlements |
| `tenant_onboarding_steps` | Tenancy | **New.** One row per `(tenant_id, step_code)` — the resumability cache |
| `tenant_api_clients` | IdentityAccess | The issued credential. At most **one** `ACTIVE` row per tenant (partial unique index) |
| `crypto_keys` | KeyCustody | The signing key. Versioned, `ACTIVE`/`SUSPENDED`/`RETIRED`/`REVOKED` |
| `institution_keys` | InstitutionTrust | Trust directory, keyed by `institution_code` (no FK — joined by lookup) |

Column-level reference: `rvl-secure-bqr-manager/docs/design/database-design.md`
(§2.3 the onboarding table, §3.1 credentials, §6.1 trust directory).

**Why a child table.** `PRIMARY KEY (tenant_id, step_code)` — which is also the
upsert key, and that is what makes reconcile-on-read idempotent. Not columns on
`tenants`, because the step list is expected to grow and each step carries its
own `completed_at` / `completed_by` / `last_error`. `ON DELETE RESTRICT` — step
history is never cascaded away.

**Migrations are hand-written SQL** in `db/migrations/`, **not** EF-generated.
The schema is owned by SQL, EF must match, and
`SBQR.ArchitectureTests/SchemaModelDriftTests` fails the build if it drifts. To
add a column or index: edit the SQL, make the EF model match, let the drift test
confirm both.

> **One trap if you add a step or a status.** The step-code and status
> vocabularies are enforced by DB `CHECK` whitelists in migration 013, but
> **nothing in CI compares those against the C# enums** — the drift test
> deliberately skips CHECK constraints (`EfModelSchemaReader.Read` returns an
> empty list) because EF Core 10's `IReadOnlyCheckConstraint` API is unstable.
> This is exactly how the removed `CERTIFICATE` step and unused `SKIPPED` status
> lingered in the SQL after being deleted from the enums. **The enum and the
> CHECK must be changed by hand, or the database will silently accept a value the
> code can never produce.**

---

## 5. Where the code lives

### Backend (`rvl-secure-bqr-manager`)

A **modular monolith**: 11 modules, each split into 5 projects (`Api` /
`Application` / `Domain` / `Infrastructure` / `Contracts`), assembled by
`src/Host/SBQR.Api`. Onboarding lives entirely in **Tenancy**; other modules are
reached through **`*.Contracts` seams** (MediatR query interfaces + narrow
DTOs), never by reaching into another module's DbContext.

Paths relative to `src/Modules/Tenancy/`:

| Concern | Path |
|---|---|
| HTTP surface | `SBQR.Modules.Tenancy.Api/Controllers/TenantsController.cs` |
| Wire DTOs | `SBQR.Modules.Tenancy.Api/Contracts/` |
| **Pure rule engine** | `SBQR.Modules.Tenancy.Application/Onboarding/OnboardingReconcilerLogic.cs` |
| **Orchestration (I/O)** | `SBQR.Modules.Tenancy.Application/Onboarding/OnboardingReconciler.cs` |
| Response view records | `SBQR.Modules.Tenancy.Application/Onboarding/OnboardingView.cs` |
| Step aggregate | `SBQR.Modules.Tenancy.Domain/Aggregates/TenantOnboardingStep.cs` |
| Onboarding query | `SBQR.Modules.Tenancy.Application/Queries/GetTenantOnboarding/` |
| Signing-key endpoint | `src/Modules/KeyCustody/SBQR.Modules.KeyCustody.Api/Controllers/CryptoKeysController.cs` |

Policies are declared in
`src/SharedKernel/SBQR.SharedKernel/Application/PolicyNames.cs`.

**Tests to read as executable spec:** `SBQR.Modules.Tenancy.Tests` (the rule
table, step state machine, activation blockers),
`SBQR.Tenancy.IntegrationTests` (real Postgres — migrations, drift,
transactions), `SBQR.ArchitectureTests` (layering + `SchemaModelDriftTests`).

### Frontend (`rvl-sbqr-portal`)

One React SPA with two surfaces: `src/surfaces/staff` (RVL admin/finance) and
`src/surfaces/fi` (the institutions themselves). Surfaces are lazy-loaded and
must never import across each other; shared code lives in `src/shared`.

| Concern | Path |
|---|---|
| Wizard shell + resume | `src/surfaces/staff/onboarding/onboarding-page.tsx` |
| The 5 step components | `src/surfaces/staff/onboarding/steps.tsx` |
| Directory picker + Annex A fallback | `src/surfaces/staff/onboarding/use-institution-directory.ts`, `institution-picker.tsx` |
| One-time secret dialog | `src/surfaces/staff/onboarding/secret-dialog.tsx` |
| **All API hooks** | `src/surfaces/staff/institutions/api/hooks.ts` |
| Wire types (the contract) | `src/surfaces/staff/institutions/api/types.ts` |
| DTO → UI mappers | `src/surfaces/staff/institutions/api/mappers.ts` |
| Authenticated fetch seam | `src/shared/api/client.ts` |

**The pattern to imitate:** wire DTOs in `api/types.ts` → mappers in
`mappers.ts` → UI models in `../types.ts` → hooks in `api/hooks.ts`. The step
components stay presentational — `onboarding-page.tsx` owns the requests and
hands each step `busy` / `error` / `onSubmit`. A new step should add a hook and a
step component and touch nothing else.

**Auth:** `shared/api/client.ts` runs an OAuth 2.1 client-credentials grant, keeps
the token **in memory only** (a page refresh signs you out), re-mints 30s before
expiry, and retries once on `401`. All requests are same-origin `/v1/...`.

---

## 6. Known gaps

Factual state of the branches. No editorialising.

| # | Gap | Where |
|---|---|---|
| 1 | Trust publish has no shared transaction — see §3.4. A failed publish leaves a `crypto_keys` row the wizard can't reconcile around, and re-minting is blocked | `GenerateOrAdoptCryptoKeyCommandHandler` |
| 2 | Signing-key **rotation** is a stub — the handler returns a failure | `CryptoKeysController.cs` (`PUT {tenantId}`) |
| 3 | No re-display of an issued secret. If the connection drops at step 3, the admin must revoke the old credential and re-provision by hand | `TenantsController.ProvisionConfigurationAsync` |
| 4 | `last_error` and the `FAILED` step status are modelled end-to-end but nothing writes them — there's no transition to `FAILED` in v1 | `TenantOnboardingStep`; `database-design.md` §2.3 |
| 5 | CHECK constraints aren't drift-tested — see the note in §4 | `EfModelSchemaReader.Read` returns an empty CHECK list |

---

## 7. Related documents

| Document | Where | Why read it |
|---|---|---|
| Full DB schema (all 11 modules) | `rvl-secure-bqr-manager/docs/design/database-design.md` | Column-level truth |
| BB P2P spec (source of the domain rules) | `rvl-secure-bqr-manager/docs/bb-banglaqr-p2p-specification.md` | Why institution codes and trust keys work the way they do |
| Metering & billing guide | `docs/features/metering-billing/dotnet-dev-onboarding-guide.md` | The rate card + statement side of the same institution |
| Onboarding migration | `rvl-secure-bqr-manager/db/migrations/013_tenant_onboarding.sql` | The step table + the CHECK whitelists |
| Portal conventions | `rvl-sbqr-portal/AGENTS.md` | Surface separation, auth rules, no-secrets rules |
| Backend charter | `rvl-secure-bqr-manager/AGENTS.md` | Spec-first rules for the domain |
