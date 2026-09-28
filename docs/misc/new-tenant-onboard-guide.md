<!--
  ============================================================================
  new-tenant-onboard-guide.md
  ----------------------------------------------------------------------------
  End-to-end onboarding playbook for one new institute (tenant) into the
  SBQR Secure BQR Manager. Single, self-contained flow that mirrors what a
  platform operator does on day one of an institute going live: bootstrap
  admin token → register the tenant → activate it → provision the FI client
  credential → mint the signing key (which auto-publishes to the trust
  directory) → exchange a tenant bearer → exercise
  qr/generate/{static,dynamic} + qr/validate → tear down with
  suspend / reactivate.

  This is the "happy path one tenant" companion to
  docs/dev-testing-guide-oauth-tenants-keys.md (which is the broader manual
  test guide for the four admin sub-domains). The flow here intentionally
  stops short of rotation and termination — those are covered in §11 of the
  broader guide.

  No codebase files were modified to produce this guide — it is a pure
  documentation artifact under docs/.
  ============================================================================
-->

# New Tenant Onboarding Guide — End-to-End

> **Audience** — Platform operators onboarding an institute (tenant) into
> SBQR Secure BQR Manager, plus QA engineers who need a copy-pasteable
> golden path that exercises every seam of the onboarding lifecycle in one
> sitting.
>
> **Scope**
>
> | Step | Concern | Bounded Context |
> |------|---------|-----------------|
> | 1 | Bootstrap admin token (one-time) | IdentityAccess |
> | 2 | Register the tenant row | Tenancy |
> | 3 | Activate the tenant | Tenancy |
> | 4 | Provision the FI client credential | IdentityAccess |
> | 5 | Mint the signing key + **auto-publish to trust directory** | KeyCustody → InstitutionTrust |
> | 6 | Register the tenant's mobile app(s) — ANDROID and/or iOS | Tenancy |
> | 7 | Exchange tenant-scoped bearer token | IdentityAccess |
> | 8 | `POST /v1/qr/generate/static` (or `/dynamic`) | QrGeneration |
> | 9 | `POST /v1/qr/validate` (verifies trust-store lookup) | Verification |
> | 10 | Suspend / reactivate cascade | Tenancy |
>
> **Out of scope** (covered elsewhere):
> - Rotation (`PUT /v1/crypto-keys/{tenantId}`), termination,
>   adopt-mode key ingest → see `docs/dev-testing-guide-oauth-tenants-keys.md`.
> - Bootstrap-secret / docs-credential generation → see
>   `docs/api-docs-usage-guide.md`.
> - Vault-provider selection (`Crypto:VaultProvider`) → see
>   `docs/api-docs-usage-guide.md` §Vault provider.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [One-time: Bootstrap the Platform Admin Credential](#2-one-time-bootstrap-the-platform-admin-credential)
3. [Step 1 — Mint the Admin Bearer Token](#3-step-1--mint-the-admin-bearer-token)
4. [Step 2 — Register the Tenant](#4-step-2--register-the-tenant)
5. [Step 3 — Activate the Tenant](#5-step-3--activate-the-tenant)
6. [Step 4 — Provision the FI Client Credential](#6-step-4--provision-the-fi-client-credential)
7. [Step 5 — Mint the Signing Key (auto-publishes to trust store)](#7-step-5--mint-the-signing-key-auto-publishes-to-trust-store)
8. [Step 6 — Register the Tenant's Mobile App(s)](#8-step-6--register-the-tenants-mobile-apps)
9. [Step 7 — Mint a Tenant-Scoped Bearer Token](#9-step-7--mint-a-tenant-scoped-bearer-token)
10. [Step 8 — Issue a QR (`POST /v1/qr/generate/static` or `/dynamic`)](#10-step-8--issue-a-qr-post-v1qrgeneratestatic-or-dynamic)
11. [Step 9 — Validate the QR (`POST /v1/qr/validate`)](#11-step-9--validate-the-qr-post-v1qrvalidate)
12. [Step 10 — Suspend → Cascade Verification](#12-step-10--suspend--cascade-verification)
13. [Step 11 — Reactivate → Cascade Reversal](#13-step-11--reactivate--cascade-reversal)
14. [Atomic-ish vs Manual Override Paths](#14-atomic-ish-vs-manual-override-paths)
15. [What Failed Onboarding Looks Like](#15-what-failed-onboarding-looks-like)
16. [Cleanup / Reset](#16-cleanup--reset)

---

## 1. Prerequisites

| Requirement | Notes |
|-------------|-------|
| `SBQR.Api` host reachable at `BASE` (default `http://localhost:5080` for `dotnet run`, `http://localhost:8080` for Docker Compose) | The full stack must be up — see the broader manual test guide §5 for boot options. |
| `psql` (via the `sbqr.postgres` container) for the verification step in §7 | Optional but strongly recommended. |
| `curl`, `jq` | All HTTP examples below assume both. |
| Bootstrap admin secret in `$BOOTSTRAP_SECRET` | See §2 below. |
| A unique, unused 6-digit institution code for the new institute | Each `institution_code` is `UNIQUE` in `public.tenants`. |

> **Run the steps in order.** Skipping activation (Step 3) blocks Step 4
> (provisioning is rejected for `PENDING` tenants). Skipping provisioning
> (Step 4) blocks Step 7 (the tenant token endpoint returns 401 without a
> credential row). Skipping the crypto-create (Step 5) blocks Step 9
> (`qr/validate` cannot resolve the tenant's signature because the public
> key is not in the trust directory). Skipping app registration (Step 6)
> blocks any client whose token carries a `package_id` claim — the OAuth
> token endpoint rejects the request with `invalid_client /
> package_mismatch` for any mobile-app identifier that is not on the
> tenant's allow-list. See [§8](#8-step-6--register-the-tenants-mobile-apps).

---

## 2. One-time: Bootstrap the Platform Admin Credential

The **bootstrap** credential (client zero) is the only credential that
carries the `admin` scope. Every `POST /v1/admin/*` and
`POST /v1/crypto-keys` call in this guide uses the bearer minted from it.

### 2.1 Generate the secret (first time only)

```bash
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret
```

Output (example):

```
bootstrap client_id    = platform-bootstrap
bootstrap client_secret = 7y4kQ9wR2pX4yB6mN8cL3jT5sY0dF1hG3aU7iE9o
configure this as: Auth:Bootstrap:ClientSecretHash = $argon2id$v=19$m=65536,t=3,p=4$...
```

- The **plaintext** secret is shown once. Put it in your team password
  manager (it cannot be re-displayed).
- The **Argon2id hash** is what you paste into configuration:
  - Local dev: user-secrets —
    `dotnet user-secrets set "Auth:Bootstrap:ClientSecretHash" "<phc>" --project src/Host/SBQR.Api/SBQR.Api.csproj`.
  - Production: env var `Auth__Bootstrap__ClientSecretHash`, sourced from
    Key Vault. The host refuses to start in Production without it.

> If you are running the Docker Compose dev stack, the bootstrap secret is
> already pinned to `dev-only-bootstrap-secret` via the compose env file —
> skip §2.1 and just export `BOOTSTRAP_SECRET=dev-only-bootstrap-secret`.

### 2.2 Export the secret for the rest of this guide

```bash
export BOOTSTRAP_SECRET=dev-only-bootstrap-secret      # dev only
# OR
export BOOTSTRAP_SECRET='<paste the plaintext from --generate-bootstrap-secret>'
```

---

## 3. Step 1 — Mint the Admin Bearer Token

```bash
BASE=http://localhost:5080   # or :8080 for Docker
ADMIN_TOKEN=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=platform-bootstrap" \
  -d "client_secret=$BOOTSTRAP_SECRET" \
  | jq -r '.access_token')

echo "Admin token (first 40 chars): ${ADMIN_TOKEN:0:40}..."
```

**Expected response** *(HTTP 200)*:

```json
{
    "access_token": "eyJhbGciOiJIUzI1NiIs...",
    "token_type":   "Bearer",
    "expires_in":   600
}
```

**Decoded JWT payload**:

```json
{
    "sub":   "platform-bootstrap",
    "scope": "admin",
    "iss":   "sbqr",
    "aud":   "sbqr-api",
    "exp":   1750000000,
    "iat":   1749996400
}
```

> ⚠️ **Rate limit.** `POST /v1/oauth/token` is limited to **10 req/min per
> remote IP**. The whole onboarding flow uses ~3 token calls. Wait 60s
> between full re-runs.

> **Why this works.** The bootstrap credential is the only one whose
> `client_id` is recognised at the token endpoint *before* a tenant row
> exists. Tenant credentials minted in Step 4 are scoped to
> `qr:generate qr:validate` and cannot call any `/v1/admin/*` endpoint.

---

## 4. Step 2 — Register the Tenant

```bash
TENANT_RESPONSE=$(curl -s -X POST "$BASE/v1/admin/tenants" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "institutionName": "Example Bank Ltd.",
        "institutionCode": "010101"
      }')

echo "$TENANT_RESPONSE" | jq
TENANT_ID=$(echo "$TENANT_RESPONSE" | jq -r '.tenantId')
echo "Tenant ID: $TENANT_ID"
```

**Sample payload**:

```json
{
    "institutionName": "Example Bank Ltd.",
    "institutionCode": "010101"
}
```

| Field | Rules |
|-------|-------|
| `institutionName` | ≤ 200 chars; becomes the trust-directory row's `institution_name` when the crypto-create auto-publish fires in Step 5. |
| `institutionCode` | Exactly 6 digits `^[0-9]{6}$`, `UNIQUE` across `public.tenants`. The first two digits also seed `institute_type` in the trust registry (e.g. `01…` → commercial banks). |

**Expected response** *(HTTP 201 Created,
`Location: /v1/admin/tenants/{tenantId}`)*:

```json
{
    "tenantId":        "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "institutionName": "Example Bank Ltd.",
    "institutionCode": "010101",
    "status":          "Pending",
    "isActive":        true
}
```

> The tenant is created in `PENDING`. **It cannot yet authenticate at the
> token endpoint** — proceed to Step 3.

---

## 5. Step 3 — Activate the Tenant

```bash
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq
```

No request body.

**Expected response** *(HTTP 200)*:

```json
{
    "tenantId":    "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status":      "Active",
    "activatedAt": "2026-09-06T12:00:01+00:00"
}
```

> **State machine.** `Activate` is legal from `PENDING` or `SUSPENDED`.
> Calling it on an already-`Active` tenant returns `409
> ErrorCode.InvariantViolation`. `Activate` on a `TERMINATED` tenant also
> returns `409` (terminated is terminal — register a new tenant instead).

---

## 6. Step 4 — Provision the FI Client Credential

```bash
CRED_RESPONSE=$(curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $ADMIN_TOKEN")

echo "$CRED_RESPONSE" | jq
CLIENT_ID=$(echo "$CRED_RESPONSE"     | jq -r '.clientId')
CLIENT_SECRET=$(echo "$CRED_RESPONSE" | jq -r '.clientSecret')

echo "Client ID:     $CLIENT_ID"
echo "Client Secret: $CLIENT_SECRET"   # shown ONCE — store it now
```

No request body. The server generates both values.

**Expected response** *(HTTP 201 Created,
`Location: /v1/admin/tenants/{id}/tenant-configuration`)*:

```json
{
    "tenantId":     "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "credentialId": "c2a8f000-4d5e-6f70-8192-0a1b2c3d4e5f",
    "clientId":     "010101-7c1b4d88",
    "clientSecret": "K7vQ9wR2pX4yB6mN8cL3jT5sY0dF1hG3aU7iE9o",
    "expiresAt":    "2027-09-06T12:00:00+00:00"
}
```

| Field | Notes |
|-------|-------|
| `clientId` | Shape `{institutionCode}-{8 random hex}`. Used at the token endpoint. |
| `clientSecret` | Plaintext shown **once**; only an Argon2id hash is persisted. |
| `expiresAt` | Default 1 year. |

> **Hand these two values to the institute.** They are the only thing
> the institute's app needs to authenticate. If the institute loses them,
> suspend the credential and provision a new one (rotation is a separate
> flow — see the broader manual guide §7.9).

> **Re-provisioning while active is a 409.** A second
> `POST /v1/admin/tenants/{id}/tenant-configuration` against an `Active`
> tenant returns `409 ErrorCode.InvariantViolation`.

---

## 7. Step 5 — Mint the Signing Key (auto-publishes to trust store)

```bash
KEY_RESPONSE=$(curl -s -X POST "$BASE/v1/crypto-keys" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
        \"tenantId\": \"$TENANT_ID\",
        \"mode\":     \"Generate\"
      }")

echo "$KEY_RESPONSE" | jq
```

**Sample payload**:

```json
{
    "tenantId": "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "mode":     "Generate"
}
```

**Expected response** *(HTTP 201 Created,
`Location: /v1/crypto-keys/{cryptoKeyId}`)*:

```json
{
    "cryptoKeyId":  "d3b8f000-7a8b-9c0d-1e2f-3a4b5c6d7e8f",
    "tenantId":     "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "keyId":        "sbqr-signing",
    "keyVersion":   1,
    "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEA...\n-----END PUBLIC KEY-----\n",
    "status":       "Active",
    "isActive":     true,
    "createdAt":    "2026-09-06T12:00:02+00:00"
}
```

> **Confidentiality.** The response shape is pinned — only the public key,
> metadata, and the new `cryptoKeyId`. **No `custodyKeyReference`, no
> private-key PEM, no private bytes.** This is enforced by
> `CryptoKeySummaryShapeTests` (the test fails the build if anyone widens
> the DTO).

### 7.1 The auto-publish side-effect

A successful `201` means **the public key is now registered against
`institutionCode` in the trust directory**. There is no second
`POST /v1/admin/institutions` call to make — the handler behind
`POST /v1/crypto-keys` already published the freshly minted public key as
part of the same operation, via the
`IInstitutionTrustPublisher` Contracts seam.

Verify the side-effect directly in Postgres:

```bash
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app -c "
    SELECT institution_code, institution_name, institute_type
    FROM institution_trust.institution_registries
    WHERE institution_code = '010101';"
# Expect: institution_code=010101, institution_name='Example Bank Ltd.',
#         institute_type='01'

docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app -c "
    SELECT institution_code, key_version, status, created_by
    FROM public.institution_keys
    WHERE institution_code = '010101';"
# Expect: institution_code=010101, key_version=1, status='ACTIVE',
#         created_by='system:crypto-create'  ← the auto-publish actor tag
```

And the matching audit row:

```bash
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app -c "
    SELECT event_type, actor_id, resource_id, created_at
    FROM public.audit_logs
    WHERE tenant_id = '$TENANT_ID'
    ORDER BY created_at;"
# Expect at least:
#   tenant.registered            | platform-bootstrap
#   tenant.activated             | platform-bootstrap
#   tenant.configuration.provisioned | platform-bootstrap
#   institution.trust.key.published | system:crypto-create   ← new!
```

### 7.2 Failure mode (rare)

If the trust-publish step throws (InstitutionTrust DB outage, network
glitch, validation reject), the handler:

1. Commits the `crypto_keys` row as `Active` (so the operator does **not**
   lose the freshly minted key).
2. Emits a `crypto_key.trust_publish_failed` audit row carrying the
   tenant ID, public-key SHA-256, and the failure reason.
3. Returns HTTP 409 `ErrorCode.InvariantViolation` with body:

   ```json
   {
       "errorCode": "InvariantViolation",
       "message": "key created but trust-store publish failed (NpgsqlException: 57P03). Use POST /v1/admin/institutions to register the public key manually."
   }
   ```

The operator recovers with **one additional manual call** — paste the
`publicKeyPem` from the failed response into `POST /v1/admin/institutions`:

```bash
curl -s -X POST "$BASE/v1/admin/institutions" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "institutionCode": "010101",
        "institutionName": "Example Bank Ltd.",
        "publicKeyPem":    "<paste publicKeyPem from the failed Step 5 response>"
      }' | jq
```

The trust-store side-effect is identical to the auto-publish path —
`InstitutionUpsertService.UpsertAsync` is the underlying seam in both
cases. After the manual upsert, Step 9 (validate) works normally.

> See [`docs/dev-testing-guide-oauth-tenants-keys.md`](dev-testing-guide-oauth-tenants-keys.md)
> §7.7a for the full auto-publish rationale and §12 for the FAQ entry on
> the trust-publish 409.

---

## 8. Step 6 — Register the Tenant's Mobile App(s)

Register the mobile-app identifier(s) the tenant will ship — one row per
`(platform, package_id)`. This is the per-tenant allow-list the OAuth token
endpoint consults at mint time: a token request that carries a
`package_id` not on this list is rejected with `invalid_client /
package_mismatch`, and a row that is later `SUSPENDED` rejects every
device pinning it (the per-app kill switch — covered in the broader
manual test guide `docs/dev-testing-guide-oauth-tenants-keys.md`).

```bash
# Register the Android build first. Substitute the tenant's real
# applicationId from the Play Console / build.gradle.
APP_ANDROID=$(curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "platform":   "ANDROID",
        "package_id": "com.dhakabank.consumer"
      }')

echo "$APP_ANDROID" | jq
APP_ANDROID_ID=$(echo "$APP_ANDROID" | jq -r '.tenantApplicationId')
echo "Android application id: $APP_ANDROID_ID"

# If the tenant ships an iOS build too, register the matching bundle ID.
# The same reverse-DNS string on a second platform is legal — that's two
# rows in tenant_applications, one per platform.
APP_IOS=$(curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "platform":   "IOS",
        "package_id": "com.dhakabank.consumer"
      }')

echo "$APP_IOS" | jq
APP_IOS_ID=$(echo "$APP_IOS" | jq -r '.tenantApplicationId')
echo "iOS application id:     $APP_IOS_ID"
```

**Sample payload**:

```json
{
    "platform":   "ANDROID",
    "package_id": "com.dhakabank.consumer"
}
```

| Field | Rules |
|-------|-------|
| `platform` | `ANDROID` or `IOS` (validated against `TenantApplicationPlatform`). |
| `package_id` | Reverse-DNS identifier (`^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$`), ≤ 200 chars. Android `applicationId` or iOS bundle ID. |

**Expected response** *(HTTP 201 Created,
`Location: /v1/admin/tenants/{tenantId}/applications`)*:

```json
{
    "tenantApplicationId": "7c2b8e10-...",
    "tenantId":            "b1d9e000-...",
    "platform":            "ANDROID",
    "packageId":           "com.dhakabank.consumer",
    "status":              "ACTIVE",
    "isActive":            true,
    "createdAt":           "2026-09-06T12:00:03+00:00",
    "modifiedAt":          null
}
```

> **Why this step is mandatory.** The OAuth token endpoint rejects every
> token request whose `package_id` claim is not on this allow-list. A
> tenant whose row does not exist fails closed with `invalid_client /
> package_mismatch` on every device — there is no implicit "any package
> works" fallback.

### 8.1 Duplicate-application rule (no duplicate app per platform)

A tenant may register at most one row per `(platform, package_id)` pair
(FR-AUTH-002 / FR-AUTH-003 operational contract). The same `package_id`
on the other platform is **legal** — e.g. `com.dhakabank.consumer` may be
registered once as ANDROID and once as IOS (two rows total) — but never
twice on the same platform.

The constraint is enforced by the
`ix_tenant_applications_platform_package_id` UNIQUE index in
`db/migrations/008_tenant_applications.sql`; the EF layer translates the
PG `23505` unique-violation into `ErrorCode.InvariantViolation`, which the
controller maps to `409 Conflict`:

```bash
# Second registration of the same (platform, package_id) → 409.
curl -s -o /dev/null -w "HTTP %{http_code}\n" \
  -X POST "$BASE/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"platform":"ANDROID","package_id":"com.dhakabank.consumer"}'
# Expect: HTTP 409, body { "title": "Tenant application invariant violated",
#         "detail": "application (platform='Android', package_id='com.dhakabank.consumer') is already registered." }
```

### 8.2 Inspect the registered apps

```bash
curl -s -X GET "$BASE/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq
```

Returns the array of registered apps ordered by `(platform, package_id)`,
including the `SUSPENDED` rows from any per-app kill switch (covered in
the broader manual test guide).

### 8.3 Re-registering after a soft-delete or SUSPENDED row

The UNIQUE index ignores `is_active` and `status` — soft-deleted and
suspended rows still hold their `(platform, package_id)` slot. Re-registering
the same `package_id` on the same platform is rejected with `409`. The
intended recovery flow is the per-app `suspend` ↔ `reinstate` pair, not a
delete-and-recreate. Soft-delete (`is_active = false`) is reserved for
off-boarding an institute; rotation of the package identifier requires a
new package_id from Play Console / App Store Connect.

---

## 9. Step 7 — Mint a Tenant-Scoped Bearer Token

```bash
TENANT_TOKEN=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  | jq -r '.access_token')

echo "Tenant token (first 40 chars): ${TENANT_TOKEN:0:40}..."
```

**Decoded JWT payload**:

```json
{
    "sub":      "client:010101-7c1b4d88",
    "scope":    "qr:generate qr:validate",
    "tenant_id":"b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "inst":     "010101",
    "iss":      "sbqr",
    "aud":      "sbqr-api",
    "exp":      1750000000
}
```

> **Scope comparison:**
> - Bootstrap token: `scope = "admin"` — can call `/v1/admin/*` and
>   `/v1/crypto-keys`.
> - Tenant token: `scope = "qr:generate qr:validate"` — can call QR
>   issuance and verification only.

---

## 10. Step 8 — Issue a QR (`POST /v1/qr/generate/static` or `/dynamic`)

Choose the endpoint that matches your use case:

- **`POST /v1/qr/generate/static`** — Tag 01 = "11". The transaction amount
  is decided by the payer at scan time and is **not** in the payload. Use
  this for reusable merchant QR codes (recommended for retail displays).
- **`POST /v1/qr/generate/dynamic`** — Tag 01 = "12". The transaction
  amount is baked into the QR. Use this for one-off invoices, bill
  payments, or any single-transaction flow.

```bash
# Static example (no transactionAmount)
QR_RESPONSE=$(curl -s -X POST "$BASE/v1/qr/generate/static" \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{
        "recipientName":  "Example Merchant",
        "recipientCity":  "Dhaka",
        "recipientPan":   "0123456789012"
      }')

# Dynamic example (transactionAmount required)
# QR_RESPONSE=$(curl -s -X POST "$BASE/v1/qr/generate/dynamic" \
#   -H "Authorization: Bearer $TENANT_TOKEN" \
#   -H "Content-Type: application/json" \
#   -H "Idempotency-Key: $(uuidgen)" \
#   -d '{
#         "transactionAmount": "150.00",
#         "recipientName":     "Example Merchant",
#         "recipientCity":     "Dhaka",
#         "recipientPan":      "0123456789012"
#       }')

echo "$QR_RESPONSE" | jq
QR_PAYLOAD=$(echo "$QR_RESPONSE" | jq -r '.qrPayload')
echo "QR payload length: ${#QR_PAYLOAD}"
```

**Sample static payload**:

```json
{
    "recipientName": "Example Merchant",
    "recipientCity": "Dhaka",
    "recipientPan":  "0123456789012"
}
```

**Expected response** *(HTTP 201 Created)*:

```json
{
    "qrPayload":     "00020101021202150123456789012311080201123456789020418...6304ABCD",
    "idempotencyKey":"3f4a9c2e-...",
    "qrGenerationId":"e4d8c000-...",
    "signature":     "MEUCIQDxY..."
}
```

> The `signature` is an Ed25519 signature over the canonicalised
> `qrPayload`, produced with the **active** private key the server minted
> in Step 5. Verification (Step 9) requires the trust directory to be able
> to resolve `inst=010101` to that key — which is exactly what the
> auto-publish ensured.

> **Idempotency.** The `Idempotency-Key` header is required for this
> endpoint and prevents duplicate QRs if the client retries the call.
> `uuidgen` is available on Linux/WSL; on PowerShell use
> `[guid]::NewGuid().Guid`.

---

## 11. Step 9 — Validate the QR (`POST /v1/qr/validate`)

```bash
curl -s -X POST "$BASE/v1/qr/validate" \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
        \"qrPayload\": \"$QR_PAYLOAD\"
      }" | jq
```

**Sample payload**:

```json
{
    "qrPayload": "00020101021202150123456789012311080201123456789020418...6304ABCD"
}
```

**Expected response** *(HTTP 200, `valid: true`)*:

```json
{
    "valid":              true,
    "institutionCode":    "010101",
    "institutionName":    "Example Bank Ltd.",
    "merchantName":       "Example Merchant",
    "merchantCity":       "Dhaka",
    "publicKeyVersion":   1,
    "signatureVerifiedAt":"2026-09-06T12:00:05+00:00"
}
```

> **What this proves.** The validator:
> 1. Parsed the `qrPayload` and extracted `inst=010101` + the signature.
> 2. Looked up `010101` in `institution_trust.institution_registries` →
>    resolved to `institution_keys` version 1 (the row the
>    auto-publish from Step 5 wrote).
> 3. Verified the Ed25519 signature with that public key.
>
> **If this returns `valid: false` with `reason: "no_active_key_for_institution"`,
> the auto-publish in Step 5 did not land.** See [§15](#15-what-failed-onboarding-looks-like).

---

## 12. Step 10 — Suspend → Cascade Verification

Suspend the tenant. This cascades `SUSPENDED` to the credential row(s) and
the signing key(s) via the cross-module Contracts seams
(`ITenantCredentialProvisioner` in IdentityAccess, the suspend-cascade
handler in KeyCustody).

```bash
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/suspend" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason": "Onboarding dry-run — suspend/reactivate cascade"}' | jq
```

**Sample payload**:

```json
{
    "reason": "Onboarding dry-run — suspend/reactivate cascade"
}
```

The `reason` field is optional.

**Expected response** *(HTTP 200)*:

```json
{
    "tenantId":   "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status":     "Suspended",
    "reason":     "Onboarding dry-run — suspend/reactivate cascade",
    "suspendedAt":"2026-09-06T12:00:06+00:00"
}
```

**Verify the cascade — token issuance is now refused**:

```bash
curl -s -o /dev/null -w "HTTP %{http_code}\n" -X POST "$BASE/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET"
# Expect: HTTP 401, body { "error": "invalid_client", "error_description": "Tenant credential is suspended." }
```

**Verify the cascade in the DB**:

```sql
-- All three should now read 'SUSPENDED'
SELECT tenant_id, status FROM public.tenants WHERE tenant_id = :'tenant_id';
SELECT status FROM public.tenant_configurations WHERE tenant_id = :'tenant_id';
SELECT status FROM public.crypto_keys WHERE tenant_id = :'tenant_id';
```

> **Why the cascade is in-process.** Each module exposes a Contracts seam
> (`ITenantCredentialProvisioner` for IdentityAccess, a
> `SuspendTenantSigningKeysCommand` MediatR message for KeyCustody). The
> Tenancy module dispatches both via in-process Mediator calls when its
> own `SuspendTenantCommandHandler` runs. There is no separate API call to
> make — a single `POST /v1/admin/tenants/{id}/suspend` is enough.

---

## 13. Step 11 — Reactivate → Cascade Reversal

```bash
curl -s -X POST "$BASE/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq
```

No request body.

**Expected response** *(HTTP 200)*:

```json
{
    "tenantId":       "b1d9e000-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "status":         "Active",
    "reactivatedAt":  "2026-09-06T12:00:07+00:00"
}
```

**Verify the cascade reversed**:

```bash
TENANT_TOKEN_2=$(curl -s -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  | jq -r '.access_token')

echo "Reactivated tenant token: ${TENANT_TOKEN_2:0:40}..."

# And the trust-store side-effect survives: re-validation must still pass.
curl -s -X POST "$BASE/v1/qr/validate" \
  -H "Authorization: Bearer $TENANT_TOKEN_2" \
  -H "Content-Type: application/json" \
  -d "{\"qrPayload\": \"$QR_PAYLOAD\"}" | jq '.valid'
# Expect: true
```

> **State machine.** `Reactivate` is legal only from `SUSPENDED`. Calling
> it on `Active` or `Terminated` returns `409 ErrorCode.InvariantViolation`.

---

## 14. Atomic-ish vs Manual Override Paths

The "happy path" uses five calls in sequence (tenant → configuration →
crypto-keys → register app(s) → mint tenant token), each with `POST`
semantics. The **crypto-create step auto-publishes** the public key into
the trust directory in the same operation — there is no separate
institutions call to make. The **app registration** call(s) populate the
per-tenant mobile-app allow-list that the OAuth token endpoint consults at
mint time.

| Scenario | Calls | Result |
|----------|-------|--------|
| **Greenfield happy path** | `POST /v1/admin/tenants` → `…/activate` → `…/tenant-configuration` → `POST /v1/crypto-keys` → `POST /v1/admin/tenants/{id}/applications` (×N, one per platform) → `POST /v1/oauth/token` | Tenant ready to sign, be verified by `qr/validate`, and authenticate as a registered mobile app. ✅ |
| **Auto-publish failed at Step 5** | Above + `POST /v1/admin/institutions` with the failed public key | Same end state; one extra manual call. The audit log distinguishes `system:crypto-create` vs admin-actor publishes. |
| **Manual override of trust key** (no new keypair, just replace the public key) | `POST /v1/admin/institutions` with the new PEM | Existing active key in `crypto_keys` is untouched; trust store gets the override. Use case: re-keying trust material without rotating the server-side signing key. |
| **Re-provision credential** (lost secret) | `POST …/suspend` → `…/reactivate` semantics don't apply; instead see rotation flow in the broader guide. | New credential row generated, old revoked. |
| **Re-register an existing app** (already registered `(platform, package_id)`) | Re-running Step 6 with the same inputs returns `409 InvariantViolation`. | Use the per-app `suspend` ↔ `reinstate` pair (broader manual guide) — soft-delete and SUSPENDED rows hold their index slot. |

The auto-publish is **best-effort, not 2PC** — see the audit row taxonomy
and the trade-off discussion in the broader manual guide §7.7a.

---

## 15. What Failed Onboarding Looks Like

Quick reference for the four most common ways an operator realises a
recent onboarding did not stick.

### 15.1 `POST /v1/oauth/token` (tenant) → 401 `invalid_client`

Likely cause: tenant is `PENDING` or `SUSPENDED` (Step 3 not run, or
Step 9 suspended them). Check:

```sql
SELECT status FROM public.tenants WHERE tenant_id = '...';
SELECT status FROM public.tenant_configurations WHERE client_id = '010101-...';
```

### 15.2 `POST /v1/qr/validate` → `valid: false` with `no_active_key_for_institution`

Auto-publish in Step 5 did not land. Inspect the audit log:

```sql
SELECT event_type, created_at, metadata
FROM public.audit_logs
WHERE tenant_id = '...' AND event_type LIKE 'institution%'
ORDER BY created_at DESC;
```

If the latest row is `crypto_key.trust_publish_failed` instead of
`institution.trust.key.published`, run the manual recovery curl from
[§7.2](#72-failure-mode-rare).

### 15.3 `POST /v1/crypto-keys` → 409 `InvariantViolation` "already has an ACTIVE signing key"

The tenant already has a key from a prior onboarding attempt. Either:

- Use `GET /v1/crypto-keys/{tenantId}/active` to inspect the existing key,
  or
- `PUT /v1/crypto-keys/{tenantId}` to rotate (retires the old key, mints
  v+1, auto-publishes the new public key), or
- Suspend + delete the existing row in the DB if it was a bad test run.

### 15.4 `POST /v1/admin/tenants` → 409 on `institution_code`

The `institution_code` is already taken by another tenant row. Pick a
different 6-digit code and retry. There is no admin endpoint to change
`institution_code` after registration.

---

## 16. Cleanup / Reset

For a fully clean run, drop the volumes and re-migrate:

```bash
docker compose -f docker/docker-compose.yml down -v
docker compose -f docker/docker-compose.yml up -d sbqr.postgres
# then run the external migration tool against sbqr_app + sbqr_key_vault
docker compose -f docker/docker-compose.yml up -d
```

Or, to keep schema and just wipe rows:

```bash
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres psql -U postgres -d sbqr_app -c "
  DELETE FROM public.audit_logs;
  DELETE FROM public.institution_keys;
  DELETE FROM public.institution_registries;
  DELETE FROM public.crypto_keys;
  DELETE FROM public.tenant_configurations;
  DELETE FROM public.tenants;
"
```

> Re-running `POST /v1/admin/tenants` with the same `institution_code`
> after a wipe will then succeed.

---

## Appendix A — One-shot Bash Script

Self-contained version of the entire onboarding flow. Adjust `BASE` and
`INSTITUTION_CODE` at the top, then run.

```bash
#!/usr/bin/env bash
set -euo pipefail

BASE="${BASE:-http://localhost:5080}"
BOOTSTRAP_SECRET="${BOOTSTRAP_SECRET:-dev-only-bootstrap-secret}"
INSTITUTION_CODE="${INSTITUTION_CODE:-010101}"
INSTITUTION_NAME="${INSTITUTION_NAME:-Example Bank Ltd.}"

echo "=== Step 1 — Mint admin token ==="
ADMIN_TOKEN=$(curl -sf -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=platform-bootstrap" \
  -d "client_secret=$BOOTSTRAP_SECRET" | jq -r '.access_token')
echo "  admin token: ${ADMIN_TOKEN:0:32}..."

echo "=== Step 2 — Register tenant ==="
TENANT_ID=$(curl -sf -X POST "$BASE/v1/admin/tenants" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"institutionName\":\"$INSTITUTION_NAME\",\"institutionCode\":\"$INSTITUTION_CODE\"}" \
  | jq -r '.tenantId')
echo "  tenant_id: $TENANT_ID"

echo "=== Step 3 — Activate tenant ==="
curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq -c '{tenantId, status}'

echo "=== Step 4 — Provision FI credential ==="
CRED=$(curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
CLIENT_ID=$(echo "$CRED" | jq -r '.clientId')
CLIENT_SECRET=$(echo "$CRED" | jq -r '.clientSecret')
echo "  client_id: $CLIENT_ID"

echo "=== Step 5 — Mint signing key (auto-publishes to trust store) ==="
curl -sf -X POST "$BASE/v1/crypto-keys" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"tenantId\":\"$TENANT_ID\",\"mode\":\"Generate\"}" | jq -c '{cryptoKeyId, status, keyVersion}'

echo "=== Step 6 — Register mobile app(s) (ANDROID + iOS allow-list) ==="
# Override either of these by exporting PACKAGE_ID / IOS_PACKAGE_ID before
# running. Default: register the same package_id on both platforms — that's
# two rows in public.tenant_applications, legal under the FR-AUTH-002 rule.
PACKAGE_ID="${PACKAGE_ID:-com.dhakabank.consumer}"
IOS_PACKAGE_ID="${IOS_PACKAGE_ID:-$PACKAGE_ID}"

ANDROID_APP=$(curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"platform\":\"ANDROID\",\"package_id\":\"$PACKAGE_ID\"}" \
  | jq -c '{tenantApplicationId, platform, packageId, status}')
echo "  android: $ANDROID_APP"

# Skip iOS registration if IOS_PACKAGE_ID is empty. Same package_id on a
# second platform is legal — that's two rows; same string twice on one
# platform would 409.
if [ -n "$IOS_PACKAGE_ID" ]; then
  IOS_APP=$(curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/applications" \
    -H "Authorization: Bearer $ADMIN_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"platform\":\"IOS\",\"package_id\":\"$IOS_PACKAGE_ID\"}" \
    | jq -c '{tenantApplicationId, platform, packageId, status}')
  echo "  ios:     $IOS_APP"
fi

echo "=== Step 7 — Mint tenant token ==="
TENANT_TOKEN=$(curl -sf -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" | jq -r '.access_token')
echo "  tenant token: ${TENANT_TOKEN:0:32}..."

echo "=== Step 8 — Issue QR (static) ==="
QR=$(curl -sf -X POST "$BASE/v1/qr/generate/static" \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d "{\"recipientName\":\"Example Merchant\",\"recipientCity\":\"Dhaka\",\"recipientPan\":\"0123456789012\"}")
QR_PAYLOAD=$(echo "$QR" | jq -r '.qrPayload')
echo "  qr payload bytes: ${#QR_PAYLOAD}"

echo "=== Step 9 — Validate QR (trust-store lookup) ==="
VALID=$(curl -sf -X POST "$BASE/v1/qr/validate" \
  -H "Authorization: Bearer $TENANT_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"qrPayload\":\"$QR_PAYLOAD\"}" | jq -r '.valid')
echo "  valid: $VALID"
test "$VALID" = "true"

echo "=== Step 10 — Suspend (cascade check) ==="
curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/suspend" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"onboarding dry-run"}' | jq -c '{tenantId, status}'

SUSPENDED_HTTP=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET")
echo "  token after suspend: HTTP $SUSPENDED_HTTP (expect 401)"
test "$SUSPENDED_HTTP" = "401"

echo "=== Step 11 — Reactivate (cascade reversal) ==="
curl -sf -X POST "$BASE/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq -c '{tenantId, status}'

REACT_HTTP=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE/v1/oauth/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET")
echo "  token after reactivate: HTTP $REACT_HTTP (expect 200)"
test "$REACT_HTTP" = "200"

echo ""
echo "=== ONBOARDING HAPPY PATH COMPLETE ==="
echo "Tenant: $TENANT_ID ($INSTITUTION_CODE / $INSTITUTION_NAME)"
echo "FI client_id: $CLIENT_ID"
echo "Trust-store row at public.institution_keys version=1 ACTIVE"
echo "Validation: $VALID"
```

> ⚠️ **Rate limit reminder.** The script makes 4 token calls (admin,
> tenant, suspend-verification, reactivate-verification). Wait at least
> 60 seconds between full re-runs.

---

## Appendix B — End-to-End Audit Trail

For a successful onboarding the `public.audit_logs` table gains the
following rows in chronological order (tenant-filtered). Every row is
emitted by the handler named in the right column.

| # | `event_type` | `actor_id` | Emitted by |
|---|--------------|-----------|------------|
| 1 | `tenant.registered` | `platform-bootstrap` | `CreateTenantCommandHandler` |
| 2 | `tenant.activated` | `platform-bootstrap` | `ActivateTenantCommandHandler` |
| 3 | `tenant.configuration.provisioned` | `platform-bootstrap` | `ProvisionTenantConfigurationCommandHandler` |
| 4 | `token.issued` | `client:010101-...` | `IssueClientCredentialsTokenHandler` (×2 — admin and tenant) |
| 5 | `institution.trust.key.published` | **`system:crypto-create`** | `IInstitutionTrustPublisher` invoked from `GenerateOrAdoptCryptoKeyCommandHandler` |
| 6 | `crypto_key.minted` (or `crypto_key.adopted`) | `platform-bootstrap` | `GenerateOrAdoptCryptoKeyCommandHandler` |
| 7 | `tenant.application.registered` (×N, one per platform) | `platform-bootstrap` | `RegisterTenantApplicationCommandHandler` |
| 8 | `qr.generated` | `client:010101-...` | `GenerateQrCommandHandler` |
| 9 | `qr.verified` | `client:010101-...` | `VerifyQrCommandHandler` |
| 10 | `tenant.suspended` | `platform-bootstrap` | `SuspendTenantCommandHandler` |
| 11 | `tenant.credentials.cascade.suspended` | `platform-bootstrap` | (cascade child handler, IdentityAccess) |
| 12 | `tenant.signing_keys.cascade.suspended` | `platform-bootstrap` | (cascade child handler, KeyCustody) |
| 13 | `tenant.reactivated` | `platform-bootstrap` | `ReactivateTenantCommandHandler` |
| 14 | `tenant.credentials.cascade.reactivated` | `platform-bootstrap` | (cascade child handler, IdentityAccess) |
| 15 | `tenant.signing_keys.cascade.reactivated` | `platform-bootstrap` | (cascade child handler, KeyCustody) |

> Row 5 is the **audit row** introduced by the auto-publish change.
> The `actor_id` is `system:crypto-create` (the constant exposed at
> `InstitutionTrustPublisher.CryptoCreateActor`) so operators can
> distinguish auto-publish from manual `POST /v1/admin/institutions`
> publishes (which carry the admin's JWT subject).
>
> Row 7 — `tenant.application.registered` — is emitted once per platform
> (ANDROID, IOS) the tenant registers during Step 6. The `metadata` carries
> `(platform, package_id)`; the FR-AUTH-002 invariant that no two rows
> share the same `(platform, package_id)` is enforced by the DB unique
> index `ix_tenant_applications_platform_package_id` and surfaces as a
> 409 in the API rather than as an audit row.

---

## Appendix C — Related Docs

- [`docs/dev-testing-guide-oauth-tenants-keys.md`](dev-testing-guide-oauth-tenants-keys.md)
  — broader manual test guide with rotation, termination, and Adopt-mode
  key ingest.
- [`docs/api-docs-usage-guide.md`](api-docs-usage-guide.md) — OpenAPI
  access, bootstrap-secret / docs-credential generation, vault-provider
  selection.
- [`docs/dev-manual-test-guide.md`](dev-manual-test-guide.md) —
  InstitutionTrust + BB Trust Store mock smoke tests.
- [`docs/superpowers/specs/2026-09-04-public-vs-internal-api-docs-design.md`](superpowers/specs/2026-09-04-public-vs-internal-api-docs-design.md)
  — design rationale for the public-vs-internal docs split (context for
  which endpoints are visible where).
