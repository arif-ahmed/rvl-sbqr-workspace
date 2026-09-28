# FR-TENANT-001 — QA Test Cases (Tenant Self-Contained Registration)

Reference spec: [FR-TENANT-001 — Institute (Tenant) Self-Contained Registration](FR-TENANT-001-tenant-registration.md) ·
Story: [FR-TENANT-001-story.md](FR-TENANT-001-story.md)

## Prerequisites

- Service running over TLS; rate limiter active with a **configurable**
  per-minute limit on `/v1/admin/*` writes (know the current setting;
  default 10). Ability to retune via configuration only.
- Bootstrap credential configured; QA knows the plaintext (test env only).
- A fresh, unused 6-digit `institutionCode` for each happy-path run.
  (Test must clean up between runs — see §15 of
  `docs/new-tenant-onboard-guide.md` for the wipe script.)
- API client, JWT decoder, read access to `public.audit_logs`,
  `public.tenants`, `public.tenant_configurations`,
  `public.crypto_keys`, `public.institution_registries`,
  `public.institution_keys`.
- Vault / secret store reachable; for the failure-mode tests
  (TC-09, TC-11) the ability to *un*-reach a single seam
  (`IInstitutionTrustPublisher`, `IKeyMaterialGenerator`,
  `ISecretHasher`).

---

**TC-01 — Single-call happy path returns the full envelope** *(AC1, BR1, BR3, BR4)*

1. Mint admin bearer via `POST /v1/oauth/token` (FR-AUTH-001).
2. `POST /v1/admin/tenants:register` with body:
   ```json
   { "institutionName": "Example Bank Ltd.", "institutionCode": "010101" }
   ```
   No `credentialExpiry`. Expect `201 Created`,
   `Location: /v1/admin/tenants/{tenantId}`.
3. Response body carries **all** of: `tenantId` (UUID), `credentialId`
   (UUID), `clientId` (`010101-xxxxxxxx`), `clientSecret`
   (40-char base64url), `credentialExpiresAt` (now + 365d, ±1s),
   `cryptoKeyId` (UUID), `keyVersion = 1`, `publicKeyPem` (SPKI
   Ed25519), `status = "Active"`, `trustRegistry.autoPublished = true`,
   `trustRegistry.instituteType = "01"`, `trustRegistry.keyVersion = 1`.
4. Body must **not** contain: `custodyKeyReference`, `privateKeyPem`,
   `privateKeyBytes`, or any other private-key material — enforced by
   the response-shape test.

**TC-02 — Returned credentials mint a tenant-scoped JWT end-to-end** *(AC2, BR5)*

1. From the TC-01 response, take `clientId` + `clientSecret`.
2. `POST /v1/oauth/token` with `grant_type=client_credentials`,
   `client_id`, `client_secret` → `200`.
3. Decoded JWT: `sub = "client:010101-..."`, `scope = "qr:generate qr:validate"`,
   `tenant_id` matches `tenantId` from TC-01, **no `admin`** scope.
4. Use this tenant token on `POST /v1/qr/generate/static` with a unique
   `Idempotency-Key` → `201`. (Proves the credential is fully wired up
   without any further setup call.)

**TC-03 — Tenant is Active immediately after the response** *(AC3, BR3)*

1. After TC-01, `SELECT status FROM public.tenants WHERE tenant_id =
   :'tenant_id'` → `Active`.
2. `POST /v1/admin/tenants/{tenantId}` (read) → `200` with
   `status: "Active"`.
3. Calling `POST /v1/admin/tenants/{tenantId}:activate` on this
   tenant now returns `409 InvariantViolation`
   ("cannot activate a non-Suspended tenant").

**TC-04 — Trust registry has the auto-published key** *(AC4, BR4, BR6)*

After TC-01:

```sql
SELECT institution_code, institution_name, institute_type
FROM institution_trust.institution_registries
WHERE institution_code = '010101';
-- Expect: one row, institution_name = 'Example Bank Ltd.', institute_type = '01'

SELECT institution_code, key_version, status, public_key_pem_sha256
FROM public.institution_keys
WHERE institution_code = '010101';
-- Expect: key_version=1, status='ACTIVE',
--         public_key_pem_sha256 matches the SHA-256 of the
--         publicKeyPem from TC-01's response body
```

