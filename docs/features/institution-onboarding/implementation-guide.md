# Institution onboarding wizard: implementation guide

**Status:** Draft for review, 2026-10-04
**Targets:** `rvl-secure-bqr-manager` (Part A, do first) and `rvl-sbqr-portal` (Part B)
**Feature:** make `/staff/institutions/new` a real wizard whose progress is stored on the backend, so staff can leave and resume.

This guide is written to be followed top to bottom. Every step has an ID (A3, B7, ...). Do the steps in order, and do not start Part B before the Part A contract (section 2) is merged or at least agreed. Use `/rvl-create-branch` to cut a branch in each submodule and `/rvl-commit <plan-doc>` to finish; both skills need a plan doc, so copy the relevant part of this guide into each submodule's `docs/plans/` when you start (see section 9).

---

## 1. Background and decisions

### Where things stand
- The portal wizard (`rvl-sbqr-portal/src/surfaces/staff/onboarding/`) runs entirely on in-memory mock stores (`institutions/store.ts`, `rates/store.ts`) and fake credentials (`institutions/credentials.ts`). The only real call is `GET /v1/admin/institutions` (trust directory).
- `src/shared/api/client.ts` has `apiGet` only. There is no mutation helper, no problem-details parsing and no `QueryClientProvider` (`src/main.tsx` and `src/App.tsx` have none), although `@tanstack/react-query` is already a dependency.
- The backend already has an endpoint for most steps (section 2.3), but it keeps no per-step progress. `tenants.status = PENDING` is a single flag. The activate gate (`TenantReadiness`) is computed inside `ActivateTenantCommandHandler` only and never exposed. Nothing returns credential metadata, certificate state or key state.

### Decisions already made
| # | Decision |
|---|---|
| D1 | The tenant is created at wizard step 1 with the existing `POST /v1/admin/tenants`. There is no separate draft table and no DRAFT status. |
| D2 | Progress is stored in a new table `tenant_onboarding_steps`, and every read reconciles it against live facts (credential exists, key active, certificate set). The live facts win. |
| D3 | **Rate card is not a wizard step and does not gate activation.** It stays on its own page (`/staff/rates`) and the institution Billing tab. An Active institution without a card shows a warning. |
| D4 | Wizard order is Institution, Configuration, Credentials, Certificate, Signing key, Review. A certificate attaches to the tenant's ACTIVE credential (`db/migrations/009_tenant_client_certificates.sql`), so Credentials must come before Certificate. The mock has Certificate before Credentials, so the portal order changes. |

### Facts that shape the design (verified in code)
- `ProvisionTenantConfigurationCommandHandler` already calls `tenant.SetQrEntitlements(...)` and then mints the credential. So the Configuration step only needs to save the two flags on the tenant; provisioning reuses them (the controller defaults both to `true` when the body is empty, so the portal must send the stored values explicitly).
- `Tenant.SetQrEntitlements` throws if both flags are false.
- The activate gate requires credential + **active signing key** + trust-directory entry (`TenantReadiness`, `Tenant.Activate`). It does not check the certificate or a rate card.
- Rate cards can only start on the 1st of a **future** month (`RateCard.Create`: `effectiveFrom <= todayDhaka` is rejected). So a brand-new institution can never have a card for the current month, and `StatementCalculator` skips a tenant with no card for the month (its usage is not billed that month). This is why the warning in D3 matters; it is not just cosmetic.
- `CreateTenantCommandHandler` writes no `audit_logs` row on purpose: the row's own `created_by`/`created_at` columns (set by `TenancyAuditColumnInterceptor`) are the audit trail. Do not add one.
- Admin auth is a single scope (`scope=admin`, policy `AdminCredentialTree`). There are no roles on the backend.
- Module boundaries are enforced by `tests/SBQR.ArchitectureTests/ModuleBoundaryTests.cs`. Tenancy may reach other modules only through their `*.Contracts` projects.

### Open decisions (defaults given; change only with the owner's say-so)
| # | Question | Default for v1 |
|---|---|---|
| O1 | Institutions picked by manual entry (not in the trust directory, such as NBFIs) have no `institution_keys` row, so activation is blocked by the trust-entry check. | Leave manual entry as is. The Review step shows the blocker `TRUST_ENTRY_MISSING` with an explanation. Publishing a trust entry from the wizard (`POST /v1/admin/institutions`) is a follow-up. |
| O2 | The gate requires an active signing key even when `isQrGenerationAllowed = false`, but the mock lets a validation-only institution skip the key step. | Keep the backend gate unchanged (key always required), and make the wizard match: Signing key is required for every institution. Drop the "only shown when generation is on" rule. If the owner wants validation-only institutions without a key, relax `TenantReadiness` in a separate change. |
| O3 | A tenant that loses its credential after the Credentials step completed (suspended, revoked). | Reconcile reports CREDENTIALS as `IN_PROGRESS` again. No rotation endpoint exists; it is out of scope. |

