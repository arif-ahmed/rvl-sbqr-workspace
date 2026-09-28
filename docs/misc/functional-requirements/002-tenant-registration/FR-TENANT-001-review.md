# FR-TENANT-001 — Critical Review of the Institute Registration Flow

| Field   | Value            |
|---------|------------------|
| Reviewer | Platform / Security |
| Date    | 2026-09-09       |
| Status  | **Open findings** — see §6 *Action items* |
| Scope   | `Tenancy`, `IdentityAccess`, `KeyCustody`, `InstitutionTrust` modules |

> **TL;DR — there is one real defect. Everything else is hardening.**
> The current 4-call onboarding flow (`register` → `tenant-configuration` →
> `crypto-keys` → `activate`) has a **state-machine gap**: `Activate`
> transitions `Pending → Active` unconditionally, with no check that
> the credential, signing key, or trust-store row exist. That is the
> only thing this FR fixes. The auto-publish failure branch (where
> `crypto-keys` succeeds but the trust row does not) is recovered via
> the **existing** `POST /v1/admin/institutions` endpoint, not by a
> new endpoint. The consolidated `:register` saga proposed in an
> earlier draft is rejected as over-engineering — see §5.2.

---

## 1. BC separation — how clean is it *today*?

The codebase is structured around four bounded contexts. Each has its
own `Domain`, `Application`, `Infrastructure`, and `Api` projects, and
the architecture tests (per `tests/SBQR.ArchitectureTests/`) presumably
forbid cross-BC reaches into `*.Domain.*` / `*.Infrastructure.*`. So
how do the BCs coordinate?

### 1.1 The seam inventory (verified by reading the four handlers)

| BC | Owns | Outbound seams it consumes |
|---|---|---|
| **Tenancy** | `tenants` aggregate | `ITenantDirectory` (in), `ITenantAdmissionDirectory` (out), `ITenantConfigurationProvisioner` (out), `ISender` for cross-module commands |
| **IdentityAccess** | `tenant_configurations`, JWT issuance | `ITenantAdmissionDirectory` (in) |
| **KeyCustody** | `crypto_keys`, vault | `ITenantDirectory` (in), `IInstitutionTrustPublisher` (out), `ISender` for cross-module commands |
| **InstitutionTrust** | `institution_registries`, `institution_keys` | (no outbound — it is a *sink*) |

The seams **are** well-named and the directions **are** correct. So
what's the problem?

### 1.2 The actual cross-BC violation

Look at `ReactivateTenantCommandHandler` (line 90-96):

```csharp
var reinstatedConfigurations = await _configurations
    .ReinstateAllForTenantAsync(tenant.Id.Value, cancellationToken)
    .ConfigureAwait(false);

await _mediator
    .Send(new ReinstateTenantSigningKeysCommand(tenant.Id.Value), cancellationToken)
    .ConfigureAwait(false);
```

Tenancy is **dispatching MediatR commands into two other BCs**
(`ReinstateTenantSigningKeysCommand` lives in `KeyCustody.Application`).
That is fine for a single MediatR registration if the cross-BC
command is a *request for action* with no return value semantics. The
problem is what the *current* flow asks Tenancy to do at registration
time, where the cross-BC commands are *not* dispatched:

- `SuspendTenantCommandHandler` (per `TenantsController` XML doc §
  `SuspendAsync`) cascades to credentials + signing keys via the same
  `ITenantConfigurationProvisioner` + MediatR pattern.
- `ActivateTenantCommandHandler` — the **greenfield path** — **does
  not** cascade to credentials or signing keys (lines 24-27 of the
  handler XML doc are explicit about this).

> **Finding F-1 (BC separation: nominal pass, semantic gap).** The
> *shape* of the seams is clean — Tenancy does not reach into other
> BCs' aggregates or DbContexts. The *semantic* contract is
> inconsistent: Suspend and Reactivate cascade via the seams, but
> Activate does not. This is **not a violation of BC boundaries** but
> it is a violation of **uniform behaviour across the lifecycle**.
> An operator reading the three handlers side-by-side cannot predict
> what state the child rows are in after a state transition.