The trust-store row exists **without** a separate
`POST /v1/admin/institutions` call having been made.

**TC-05 — Audit chain emits the four expected rows from one HTTP call** *(AC5, BR6)*

```sql
SELECT event_type, actor_id, resource_id, created_at
FROM public.audit_logs
WHERE tenant_id = :'tenant_id'
ORDER BY created_at;
```

Expect exactly (and in this order):

1. `tenant.registered` — `actor_id` = admin JWT's `sub`
   (e.g. `platform-bootstrap` for the bootstrap-admin path)
2. `tenant.activated` — same actor
3. `tenant.configuration.provisioned` — same actor
4. `crypto_key.minted` — same actor
5. `institution.trust.key.published` — `actor_id = "system:crypto-create"`
   (the auto-publish actor tag — distinct from manual
   `POST /v1/admin/institutions` publishes)

No `client_secret` substring anywhere in any audit row's metadata or
the application log (use a regex sweep on the response body + log
file).

**TC-06 — Duplicate `institutionCode` is rejected cleanly** *(AC6, BR8)*

1. Run TC-01 with `institutionCode = "010101"` → `201`.
2. Run **the same body** again (no `Idempotency-Key`) →
   `409 Conflict`, body
   `{ "errorCode": "InvariantViolation", "message": "institution_code '010101' is already registered." }`.
3. DB after the retry:
   - `public.tenants` still has exactly one row for `010101`.
   - `public.tenant_configurations` still has exactly one row.
   - `public.crypto_keys` still has exactly one row at `key_version=1`.
4. No extra audit row beyond the 5 from TC-05.

**TC-07 — Tenant-scoped bearer is rejected at the endpoint** *(AC7)*

1. Mint a tenant-scoped bearer (TC-02 step 2).
2. `POST /v1/admin/tenants:register` with that bearer and a fresh
   `institutionCode` → `403 Forbidden`. No row created.
3. Same body with **no** `Authorization` header → `401`.

**TC-08 — Idempotent replay returns the original response verbatim** *(AC8, BR8)*

1. Run TC-01 with an `Idempotency-Key: <uuid>` header and capture the
   full response body bytes.
2. Re-send the **same** request (same body, same `Idempotency-Key`,
   same `institutionCode`) → `201` with **byte-identical** response
   body. No new `tenant.registered` audit row.
3. Same body but a **different** `Idempotency-Key` → `409` (it would
   have created a second tenant; the duplicate-key check fires
   first, see TC-06).
4. Different body, same `Idempotency-Key` →
   `400 invalid_request` (key collision).

**TC-09 — Auto-publish failure surfaces as `201` with `autoPublished=false`** *(AC9, BR4, FR §7 step 5)*

1. In a test config, point the trust-store seam
   (`IInstitutionTrustPublisher`) at a destination that throws
   `NpgsqlException: 57P03` (DB shutdown) on `UpsertAsync`. The
   `crypto_keys` write is **not** blocked — only the trust-store leg
   fails.
2. `POST /v1/admin/tenants:register` → `201`. Body:
   - All TC-01 fields present.
   - `trustRegistry.autoPublished = false`.
   - `trustRegistry.trustPublishError` carries the original exception
     message (redacted of secrets, of course).
3. Audit log gains a `crypto_key.trust_publish_failed` row carrying
   the tenant id, the public-key SHA-256, and the failure reason.
4. Recovery: `POST /v1/admin/institutions` with the `publicKeyPem`
   from the response body → `200`; subsequent `POST /v1/qr/validate`
   succeeds.

**TC-10 — Validation rejections are clean** *(AC10, FR §6)*

For each row below, the request goes through and the test asserts
**no** row appears in `public.tenants`, `public.tenant_configurations`,
`public.crypto_keys`, or the trust registry.