---

## 2. The contract (Part A implements it, Part B consumes it)

### 2.1 Steps
Step codes are fixed strings, in this order:

| Order | Code | Required | Completed when (live fact) |
|---|---|---|---|
| 1 | `PROFILE` | yes | the tenant row exists |
| 2 | `CONFIGURATION` | yes | the configuration PATCH was saved, or an active credential exists |
| 3 | `CREDENTIALS` | yes | the tenant has an ACTIVE `tenant_api_clients` row |
| 4 | `CERTIFICATE` | no | the active credential has a certificate thumbprint, or the step was explicitly skipped |
| 5 | `SIGNING_KEY` | yes (O2) | the tenant has an ACTIVE `crypto_keys` row |
| 6 | `REVIEW` | yes | the tenant status is ACTIVE |

Step statuses: `NOT_STARTED`, `IN_PROGRESS`, `COMPLETED`, `SKIPPED`, `FAILED`.

### 2.2 Reconcile rules (the single source of truth for status)
Computed on every `GET .../onboarding` and after every step write. `row` is the stored row, if any.

| Step | Result |
|---|---|
| `PROFILE` | `COMPLETED` always. |
| `CONFIGURATION` | `COMPLETED` if `row.status = COMPLETED` or an active credential exists; else `NOT_STARTED`. |
| `CREDENTIALS` | `COMPLETED` if an active credential exists. Else `IN_PROGRESS` if `row.status = COMPLETED` (it was issued and is no longer active, O3). Else `NOT_STARTED`. |
| `CERTIFICATE` | `COMPLETED` if the active credential has a thumbprint. Else `SKIPPED` if `row.status = SKIPPED`. Else `NOT_STARTED`. |
| `SIGNING_KEY` | `COMPLETED` if `HasActiveSigningKeyQuery` is true; else `NOT_STARTED`. |
| `REVIEW` | `COMPLETED` if the tenant is ACTIVE; else `NOT_STARTED`. |

`currentStep` is the first step, in order, whose status is not `COMPLETED` or `SKIPPED`. It is `null` when the tenant is ACTIVE. Reconcile writes back any status that changed (with `completed_at`/`completed_by` set on the first transition to `COMPLETED`).

`blockers[]` is a list of `{code, message}` for everything that stops activation: `CONFIGURATION_MISSING`, `CREDENTIAL_MISSING`, `SIGNING_KEY_MISSING`, `TRUST_ENTRY_MISSING`. `canActivate` is `blockers.length == 0 && status == PENDING`.

### 2.3 Endpoints
All under `/v1/admin/...`, policy `AdminCredentialTree`, errors as problem-details. "New" is what Part A builds.

| Step | Verb and route | State |
|---|---|---|
| Read progress | `GET /tenants/{id}/onboarding` | **new** |
| 1 Profile | `POST /tenants` (`RegisterTenantRequest`) | exists |
| 2 Configuration | `PATCH /tenants/{id}/configuration` body `{isQrGenerationAllowed, isQrValidationAllowed}` | **new**, Pending tenants only |
| 3 Credentials | `POST /tenants/{id}/tenant-configuration` body with the two flags; 201 returns the one-time `clientSecret`; 409 if already provisioned | exists |
| 4 Certificate | `POST /tenants/{id}/client-certificate` `{thumbprintSha256, subject, expiresAt}`; or skip with `PUT /tenants/{id}/onboarding/steps/CERTIFICATE` body `{"status":"SKIPPED"}` | exists, PUT is **new** |
| 5 Signing key | `POST /v1/crypto-keys` `{tenantId, mode: "Generate"|"Adopt", privateKeyPem?}` (policy `KeyAdmin`, which accepts `admin`) | exists |
| 6 Review | `POST /tenants/{id}/activate`; 409 now carries `blockers[]` | exists, body **extended** |
| List | `GET /tenants` rows gain `onboarding:{completedSteps,totalSteps,currentStep}` (only for PENDING) and `hasRateCard` | **extended** |
| Detail | `GET /tenants/{id}` gains `hasRateCard` | **extended** |
| Rate cards | `POST/GET/DELETE /billing/rate-cards`: `createdBy` is now taken from the authenticated actor and the request field is ignored | exists, **changed** |