> **Finding F-2 (responsibility leakage: Tenancy owns
> `tenant_configurations.cascade.suspended` audit rows).** Look at
> `ReactivateTenantCommandHandler` lines 113-122: when the cascade
> reinstates N configurations, Tenancy writes a
> `tenant.credentials.cascade.reinstated` audit row with `count: N`.
> Tenancy does not own those credentials; it is **second-guessing**
> what IdentityAccess already audited. Two audit rows for one event
> is a smell.

### 1.3 What the user is asking for is a *different* kind of separation

The user's framing is:

> "tenant configuration and create crypto-key and only upon crypto-key creation and public-key upload happened Tenant should be set activated — through proper separation of BC specific responsibilities"

That is a **causal ordering constraint**, not a BC-boundary issue.
Reading it carefully:

1. **Save tenant configuration** — done in IdentityAccess.
2. **Create crypto-key** — done in KeyCustody.
3. **Public-key upload** (auto-publish into InstitutionTrust) — done
   in InstitutionTrust, called from KeyCustody.
4. **Tenant activates** — done in Tenancy, **only** after 1+2+3.

The current flow does 1 → activate → 2 → 3. So **the activation
fires before the trust store has the public key**. From the user's
perspective, this is the *real* security defect.

---

## 2. State-machine integrity

### 2.1 Today: `Pending` → `Active` is unconditional

`ActivateTenantCommandHandler.Handle` (lines 50-104):

```csharp
tenant.Activate(actor: actorId);                 // line 74
await _tenants.UpdateAsync(tenant, ...);         // line 84
await _uow.SaveChangesAsync(...);                // line 85
await _audit.LogAsync(new AuditEntry(...));      // line 91
```

The aggregate `Tenant.Activate(string actor)` (Tenant.cs lines
125-148):

```csharp
if (Status == TenantStatus.Active)   throw ...;
if (Status == TenantStatus.Terminated) throw ...;
Status = TenantStatus.Active;
IsActive = true;
```

**No precondition checks for the existence of an active signing key,
trust-store row, or API credential.** The state machine guarantees
only that the tenant is not already Active and not Terminated. So:

> **Finding F-3 (state-machine gap).** A tenant can be `Active` while
> having zero crypto-keys and zero trust-store rows. **Every** QR
> the tenant would mint (which would be impossible today only because
> `qr/generate` requires a private key and there is none yet) cannot
> be **verified** by another party because the trust store has no
> public key for that `institution_code`. From a regulator /
> auditor's perspective the platform **asserts** an `Active`
> institute exists when it cannot yet transact or be verified.

### 2.2 The token endpoint already enforces one half of the answer

`IssueClientCredentialsTokenCommandHandler.IssueTenantTokenAsync`
(lines 202-221 of the handler we read in the previous turn):

```csharp
var admission = await _tenants
    .GetAsync(active.TenantId, cancellationToken)
    .ConfigureAwait(false);

if (admission is not (null or TenantAdmissionState.Active))
{
    // ...
    return InvalidClientFailure();
}
```

> **Finding F-4 (admission-state check is downstream-only).** The
> token endpoint already proves the *concept* that "this tenant is
> `Active` *and* has valid credentials". The same shape — "tenant is
> `Active` *and* has a valid trust-store row" — should be enforced at
> state-transition time, not only at token-mint time. Today, a
> `Pending → Active` jump without a signing key means the next QR
> the tenant *thinks* they can issue is silently unverifiable.

### 2.3 What a correct state machine would look like

The activation preconditions should be:

| Precondition | Source BC | Why |
|---|---|---|
| Tenant exists and is not `Active`/`Terminated` | Tenancy | (already enforced today) |
| Tenant has ≥ 1 `Active` API credential | IdentityAccess | The token endpoint is the FI's entry point. Without it, "Active" is a lie. |
| Tenant has ≥ 1 `Active` signing key | KeyCustody | The QR generation endpoint needs the private key. |
| The trust-store has a row for `institution_code` with `status=ACTIVE` | InstitutionTrust | The verification endpoint needs the public key. |

The user is asking for **the last two**. The first two are equally
important and are free given the existing repository reads.

### 2.4 The activation precondition has to live where?

If Tenancy is to enforce "the tenant must have an active signing key
+ trust-store row before activation", it must *read* those rows.
There are two ways:

