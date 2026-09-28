# FR-TENANT-001 — Dev Manual Test Guide: Adopt-Mode Tenant Registration

| Field   | Value                  |
|---------|------------------------|
| Area    | Tenancy + IdentityAccess + KeyCustody + InstitutionTrust |
| Status  | Live (FR-TENANT-001 §1.4 implemented) |
| Updated | 2026-09-10             |

> **Reference spec.** Functional requirements, business rules, and the
> 4-step Adopt flow diagram:
> [`FR-TENANT-001-tenant-registration.md §1.4`](FR-TENANT-001-tenant-registration.md#14-adopt-mode-variant-institute-supplies-its-own-ed25519-keypair).
>
> **Companion:** [`FR-TENANT-001-dev-manual-test-guide.md`](FR-TENANT-001-dev-manual-test-guide.md) — the equivalent guide for **Generate mode**. Steps 1, 2, and 4 are byte-identical here; only Step 3 differs.

**Use this guide when the institute already operates an Ed25519 keypair
out-of-band** (HSM, Bangladesh Bank-issued, air-gapped) and you need to
onboard the institute by uploading its **private-key half into our vault**
while leaving the **public-key half in the trust directory untouched.**

> **2026-09-10 refactor — Adopt body shape.** The request body for Step 3b
> carries **only the private-key PEM**. The public-key PEM is no longer
> supplied in the request — the server derives it from the supplied
> private half (RFC 8032 `pub = scalar_base_mult(seed)`) and compares its
> SHA-256 against `public.institution_keys.public_key_sha256`
> in the drift guard. This makes the previous half-pair mismatch path
> (`EC-Adopt-2`) impossible by construction.

---

## 1. Mental model

```
INSTITUTE (HSM)           OPERATOR (Postman)              SBQR
───────────────           ──────────────────              ────
pk_A.pem (trusted)  ───►  Step 1  POST /v1/admin/tenants
sk_A.pem             ───►  Step 2  POST /v1/admin/tenants/{id}/tenant-configuration
                             Step 3a POST /v1/admin/institutions (re-confirm pk_A)
                             Step 3b POST /v1/crypto-keys {mode:"Adopt", sk}
                                ──►  derive pk from sk (RFC 8032)
                                ──►  drift-guard: derived_pk sha256 == trust row sha256
                                ──►  vault: keys/010101_v1_private.pem  (overwrite)
                                ──►  auto-publish: trust row already matches, refresh
                                ──►  audit: crypto_key.adopted
                             Step 4  POST /v1/admin/tenants/{id}/activate
                                ──►  gate: cred✓ key✓ trust✓ → Active
```

Three things are different from Generate mode:

| | Generate | Adopt |
|---|---|---|
| Server creates a new Ed25519 pair | yes | **no** |
| Public key already in trust directory | no | **yes** |
| Vault object suffix | `_private.pem` | **`_private.pem` (overwrites Generate)** |
| Audit row on success | `crypto_key.minted` | **`crypto_key.adopted`** |
| Drift guard runs | no | **yes (returns 409 if derived_pk_sha ≠ trust_store_pk_sha; **also 409 if no trust row at all**)** |
| Step 3a pre-seed required | no (auto-publish creates it) | **yes (Adopt fails closed without it)** |

---

## 2. Prerequisites

- Local stack up: `docker compose up -d` from repo root (Postgres, LocalStack, SBQR.Api).
- Base URL: `http://localhost:8080` (set as Postman collection variable `{{base}}`).
- An institute-issued Ed25519 PKCS#8 PEM (private) and matching SPKI PEM (public).
  Generate locally with:
  ```bash
  openssl genpkey -algorithm Ed25519 -out priv.pem
  openssl pkey -in priv.pem -pubout -out pub.pem
  ```
  Or use the RFC 8032 test vector 1 pair (non-secret fixture) — see `dev-testing-guide-oauth-tenants-keys.md §7.10` for the values.
- Operator token: `{{token}}` — Bearer token from `POST /v1/oauth/token` with `crypto-keys:write` + `admin:tenants:write` + `institutions:write` scopes.

Postman collection variables:
```
base      = http://localhost:8080
token     = <bearer>
tenantId  = <set from Step 1 response>
trustSha  = <set from Step 3a response publicKeySha256>
```

---

## 3. The 4-step Adopt happy path

### Step 1 — Register the tenant

`POST {{base}}/v1/admin/tenants`

**Body (JSON):**
```json
{
  "institutionName": "Example Bank",
  "institutionCode": "010101"
}
```

**Sample 201 response:**
```json
{
  "tenantId": "8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10",
  "institutionName": "Example Bank",
  "institutionCode": "010101",
  "status": "Pending",
  "isActive": true
}
```

Save `tenantId` as `{{tenantId}}`.

### Step 2 — Provision the tenant configuration

`POST {{base}}/v1/admin/tenants/{{tenantId}}/tenant-configuration`

**No body.** The platform mints the `clientId` (`{institutionCode}-{8hex}`) and a 256-bit `clientSecret` server-side; the Argon2id hash is the only thing persisted.

**Sample 201 response:**
```json
{
  "tenantId": "8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10",
  "credentialId": "7f3d2e88-1b6a-4c25-9f31-77b9e1a40d4a",
  "clientId": "010101-a1b2c3d4",
  "clientSecret": "wkzV9p7eR3sK…(32 base64url chars; shown EXACTLY ONCE)",
  "expiresAt": null
}
```

> ⚠️ **The `clientSecret` is the only time you'll ever see it.** Only the Argon2id hash is stored; there is no re-display endpoint. The institute must capture it immediately — use it at `POST /v1/oauth/token` with `grant_type=client_credentials`.

Re-provisioning while a configuration already exists returns **409** (rotation is a separate future flow).

### Step 3a — Pre-seed the trust directory

The trust row must exist with the **same public key** the institute will Adopt. The institute's pk_A is the source of truth; this call records it (and retires any prior active version).

`POST {{base}}/v1/admin/institutions`

**Body (JSON):**
```json
{
  "institutionCode": "010101",
  "instituteType": "01",
  "institutionName": "Example Bank",
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA11qYAYKxCrd9EUvphnl7bH5ubwTlpOjjo9XCo8mj6ek=\n-----END PUBLIC KEY-----\n"
}
```

> The trust-directory row carries the cryptographic publication (institution code + public-key PEM) **plus the human-readable display name** and **the 2-digit Annex A Institution Type** (Tag 26 sub 01 — the BB InstitutionId prefix). All three are NOT NULL on the row, denormalised onto `institution_keys` so a verifier resolving a QR gets the issuer's identity without a join to `public.tenants`. While `instituteType` is technically derivable from `institution_code[..2]`, the trust-store is the source of truth — a BB correction out-of-band propagates through this row rather than being silently re-derived. Omitting or blanking either `institutionName` or `instituteType` is rejected with a 400.

**Sample 201 response:**
```json
{
  "institutionCode": "010101",
  "instituteType": "01",
  "institutionName": "Example Bank",
  "activeKeyVersion": 1,
  "publicKeySha256": "5b6a98060982b10adddde1526f98d9e7a1b87a3bb06f3a6348e88a70fc6a1e13"
}
```

Save `publicKeySha256` as `{{trustSha}}` — you will compare it against the trust row after Step 3b.

### Step 3b — Adopt the private key

`POST {{base}}/v1/crypto-keys`

**Body (JSON):**
```json
{
  "tenantId": "8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10",
  "mode": "Adopt",
  "privateKeyPem": "-----BEGIN PRIVATE KEY-----\n<PKCS8 Ed25519 private key bytes base64>\n-----END PRIVATE KEY-----\n"
}
```

> `mode: "Adopt"` (not `"Generate"`) is the only difference from the Generate-mode flow at this endpoint. Note: **only the private-key PEM is supplied**; the public half is derived server-side and cross-checked against the trust row that Step 3a pre-seeded.

**Sample 201 response (`CryptoKeySummary`):**
```json
{
  "cryptoKeyId": "b1e0f2c7-3a8d-4d9a-9c51-6f2c8b7e1a04",
  "tenantId": "8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10",
  "keyId": "sbqr-signing",
  "keyVersion": 1,
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA11qYAYKxCrd9EUvphnl7bH5ubwTlpOjjo9XCo8mj6ek=\n-----END PUBLIC KEY-----\n",
  "status": "ACTIVE",
  "isActive": true,
  "createdAt": "2026-09-10T10:18:00Z"
}
```

> The wire shape does **not** include `publicKeySha256` or `custodyKeyReference`. To verify the drift guard passed, query the DB after the call: `SELECT public_key_sha256 FROM public.crypto_keys WHERE tenant_id=…;` and compare to `{{trustSha}}` from Step 3a — they must match exactly.

### Step 3c — Verify the vault object (postman-only check via UI)

In Postman, you cannot list S3 directly. Use the **MinIO/LocalStack console** in a browser at `http://localhost:9001` (login `sbqr / sbqr-dev-key`) and navigate to bucket `sbqr-dev` → `keys/` → `010101/`. You must see:

```
010101_v1_private.pem      <-- present
```

The S3 object key is the **same shape** as Generate mode. Adopt overwrites any prior Generate-mode blob at the same `(institution, version)` in place. The mode distinction is recorded in the `crypto_keys` row and the audit trail (`crypto_key.minted` vs `crypto_key.adopted`), not in the on-disk filename.

### Step 4 — Activate

`POST {{base}}/v1/admin/tenants/{{tenantId}}/activate`

No body.

**Sample 200 response:**
```json
{
  "tenantId": "8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10",
  "institutionName": "Example Bank",
  "institutionCode": "010101",
  "status": "Active",
  "isActive": true
}
```

The activate-gate (ACTIVE `tenant_configurations` + ACTIVE `crypto_keys` + ACTIVE `institution_keys`) runs server-side before the state transition. If any precondition is missing, the server returns **409 InvariantViolation** with a message naming which precondition failed — re-run the missing step and retry.

**Sample 409 response (gate failure, e.g. trust row missing):**
```json
{
  "title": "Tenant invariant violated",
  "detail": "Tenant 8a1b6c44-… cannot be Activated: public.institution_keys has no ACTIVE row for institution code '010101'. Run POST /v1/admin/institutions (Step 3a) and retry.",
  "status": 409
}
```

---

## 4. Edge cases

### EC-Adopt-1 — Drift rejection (409)

Same Step 3b body as the happy path, but the trust row from Step 3a contains a **different** public key (e.g. you used the wrong keypair, or the trust row was overwritten by `DailyTrustSyncService` / a prior run between Step 3a and Step 3b). The drift guard catches the mismatch and refuses to write to DB or vault.

**Sample 409 response (RFC 7807 ProblemDetails):**
```json
{
  "title": "Signing key invariant violated",
  "detail": "trust_store public-key drift: institute 010101 already has a trusted public key (sha256=5b6a9806…1e13) which does NOT match the public key DERIVED from the supplied private half (sha256=a8d3c4f2…77b9). Either supply the matching private half or rotate the trust row explicitly via POST /v1/admin/institutions before retrying.",
  "status": 409
}
```

**Audit trail:** exactly one `crypto_key.adopt.drift_rejected` row with metadata `{"tenant_id":..., "institution_code":..., "existing_pk_sha256":..., "derived_pk_sha256":...}`. **No** rows in `crypto_keys` or `institution_keys` for the failed attempt.

**Resolution:** run EC-Adopt-3 (drift recovery) or supply the matching priv half.

### EC-Adopt-2 — No trust row pre-seeded (409)

Same Step 3b body as the happy path, but Step 3a was never run (or was run for a different institution code). Adopt is **two-step mandatory** — the public-key half must already be in `public.institution_keys` for the tenant's institution code before the server will accept the private half.

**Sample 409 response (RFC 7807 ProblemDetails):**
```json
{
  "title": "Signing key invariant violated",
  "detail": "no trust row for institute 010101: public.institution_keys has no ACTIVE row for this institution. Pre-seed the trust row via POST /v1/admin/institutions (with the public half matching the private key you just supplied — derived SHA-256=11qYAYK…j6ek=) and retry Adopt. Adopt never auto-publishes a trust row on its own: the public-key half must already be in the trust directory.",
  "status": 409
}
```

**Audit trail:** exactly one `crypto_key.adopt.no_trust_row` row with metadata `{"tenant_id":..., "institution_code":..., "derived_pk_sha256":...}`. **No** rows in `crypto_keys` or `institution_keys` for the failed attempt.

**Resolution:** run Step 3a (`POST /v1/admin/institutions`) with the public-key PEM matching the private-key PEM you intend to upload at Step 3b, then retry Adopt.

### EC-Adopt-3 — Drift recovery via `/admin/institutions`

When Step 3a pre-seeded the **wrong** pub, fix the trust row first, then retry Step 3b.

`POST {{base}}/v1/admin/institutions`

```json
{
  "institutionCode": "010101",
  "instituteType": "01",
  "institutionName": "Example Bank",
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA<correct pk that matches the HSM's sk>\n-----END PUBLIC KEY-----\n"
}
```

Now repeat Step 3b with the private-key PEM whose derivation matches the corrected `publicKeyPem` — drift guard will pass.

### EC-Adopt-4 — Second Adopt while a key is already ACTIVE (409)

The single-active constraint (`uq_crypto_keys_active_tenant`) blocks a second Adopt.

**Sample 409 response (RFC 7807 ProblemDetails):**
```json
{
  "title": "Signing key invariant violated",
  "detail": "tenant 8a1b6c44-… already has an ACTIVE signing key (v1, id b1e0f2c7-…). Retire it via PUT /v1/crypto-keys/{id}/rotate or /suspend, then retry.",
  "status": 409
}
```

**Resolution:** rotate the existing key first (`PUT /v1/crypto-keys/{id}/rotate`), then retry Adopt.

---

## 5. Post-mint verification (DB)

Run in `psql` against the dev DB. Replace `8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10` with `{{tenantId}}`.

```sql
-- (a) crypto_keys row exists, status ACTIVE, mode=Adopt (same handle shape as Generate)
SELECT id, key_version, status, public_key_sha256, custody_key_reference
FROM   public.crypto_keys
WHERE  tenant_id = '8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10'
  AND  status    = 'ACTIVE';

-- (b) trust row exists, sha matches crypto_keys row
SELECT institution_code, institute_type, institution_name, active_key_version, public_key_sha256, status
FROM   public.institution_keys
WHERE  institution_code = '010101'
  AND  status           = 'ACTIVE';

-- (c) after Step 4: tenant is Active
SELECT id, status, activated_at
FROM   public.tenants
WHERE  id = '8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10';

-- (d) audit trail
SELECT action, actor, resource_type, occurred_at, metadata
FROM   audit.audit_log
WHERE  tenant_id = '8a1b6c44-2c1d-4f33-9d4a-12c6f7e8ab10'
ORDER  BY occurred_at;
```

Expected audit rows in order:
```
tenant.registered                 platform-bootstrap
tenant.configuration.provisioned  platform-bootstrap
institution.trust.key.published   system:crypto-create   (from Step 3a)
crypto_key.adopted                platform-bootstrap      (from Step 3b)
tenant.activated                  platform-bootstrap      (from Step 4)
```

Failure-path audit rows (single occurrence, no DB or vault side-effects):

| Failure path | Audit action | Metadata keys |
|---|---|---|
| Trust row missing (EC-Adopt-2) | `crypto_key.adopt.no_trust_row` | `tenant_id`, `institution_code`, `derived_pk_sha256` |
| Drift mismatch (EC-Adopt-1) | `crypto_key.adopt.drift_rejected` | `tenant_id`, `institution_code`, `existing_pk_sha256`, `derived_pk_sha256` |
| Priv PEM malformed (caught by validator before drift guard) | (none — matches the original Generate-failure behaviour) | — |

---

## 6. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Step 3a returns 400 "public key must be a PEM block" | PEM missing trailing newline | Re-export with `openssl pkey -pubout -out file.pem`; verify with `cat -A file.pem` shows `-----END PUBLIC KEY-----$` |
| Step 3b returns 409 "no trust row for institute …" | Step 3a was never run, or was run for a different institution_code | Run `POST /v1/admin/institutions` with the public-key PEM matching the private-key PEM you intend to upload at Step 3b, then retry Adopt |
| Step 3b returns 409 "trust_store public-key drift" even though Step 3a was just run | The priv PEM does not actually match the pub PEM you pre-seeded (different keypair, or the trust row was overwritten by `DailyTrustSyncService` / a prior run between Step 3a and Step 3b) | Compute the derived-from-priv sha256 (`openssl pkey -in priv.pem -pubout \| sha256sum`) and compare to `SELECT public_key_sha256 FROM public.institution_keys WHERE institution_code='010101'`. Either supply the matching priv half, or run EC-Adopt-3 to rotate the trust row to the correct pub |
| Step 4 returns 409 naming `institution_keys` / `crypto_keys` / `tenant_configurations` | The corresponding prerequisite step did not commit (e.g. daily trust-sync overwrote Step 3a) | Re-run the missing step, verify with the SQL in §5, then retry Step 4 |
| MinIO console doesn't show `_private.pem` after Adopt | Vault-provider misconfig | Check your dev config (user-secrets or launch-profile env vars) sets `Crypto:VaultProvider=S3` and, for an emulator, `Storage:ServiceUrl=http://localhost:4566` + `Storage:ForcePathStyle=true` |
| EC-Adopt-4 fires immediately on the happy path | A leftover ACTIVE key from a prior test run exists | `UPDATE public.crypto_keys SET status='RETIRED' WHERE tenant_id='…' AND status='ACTIVE';` then retry |

---

## 7. Cross-references

- [`FR-TENANT-001-tenant-registration.md §1.4`](FR-TENANT-001-tenant-registration.md#14-adopt-mode-variant-institute-supplies-its-own-ed25519-keypair) — Adopt-mode functional spec, business rules, acceptance criteria.
- [`FR-TENANT-001-dev-manual-test-guide.md`](FR-TENANT-001-dev-manual-test-guide.md) — the equivalent Generate-mode guide.
- [`FR-TENANT-001-review.md`](FR-TENANT-001-review.md) — Finding F-3 (silent trust overwrite) motivates the Adopt-mode drift guard.
- [`docs/dev-testing-guide-oauth-tenants-keys.md §7.10`](../../dev-testing-guide-oauth-tenants-keys.md#710-step-10--adopt-a-caller-supplied-key-crypto-keys-optional) — quick-reference curl for the Adopt call only.