### 2.4 `GET /tenants/{id}/onboarding` response
```json
{
  "tenantId": "guid",
  "status": "PENDING",
  "currentStep": "CREDENTIALS",
  "canActivate": false,
  "hasRateCard": false,
  "steps": [
    { "code": "PROFILE", "status": "COMPLETED", "required": true,
      "completedAt": "2026-10-04T08:00:00Z", "completedBy": "platform:admin" }
  ],
  "blockers": [ { "code": "CREDENTIAL_MISSING", "message": "No active API credential." } ],
  "configuration": { "isQrGenerationAllowed": true, "isQrValidationAllowed": true },
  "credential": { "clientId": "031008-ab12cd34", "status": "ACTIVE", "expiresAt": "..." },
  "certificate": { "thumbprintSha256": "...", "subject": "...", "expiresAt": "..." },
  "signingKey": { "keyId": "...", "version": 1, "status": "ACTIVE" }
}
```
`credential`, `certificate` and `signingKey` are `null` when absent. **Never** include a secret, a hash or key material. The 6 `steps` are always returned, in order.

---

## Part A: `rvl-secure-bqr-manager`

Branch: cut with `/rvl-create-branch`. Note the working branch today is `feature/metering-billing`, while `AGENTS.md` at the workspace root says this submodule tracks `feature/mtls-server`. Ask the owner which one to branch from before step A1.

Charter note: the submodule's `AGENTS.md` says anything outside the BanglaQR spec must come from a separate approved doc. This guide is that doc; reference it in the commit.

### A. Database
- **A1.** Fix `db/migrations/README.md` and `docs/design/database-design.md`: both mention `013_drop_tenant_applications.sql`, which is not in `db/migrations/`. Replace the 013 row with `013_tenant_onboarding.sql`. Do not recreate the dropped migration.
- **A2.** Create `db/migrations/013_tenant_onboarding.sql`, copying the style of `009_tenant_client_certificates.sql` (header comment, `BEGIN; ... COMMIT;`, fully idempotent):
  ```sql
  CREATE TABLE IF NOT EXISTS public.tenant_onboarding_steps (
      tenant_id     uuid         NOT NULL REFERENCES public.tenants (tenant_id) ON DELETE RESTRICT,
      step_code     varchar(30)  NOT NULL,
      status        varchar(20)  NOT NULL DEFAULT 'NOT_STARTED',
      completed_at  timestamptz  NULL,
      completed_by  varchar(200) NULL,
      last_error    varchar(500) NULL,
      created_by    varchar(200) NULL,
      created_at    timestamptz  NOT NULL DEFAULT now(),
      modified_by   varchar(200) NULL,
      modified_at   timestamptz  NULL,
      CONSTRAINT pk_tenant_onboarding_steps PRIMARY KEY (tenant_id, step_code)
  );
  ```
  Add two CHECK constraints (`step_code IN ('PROFILE','CONFIGURATION','CREDENTIALS','CERTIFICATE','SIGNING_KEY','REVIEW')` and `status IN ('NOT_STARTED','IN_PROGRESS','COMPLETED','SKIPPED','FAILED')`) using whatever idempotent constraint helper `db/migrations/README.md` describes (the `pg_temp.add_constraint_if_missing` pattern in `002_tenancy.sql`). Add the grant in the same `DO` block style as `012_billing.sql` (lines ~334-339): `IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sbqr_app_runtime') THEN GRANT SELECT, INSERT, UPDATE ON public.tenant_onboarding_steps TO sbqr_app_runtime; END IF;`. No backfill: missing rows mean NOT_STARTED and reconcile creates them.
- **A3.** Apply the migration twice to a local Postgres (`docker/`) to prove it is idempotent.

### B. Domain (Tenancy.Domain)
- **B1.** Add `Aggregates/OnboardingStepCode.cs` (enum of the 6 codes, plus `ToWire()`/`Parse()` for the upper-snake strings) and `Aggregates/OnboardingStepStatus.cs` (enum of the 5 statuses).
- **B2.** Add `Aggregates/TenantOnboardingStep.cs`: fields `TenantId`, `Code`, `Status`, `CompletedAt`, `CompletedBy`, `LastError`, implementing `IAuditableEntity` (`src/SharedKernel/SBQR.SharedKernel/Persistence/IAuditableEntity.cs`) so `TenancyAuditColumnInterceptor` stamps the audit columns, as it does for `Tenant`. Methods: `MarkCompleted(actor, now)`, `MarkSkipped(actor, now)`, `MarkInProgress()`, `Reset()`. `MarkCompleted` keeps the first `CompletedAt`/`CompletedBy` if already completed.
- **B3.** Add `Interfaces/ITenantOnboardingRepository.cs`: `GetAllAsync(TenantId)`, `UpsertAsync(TenantOnboardingStep)`, and a bulk `GetProgressAsync(IReadOnlyCollection<TenantId>)` returning `(completed, total, currentStep)` for the list.
- **B4.** (O2) No change to `TenantReadiness` or `Tenant.Activate`.