1. **Add a Tenant admission precondition query.** Tenancy already
   imports `ITenantAdmissionDirectory` (in the IdentityAccess
   package's Contracts project). Add a parallel `ITenantReadinessCheck`
   (or extend the existing seam) that asks IdentityAccess +
   KeyCustody + InstitutionTrust, via published language, whether
   the tenant is "ready". Tenancy calls it from `Activate`.

2. **Push the activation into KeyCustody's
   `GenerateOrAdoptCryptoKeyCommandHandler`.** That handler is the
   *natural completion event* of registration. Today, after a
   successful publish it returns `Ok`. It could — instead — call
   `ITenantActivator.ActivateAsync(tenantId)` (a new seam in the
   Tenancy.Contracts package), which is a one-method, idempotent
   `Pending → Active` transition with the precondition checks
   baked in.

Option 2 is what the user is implicitly asking for, and it is the
cleanest BC split:

- **Tenancy** owns `Pending → Active` (state-machine authority).
- **KeyCustody** owns "a tenant is ready when its key is published".
- **Tenancy** listens (via a domain-event subscriber or via the
  MediatR command in option 2) for "KeyCustody has published" and
  gates the transition on the cross-BC preconditions.

---

## 3. Error scenarios

The current flow has **three** error windows where a partial state
is visible to another operator. They all map to the same underlying
defect: **state is committed too eagerly**, then the cross-BC leg
fails.

### 3.1 The "trust-publish-failed-but-key-committed" window

`GenerateOrAdoptCryptoKeyCommandHandler` (lines 133-159) is explicit:

```csharp
try
{
    await _trust.PublishActivePublicKeyAsync(...).ConfigureAwait(false);
}
catch (Exception ex)
{
    await _audit.LogAsync(new AuditEntry(
        Action: "crypto_key.trust_publish_failed", ...));
    return Result<CryptoKeySummary>.Failure(
        ErrorCode.InvariantViolation,
        $"key created but trust-store publish failed ... Use POST /v1/admin/institutions to register the public key manually.");
}
```

> **Finding F-5 (silent partial-success).** The handler returns
> `Failure` *to the controller*, but the `crypto_keys` row has already
> been committed (line 127: `await _uow.SaveChangesAsync(...)`).
> The operator now has a "key but no trust-store entry" situation
> that is **not** the success nor the failure of any single API
> call — it's a third state visible only in DB introspection.

This is **intentional** in today's design (see `docs/new-tenant-onboard-guide.md`
§7.2: "the operator does not lose the freshly minted key"). But the
audit-log entry is the only signal. **No metric, no alert, no
dashboard** surfaces `crypto_key.trust_publish_failed`.

> **Finding F-6 (the auto-publish audit row is unsearchable).**
> `crypto_key.trust_publish_failed` is keyed by `resource_id =
> cryptoKey.id` — not by `tenant_id`. Operators searching for
> "is my new institute ready" need to grep on resource_id and
> correlate to the tenant. The onboarding guide §14.2 instructs
> exactly this correlation — a smell.

### 3.2 The "tenant exists, credential minted, no key" window

Between `POST /v1/admin/tenants/{id}/tenant-configuration` and
`POST /v1/crypto-keys`, the tenant is:

- `Active` (the operator was supposed to call `/activate` already).
- Has an Argon2id-hashed `clientSecret` that the operator is holding.
- Has **no signing key**.
- Has **no trust-store entry**.

The token endpoint will mint tenant-scoped JWTs for this institute.
The QR generation endpoint will return `500 InternalError` (no
private key). The QR validation endpoint will return
`valid: false` (no public key in trust store).

> **Finding F-7 (token issuance outpaces crypto onboarding).** A
> compromised admin token at this stage can mint a **valid**
> tenant-scoped JWT for an institute that *cannot transact*. From a
> regulator's perspective, the platform is *promising* an `Active`
> tenant exists. From a fraud-prevention perspective, the FI is
> already in possession of a credential that, on its own, looks
> legitimate.

### 3.3 The "trust-store DB outage" window

If `public.institution_keys` is unavailable at the
moment of `POST /v1/crypto-keys`:

- `crypto_keys` row is committed.
- `crypto_key.trust_publish_failed` audit row fires.
- The handler returns `409 InvariantViolation` to the operator.
- Operator must manually `POST /v1/admin/institutions`.

If, however, the outage is at the moment of the **first** QR
generation (the trust-store sync service has not yet pulled the
public key from any source) the validator will return
`valid: false` with `reason: "no_active_key_for_institution"`. This
is **expected** per the onboarding guide §14.2 but is still a
non-obvious failure for the operator.

> **Finding F-8 (no end-to-end readiness check endpoint).** There is
> no `GET /v1/admin/tenants/{id}/readiness` that returns
> `{ credential: ok, signing_key: ok, trust_store: ok, status: Active }`.
> The operator has no way to ask "is this tenant really ready?".

### 3.4 The "cascade reversal" window

The suspend/reactivate cascade in
`ReactivateTenantCommandHandler.ReinstateAllForTenantAsync` (line
90) **also** reinstates *suspended* credentials. If a credential
was *intentionally* suspended (e.g. compromised secret) and the
tenant is reactivated, the secret comes back online.

> **Finding F-9 (cascade reversal can re-enable a compromised
> credential).** The current `ReinstateAllForTenantAsync` is a
> blanket un-suspend. There is no way for an operator to say "this
> credential was rotated out for fraud; do not reinstate it on
> reactivate". (Out of scope for this review, but worth flagging
> because it is the *same* class of "state-machine gap" defect as
> F-3.)

---

## 4. Security concerns

### 4.1 Plaintext `clientSecret` lifetime

The plaintext crosses the boundary *once*: `ProvisionTenantConfigurationCommandHandler`
(line 122) → controller → response body. It is shown ONCE. Good.
But:

> **Finding F-10 (no TLS-only enforcement on the response).** The
> response carries the plaintext. `clientSecret` should never travel
> over plaintext HTTP. Today, this is enforced only by deployment
> policy. The controller should add a defensive header (`Cache-Control:
> no-store`, `Pragma: no-cache`) to ensure intermediate proxies do
> not cache the response. (The OAuth token endpoint has these headers
> per RFC 6749; the configuration endpoint should too.)

> **Finding F-11 (the response body is logged by default ASP.NET
> Core request logging).** Even with `EnableBuffering`, the default
> `IHttpLoggingMiddleware` may include response bodies in DEBUG
> logs. The provisioning endpoint should be marked `[DisableHttpLogging]`
> or wrapped to redact the secret field. (Worth verifying against
> the host's `Program.cs` logging configuration; this is *not*
> verified here because the host project is not in scope.)

### 4.2 Race: double-registration on the same `institution_code`

Two concurrent `POST /v1/admin/tenants` calls with the same
`institutionCode`:

- Both pass FluentValidation (the regex matches).
- Both call `Tenant.Register(...)` and `AddAsync` + `SaveChangesAsync`.
- The DB has a UNIQUE constraint on `tenants.institution_code` (per
  the onboarding guide §4 + schema doc).
- One call wins; the other gets a `23505` unique-violation → mapped
  to `409 InvariantViolation` per the controller (lines 98-103).

> **Finding F-12 (race window is acceptable but not advertised).**
> The race is handled correctly via the DB constraint. But the
> controller does not document this anywhere. A test must pin the
> behaviour; today it is implicit.

### 4.3 Race: `Activate` racing with `ProvisionConfiguration`

- Operator A: `POST /v1/admin/tenants/{id}/activate` (succeeds).
- Operator B (admin token compromised): `POST /v1/admin/tenants/{id}/tenant-configuration`
  on the now-`Active` tenant.
- Both commits succeed. The "Pending or Active" guard in
  `ProvisionTenantConfigurationCommandHandler` (line 80) allows
  both.

This is the **intended** behaviour, but it means a credential can
be minted *after* the tenant is `Active`. Combined with F-7, this
is the most likely path for a tenant token to be minted for an
institute with no signing key.

### 4.4 Idempotency-Key is *not* honored

The current 4-call flow has no `Idempotency-Key` handling at all.
Each call is fire-and-forget. The operator who retries after a
network blip may end up with:

- Two `tenants` rows for the same `institution_code` — caught by
  the unique constraint (F-12).
