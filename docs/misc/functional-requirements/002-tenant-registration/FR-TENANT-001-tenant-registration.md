# FR-TENANT-001 — Institute (Tenant) Onboarding: 4-Step Gated Flow

| Field   | Value            |
|---------|------------------|
| Area    | Tenancy (state machine) + IdentityAccess / KeyCustody / InstitutionTrust (data owners) |
| Status  | Draft            |
| Updated | 2026-09-09       |

> **Minimal-viable framing.** Four existing endpoints. One behavioural
> change: `POST /v1/admin/tenants/{id}/activate` is gated by a
> three-read precondition. The gate uses the *existing* manual recovery
> endpoint (`POST /v1/admin/institutions`) as the upload path when the
> crypto-key auto-publish fails. No new endpoints. No saga. No new
> Contracts project. See [FR-TENANT-001-review.md §5](FR-TENANT-001-review.md)
> for the rejected larger designs.

## 1. The four-step flow

```
Step 1  POST /v1/admin/tenants                 { institutionName, institutionCode }
Step 2  POST /v1/admin/tenants/{id}/tenant-configuration
Step 3  POST /v1/crypto-keys                   { tenantId, mode: "Generate" }
Step 4  POST /v1/admin/tenants/{id}/activate
```

### 1.1 Happy path (auto-publish succeeds inside Step 3)

```
Step 1 → 201  tenant created, status = Pending
Step 2 → 201  FI credential provisioned; clientId/clientSecret returned
Step 3 → 201  Ed25519 key minted; publicKeyPem returned; trust row auto-published
Step 4 → 200  status = Active   (gate: credential ✓, key ✓, trust ✓)
```

After Step 4 the tenant is fully onboarded: it has an FI credential, a
signing key, and a trust-directory entry — the platform can mint tenant
tokens, the tenant can mint verifiable QR codes, and verifiers can
resolve the public key.

### 1.2 Recovery path (auto-publish fails inside Step 3)

When `POST /v1/crypto-keys` succeeds (the `crypto_keys` row commits and
`publicKeyPem` is returned) but the auto-publish to InstitutionTrust
throws (DB outage, validation reject), the operator:

```
Step 1 → 201  Pending
Step 2 → 201  credential ✓
Step 3 → 409  InvariantViolation
              (crypto_key committed; trust_publish_failed audit row)
Step 3a → POST /v1/admin/institutions   { institutionCode, institutionName,
                                          publicKeyPem = (from Step 3 body) }
            → 201  trust row published
Step 4 → POST /v1/admin/tenants/{id}/activate
            → 200  Active   (gate now sees credential ✓, key ✓, trust ✓)
```

> **Step 3a uses an endpoint that already exists.**
> `POST /v1/admin/institutions` (the manual trust-directory upsert
> endpoint) is the recovery path. The FR does **not** add a new
> "upload the public key" endpoint — it spells out that the existing
> endpoint serves this role. The gate at Step 4 is the single
> synchronisation point.

### 1.3 Refusal path (operator jumps Step 4 too early)

```
Step 1 → 201  Pending
Step 2 → 201  credential ✓
Step 4 → 409  InvariantViolation
              "Tenant {id} cannot be activated: credential=true,
               signing_key=false, trust_entry=false"
```

The operator must run Step 3 (and Step 3a if Step 3's auto-publish
failed) before Step 4 will succeed.

### 1.4 Adopt-mode variant (institute supplies its own Ed25519 keypair)