### C. Persistence (Tenancy.Infrastructure)
- **C1.** Add `Persistence/Configurations/TenantOnboardingStepConfiguration.cs` (`IEntityTypeConfiguration`, `builder.ToTable("tenant_onboarding_steps", "public")`). Mirror `TenantConfiguration.cs`: explicit column names and types, the status and code stored as upper-snake strings through a `HasConversion` like the `Status` conversion at lines 87-94, composite key `(TenantId, Code)`, audit columns as at lines 97-117, and the same CHECK names as in A2 so the drift test matches. `TenancyDbContext` already calls `ApplyConfigurationsFromAssembly`, so only the new `DbSet<TenantOnboardingStep> OnboardingSteps` property is needed there.
- **C2.** Add `Persistence/Repositories/TenantOnboardingRepository.cs` implementing B3, and register it in `TenancyModule.RegisterServices` next to `ITenantRepository`.
- **C3.** Schema drift test: in `tests/SBQR.Tenancy.IntegrationTests/Schema/SchemaModelDriftTests.cs`, add `"public.tenant_onboarding_steps"` to `TenancyOwnedTables` (line ~40-43). If the drift test reports a name or default mismatch, fix the EF configuration or the migration, not the test.

### D. Cross-module seams (Contracts only; run `ModuleBoundaryTests` after each)
- **D1.** IdentityAccess: add to `ITenantApiClientProvisioner` (`src/Modules/IdentityAccess/SBQR.Modules.IdentityAccess.Contracts/ITenantApiClientProvisioner.cs`) one read method:
  `Task<ActiveConfigurationSummary?> GetActiveAsync(Guid tenantId, CancellationToken ct = default);`
  plus a record `ActiveConfigurationSummary(Guid CredentialId, string ClientId, string Status, DateTimeOffset? ExpiresAt, string? CertificateThumbprint, string? CertificateSubject, DateTimeOffset? CertificateExpiresAt)`. Implement it in `src/Modules/IdentityAccess/SBQR.Modules.IdentityAccess.Application/TenantApiClientProvisioner.cs` from the same row `HasActiveAsync` reads (the single ACTIVE row per tenant). This one call supplies both credential and certificate facts, so no separate certificate seam is needed. Update every fake or stub implementing the interface in tests.
- **D2.** KeyCustody: reuse `GetSigningKeyQuery` (`KeyCustody.Contracts/GetSigningKeyQuery.cs`) for the summary shown in the response, and `HasActiveSigningKeyQuery` for the status fact. Read `GetSigningKeyQuery.cs` first to see its result shape. If it cannot tell you the key's status, add a small `GetActiveSigningKeySummaryQuery(Guid TenantId)` returning `{KeyId, Version, Status}?` in the same Contracts project, with a handler in KeyCustody.Application.
- **D3.** Billing: `src/Modules/Billing/SBQR.Modules.Billing.Contracts` exists. Add `HasRateCardQuery(Guid TenantId) : IRequest<bool>` (or a batch `GetTenantIdsWithRateCardQuery(IReadOnlyCollection<Guid>)` returning a set, for the list) with its handler in Billing.Application, reading through `IRateCardRepository` (`Billing.Domain/RateCards/IRateCardRepository.cs`). Add a project reference from Tenancy.Application to Billing.Contracts, and update `ModuleBoundaryTests` if it needs to allow this edge (it currently has a rule for who may reference what; read it before editing, and keep the rule as tight as it is).
- **D4.** InstitutionTrust: reuse `GetInstitutionPublicKeyQuery` exactly as `ActivateTenantCommandHandler.EvaluateReadinessAsync` does. No new seam.

