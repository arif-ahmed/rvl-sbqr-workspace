# FR-AUTH-003 — QA Testing Guide (Device-Binding Flow)

> **Part of the [FR-AUTH-003 dev guide](./FR-AUTH-003-dev-guide.md) series.**
> - [← Back to main guide](./FR-AUTH-003-dev-guide.md)
> - [Security Architecture & VAPT](./FR-AUTH-003-security-architecture.md)
> - [Local Dev Guide](./FR-AUTH-003-local-dev.md)
> - [Production & Operations](./FR-AUTH-003-production-operations.md)
> - [Appendix: Payloads](./FR-AUTH-003-appendix-payloads.md)

---

## How QA tests the device-binding flow — without a real phone

This section is written **for QA engineers**. You do NOT need a physical Android
or iOS device. You act as the "mobile app" by sending HTTP requests to the backend.
The backend does all the verification. The table below shows who does what at each step:

### The split: what QA simulates vs. what the backend verifies

| Step | What QA does (simulates the mobile app) | What the backend does (you don't control this) |
|---|---|---|
| Pre-condition | Start the API in `Soft` mode + `Simulator` verifier | Reads config, picks the `SimulatorAttestationVerifier` |
| 1. Nonce | Call `GET /v1/oauth/attestation-nonce` with `client_id` + `client_secret` | Generates a single-use random nonce, stores it with TTL |
| 2. Verdict | Call `POST /v1/qa/simulator/verdict` to mint a fake Google/Apple verdict | Signs the verdict with a test Ed25519 key, returns base64 blob |
| 3. Enroll | Call `POST /v1/oauth/device-enrollment` with the verdict + a device public key | Verifies the verdict signature, checks package + cert match, saves the device, issues a token with `cnf` claim |
| 4. Use API | Call `POST /v1/qr/generate/static` with the token + an `X-Signature` PoP header | Verifies the JWT, checks the `cnf` claim, verifies the PoP signature against the device public key |
| 5. Negative tests | Tamper the verdict, reuse the nonce, omit the PoP header | Logs the failure (Soft mode) or rejects (Enforced mode) |

### Test environment setup (QA does this)

1. **Start the API in Dev/DevTest mode:**

   ```bash
   dotnet run --launch-profile Development
   ```

   Set the dev config in user-secrets (single-file convention — no
   `appsettings.Development.json` exists):

   ```bash
   dotnet user-secrets set "Attestation:Mode" "Soft" --project src/Host/SBQR.Api/SBQR.Api.csproj
   dotnet user-secrets set "Attestation:Verifier" "Simulator" --project src/Host/SBQR.Api/SBQR.Api.csproj
   ```

2. **Ensure a tenant + app are registered** (ask a backend dev to run this once, or use the admin endpoints if you have an admin token):

   ```bash
   # Register a tenant
   curl -X POST http://localhost:5000/v1/admin/tenants \
     -H "Authorization: Bearer $ADMIN_TOKEN" \
     -d '{"code":"DHB","name":"Dhaka Bank"}'
   # → 201, note the tenant_id

   # Register an app (one row per platform)
   curl -X POST "http://localhost:5000/v1/admin/tenants/$TENANT_ID/applications" \
     -H "Authorization: Bearer $ADMIN_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"platform":"ANDROID","package_id":"com.dhakabank.consumer"}'
   # → 201 Created
   ```

3. **Get your test credentials** (`client_id` + `client_secret`) from the devops team. You'll need them for every request.

4. **Generate a device keypair** (this simulates the phone's secure hardware keystore):

   ```bash
   openssl genpkey -algorithm Ed25519 -out /tmp/device-key.pem
   openssl pkey -in /tmp/device-key.pem -pubout -outform PEM -out /tmp/device-pub.pem
   ```

---

### Test scenario 1 — Happy path (enroll + call QR)

This is the main flow. It should succeed end-to-end.

```bash
# ─── Step 1: Get a nonce ───
NONCE=$(curl -s http://localhost:5000/v1/oauth/attestation-nonce \
  -u "$CLIENT_ID:$CLIENT_SECRET" | jq -r .nonce)
echo "Nonce: $NONCE"
# ✅ Expected: 200 with {"nonce":"x7y9q2m4p1..."}

# ─── Step 2: Mint a simulator verdict ───
VERDICT=$(curl -s -X POST http://localhost:5000/v1/qa/simulator/verdict \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -H "Content-Type: application/json" \
  -d "{
    \"platform\": \"android\",
    \"package_name\": \"com.dhakabank.consumer\",
    \"signing_cert_sha256\": \"9e3a1bff2c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a\",
    \"nonce\": \"$NONCE\",
    \"is_genuine\": true,
    \"is_device_tampered\": false
  }" | jq -r .verdict)
# ✅ Expected: 200 with {"verdict":"<base64>"}

# ─── Step 3: Enroll the device ───
# The enrollment endpoint returns the JWT access_token. This is how the real app
# gets its token — the SDK calls this 3-call sequence once at first launch.
# See: [Production & Operations: How the app gets its JWT token](../FR-AUTH-003-production-operations.md#how-the-app-or-sdk-gets-its-jwt-token--step-by-step)
ENROLL=$(curl -s -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -H "Content-Type: application/json" \
  -d "{
    \"platform\": \"android\",
    \"package_id\": \"com.dhakabank.consumer\",
    \"attestation_verdict\": \"$VERDICT\",
    \"device_public_key\": $(jq -Rs . /tmp/device-pub.pem),
    \"nonce\": \"$NONCE\"
  }")
echo "$ENROLL" | jq .
# ✅ Expected: 200 with:
#   {
#     "device_id": "f00dcafe-1234-...",
#     "access_token": "eyJ...",
#     "token_type": "Bearer",
#     "expires_in": 3600
#   }

# ─── Step 4: Call QR endpoint WITH PoP signature → 200 ───
TOKEN=$(echo "$ENROLL" | jq -r .access_token)
TIMESTAMP=$(date +%s)
BODY='{"name":"Test User","account":"01711111111","amount":100.00}'
BODY_HASH=$(echo -n "$BODY" | sha256sum | cut -d' ' -f1)
PAYLOAD="POST /v1/qr/generate/static $TIMESTAMP $BODY_HASH"
SIGNATURE=$(echo -n "$PAYLOAD" | openssl pkeyutl -sign -inkey /tmp/device-key.pem -rawin | base64 -w0)

curl -X POST http://localhost:5000/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H "X-Timestamp: $TIMESTAMP" \
  -H "X-Signature: v1=$SIGNATURE" \
  -H "Content-Type: application/json" \
  -d "$BODY"
# ✅ Expected: 200 with {"qr_payload":"00010A01..."}

# ─── Step 5: Call WITHOUT PoP signature → 401 ───
curl -X POST http://localhost:5000/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "$BODY"
# ✅ Expected: 401 Unauthorized — "PoP signature required"
```

---

### Test scenario 2 — Tampered verdict signature

An attacker intercepts a valid verdict and modifies it (same signature, tampered payload):

```bash
# Get a fresh nonce
NONCE=$(curl -s http://localhost:5000/v1/oauth/attestation-nonce \
  -u "$CLIENT_ID:$CLIENT_SECRET" | jq -r .nonce)

# Mint a valid verdict
VERDICT=$(curl -s -X POST http://localhost:5000/v1/qa/simulator/verdict \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -H "Content-Type: application/json" \
  -d "{\"platform\":\"android\",\"package_name\":\"com.dhakabank.consumer\",
       \"signing_cert_sha256\":\"9e3a1b...\",\"nonce\":\"$NONCE\",\"is_genuine\":true}" \
  | jq -r .verdict)

# Tamper: decode, change is_genuine to false, re-encode (signature stays the SAME — now invalid)
TAMPERED=$(echo "$VERDICT" | base64 -d | jq '.payload.is_genuine = false' | base64 -w0)

curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -H "Content-Type: application/json" \
  -d "{\"platform\":\"android\",\"package_id\":\"com.dhakabank.consumer\",
       \"attestation_verdict\":\"$TAMPERED\",\"device_public_key\":\"...\",\"nonce\":\"$NONCE\"}"
# Soft mode: ✅ 200 (logged as "verdict_invalid" in audit, enrollment proceeds)
# Enforced mode: ❌ 401 (signature verification fails — payload was tampered after signing)
```

---

### Test scenario 3 — Signing cert mismatch (repackaged APK)

An attacker repackaged your APK with a different signing key:

```bash
NONCE=$(curl -s http://localhost:5000/v1/oauth/attestation-nonce \
  -u "$CLIENT_ID:$CLIENT_SECRET" | jq -r .nonce)

# Mint a verdict with a WRONG signing cert hash
VERDICT=$(curl -s -X POST http://localhost:5000/v1/qa/simulator/verdict \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d "{\"platform\":\"android\",\"package_name\":\"com.dhakabank.consumer\",
       \"signing_cert_sha256\":\"ffffffffffffffffffffffffffffffffffffffffffffffffffffffff\",
       \"nonce\":\"$NONCE\",\"is_genuine\":true}" | jq -r .verdict)

curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -H "Content-Type: application/json" \
  -d "{\"platform\":\"android\",\"package_id\":\"com.dhakabank.consumer\",
       \"attestation_verdict\":\"$VERDICT\",\"device_public_key\":\"...\",
       \"nonce\":\"$NONCE\"}"
# Soft mode: ✅ 200 (logged as "signing_cert_mismatch", continues)
# Enforced mode: ❌ 401 (verdict says cert=ffff… but registered row says 9e3a1b…)
```

---

### Test scenario 4 — Nonce reuse (replay attack)

An attacker captured a valid verdict and replays it:

```bash
# Steps 1-3: enroll a device (consumes nonce)
# ... (same as happy path) ...
# → nonce is now marked "used" server-side

# Step 4: enroll AGAIN with the SAME nonce + SAME verdict
curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d "{\"platform\":\"android\",\"package_id\":\"com.dhakabank.consumer\",
       \"attestation_verdict\":\"$VERDICT\",\"device_public_key\":\"...\",
       \"nonce\":\"$NONCE\"}"
# ❌ Expected: 401 — "nonce already consumed" (single-use enforcement)
```

---

### Test scenario 5 — Wrong package_id

An attacker tries to enroll with a package_id not registered for this tenant:

```bash
NONCE=$(curl -s http://localhost:5000/v1/oauth/attestation-nonce \
  -u "$CLIENT_ID:$CLIENT_SECRET" | jq -r .nonce)

VERDICT=$(curl -s -X POST http://localhost:5000/v1/qa/simulator/verdict \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d "{\"platform\":\"android\",\"package_name\":\"com.dhakabank.consumer\",
       \"signing_cert_sha256\":\"9e3a1b...\",\"nonce\":\"$NONCE\",\"is_genuine\":true}" \
  | jq -r .verdict)

# Enroll with a DIFFERENT package_id than what's in the verdict
curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d "{\"platform\":\"android\",\"package_id\":\"com.evilbank.stealer\",
       \"attestation_verdict\":\"$VERDICT\",\"device_public_key\":\"...\",
       \"nonce\":\"$NONCE\"}"
# ❌ Expected: 401 — package_name in verdict (com.dhakabank.consumer) ≠
#    package_id in request (com.evilbank.stealer)
```

---

### Test scenario 6 — Stolen token, no device key (Postman replay)

An attacker stole a token from the logs and tries to use it from Postman:

```bash
curl -X POST http://localhost:5000/v1/qr/generate/static \
  -H "Authorization: Bearer $STOLEN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Test User","account":"01711111111","amount":100.00}'
# ❌ Expected: 401 — "PoP signature required"
# Token has cnf claim → PoP is mandatory. Postman has no device private key.
```

---

### Test scenario 7 — Android and iOS on the same package_id

Two rows, same `package_id`, different platforms + signing certs:

```bash
# Register Android row
curl -X POST "http://localhost:5000/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"platform":"ANDROID","package_id":"com.dhakabank.consumer","signing_cert_sha256":"9e3a1b..."}'
# → 201

# Register iOS row (same package_id, different platform + cert)
curl -X POST "http://localhost:5000/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"platform":"IOS","package_id":"com.dhakabank.consumer","signing_cert_sha256":"f7c8d2..."}'
# → 201 (no conflict — platform differs)

# List → returns 2 rows
curl "http://localhost:5000/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq '.[] | {platform, package_id, signing_cert_sha256}'
# → { "platform":"ANDROID", "package_id":"com.dhakabank.consumer", "signing_cert_sha256":"9e3a1bff..." }
# → { "platform":"IOS",     "package_id":"com.dhakabank.consumer", "signing_cert_sha256":"f7c8d2aa..." }
```

---

### Test scenario 8 — Android-only app (no iOS row registered)

```bash
# Admin registered only: (ANDROID, com.dhakabank.consumer, 9e3a1b...)
# iOS app tries to enroll:
curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d '{"platform":"ios","package_id":"com.dhakabank.consumer",
       "attestation_verdict":"<ios-verdict>","device_public_key":"...","nonce":"$NONCE"}'
# ❌ Expected: 401 — no IOS row exists for (tenant, com.dhakabank.consumer)
#    The admin MUST register the iOS row before iOS users can enroll.
```

---

### Configuration matrix for QA

| `Mode` you set | What happens on verification failure | When to use |
|---|---|---|
| `Off` | No attestation checks. No PoP required. | Initial local dev — QR flow only |
| `Soft` | Failure is **logged** to audit table, but **enrollment still proceeds**. PoP required. | **QA testing** — exercise both pass and fail paths |
| `Enforced` | Failure is **rejected** with 401. | Staging / pre-prod validation |

| `Verifier` you set | What key it uses | When to use |
|---|---|---|
| `Simulator` | Your test Ed25519 key from config | **QA / local dev** — no phone needed |
| `PlayIntegrity` | Google's public key (downloaded at startup) | Production (Android) |
| `AppAttest` | Apple's root cert | Production (iOS) |

> ⚠️ **Never mix modes.** `Soft + Simulator` = QA combo. `Enforced + PlayIntegrity/AppAttest` = production. Startup guard rejects `Enforced + Simulator`.

### Verifying results — check both layers

For every scenario, check **both** the HTTP response and the audit log:

```bash
# 1. HTTP response code (what curl/Postman shows)
#    → 200 = pass, 401 = rejected as expected

# 2. Audit log (what the backend recorded)
curl "http://localhost:5000/v1/audit?tenant_id=$TENANT_ID" \
  -H "Authorization: Bearer $ADMIN_TOKEN" | jq .
#    → "attestation_verification_failed" for tampered verdicts
#    → "signing_cert_mismatch" for repackaged APKs
#    → "nonce_reused" for replay attempts
#    → "pop_signature_missing" for token-only calls
```

---