| Body | Expected status | Expected errorCode |
|---|---|---|
| Empty body | `400` | `ValidationFailed` |
| Missing `institutionName` | `400` | `ValidationFailed` |
| Missing `institutionCode` | `400` | `ValidationFailed` |
| `institutionCode = "12345"` (5 digits) | `400` | `ValidationFailed` |
| `institutionCode = "1234567"` (7 digits) | `400` | `ValidationFailed` |
| `institutionCode = "abcdef"` (non-digits) | `400` | `ValidationFailed` |
| `institutionName` = 201-char string | `400` | `ValidationFailed` |
| `credentialExpiry = "2020-01-01T00:00:00Z"` (past) | `400` | `ValidationFailed` |
| `credentialExpiry = "2035-01-01T00:00:00Z"` (> 5y out) | `400` | `ValidationFailed` |
| Malformed JSON | `400` | `ValidationFailed` |
| Wrong Content-Type | `415` | (no body) |

No response may echo the request body. No `500`, no stack trace.

**TC-11 — Forced failure mid-saga rolls back every row** *(AC11, BR4, FR §7 step 6)*

1. In a test config, make `IKeyMaterialGenerator.GenerateAsync` throw
   `InvalidOperationException` ("simulated vault outage") on its
   second call.
2. `POST /v1/admin/tenants:register` with `institutionCode = "020202"`
   → `500 InternalError`. Body does **not** echo inputs.
3. After the call, the DB must contain **zero** rows for `020202` in:
   - `public.tenants`
   - `public.tenant_configurations`
   - `public.crypto_keys`
   - `public.institution_registries`
   - `public.institution_keys`
4. Audit log contains **no** `tenant.registered` row for this
   `tenant_id` (the saga aborts before `CreateTenantCommandHandler`
   commits). It **may** contain an error-class audit row.

**TC-12 — Rate limit is enforced** *(AC12, BR7)*

1. Mint a fresh admin bearer.
2. With the default `AdminWrite` rate limit (10/min), 10 successful
   calls (each with a unique `institutionCode`) → all `201`. The
   11th within the same minute → `429`.
3. Retune the limit to 2 → 3rd call within a minute → `429`.
4. After the window expires → request succeeds again.
5. `Idempotency-Key` replays (TC-08) **do not** count against the
   rate-limit budget — they return cached responses.

**TC-13 — Plaintext secret only ever appears in the registration response** *(BR5)*

1. Sweep application logs, structured-log metadata, and every row in
   `public.audit_logs` for the plaintext `clientSecret` returned in
   TC-01 → **zero hits**.
2. Sweep `public.tenant_configurations.client_secret_hash` for the
   new row → present (Argon2id PHC). The plaintext column does not
   exist in the schema.
3. Sweep `response` from `POST /v1/admin/tenants/{tenantId}` (read) →
   `clientSecret` is **not** present.
4. `POST /v1/qr/generate/static` log lines → no `clientSecret`
   substring anywhere.

**TC-14 — Deprecated bare-create endpoint returns `410`** *(FR §9, BR1)*

1. `POST /v1/admin/tenants` with the same body as TC-01 →
   `410 Gone`, body
   `{ "errorCode": "EndpointRetired", "message": "Use POST /v1/admin/tenants:register instead. This endpoint will be removed in v2." }`.
2. `POST /v1/admin/tenants/{tenantId}:activate` on a `Pending` tenant
   (manually constructed via SQL for the test) → `409 InvariantViolation`.

**TC-15 — Concurrency: two simultaneous calls with the same `institutionCode`** *(AC6, AC11, BR4)*

1. Fire two `POST /v1/admin/tenants:register` calls with
   `institutionCode = "030303"` in parallel from two threads.
2. Exactly **one** returns `201` with the full envelope; the other
   returns `409 InvariantViolation` ("institution_code '030303' is
   already registered"). No duplicate rows.
3. Audit log has the 5 expected rows for the winning call only.

---

**Out of scope:** load/stress beyond the rate-limit TC, signature
forgery (VAPT), TLS/cert pinning (C12), the rotation
(`PUT /v1/crypto-keys/{tenantId}`), termination
(`POST /v1/admin/tenants/{id}:terminate`), and the manual trust-store
recovery path (TC-09 step 4 only checks recovery *succeeds*; full
recovery semantics are covered by the manual test guide).