### E. Application (Tenancy.Application)
- **E1.** Add `Onboarding/IOnboardingReconciler.cs` and `Onboarding/OnboardingReconciler.cs`: `Task<OnboardingView> ReconcileAsync(Tenant tenant, CancellationToken ct)`. It gathers the facts through D1-D4 (one call each), applies the rules in section 2.2, upserts any changed rows, and builds the view (steps, currentStep, blockers, canActivate, configuration, credential, certificate, signingKey, hasRateCard). Put the pure rule logic in a static, easily unit-tested method that takes the facts as plain values.
- **E2.** Query `Queries/GetTenantOnboarding/` (query, handler, result): loads the tenant (NotFound if missing), calls the reconciler, returns the view. Reconcile writes rows, so the handler commits through `IUnitOfWork` like the command handlers do.
- **E3.** Command `Commands/SaveTenantConfiguration/` (command, handler, validator), following the layout of `ProvisionTenantConfiguration/`. Handler: load tenant; refuse with `InvariantViolation` unless status is PENDING; refuse if an active credential already exists (the flags are already in force; changing them goes through the existing lifecycle, not this wizard); call `tenant.SetQrEntitlements(...)` (map its `ArgumentException` to `ValidationFailed`); save; mark `CONFIGURATION` completed; log audit action `tenant.onboarding.step_completed` with metadata `{"step":"CONFIGURATION"}` via `IAuditLogger` (see how `ProvisionTenantConfigurationCommandHandler` builds `AuditEntry`).
- **E4.** Command `Commands/SkipOnboardingStep/`: allowed only for `CERTIFICATE`. Anything else is `ValidationFailed` (400). Refuse if a certificate is already registered (the step is completed, nothing to skip). Refuse for non-PENDING tenants. Marks the row `SKIPPED`, audits `tenant.onboarding.step_skipped`.
- **E5.** Existing handlers record their step so timestamps and actors are right even before the next read. In `ProvisionTenantConfigurationCommandHandler` (after step 4/5), `RegisterClientCertificateCommandHandler`, and `ClearClientCertificateCommandHandler`, call the reconciler (or the repository directly) so `CREDENTIALS` / `CERTIFICATE` rows are updated. Keep it best-effort and cheap: a failure here must never fail the primary operation, because the next `GET /onboarding` reconciles anyway. Do not add steps-table writes to `CreateTenantCommandHandler`; `PROFILE` is always `COMPLETED` by rule.
- **E6.** Activate: in `ActivateTenantCommandHandler`, when `Tenant.Activate` throws on a failed gate, build the same blocker list the reconciler uses (reuse one helper) and return it with the failure. Extend `Result`/the controller mapping so the 409 body includes `blockers`. Read `TenantsController.MapLifecycleFailure` (line ~527) and `Result<T>` in `SharedKernel.Application` first and extend them in the least invasive way (for example an optional `Extensions` dictionary on the problem-details body). Do not change the gate's pass/fail logic.
- **E7.** List and detail: extend `TenantResponse` (`Application/Contracts/TenantResponse.cs`) and `TenantResponseBuilder`, plus `ListTenantsQueryHandler` and `GetTenantByIdQueryHandler`, with `hasRateCard` and, for PENDING rows, `onboarding {completedSteps, totalSteps, currentStep}`. For the list page, do it in bulk (one repository call and one Billing call for the page's tenant ids), never per row. For the list, `completedSteps` may come from reconcile-free logic over stored rows plus the cheap facts; if that gets complicated, call the reconciler per PENDING row only (Pending institutions are few) and note the cost in a comment.

### F. API (Tenancy.Api)
- **F1.** DTOs in `Api/Contracts/`: `SaveTenantConfigurationRequest`, `SkipOnboardingStepRequest {status}`, `TenantOnboardingResponse` and its nested records, matching section 2.4 exactly (camelCase JSON, same as the other DTOs).
- **F2.** In `Controllers/TenantsController.cs` add `GetOnboardingAsync` (`GET {id:guid}/onboarding`), `SaveConfigurationAsync` (`PATCH {id:guid}/configuration`) and `SkipOnboardingStepAsync` (`PUT {id:guid}/onboarding/steps/{code}`). Copy the attribute block and the `MapLifecycleFailure` handling from `ProvisionConfigurationAsync` (line ~171). Reject an unknown `{code}` with 400.
- **F3.** `RateCardsController` (`Billing.Api/Controllers/RateCardsController.cs`, `POST` at line ~32): take `createdBy` from `IActorProvider.CurrentActor()` and ignore `CreateRateCardRequest.CreatedBy`. Keep the field in the DTO (marked obsolete in the XML doc) so existing clients do not break. Update `AddRateCardCommand` call site accordingly. Check `tests/SBQR.Billing.IntegrationTests/RateCardIntegrationTests.cs` still passes.
- **F4.** Update the OpenAPI/contract docs under `contracts/` for every route or DTO changed or added.

### G. Tests (write each with its step, red-green)
- **G1.** Unit, `tests/SBQR.Modules.Tenancy.Tests/Domain/`: `TenantOnboardingStepTests` (state transitions, first-completion timestamp kept).
- **G2.** Unit, `tests/SBQR.Modules.Tenancy.Tests/Application/`: `OnboardingReconcilerTests` covering every row of the rule table in 2.2 and the drift case (stored COMPLETED credential, no active credential, expects IN_PROGRESS); `currentStep` ordering; blockers; `SaveTenantConfigurationCommandHandlerTests` (Pending ok, Active refused, both-false refused, credential-exists refused); `SkipOnboardingStepCommandHandlerTests` (only CERTIFICATE, refused when a certificate exists). Follow the style of `ProvisionTenantConfigurationCommandHandlerTests.cs`.
- **G3.** Integration, `tests/SBQR.Tenancy.IntegrationTests/Onboarding/`, using `PostgreSqlFixture` and `TenancyHostBuilder` as `ActivateTenant/ActivateTenantTests.cs` does: register, save configuration, provision, register certificate, skip, create key (if the host builder can reach KeyCustody; otherwise stub the seam), activate; assert the onboarding view after each step; a resume test that builds a fresh scope between steps; the list returns `onboarding` for a Pending tenant.
- **G4.** Update any existing test that fakes `ITenantApiClientProvisioner` or constructs `TenantResponse`.

### H. Docs
- **H1.** Add `docs/onboarding-runbook.md` (steps, endpoints, what the statuses mean, the one-time-secret rule, the rate-card-must-be-future-month rule and the effect of a missing card).
- **H2.** Update `docs/design/database-design.md` with the new table.

### Part A done when
`dotnet build` and `dotnet test` pass for the whole solution (including `SchemaModelDriftTests` and `ModuleBoundaryTests`), `013` applies twice cleanly, and a manual run against the local stack shows the section 2.4 response for a half-onboarded tenant.

---

## Part B: `rvl-sbqr-portal`

Branch: cut off `main` with `/rvl-create-branch`. Read `AGENTS.md` and `DESIGN.md` first. Hard rules from `AGENTS.md`: same-origin `/v1` URLs only, tokens in memory only, secrets and keys shown once and never stored, no new UI or state libraries (TanStack Query is allowed), design tokens only, no imports across surfaces (shared code goes in `src/shared`), match `design/portal-prototype.html`. Run `npm run lint`, `npm test`, `npm run build` before finishing.

Work against the real backend from Part A. While Part A is unmerged, develop against `vi.stubGlobal('fetch')` fixtures that match section 2.

### A. API infrastructure
- **B1.** `src/shared/api/client.ts`: add `apiSend<T>(method, path, body?)` next to `apiGet` (line ~210). Same bearer token, same single retry on 401, `Content-Type: application/json`, returns parsed JSON or `undefined` for 204. Also change error handling for both helpers: throw an `ApiError` (new class, exported) with `status`, `title`, `detail` and `blockers` parsed from the problem-details body (fall back to the status text if the body is not JSON). Keep the current message text for `apiGet` failures that existing tests assert on, or update those tests in this step. Extend `src/shared/api/client.test.ts` for 201, 204, 409 with a body, and non-JSON errors.
- **B2.** Add a `QueryClientProvider` in `src/App.tsx` around the router (one `QueryClient`, `retry: false` for mutations, `staleTime` of about 10 seconds for queries). Tests that render surfaces need a provider; add a small test helper in `src/test/` (for example `renderWithProviders`) and use it in the tests you touch.

### B. Data layer (surface-local: `src/surfaces/staff/institutions/api/`)
- **B3.** `types.ts`: wire types matching sections 2.3 and 2.4 (`TenantDto`, `TenantOnboardingDto`, step and status unions, `Blocker`). `mappers.ts`: `toInstitution(dto)` producing the existing `Institution` shape, with `id` = tenant GUID. Update `institutions/types.ts` so `Institution` carries what the screens need from the server (`onboarding`, `hasRateCard`) and drop the fields only the mock used.
- **B4.** Query hooks with stable keys: `useTenants(filters)` -> `['tenants', filters]`, `useTenant(id)` -> `['tenant', id]`, `useOnboarding(id)` -> `['onboarding', id]` (all via `apiGet`).
- **B5.** Mutation hooks, each invalidating `['onboarding', id]`, `['tenant', id]` and `['tenants']` on success: `useRegisterTenant`, `useSaveConfiguration`, `useProvisionCredentials` (sends the stored flags explicitly), `useRegisterCertificate`, `useSkipCertificate`, `useCreateSigningKey`, `useActivateTenant`. `useProvisionCredentials` must treat a 409 as "already done": refetch the onboarding view and resolve without surfacing an error. The mutation result holding `clientSecret` is returned to the caller only; never put it in the query cache (`gcTime: 0`, do not call `setQueryData` with it).
- **B6.** Tests with `vi.stubGlobal('fetch')` (no MSW; copy the pattern in `src/surfaces/staff/onboarding/use-institution-directory.test.ts`): one per hook for the success path and the error path.

### C. Wizard rewrite (`src/surfaces/staff/onboarding/`)
Do one wizard step per commit-sized change, keeping `npm test` green throughout. Steps 1-3 can reuse the existing zod schemas in `steps.tsx`.
- **B7.** Shell (`onboarding-page.tsx`): replace the store-backed state with `tenantId` from the route (`?resume=<id>`) or from the Profile mutation result. When resuming, fetch `useOnboarding(tenantId)` and start at `currentStep`. Remove the mock-store `Pending` guard and `firstIncomplete()`. If the tenant is not PENDING or is unknown, redirect to the list (use the 404 from the API). Step order becomes Institution, Configuration, Credentials, Certificate, Signing key, Review.
- **B8.** Stepper: render each step's status from the server (`COMPLETED`, `SKIPPED`, current, upcoming). Allow going back to a completed step only where it is meaningful (the profile is read-only after creation; say so in the UI rather than faking an edit).
- **B9.** Institution step: on Continue call `useRegisterTenant` with the existing profile fields and advance on success. Show a 409 (duplicate code) as a field error on the institution picker. Keep the trust-directory picker (`use-institution-directory.ts`) as is (O1).
- **B10.** Configuration step: on Continue call `useSaveConfiguration`. Keep the rule that at least one switch is on (server also enforces it).
- **B11.** Credentials step: new position (before Certificate). On Issue call `useProvisionCredentials`, put the returned `clientId`/`clientSecret` only into component state, and open the existing one-time `SecretDialog` (`secret-dialog.tsx`). Continue is enabled only after the dialog was acknowledged. On resume with CREDENTIALS already `COMPLETED`, show "Credentials issued on <date> (client ID ...). The secret cannot be shown again." and a Continue button, with no re-issue action. Remove `demoCredentials()`.
- **B12.** Certificate step (optional): form posts `useRegisterCertificate`; a "Skip for now" button calls `useSkipCertificate`. Keep `certSchema` (`institutions/schemas.ts`).
- **B13.** Signing key step: required (O2). Modes Generate or Adopt via `useCreateSigningKey`. Keep `pemSchema` validation; the PEM goes into the request once and is dropped (no state, no storage, no logging). Remove the "only when generation is on" condition and the `keyMode` field.
- **B14.** Review step: show the onboarding view's `blockers` (with `TRUST_ENTRY_MISSING` explained per O1) and disable Activate while `canActivate` is false. Activate calls `useActivateTenant`; on a 409 with `blockers`, render them. Add a "Finish later" button that just navigates to the list (progress is already saved). Show a non-blocking notice: "No rate card yet. Usage will not be billed until one is set for a future month," linking to the Billing tab.
- **B15.** Remove the Rate card step, `addRateCard`, and the "Activate disabled without a rate card" rule from the wizard.
- **B16.** Rewrite `onboarding.test.tsx` against stubbed `fetch`: happy path through all six steps; resume at CREDENTIALS after a reload; 409 on provisioning treated as done; 409 on activate shows blockers; skip certificate; the secret is not present in the DOM after the dialog closes.

### D. List, detail and billing
- **B17.** `institutions-page.tsx`: use `useTenants`. Keep the search and filters (apply them client-side if the API paging does not support them; read `ListTenantsRequest` in the backend to see what it does). Pending rows show "N of M setup steps done" and a "Continue setup" button to `/staff/institutions/new?resume=<id>`. Active rows with `hasRateCard === false` show a "No rate card" warning.
- **B18.** `institutions/actions.ts`: rebuild `setupItems()` and `institutionNote()` from the onboarding DTO; remove the rate-card item. Update `actions.test.ts`.
- **B19.** `institution-detail.tsx`: use `useTenant` and `useOnboarding`. The Setup progress card, credential card (client ID, status, expiry, flags), certificate card and signing-key card read from the DTO. Replace the text-only `keyText`. Show the "No rate card" warning on the Billing tab (`institution-billing.tsx`) with the add-rate-card entry point.
- **B20.** Rate cards: the existing rates page (`src/surfaces/staff/rates/`) and drawer (`new-rate-card-drawer.tsx`) become API-backed (`GET/POST/DELETE /v1/admin/billing/rate-cards`, `GET` filtered by `tenantId`). The drawer must default `effectiveFrom` to the 1st of **next** month and reject earlier dates in the form (the backend rejects any date up to today in Dhaka). Do not send `createdBy`. Replace `rates/store.ts`. Keep money formatting through `src/shared/format.ts` and periods as `YYYY-MM`.
- **B21.** Update `institutions-page.test.tsx`, `institution-detail.test.tsx`, `rates-page.test.tsx`, `rates.test.ts` for the new data source.

### E. Remove the mocks
- **B22.** Delete `institutions/store.ts`, the demo helpers in `institutions/credentials.ts`, and the "UI only" header comments in `steps.tsx` and `onboarding-page.tsx`. The list-page action buttons (suspend, reactivate, terminate, certificate replace, credentials drawer: `use-institution-actions.tsx`, `credentials-drawer.tsx`, `certificate-drawer.tsx`) still call the store. Either wire them to the existing endpoints (`POST /tenants/{id}/suspend|reactivate|terminate`, `POST/DELETE .../client-certificate`) in this change, or, if time is short, keep a small adapter and list it as a follow-up in the PR. Do not leave dead code that still imports the deleted store; `npm run build` must pass.

### Part B done when
`npm run lint`, `npm test` and `npm run build` pass, and the manual end-to-end below passes against the local stack.

---

## 5. Manual end-to-end check (after both parts)
1. Start the local stack, sign in as admin, open `/staff/institutions/new`.
2. Register an institution from the directory. Close the tab. Reopen the list: the institution is Pending with "1 of 6 steps done". Click Continue setup: you land on Configuration.
3. Save configuration, issue credentials (copy the secret from the dialog), close the tab, resume: you land on Certificate, and Credentials shows as issued with no secret.
4. Skip the certificate, create a signing key, reach Review. If the institution is not in the trust directory, confirm the `TRUST_ENTRY_MISSING` blocker shows and Activate is disabled.
5. Activate a directory institution. Confirm the Active row shows "No rate card". Add a rate card for next month on the Billing tab: the warning clears.
6. Confirm no secret, hash or PEM appears in the `/onboarding` response, the browser's storage, the console or the backend log.
7. Check `audit_logs` for `tenant.configuration.provisioned`, `tenant.onboarding.step_completed`, `tenant.onboarding.step_skipped`, `tenant.activated`.

## 6. Risks to watch
- **Reconcile on read writes to the DB.** Keep it cheap and idempotent. Two admins on the same draft can both reconcile; the composite primary key plus upsert makes that safe.
- **One-time secret.** If someone closes the tab after provisioning but before copying the secret, it is lost. Recovery is a rotation flow that does not exist yet; tell the owner this limitation explicitly in the PR.
- **Billing gap.** An institution activated now has no card until at least next month, so its usage this month is not drafted into a statement (`StatementCalculator`). Make sure Finance knows (runbook H1).
- **Cross-module coupling.** The Billing seam (D3) adds an edge from Tenancy to Billing.Contracts. If `ModuleBoundaryTests` forbids it and the owner does not want it relaxed, compute `hasRateCard` in the portal from `GET /billing/rate-cards?tenantId=` instead and drop D3; Part B then calls that endpoint for the badge.

## 7. Out of scope
Credential rotation, publishing trust entries from the wizard (O1), relaxing the signing-key gate (O2), roles or maker-checker approval, assigning a draft to a staff member, and any change to the QR spec behavior.

## 8. Suggested commit slices
Part A: migration + domain + persistence (A, B, C) | seams (D) | application (E) | API + rate-card change (F) | tests and docs (G, H). Part B: infrastructure (B1-B2) | data layer (B3-B6) | wizard (B7-B16, one step per commit) | list/detail/rates (B17-B21) | mock removal (B22). Bump the submodule pointers in the workspace root last, in a separate commit.

## 9. Using this guide with `/rvl-commit`
`/rvl-commit` needs a plan doc inside the submodule it reviews. When you start Part A, copy sections 1-2 and Part A into `rvl-secure-bqr-manager/docs/plans/2026-10-04-institution-onboarding.md`; for Part B copy sections 1-2 and Part B into `rvl-sbqr-portal/docs/plans/2026-10-04-institution-onboarding.md`. Freeze each copy before implementing, as the existing `docs/plans/2026-10-03-remove-tenant-applications.md` does (Status, Target submodule, Branch, Rationale, then lettered steps).