- Two `tenant_configurations` rows — caught by the
  `single-active-configuration` guard (provisioning throws on a
  second mint).
- Two `crypto_keys` rows — caught by the
  "already has an ACTIVE signing key" guard in
  `GenerateOrAdoptCryptoKeyCommandHandler` (line 106).

So today, *every* call in the flow is at-least-once-safe by virtue
of three separate guards. **That is fortunate, not designed.**

> **Finding F-13 (idempotency-by-guards, not by design).** The four
> handlers each independently re-check their guard on retry. The
> audit log gains an extra `tenant.registered` row attempt that
> never committed (the failed retry still writes a domain event
> before the SaveChanges call? — needs verification; in this code
> path it doesn't because EF Core does not raise events until
> SaveChanges succeeds, but the failure path is fragile). Add an
> `Idempotency-Key` header check on every onboarding endpoint, and
> document the guards in the FR.

### 4.5 Audit attribution

`CreateTenantCommandHandler` (lines 33-60) does **not** write an
`IAuditLogger` row — it relies on the row's own `created_by` /
`created_at` columns (per the XML doc lines 14-22). That is a
deliberate choice to avoid duplication.

> **Finding F-14 (audit-trail asymmetry).** `tenant.registered` is
> recorded via `tenants.created_by` + `tenants.created_at`, but
> `tenant.activated`, `tenant.suspended`, `tenant.configuration.provisioned`,
> etc., are recorded in `public.audit_logs`. **Two storage systems
> hold the same shape of data.** Forensics on a 6-month-old row
> requires knowing which event lives where. The DB design doc
> (`docs/design/database-design.md`) should be the source of truth;
> verify it documents this split.

### 4.6 No outbox / inbox

Cross-BC commands dispatched via MediatR (`ReinstateTenantSigningKeysCommand`,
`SuspendTenantSigningKeysCommand`) are *in-process*. There is no
retry, no persistence, no dead-letter. A `Restart` of the host
between Tenancy's commit and KeyCustody's cascade leaves the
cascade un-run.

> **Finding F-15 (in-process cascade, no outbox).** The
> `SuspendTenantCommandHandler` fires the credential cascade via
> `_configurations.ReinstateAllForTenantAsync` (synchronous, in
> process) and the signing-key cascade via MediatR `Send(...)`. If
> the process dies between the two, the credential cascade
> commits but the signing-key cascade does not. There is no
> retry / reconciliation job documented in this codebase path.
> (The DB design doc may have an outbox; not verified here.)

---

## 5. The fix — what *good* looks like (minimal)