Use this variant when an institute already operates a keypair out-of-band
(e.g. issued by BB, generated on an HSM the platform doesn't control) and
the public half is already present (or about to be present) in
`public.institution_keys`. The institute uploads **both PEM
halves** to the platform; the platform re-canonicalises the public half
via BouncyCastle and stores the private half in our vault. The S3 object
key is the **same shape as Generate mode** — Adopt overwrites any prior
Generate-mode blob at the same `(institution, version)` in place. The
Generate-vs-Adopt distinction is recorded in the `crypto_keys` row and
in the audit trail, not in the on-disk filename.

```
Step 1 → 201  Pending                                  (unchanged)
Step 2 → 201  credential ✓                             (unchanged)
Step 3 → POST /v1/crypto-keys  { tenantId,
                                 mode: "Adopt",
                                 publicKeyPem:  "-----BEGIN PUBLIC KEY-----...",
                                 privateKeyPem: "-----BEGIN PRIVATE KEY-----" }
        → 201  crypto_keys row ACTIVE; private half sealed in vault at
               S3 object key `keys/<6digit>_v1_private.pem` (overwrites any
               prior Generate blob at the same version); public half
               re-canonicalised via BouncyCastle, sha256 recorded;
               auto-publish to institution_keys is a no-op when the trust
               row already matches (BR-Adopt-2); emits a `crypto_key.adopted`
               audit row.
        → 400  PEM halves don't form a valid Ed25519 pair (handler catches
                `ArgumentException` from `Ed25519PEMValidator`).
        → 400  malformed PEM.
        → 409  trust-store public-key drift (BR-Adopt-1; no DB write, no
                vault write, `crypto_key.adopt.drift_rejected` audit row).
Step 4 → 200  Active   (same gate as 1.1)
```

The 4-step flow, the readiness gate, the activation audit chain and the
recovery semantics (`POST /v1/admin/institutions`) are unchanged. Only
the body of Step 3 differs.

## 2. The three preconditions

`POST /v1/admin/tenants/{id}/activate` reads three flags from the
existing Contracts seams before driving the aggregate state machine:

| # | Precondition | Existing seam | New method |
|---|---|---|---|
| 1 | Tenant has ≥ 1 `Active` row in `public.tenant_configurations` | `ITenantConfigurationProvisioner` (IdentityAccess.Contracts) | `HasActiveAsync(Guid)` |
| 2 | Tenant has ≥ 1 `Active` row in `crypto_keys` | `ITenantDirectory` (KeyCustody.Contracts) | `HasActiveSigningKeyAsync(Guid)` |
| 3 | Trust store has an `ACTIVE` row for `institution_code` | `ITenantDirectory` (InstitutionTrust.Contracts) | `HasActiveTrustEntryAsync(string)` |

Each method is a pure read returning `bool`, implemented in its BC's
Infrastructure project against its own DbContext. Each is unit-tested
with a mocked repo. **No new Contracts projects. No new interfaces.**

> **Why three reads, not one composed seam?** Each BC owns its data.
> Adding one read method per BC is the smallest possible change. A
> composed `ITenantReadinessGate` was considered and rejected — see
> [review §5.2](FR-TENANT-001-review.md#52-rejected-alternatives).

## 3. What changes in the code

### 3.1 One new record

```csharp
// SBQR.Modules.Tenancy.Domain.Aggregates.TenantReadiness
public sealed record TenantReadiness(
    bool HasCredential,
    bool HasSigningKey,
    bool HasTrustEntry)
{
    public bool IsReady => HasCredential && HasSigningKey && HasTrustEntry;

    public override string ToString() =>
        $"credential={HasCredential}, signing_key={HasSigningKey}, trust_entry={HasTrustEntry}";
}
```

### 3.2 One widened aggregate method

```csharp
// SBQR.Modules.Tenancy.Domain.Aggregates.Tenant
// signature widens by one parameter; existing checks unchanged
public void Activate(string actor, TenantReadiness readiness)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(actor);
    ArgumentNullException.ThrowIfNull(readiness);

    if (!readiness.IsReady)
    {
        throw new InvalidOperationException(
            $"Tenant {Id} cannot be activated: {readiness}");
    }

    // Existing checks — unchanged.
    if (Status == TenantStatus.Active)    throw ...;
    if (Status == TenantStatus.Terminated) throw ...;

    Status = TenantStatus.Active;
    IsActive = true;
    RaiseDomainEvent(new TenantActivated(...));
}
```

The `InvalidOperationException` is caught by the handler (existing
pattern in `ActivateTenantCommandHandler.Handle` lines 76-81) and
mapped to `409 InvariantViolation` via the existing controller's
`MapLifecycleFailure`.

### 3.3 Two handler diffs

`ActivateTenantCommandHandler.Handle` and
`ReactivateTenantCommandHandler.Handle` get the same change (~6 lines
each):

```csharp
var readiness = new TenantReadiness(
    HasCredential: await _configurations.HasActiveAsync(tenant.Id.Value, ct),
    HasSigningKey: await _keys.HasActiveSigningKeyAsync(tenant.Id.Value, ct),
    HasTrustEntry: await _trust.HasActiveTrustEntryAsync(tenant.InstitutionCode, ct));

try
{
    tenant.Activate(actor: actorId, readiness: readiness);
}
catch (InvalidOperationException ex)
{
    return Result<...>.Failure(ErrorCode.InvariantViolation, ex.Message);
}
```

The seam dependencies (`ITenantConfigurationProvisioner`,
`ITenantDirectory` for KeyCustody, `ITenantDirectory` for
InstitutionTrust) are injected as constructor parameters. Two of them
(`ITenantConfigurationProvisioner`, KeyCustody `ITenantDirectory`) are
already injected into `ReactivateTenantCommandHandler` for the cascade.
`InstitutionTrust.ITenantDirectory` is a new dependency in both
handlers.

### 3.4 Three new method implementations

Each BC adds one method to its own DbContext-backed implementation:

```csharp
// IdentityAccess.Infrastructure
public async Task<bool> HasActiveAsync(Guid tenantId, CancellationToken ct)
    => await _db.TenantConfigurations
        .AnyAsync(c => c.TenantId == tenantId
                    && c.Status == TenantConfigurationStatus.Active, ct);

// KeyCustody.Infrastructure
public async Task<bool> HasActiveSigningKeyAsync(Guid tenantId, CancellationToken ct)
    => await _db.CryptoKeys
        .AnyAsync(k => k.TenantId == tenantId
                    && k.Status == CryptoKeyStatus.Active, ct);

// InstitutionTrust.Infrastructure
public async Task<bool> HasActiveTrustEntryAsync(string institutionCode, CancellationToken ct)
    => await _db.InstitutionKeys
        .AnyAsync(k => k.InstitutionCode == institutionCode
                    && k.Status == InstitutionKeyStatus.Active, ct);
```

These are pure reads against existing tables. **No schema changes.**

### 3.5 What the controller does NOT change

`TenantsController.ActivateAsync` and `ReactivateAsync` are unchanged.
The `MapLifecycleFailure` already maps `InvariantViolation` → `409`
with the handler's error message in the body. The readiness summary
("credential=true, signing_key=false, trust_entry=false") rides in
the existing `detail` field.

## 4. Business Rules

- **BR-Activation-1.** A `Pending → Active` or `Suspended → Active`
  transition requires all three readiness flags to be `true`. The
  gate fires before the aggregate's own state-transition guard.
- **BR-Activation-2.** The gate is **idempotent** on the aggregate
  side: a second `activate` call on an already-`Active` tenant is
  rejected by the aggregate's existing self-transition guard before
  the gate runs. No behaviour change for that case.
- **BR-Activation-3.** The gate is **fail-closed**: if any of the
  three seam calls throws (DB outage, network blip), the handler
  propagates the exception and the controller returns `500`. The
  system refuses to guess readiness. Stricter than the existing
  `Suspend` cascade, which swallows cascade failures; this is
  intentional — a guessed-yes gate could put a `Pending` tenant
  into `Active` with a half-broken downstream.
- **BR-Activation-4.** The readiness check is performed **at the
  moment of activation**, not at registration time. There is no
  background reconciler. If the operator runs `provisioning` and
  then deletes the credential row directly in the DB before
  `activate`, the gate will catch it on the next attempt. This is
  a feature, not a bug.
- **BR-Recovery-1.** When `POST /v1/crypto-keys` returns `409`
  because the auto-publish to InstitutionTrust failed, the recovery
  path is `POST /v1/admin/institutions` with the `publicKeyPem`
  from the failed Step 3 response. The body of the recovery call is
  exactly the same shape the auto-publish would have used (per
  `IInstitutionTrustPublisher.PublishActivePublicKeyAsync`).
- **BR-Recovery-2.** After the recovery call, the operator MUST run
  `POST /v1/admin/tenants/{id}/activate` separately. The recovery
  endpoint is a pure trust-directory upsert — it does **not**
  touch tenant state. This keeps Tenancy as the single owner of
  `tenants.status`.

### 4.1 Adopt-mode rules (FR-TENANT-001 §1.4)

- **BR-Adopt-1.** When `mode: "Adopt"` is invoked against a tenant
  whose `public.institution_keys` already has an ACTIVE row
  with a *different* `public_key_sha256` than the canonical public half
  of the supplied PEM pair, the handler MUST reject with `409
  InvariantViolation` and a `crypto_key.adopt.drift_rejected` audit row.
  **No** `crypto_keys` row is committed; **no** vault object is written.
  The institute must either supply the matching public half or
  explicitly rotate the trust row via `POST /v1/admin/institutions`.
- **BR-Adopt-2.** When Adopt matches (no prior row OR the existing
  trust row's sha256 equals the canonical public PEM's sha256), the
  auto-publish to `institution_keys` is functionally a no-op (same
  `public_key_sha256`, same trust-row version). The handler still
  calls the publisher to keep the audit + idempotency symmetric with
  the Generate path.
- **BR-Adopt-3.** The S3 vault object key for an Adopt'd private half is
  the **same** as Generate: `keys/<institutionCode>_v<n>_private.pem`.
  Adopt overwrites any prior Generate blob at the same `(institution,
  version)` in place — the institute's private key is replacing whatever
  the server had. The Generate-vs-Adopt distinction is recorded in the
  `crypto_keys` row and the audit trail, not in the on-disk filename.
- **BR-Adopt-4.** The Adopt audit row shape is `crypto_key.adopted`,
  with metadata `{tenant_id, institution_code, public_key_sha256,
  key_version}`. The drift-rejection row shape is
  `crypto_key.adopt.drift_rejected`, with metadata including
  `existing_pk_sha256` and `supplied_pk_sha256` so an auditor can
  prove *which* hash mismatch triggered the rejection.

## 5. Acceptance Criteria

1. **Happy path.** Register → `tenant-configuration` → `crypto-keys`
   (auto-publish succeeds) → `activate` → `200`, `status = Active`.
2. **Activate before credential.** Register → `activate` → `409`,
   body `credential=false, signing_key=false, trust_entry=false`.
   No `tenant.activated` audit row.
3. **Activate before key.** Register → `tenant-configuration` →
   `activate` → `409`, `signing_key=false`. No state change.
4. **Activate before trust row.** Register → `tenant-configuration` →
   `crypto-keys` (auto-publish **fails**) → `activate` → `409`,
   `trust_entry=false`. No state change.
5. **Recovery then activate.** After step 4, operator runs
   `POST /v1/admin/institutions` with the `publicKeyPem` from the
   failed Step 3 → `201`. Then re-runs `activate` → `200`,
   `status = Active`.
6. **Reactivate is also gated.** Suspend a fully-onboarded Active
   tenant → `Suspended`. Reactivate without any rows being deleted →
   `200` (gate passes — same as AC1). Reactivate after an admin
   manually deletes the credential row → `409`, `credential=false`.
7. **No regression on existing failure modes.** Wrong-tenant
   activate (`404`), unknown tenant (`404`), self-transition (`409`
   from the aggregate's own guard — gate not reached) all behave as
   today.
8. **Audit trail.** Gate-passed activation fires exactly one
   `tenant.activated` (or `tenant.reactivated`) audit row. Gate
   failure fires **no** `tenant.activated` row — the transaction
   rolls back before the audit call.
9. **No new endpoint.** A grep for `POST /v1/admin/tenants:register`
   returns zero matches. A grep for `ITenantReadinessGate` returns
   zero matches. The four onboarding endpoints remain the only
   way to onboard a tenant.

### 5.1 Adopt-mode acceptance criteria (FR-TENANT-001 §1.4)

10. **AC-Adopt-1.** Adopt with both PEM halves forming a valid
    Ed25519 pair and no prior trust row → `201`, `crypto_keys` row
    ACTIVE, `institution_keys` row ACTIVE, S3 object key is
    `keys/<6digit>_v1_private.pem` (same shape as Generate — Adopt
    overwrites any prior blob at the same version in place),
    `crypto_key.adopted` audit row fires, tenant activates after
    Step 4.
11. **AC-Adopt-2.** Adopt with mismatched PEM halves (private key
    derives a public SPKI that differs from the supplied public
    PEM) → `400 ValidationFailed` (existing
    `Ed25519PEMValidator.EnsureMatches` behaviour), no rows in
    `crypto_keys`, no vault object, no `crypto_key.adopted` audit row.
12. **AC-Adopt-3.** Adopt against a tenant whose `institution_keys`
    already has an ACTIVE row with a *different*
    `public_key_sha256` → `409 InvariantViolation` with
    `trust_store public-key drift` message,
    `crypto_key.adopt.drift_rejected` audit row fires, **no**
    `crypto_keys` row, **no** vault object. The existing trust row
    is unchanged (proved by re-running
    `POST /v1/admin/institutions` afterwards with the same PEM —
    `201`).
13. **AC-Adopt-4.** Adopt whose canonical public sha256 equals the
    existing trust row's sha256 → `201`, auto-publish is a no-op
    (same row, same key_version, same sha256), `crypto_key.adopted`
    audit row fires.

## 6. Test cases (lean set)

| TC | Given | When | Then |
|---|---|---|---|
| TC-01 | Fresh tenant, no downstream rows | `activate` | `409`, `credential=false, signing_key=false, trust_entry=false` |
| TC-02 | Fresh tenant + credential | `activate` | `409`, `signing_key=false, trust_entry=false` |
| TC-03 | Fresh tenant + credential + key (no trust row) | `activate` | `409`, `trust_entry=false` |
| TC-04 | Fresh tenant + credential + key + trust row | `activate` | `200`, `status=Active` |
| TC-05 | Suspended tenant that had all three rows pre-suspend; admin deletes credential row manually | `reactivate` | `409`, `credential=false` |
| TC-06 | Already-Active tenant | `activate` | `409` from the existing self-transition guard (gate not reached) |
| TC-07 | `HasActiveAsync` throws (simulated DB outage) | `activate` | `500`, no state change |
| TC-08 | Recovery: Step 3 returns `409` (auto-publish failed), then operator runs `POST /v1/admin/institutions` with the `publicKeyPem` from Step 3, then re-runs `activate` | `activate` | `200`, `status=Active` |
| TC-09 | Full happy-path smoke (Steps 1-4 in order) | All four calls succeed | Tenant ends `Active`; audit chain has the expected rows |
| TC-10 | Fresh tenant, valid Adopt PEM pair, no prior trust row | Step 3 with `mode:"Adopt"` | `201`; tenant ends `Active` after Step 4; `crypto_key.adopted` audit row; S3 object `keys/<code>_v1_private.pem` exists (same key as Generate mode) |
| TC-11 | Fresh tenant, mismatched PEM halves | Step 3 with `mode:"Adopt"` and bad PEM pair | `400`; no rows; no vault object; no `crypto_key.adopted` row |
| TC-12 | Tenant with pre-existing trust row (sha256=X), Adopt supplying priv deriving pub sha256=Y≠X | Step 3 with `mode:"Adopt"` and divergent pub | `409`; no `crypto_keys` row; no vault object; `crypto_key.adopt.drift_rejected` audit row; trust row unchanged |
| TC-13 | Tenant with pre-existing trust row (sha256=X), Adopt supplying priv deriving pub sha256=X | Step 3 with `mode:"Adopt"` and matching pub | `201`; auto-publish is a no-op; `crypto_key.adopted` row; trust row preserved |

## 7. Why this is minimal

| Concern | What this FR adds |
|---|---|
| New endpoint | **0** |
| New Contracts project | **0** |
| New interface | **0** (4 methods on 3 existing interfaces — 3 readiness seams + 1 Adopt-drift seam) |
| New MediatR command | **0** |
| New aggregate event | **0** |
| New DB table / column | **0** |
| New file in `Tenancy.Domain` | **1** (`TenantReadiness.cs`) |
| Modified files (this PR + Adopt amendment) | **8** — `Tenant.cs`, `ActivateTenantCommandHandler.cs`, `ReactivateTenantCommandHandler.cs`, the 3 readiness-seam implementations, `InstitutionTrustPublisher.cs` + `InstitutionUpsertService.cs` (drift seam), `S3SigningKeyStore.cs` (`.adopted` suffix), `GenerateOrAdoptCryptoKeyCommandHandler.cs` (drift guard + audit) |

The whole change fits in **one PR, ~200 lines including tests.**

## 8. What this FR explicitly does NOT do

- **No saga / orchestrator.** The 4-call flow is unchanged. The
  operator runs the steps in order; the gate at Step 4 enforces
  the ordering.
- **No new endpoint for "upload the public key".** The existing
  `POST /v1/admin/institutions` is the recovery path (BR-Recovery-1).
- **No `Idempotency-Key` header handling.**
- **No new Contracts project / `ITenantReadinessGate` interface.**
- **No `MarkTenantReadyCommand` / domain-event listener.**
- **No defensive headers on the provisioning response.**
- **No audit-row re-keying of `crypto_key.trust_publish_failed`.**
- **No outbox pattern for cross-BC cascades.**
- **No change to the suspend/reactivate cascade semantics beyond
  gating the reactivate transition.**
- **No change to the rotation flow.**
- **No consolidated `:register` endpoint** — the recovery path is
  spelled out in §1.2 because the operator's mental model needs to
  be explicit, but it is implemented using the endpoints that
  exist today.

## 9. Out of Scope

- Rotation (`PUT /v1/crypto-keys/{tenantId}`).
- Termination cascade.
- Tenant directory in BB Trust Store (mocked in dev only).
- Vault provider selection (`Crypto:VaultProvider`).
- Human user accounts / logins.

## 10. Cross-references

- [`docs/new-tenant-onboard-guide.md`](../../new-tenant-onboard-guide.md) —
  the existing 4-call flow this FR gates. **No changes needed** to
  the guide; the four steps remain, with Step 4 (`activate`) now
  returning `409 InvariantViolation` until Steps 1, 2, and the
  equivalent of Step 3 are all done. Step 3a (recovery via
  `POST /v1/admin/institutions`) is described in §14.2 of the guide
  today; this FR formalises it.
- [`FR-TENANT-001-review.md`](FR-TENANT-001-review.md) — the full
  review with the rejected larger designs (saga, readiness gate,
  mark-ready command, reconciler).
- [`FR-AUTH-001-platform-admin-token`](../001-platform-admin-token/FR-AUTH-001-platform-admin-token.md)
  — prerequisite for any of the four admin endpoints.
