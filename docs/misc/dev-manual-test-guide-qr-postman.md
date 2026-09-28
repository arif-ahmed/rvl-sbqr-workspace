# Dev Manual Test Guide — QR Generation & QR Verification (Postman)

A short, copy-paste friendly guide for testing the two QR endpoints end-to-end with Postman.

> Assumes you already have the API running locally and an **admin token** + a **tenant token** with scope `qr:generate,qr:validate` ready. See [docs/bootstrap-dev-guide.md](bootstrap-dev-guide.md) and [docs/dev-manual-test-guide.md](dev-manual-test-guide.md) §§5–8 for the full OAuth + tenant + crypto-key setup.

**Base URL (default):** `http://localhost:5001`

**Two endpoints only:**

| # | Endpoint | Purpose |
|---|---|---|
| 1 | `POST /v1/qr/generate/static`  | Build & sign a **static** QR (no amount) |
| 2 | `POST /v1/qr/generate/dynamic` | Build & sign a **dynamic** QR (with amount) |
| 3 | `POST /v1/qr/validate`        | Verify any QR, returns `verdict` |

---

## Table of contents

1. [Setup in Postman (one-time)](#1-setup-in-postman-one-time)
2. [Step 1 — Generate a STATIC QR](#2-step-1--generate-a-static-qr)
3. [Step 2 — Generate a DYNAMIC QR](#3-step-2--generate-a-dynamic-qr)
4. [Step 3 — Verify (round-trip) your own QR](#4-step-3--verify-round-trip-your-own-qr)
5. [Step 4 — Failure scenarios you should try](#5-step-4--failure-scenarios-you-should-try)
6. [Field reference card](#6-field-reference-card)

---

## 1. Setup in Postman (one-time)

### 1.1 Create an environment

Top-right → gear → **Manage Environments** → **Add**.

Name: `SBQR Local`

Variables:

| Variable | Initial Value |
|---|---|
| `baseUrl` | `http://localhost:5001` |
| `tenantToken` | *(leave blank — Postman fills it in step 1.3)* |
| `lastQrPayload` | *(leave blank — Postman fills it in step 2 / 3)* |

Click **Save**.

### 1.2 Get the bootstrap (admin) token

Add a new request:

- **Method:** `POST`
- **URL:** `{{baseUrl}}/v1/oauth/token`
- **Body tab** → `x-www-form-urlencoded` with three rows:

| KEY | VALUE |
|---|---|
| `grant_type` | `client_credentials` |
| `client_id` | `platform-bootstrap` |
| `client_secret` | *(the dev-only value from `docker-compose.yml`: `dev-only-bootstrap-secret`)* |

- **Tests tab** → paste:

```javascript
pm.environment.set("adminToken", pm.response.json().accessToken);
console.log("admin token:", pm.response.json().accessToken);
```

Click **Send**. Expect **200 OK**.

> Use this `adminToken` (scope = `admin`) for registering tenants, provisioning configuration and minting crypto keys. It cannot generate or validate QRs.

### 1.3 Get the tenant token (for QR testing)

First you need a tenant + tenant configuration + ACTIVE signing key. Two options:

**Option A — already done it in a previous session.** Just reuse the tenant's `clientId` and `clientSecret` from earlier (§7 of [dev-manual-test-guide.md](dev-manual-test-guide.md)).

**Option B — quick path (copy-paste in order):**

1. **Register the tenant:**

   - **Method:** `POST`
   - **URL:** `{{baseUrl}}/v1/admin/tenants`
   - **Headers:** `Authorization: Bearer {{adminToken}}`
   - **Body** (raw JSON):

   ```json
   {
     "institutionName": "ACME Bank",
     "institutionCode": "031008"
   }
   ```

   - **Send.** Expect **201 Created**. Copy the `tenantId` from the response into a new Postman variable `tenantId` (optional, for clarity).

2. **Provision a client configuration (one-time secret):**

   - **Method:** `POST`
   - **URL:** `{{baseUrl}}/v1/admin/tenants/{{tenantId}}/tenant-configuration`
   - **Headers:** `Authorization: Bearer {{adminToken}}`

   - **Send.** Expect **201 Created**. The response body has `clientId` (looks like `031008-7f3a91c2`) and `clientSecret` (43-char string). **Copy both now — the secret is shown exactly once.**

3. **Mint the tenant's first ACTIVE signing key:**

   - **Method:** `POST`
   - **URL:** `{{baseUrl}}/v1/crypto-keys`
   - **Headers:** `Authorization: Bearer {{adminToken}}`
   - **Body** (raw JSON):

   ```json
   {
     "tenantId": "{{tenantId}}",
     "mode": "Generate"
   }
   ```

   - **Send.** Expect **201 Created**. Note the returned `keyVersion` (e.g. `1`).

4. **Exchange the tenant configuration for a JWT:**

   - **Method:** `POST`
   - **URL:** `{{baseUrl}}/v1/oauth/token`
   - **Body** → `x-www-form-urlencoded`:

   | KEY | VALUE |
   |---|---|
   | `grant_type` | `client_credentials` |
   | `client_id` | *(the `clientId` from step 2)* |
   | `client_secret` | *(the `clientSecret` from step 2)* |

   - **Tests tab** → paste:

   ```javascript
   pm.environment.set("tenantToken", pm.response.json().accessToken);
   ```

   - **Send.** Expect **200 OK**. The environment variable `tenantToken` is now populated — Postman uses it automatically for §§2–4 below.

You are ready. Open the **environment dropdown** (top-right) and confirm `SBQR Local` is selected.

---

## 2. Step 1 — Generate a STATIC QR

**Purpose:** produce a string you can later paste into the verify endpoint.

- **Method:** `POST`
- **URL:** `{{baseUrl}}/v1/qr/generate/static`
- **Headers:**

| KEY | VALUE |
|---|---|
| `Authorization` | `Bearer {{tenantToken}}` |
| `Content-Type` | `application/json` |
| `Idempotency-Key` | *(optional — any unique string, e.g. `{{$guid}}`)* |

- **Body** → **raw** → **JSON**:

```json
{
  "recipientName": "Arif Mahmood",
  "recipientCity": "Dhaka",
  "recipientPan":  "01711111111"
}
```

- **Tests tab** → paste (this auto-captures the payload for the verify step):

```javascript
pm.environment.set("lastQrPayload", pm.response.json().qrPayload);
console.log("payloadHash:", pm.response.json().payloadHash);
```

- **Send.** Expect **201 Created**. Response body:

```json
{
  "qrPayload": "000201010211...6304A1B2",
  "payloadHash": "9f2c4e7b1a2f3e4d5c6b7a8d9e0f1234c5b6a7d8e9f0123456789abcdef01234",
  "qrType": "STATIC",
  "signatureKeyVersion": 1
}
```

**Notes:**
- `qrPayload` always **starts with `000201`** (Payload Format Indicator = "01") and the next 4 chars are `0102` or `0105` (Point of Initiation = "11" or "12" padded). For this static request you should see **`11`** in there.
- `qrPayload` is **returned once and never persisted** — only `payloadHash` is saved.
- The `Idempotency-Key` header is optional. Replaying the **same body + same header** for the same tenant returns **409 Conflict** with error `DUPLICATE_IDEMPOTENCY_KEY`.

---

## 3. Step 2 — Generate a DYNAMIC QR

Same shape as static, but with a required `transactionAmount`.

- **Method:** `POST`
- **URL:** `{{baseUrl}}/v1/qr/generate/dynamic`
- **Headers:** same as Step 1 (use `{{tenantToken}}`)

- **Body** → **raw** → **JSON**:

```json
{
  "transactionAmount": "150.00",
  "recipientName": "Arif Mahmood",
  "recipientCity": "Dhaka",
  "recipientPan":  "01711111111",
  "postalCode":    "1207",
  "customerLabel": "FT",
  "purposeOfTransaction": "Invoice #4821"
}
```

- **Tests tab** → paste:

```javascript
pm.environment.set("lastDynamicQr", pm.response.json().qrPayload);
console.log("payloadHash:", pm.response.json().payloadHash);
```

- **Send.** Expect **201 Created**. Response body:

```json
{
  "qrPayload": "000201010212...6304ZZZZ",
  "payloadHash": "<64-char hex>",
  "qrType": "DYNAMIC",
  "signatureKeyVersion": 1
}
```

**Field rules (cheat sheet):**

| Field | Required? | Rule |
|---|---|---|
| `transactionAmount` | yes (dynamic only) | digits, optional single `.`, ≤ 13 chars |
| `recipientName` | yes | ≤ 25 chars UTF-8 |
| `recipientCity` | yes | ≤ 15 chars UTF-8 |
| `recipientPan` | yes | ≤ 19 chars |
| `postalCode` | no | ≤ 10 chars |
| `customerLabel` | no | ≤ 25 chars |
| `purposeOfTransaction` | no | ≤ 25 chars |

Static endpoint **must NOT contain `transactionAmount`** (400 otherwise — see [§5.1](#51-validation-400)).

---

## 4. Step 3 — Verify (round-trip) your own QR

This is the test that **proves the whole pipeline works**: build → sign with your key → verify with your key.

> The body is **just the QR string**. The server fills in the internal replay-guard fields on your behalf.

- **Method:** `POST`
- **URL:** `{{baseUrl}}/v1/qr/validate`
- **Headers:**

| KEY | VALUE |
|---|---|
| `Authorization` | `Bearer {{tenantToken}}` |
| `Content-Type` | `application/json` |

- **Body** → **raw** → **JSON**:

```json
{
  "qrPayload": "{{lastQrPayload}}"
}
```

- **Tests tab** → paste:

```javascript
console.log("verdict:", pm.response.json().verdict,
            "| trustSource:", pm.response.json().trustSource,
            "| reasonCode:", pm.response.json().reasonCode,
            "| payloadHash:", pm.response.json().payloadHash);
```

- **Send.** Expect **200 OK**. Response body:

```json
{
  "verdict":          "VALID",
  "trustSource":      "OWN_CUSTODY",
  "reasonCode":       null,
  "institutionCode":  "031008",
  "payloadHash":      "<64-char hex — must match the one from §2/§3>",
  "recipientName":    "Arif Mahmood",
  "recipientPan":     "01711111111",
  "qrClassification": "P2P"
}
```

**Sanity checks:**
- `verdict` = `"VALID"`.
- `trustSource` = `"OWN_CUSTODY"` (your tenant issued it; key came from your local `crypto_keys`).
- `institutionCode` = the **6-digit** code you registered the tenant with.
- `payloadHash` is **byte-for-byte identical** to the one in the generate response. If not, the payload was mutated in transit.
- `qrClassification` = `"P2P"` (Tag 52 = `4829`).

**Tip — paste any QR by hand.** If you want to validate an arbitrary QR string (e.g. one from another test run), just type the body as:

```json
{ "qrPayload": "<paste the full string here>" }
```

---

## 5. Step 4 — Failure scenarios you should try

Rejection verdicts come back as **200 OK** with a non-`VALID` `verdict`. Only **request-shape problems** return 4xx.

### 5.1 Validation (400)

| # | How to trigger | Endpoint | Status | `error` |
|---|---|---|---|---|
| a | Omit `recipientName` (or send `""`) | either | **400** | `{"error":1,"message":"...must not be empty."}` |
| b | `recipientCity` longer than 15 chars | either | **400** | `{"error":1,"message":"...must be 15 characters or fewer..."}` |
| c | Send `transactionAmount` to `/generate/static` | static | **400** | `{"error":1,"message":"Static QR does not accept 'transactionAmount';..."}` |
| d | Omit `transactionAmount` from `/generate/dynamic` | dynamic | **400** | `{"error":1,"message":"TransactionAmount: ...must not be empty."}` |
| e | Bad `transactionAmount` like `"abc"` or `"1.2.3"` | dynamic | **400** | validation message about digits + optional decimal |
| f | Empty / missing `qrPayload` on `/validate` | validate | **400** | request-shape error |
| g | `qrPayload` longer than 2048 chars on `/validate` | validate | **400** | request-shape error |

> Cases (a/b/c/d/e) — `error` is the **numeric** `ErrorCode` (`1` = `ValidationFailed`). That's expected; no `JsonStringEnumConverter` is registered for this branch.

### 5.2 Activation gate (422)

| # | How to trigger | Endpoint | Status | Body |
|---|---|---|---|---|
| j | Call §2/§3 with a tenant that **has no ACTIVE signing key** (e.g. a freshly registered tenant on which you didn't run §1.3 step 3) | generate | **422** | `{"error":"KEY_NOT_ACTIVE","message":"KEY_NOT_ACTIVE: ... status is 'NONE'."}` |

### 5.3 Idempotency replay (409)

| # | How to trigger | Endpoint | Status | Body |
|---|---|---|---|---|
| k | Repeat the **exact same** generate call with the **same `Idempotency-Key`** header for the same tenant | generate | **409** | `{"error":"DUPLICATE_IDEMPOTENCY_KEY","message":"...was already used by this tenant."}` |

> Don't forget — the same **body + same header** is what triggers this. Change either to mint a new QR.

### 5.4 Validate-side failures (200 OK with rejection verdict)

These all return **200 OK**. Inspect the `verdict` and `reasonCode` fields.

| # | How to trigger | `verdict` | `reasonCode` |
|---|---|---|---|
| l | Flip the last character of `lastQrPayload` (CRC mismatch) | `STRUCTURAL_INVALID` | `CRC_MISMATCH` |
| m | Strip the trailing signature/CRC tags (no signature) | `INVALID_SIGNATURE` | `SIGNATURE_TAGS_MISSING` |
| n | Build a fresh QR with a private key the server doesn't know | `INVALID_SIGNATURE` | `SIGNATURE_MISMATCH` |
| o | QR whose Tag 26 institution code is **not** in any tenant row and **not** in the trust directory | `KEY_NOT_FOUND` | `TRUST_DIRECTORY_MISS` |
| q | QR whose Tag 52 is **not** `4829` (e.g. merchant-presented-mode) | `NON_P2P` | `null` *(informational — not an error)* |

### 5.5 Auth failures (401 / 403)

| # | How to trigger | Status |
|---|---|---|
| r | No `Authorization` header at all | **401** |
| s | Bearer `{{adminToken}}` instead of `{{tenantToken}}` for generate / validate (wrong scope) | **403** |

### 5.6 Wrong path (404)

| # | How to trigger | Status |
|---|---|---|
| t | `POST /v1/qr/generate/statik` (typo) or `POST /v2/qr/generate/static` | **404** |

---

## 6. Field reference card

### 6.1 `POST /v1/qr/generate/static` and `POST /v1/qr/generate/dynamic`

**Outbound — `GenerateQrResponse`:**

| Field | Type | Notes |
|---|---|---|
| `qrPayload` | string | signed QR string, **returned exactly once**, never persisted |
| `payloadHash` | string (64-char lowercase hex) | SHA-256 of `qrPayload`; persisted (also in audit `resource_id`) |
| `qrType` | string | `"STATIC"` or `"DYNAMIC"` |
| `signatureKeyVersion` | int | the key version that signed this QR |

### 6.2 `POST /v1/qr/validate`

**Outbound — `ValidateQrResponse`:**

| Field | Type | Always present? | Legal values |
|---|---|---|---|
| `verdict` | string | yes | `VALID` · `INVALID_SIGNATURE` · `STRUCTURAL_INVALID` · `KEY_NOT_FOUND` · `KEY_SUSPENDED` · `KEY_REVOKED` · `KEY_NOT_ACTIVE` · `NON_P2P` · `REQUEST_STALE` · `REQUEST_REPLAYED` |
| `trustSource` | string? | yes, but `null` on every non-`VALID` verdict | `OWN_CUSTODY` · `TRUST_DIRECTORY` · `null` |
| `reasonCode` | string? | yes, but `null` on `VALID` and `NON_P2P` | `CRC_MISMATCH` · `SIGNATURE_MISMATCH` · `SIGNATURE_TAGS_MISSING` · `SIGNATURE_MALFORMED` · `CUSTODY_KEY_NOT_FOUND` · `CUSTODY_KEY_SUSPENDED` · `CUSTODY_KEY_REVOKED` · `TRUST_DIRECTORY_MISS` · `REPLAY_DETECTED` · `TIMESTAMP_STALE` |
| `institutionCode` | string? | yes when Tag 26 decoded | 6-digit string (leading zeros preserved) · `null` on structural failure |
| `payloadHash` | string | yes | 64-char lowercase hex SHA-256 of the raw `qrPayload` |
| `recipientName` | string? | yes when Tag 59 decoded | up to 25 UTF-8 bytes |
| `recipientPan` | string? | yes when Tag 62 PAN sub-tag decoded | up to 19 chars |
| `qrClassification` | string | yes | `P2P` (Tag 52 = 4829) · `NON_P2P` · `UNKNOWN` |

**Inbound — `ValidateQrRequest`:**

| Field | Type | Rule |
|---|---|---|
| `qrPayload` | string | required, ≤ 2048 chars |

> The server fills in the internal replay-guard fields (`requestId`, `requestTimestamp`) for now. When the C6 replay-guard contract returns to the wire, the DTO will accept them back as optional fields and the server will only fill them in when the client omits them.

---

## Quick postman collection (copy-paste ready)

If you prefer to import a pre-built collection, paste the JSON below into a file named `sbqr-qr.postman_collection.json` and **Import** in Postman:

```json
{
  "info": {
    "name": "SBQR - QR Generation & Verification",
    "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
  },
  "item": [
    {
      "name": "1. Tenant token (OAuth client_credentials)",
      "request": {
        "method": "POST",
        "header": [{ "key": "Content-Type", "value": "application/x-www-form-urlencoded" }],
        "url": { "raw": "{{baseUrl}}/v1/oauth/token", "host": ["{{baseUrl}}"], "path": ["v1", "oauth", "token"] },
        "body": {
          "mode": "urlencoded",
          "urlencoded": [
            { "key": "grant_type",    "value": "client_credentials" },
            { "key": "client_id",     "value": "<paste from §1.3 step 2>" },
            { "key": "client_secret", "value": "<paste from §1.3 step 2>" }
          ]
        }
      },
      "event": [{
        "listen": "test",
        "script": { "exec": ["pm.environment.set('tenantToken', pm.response.json().accessToken);"] }
      }]
    },
    {
      "name": "2. Generate STATIC QR",
      "request": {
        "method": "POST",
        "header": [
          { "key": "Authorization",  "value": "Bearer {{tenantToken}}" },
          { "key": "Content-Type",   "value": "application/json" },
          { "key": "Idempotency-Key","value": "{{$guid}}" }
        ],
        "url": { "raw": "{{baseUrl}}/v1/qr/generate/static", "host": ["{{baseUrl}}"], "path": ["v1","qr","generate","static"] },
        "body": {
          "mode": "raw",
          "raw": "{\n  \"recipientName\": \"Arif Mahmood\",\n  \"recipientCity\": \"Dhaka\",\n  \"recipientPan\":  \"01711111111\"\n}"
        }
      },
      "event": [{
        "listen": "test",
        "script": {
          "exec": [
            "pm.environment.set('lastQrPayload', pm.response.json().qrPayload);",
            "console.log('payloadHash=', pm.response.json().payloadHash);"
          ]
        }
      }]
    },
    {
      "name": "3. Generate DYNAMIC QR",
      "request": {
        "method": "POST",
        "header": [
          { "key": "Authorization",  "value": "Bearer {{tenantToken}}" },
          { "key": "Content-Type",   "value": "application/json" },
          { "key": "Idempotency-Key","value": "{{$guid}}" }
        ],
        "url": { "raw": "{{baseUrl}}/v1/qr/generate/dynamic", "host": ["{{baseUrl}}"], "path": ["v1","qr","generate","dynamic"] },
        "body": {
          "mode": "raw",
          "raw": "{\n  \"transactionAmount\": \"150.00\",\n  \"recipientName\": \"Arif Mahmood\",\n  \"recipientCity\": \"Dhaka\",\n  \"recipientPan\":  \"01711111111\"\n}"
        }
      }
    },
    {
      "name": "4. Validate your QR (round-trip)",
      "request": {
        "method": "POST",
        "header": [
          { "key": "Authorization", "value": "Bearer {{tenantToken}}" },
          { "key": "Content-Type",  "value": "application/json" }
        ],
        "url": { "raw": "{{baseUrl}}/v1/qr/validate", "host": ["{{baseUrl}}"], "path": ["v1","qr","validate"] },
        "body": {
          "mode": "raw",
          "raw": "{\n  \"qrPayload\": \"{{lastQrPayload}}\"\n}"
        }
      }
    }
  ]
}
```

After importing:
1. Set `baseUrl` to `http://localhost:5001` in the environment.
2. Open request **1** → paste the tenant's real `client_id` and `client_secret` → **Send** once. `tenantToken` is captured.
3. Walk through requests 2 → 3 → 4. Each captures what the next one needs.

That's it — if request **4** returns `verdict = "VALID"`, your generation + verification pipeline is healthy end-to-end.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `/v1/qr/validate` returns **401 invalid_client** after a few minutes | Tenant JWT expired (TTL = 10 min in dev). Re-run request **1**. |
| `403` on `/generate/static` with `{{tenantToken}}` | The token carries `admin` scope only. You used the bootstrap token by mistake. Re-do §1.3. |
| `422 KEY_NOT_ACTIVE` on generate | You skipped §1.3 step 3 (mint the signing key) or the key was suspended. |
| Generate returns the **same** `qrPayload` twice | Determinism bug or you are replaying without an `Idempotency-Key` header. Actually — `qrPayload` is generated fresh each call, so a duplicate means something else is caching. Check you are sending the new `Idempotency-Key`. |
| Validate `verdict = STRUCTURAL_INVALID CRC_MISMATCH` on your own freshly minted QR | The QR was mangled between §2 and §4 (Postman variable interpolation, whitespace, etc.). Open the §2 response, manually copy `qrPayload` into the §4 body. |
| `404` on every request | You forgot `/v1/` in the URL, or you used `/v2/`. URL versioning is mandatory. |