The fix is the activation gating specified in
[FR-TENANT-001 §3](FR-TENANT-001-tenant-registration.md#3-what-changes-in-the-code).
This section records the **rejected** larger designs, so the choice is
auditable.

### 5.1 What ships

A single, scoped change:

1. **One** new record (`TenantReadiness`) in `Tenancy.Domain.Aggregates`.
2. **Three** new pure-read methods on **existing** Contracts interfaces
   — one per BC (`HasActiveAsync`, `HasActiveSigningKeyAsync`,
   `HasActiveTrustEntryAsync`). Each is implemented against the BC's
   own DbContext.
3. **One** added parameter on `Tenant.Activate(string actor)` →
   `Tenant.Activate(string actor, TenantReadiness readiness)`.
   The aggregate throws if `IsReady == false`.
4. **One** seam call change in `ActivateTenantCommandHandler.Handle`
   (~6 lines) and the same in `ReactivateTenantCommandHandler.Handle`.

Net diff: **~80 lines including tests, four files touched, zero new
endpoints, zero new MediatR commands, zero new Contracts projects.**

### 5.2 Rejected alternatives

**A. Consolidated `POST /v1/admin/tenants:register` saga.** A single
endpoint that creates the tenant, provisions the credential, mints
the key, publishes the trust row, and conditionally activates. Rejected:

- Adds a new endpoint, a new MediatR saga, an idempotency table, and
  a "what if step N fails" decision tree.
- Requires deprecating `POST /v1/admin/tenants` (introduces a 6-month
  `410 Gone` window).
- Doesn't fix F-3 by itself — the saga still needs the same readiness
  gate, just inside the saga handler instead of `Activate`.
- Doesn't fix F-1 (lifecycle cascade semantics) at all.
- ~5x more code for ~zero marginal value over the 4-call flow once
  the gate is in place.

**B. `ITenantReadinessGate` composed seam in Tenancy.Contracts.** A
new interface in Tenancy that aggregates the three BC reads into
one `EvaluateAsync` call. Rejected:

- One new Contracts project (or one new file in the existing one)
  for **one** caller (`ActivateTenantCommandHandler`).
- Forces Tenancy to declare *what readiness means*, which is a
  coupling smell — each BC owns its data and already exposes the
  "do I have an X?" boolean.
- Three sibling `Has…Async` methods on the existing interfaces
  are 3 lines of interface code, vs ~30 lines for the composed
  seam + DI registration + tests.

**C. `MarkTenantReadyCommand` dispatched from KeyCustody on
successful publish.** A MediatR command in Tenancy that runs the
gate and transitions `Pending → Active` automatically after the
crypto-key publish. Rejected:

- Adds a cross-BC command for **one** orchestration step that an
  operator can already do with `POST …/activate`.
- Hides the activation behind a `crypto-keys` 201 — operators
  reading the `crypto_keys` audit log will not expect a tenant
  state transition as a side-effect.
- Makes the trust-publish-failure branch (where we *don't* activate)
  silently asymmetric vs the success branch — the operator has no
  clean signal that "the key worked but the tenant is still
  Pending".
- ~2x the logic, with no marginal capability: the gate still has to
  exist somewhere, and `Activate` is the right place because it is
  the only endpoint that *changes* `Status`.

**D. Background readiness reconciler.** A job that polls tenants in
`Pending` and transitions them to `Active` when ready. Rejected:

- Adds an `IHostedService` / `BackgroundService`, a polling
  interval, and a "what about pending-then-cancelled" question.
- The "Activate fires before all rows ready" defect is closed by
  the gate at `Activate` time — there is no auto-activation path
  for an operator who isn't making the activation call.
- Operational observability is worse: a reconciler would need its
  own audit row, metric, and dashboard to explain why some tenants
  flipped status without an admin action.

### 5.3 Why the minimal design is correct

- **Closes F-3** with one precondition check at the only state
  transition that matters.
- **Preserves F-12** (the unique-constraint race) unchanged.
- **Preserves F-14** (audit-storage asymmetry) unchanged.
- **Preserves the Idempotency-by-guards (F-13)** behaviour — every
  handler still re-checks its own guard.
- **Does not pretend to close F-5 / F-6** (the trust-publish audit
  story) — those are a separate hardening PR with their own scope.
- **Two-line diff to Reactivate** gets us the symmetry F-1 was
  complaining about at *zero* cost: if `Reactivate` is gated, the
  operator who deletes a credential out from under a `Suspended`
  tenant and then reactives it gets a clean `409` instead of
  reactivating a half-broken state.

### 5.4 What is *not* in this FR

- No saga. The 4-call flow is unchanged.
- No new endpoint. (`POST /v1/admin/tenants:register` is rejected —
  see §5.2A.)
- No `Idempotency-Key` handling.
- No `ITenantReadinessGate`.
- No `MarkTenantReadyCommand` or domain-event listener.
- No `BackgroundService` reconciler.
- No defensive headers on the provisioning response (separate
  PR; not in scope).
- No audit-row re-keying (separate PR; F-6 stays open).
- No outbox for cross-BC cascades (separate PR; F-15 stays open).

This is intentional. **One defect, one PR, one test file per BC.**

---

## 6. Action items

Minimal viable implementation — exactly the change described in
[FR-TENANT-001 §3](FR-TENANT-001-tenant-registration.md#3-what-changes-in-the-code).

| # | Title | Owner | Severity | Closes |
|---|---|---|---|---|
| **A-1** | Add `TenantReadiness` record in `Tenancy.Domain.Aggregates` (one file). | Platform | **High** | F-3 |
| **A-2** | Add `HasActiveAsync(Guid)` to `ITenantConfigurationProvisioner` (IdentityAccess.Contracts) + EF Core implementation against the existing `tenant_configurations` DbContext. | Platform | **High** | F-3 |
| **A-3** | Add `HasActiveSigningKeyAsync(Guid)` to `ITenantDirectory` (KeyCustody.Contracts) + EF Core implementation against `crypto_keys`. | Platform | **High** | F-3 |
| **A-4** | Add `HasActiveTrustEntryAsync(string)` to `ITenantDirectory` (InstitutionTrust.Contracts) + EF Core implementation against `institution_keys`. | Platform | **High** | F-3 |
| **A-5** | Add the `TenantReadiness` parameter to `Tenant.Activate`; aggregate throws on `!IsReady`. | Platform | **High** | F-3 |
| **A-6** | Gate `ActivateTenantCommandHandler.Handle` (3 seam calls + pass `TenantReadiness` to the aggregate). ~6 lines. | Platform | **High** | F-3 |
| **A-7** | Apply the same gate in `ReactivateTenantCommandHandler.Handle` (the seam dependencies are already injected for the cascade). ~6 lines. | Platform | **Medium** | F-1 |
| **A-8** | Unit tests: three per-BC tests on each `Has…Async` implementation (existing/fresh/suspended); three on `Activate`/`Reactivate` handler gate logic with mocked seams. | QA | **Medium** | F-3 |
| **A-9** | Integration test: full 4-call happy path (existing) + new TC-01..TC-08 from [FR-TENANT-001 §8](FR-TENANT-001-tenant-registration.md#8-test-cases-lean-set). | QA | **Medium** | F-3 |

### 6.1 Explicitly deferred (separate PRs)

These are real findings but **out of scope** for this minimal fix.
Each gets its own PR with its own scope:

| Finding | Why deferred | Target |
|---|---|---|
| F-5 / F-6 (trust-publish audit row keyed on `cryptoKey.id`) | Not blocking; `crypto_key.trust_publish_failed` already exists in the log; re-keying is a separate observability change. | Audit/observability hardening PR |
| F-9 (cascade can re-enable a compromised credential) | Real but tangential. The `Activate` gate doesn't make this worse. | Suspend/reactivate flow FR |
| F-10 / F-11 (`clientSecret` headers + logging redaction) | Defensive headers / log scrubbing — orthogonal to state-machine integrity. | Security hardening PR |
| F-13 (Idempotency-Key) | Today's three-guards pattern is at-least-once-safe; explicit idempotency is nice-to-have. | API hardening PR |
| F-14 (audit-storage asymmetry) | Documentation gap, not a defect. | DB design doc update |
| F-15 (no outbox) | Operational concern. Today's in-process cascades are correct; restart-window risk is acceptable. | Infrastructure PR if a real incident occurs |
| F-12 (concurrency test) | Already handled by unique constraint; test is documentation. | Existing integration suite |

### 6.2 Priority

**Ship A-1 through A-9 in one PR.** Total: ~80 lines of production
code, ~120 lines of tests, one new file (`TenantReadiness.cs`), four
files modified, zero new endpoints, zero new MediatR commands, zero
new Contracts projects.

That is the entire change.

---

## 7. Out of scope for this review

- Rotation (`PUT /v1/crypto-keys/{tenantId}`).
- Termination cascade.
- Tenant directory in BB Trust Store (mocked in dev only).
- Vault provider selection (`Crypto:VaultProvider`).
- Human user accounts / logins.

## 8. Cross-references

- [`docs/new-tenant-onboard-guide.md`](../../new-tenant-onboard-guide.md) —
  the four-call flow this FR gates. **No changes needed** to the
  guide; the four steps remain, with Step 3 (`activate`) now
  returning `409 InvariantViolation` until Steps 1, 2, and the
  equivalent of Step 4 are all done.
- [`FR-TENANT-001-tenant-registration.md`](FR-TENANT-001-tenant-registration.md) —
  the minimal implementation. The 4-step flow (§§1.1, 1.2, 1.3 of
  the FR) is the spec; this review documents the rejected
  alternatives (§5.2).
- [`FR-AUTH-001-platform-admin-token`](../001-platform-admin-token/FR-AUTH-001-platform-admin-token.md) —
  prerequisite for any of the four admin endpoints.
- [`docs/security-constraints.md`](../../security-constraints.md) —
  the C1–C22 security constraints referenced throughout the audit
  findings.
