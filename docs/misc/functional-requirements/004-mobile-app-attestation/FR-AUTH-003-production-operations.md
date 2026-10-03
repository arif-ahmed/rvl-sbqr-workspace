# FR-AUTH-003 — Production Journey & Operations

> **Part of the [FR-AUTH-003 dev guide](./FR-AUTH-003-dev-guide.md) series.**
>
> **SUPERSEDED (2026-10-03):** FR-AUTH-003 was built on the FR-AUTH-002
> `tenant_applications` allow-list, which was removed (server-to-server token
> model — see the banner on `FR-AUTH-003-mobile-app-attestation.md`).
> - [← Back to main guide](./FR-AUTH-003-dev-guide.md)
> - [Security Architecture & VAPT](./FR-AUTH-003-security-architecture.md)
> - [QA Testing Guide](./FR-AUTH-003-qa-testing.md)
> - [Local Dev Guide](./FR-AUTH-003-local-dev.md)
> - [Appendix: Payloads](./FR-AUTH-003-appendix-payloads.md)

---

## How this works in production — a real user's journey

### Phase A: The bank prepares (admin work)

The bank's admin team does this once, after the app is built:

```bash
# 1. Register the tenant (bank) in your system
curl -X POST http://your-api/v1/admin/tenants \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"code":"DHB","name":"Dhaka Bank","country":"BD"}'

# 2. Register the mobile app (signing_cert_sha256 is optional for now)
curl -X POST http://your-api/v1/admin/tenants/{tenant-id}/applications \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"platform":"ANDROID","package_id":"com.dhakabank.app" }'
```

### Phase B: The real user on their phone

The user downloads `com.dhakabank.app` from the Play Store and opens it for the first time:

**Step 1 — Get a nonce**

```
App → GET /v1/oauth/attestation-nonce
  (sends client_id + client_secret)
← Backend returns: { "nonce": "x7y9q2m4p1" }
```

**Step 2 — Ask Google to vouch for the app**

```
App → Google Play Integrity API
  (sends the nonce "x7y9q2m4p1" as the challenge)
← Google returns a signed JSON object (verdict) that says:
   "Yes, on device ABC-123, package name is com.dhakabank.app,
    signing cert digest is 9e3a1b..., this is a genuine,
    unmodified app, verified at 2026-09-14T11:50."
   (This is cryptographically signed by Google — can't be forged.)
```

**Step 3 — Enroll the device**

```
App → POST /v1/oauth/device-enrollment
  {
    "platform": "android",
    "package_id": "com.dhakabank.app",
    "attestation_verdict": "<base64 Google verdict>",
    "device_public_key": "-----BEGIN PUBLIC KEY-----..."
  }
← Backend:
  1. Checks client_id + client_secret (Argon2id verify — same as today)
  2. Downloads Google's public key, verifies the verdict signature
  3. Checks package name + signing cert match the registered app row
  4. Checks nonce is valid (not expired, not reused)
  5. Creates a row in enrolled_devices
  6. Returns: { device_id: "...", access_token: "...", token_type: "Bearer", expires_in: 600 }
```

The token now has a `cnf` (confirmation) claim containing the **thumbprint** of the
device's public key.

**Step 4 — Generate a QR code**

```
App → POST /v1/qr/generate/static
  Authorization: Bearer <the token from Step 3>
  X-Signature: v1=<signature of "POST /v1/qr/generate/static <timestamp> <body-hash>">
  X-Timestamp: 1726307412
  Content-Type: application/json

  { "name": "Areeba Nawar", "account": "01711111111", ... }
← Backend:
  1. JWT validation — token signature is valid, not expired. OK.
  2. Token has a "cnf" claim → PoP is required.
  3. Looks up the device's public key from enrolled_devices by thumbprint.
  4. Reconstructs the signed payload:
     "POST /v1/qr/generate/static 1726307412 <body-hash>"
  5. Verifies the X-Signature with the device's public key. ✓
  6. Generates the QR code payload. Returns it.
```

### Phase C: What happens when someone attacks

**Attack 1 — Steal the credentials from the APK**

```
Attacker → POST /v1/oauth/device-enrollment  (with stolen client_id + secret)
  { "platform": "android", "package_id": "com.dhakabank.app",
    "attestation_verdict": "<fake verdict>",
    "device_public_key": "..." }
← Backend: tries to verify the "fake verdict" with Google's public key.
   Signature check fails → 401 invalid_client. Attacker can't forge a Google verdict.
```

**Attack 2 — Steal the issued token, replay from Postman**

```
Attacker → POST /v1/qr/generate/static  (with stolen Bearer token)
  Authorization: Bearer <stolen-token>
  (no X-Signature, or a wrong one)
← Backend: token is valid, but it has a "cnf" claim → PoP required.
   No valid X-Signature → 401. Token is useless without the device's private key.
```

**Attack 3 — Use the app on a rooted device**

This still works (it's an accepted limitation). The genuine app on a rooted device passes
attestation because Google can't fully detect root in all cases. Phase 3 (out of scope here)
addresses continuous in-app monitoring. FR-AUTH-003 raises the cost — it's just not infinite.

---

### How the app (or SDK) gets its JWT token — step by step

This section is written **from the app's perspective.** It answers: *"I'm writing the mobile
app (or the SDK). I need a JWT access token. What HTTP calls do I make, in what order?"*

The token is issued by `POST /v1/oauth/device-enrollment` **after** the app proves its
identity via Google/Apple attestation. The app never calls `POST /v1/oauth/token` for a
plain bearer token — the enrollment endpoint IS the token endpoint for enrolled devices.

**The 3-call sequence (happens once, at first launch):**

```
App (or SDK)                           sbqr.service
  │                                        │
  ├── 1. GET /v1/oauth/attestation-nonce   │
  │    Authorization: Basic base64(client_id:client_secret)│
  │                                        │
  │                                        ├── Argon2id-verify credentials
  │                                        ├── Generate random nonce ("x7y9q2m4p1")
  │                                        ├── Store nonce (TTL=5min, used=false)
  │                                        └── Returns: { "nonce": "x7y9q2m4p1" }
  │◄───                                  │
  │  { "nonce": "x7y9q2m4p1" }            │
  │                                        │
  ├── 2. (SDK calls Google / Apple)        │
  │    (NOT your backend — platform API only)│
  │    SDK: GooglePlayIntegrityManager.attest(nonce)│
  │                                        │
  │                                        │ (your backend does nothing here)
  │                                        │
  ├── 3. POST /v1/oauth/device-enrollment  │
  │    Authorization: Basic base64(client_id:client_secret)│
  │    {                                    │
  │      "platform": "android",             │
  │      "package_id": "com.dhakabank.app", │
  │      "attestation_verdict": "<base64>", │
  │      "device_public_key": "<PEM>"       │
  │    }                                    │
  │                                        ├── Verify credentials (Argon2id)
  │                                        ├── Verify verdict signature (Google's key)
  │                                        ├── Match package + signing_cert to DB
  │                                        ├── Validate nonce (not expired, not reused)
  │                                        ├── Insert enrolled_devices row
  │                                        └── Returns: JWT + device_id
  │◄───                                  │
  │  {                                    │
  │    "device_id": "c3d4e5f6-...",       │
  │    "access_token": "eyJhbGci...",     │
  │    "token_type": "Bearer",            │
  │    "expires_in": 600                   │
  │  }                                    │
  │                                        │
  └── Store the JWT in secure storage (Keystore / Keychain) └── Next request uses this token
```

**What the app does with the JWT:**

1. **Store it securely** — use `EncryptedSharedPreferences` (Android) or
   `Keychain` (iOS). Never store it in `UserDefaults` or `SharedPreferences`.
2. **Use it immediately** — every subsequent API call includes it as:
   ```
   Authorization: Bearer eyJhbGci...<the-JWT-from-Step-3>
   ```
3. **Sign every request with the device key** — the PoP header:
   ```
   X-Signature: v1=<Ed25519-signature of "METHOD PATH TIMESTAMP BODY_HASH">
   X-Timestamp: 1726307412
   ```
4. **Refresh on expiry** — after 10 minutes (the token expires), repeat the
   attestation flow. The SDK does this silently.

**What happens if enrollment fails (app-side error handling):**

| Backend response | What the app should do |
|---|---|
| `401 invalid_client` (bad client_id/secret) | Stop. Credentials are misconfigured. Contact backend team. |
| `401 invalid_client` (nonce expired/reused) | Request a fresh nonce (Step 1), then re-enroll (Step 3). |
| `401 invalid_client` (signing cert mismatch) | The app was repackaged or updated with a different cert. The admin must update `signing_cert_sha256` for this app. |
| `401 invalid_client` (bad verdict signature) | Google/Apple rejected the attestation. The SDK should retry or surface a user error. |
| `503` | Backend unavailable. Retry with exponential backoff. |

**Where to find the exact payloads:**

See [Appendix: Payloads](./FR-AUTH-003-appendix-payloads.md) —
- [Step 1: App requests a nonce](./FR-AUTH-003-appendix-payloads.md#step-1--app-requests-a-nonce)
- [EnrollDeviceCommandHandler returns the JWT](./FR-AUTH-003-appendix-payloads.md#what-your-backend-returns-on-success)
- [Decoded access token (what the app stores)](./FR-AUTH-003-appendix-payloads.md#the-decoded-access-token-what-the-app-stores)

---

### The onboarding flow (who does what)

```
Bank IT team                          Your platform team (you)
   │                                        │
   │  "We will ship com.dhakabank.app       │
   │   (Android) and com.dhakabank.ios.app  │
   │   (iOS)."                              │
   │                                        │
   │────────────────── package IDs ─────────────────►     │
   │                                        │
   │                                        ├── You create the tenant:
   │                                        │   POST /v1/admin/tenants
   │                                        │   { "code":"DHB", "name":"Dhaka Bank", ... }
   │                                        │
   │                                        ├── You provision credentials:
   │                                        │   POST .../tenant-configuration
   │                                        │   → returns client_id + client_secret
   │                                        │   (the plaintext secret is shown ONCE)
   │                                        │
   │                                        ├── You register each app row:
   │                                        │   POST .../applications
   │                                        │   { platform:"ANDROID",
   │                                        │     package_id:"com.dhakabank.app",
   │                                        │     signing_cert_sha256:"9e3a1b..." }
   │                                        │   POST .../applications
   │                                        │   { platform:"IOS",
   │                                        │     package_id:"com.dhakabank.ios.app",
   │                                        │     signing_cert_sha256:"f7c8d2..." }
   │                                        │
   │                                        │───────────────┐
   │◄───────────────────── client_id + secret              │
   │                                        │              │
   │  You hand over:                       │              │
   │  - the SDK (.aar/.xcframework)         │              │
   │  - client_id + client_secret         │              │
   │                                        │              │
   │  The FI integrates your SDK into       │              │
   │  their **existing** app binary          │              │
   │  (already on Play Store / App Store)   │              │
   │  + builds with your SDK                │              │
   │  + redeploys to stores               ──►              │
   │                                        │              │
   │  Real users download and use           │              │
   │  the apps — attestation flow runs at   │              │
   │  first launch (your SDK calls your     │              │
   │  backend) ◄────────────────────────────┘
```

> **Note:** The `client_id` + `client_secret` are delivered to the FI via a secure
> channel (not via email or chat). For VAPT compliance, you should eventually move
> the FI to Pattern 1 (PKCE) so the `client_secret` is eliminated from the app
> binary entirely. Until then, the attestation + PoP layers protect against abuse.

> ⚠️  The signing cert digests above are **optional** at registration time.
> Today (FR-AUTH-002) the admin registers with just `platform` + `package_id`.
> When they add FR-AUTH-003 attestation, they re-register (or update) the app
> to include `signing_cert_sha256` from the Play Console / App Store Connect.

### What about the "10 apps" scenario?

If a bank has 10 mobile apps (consumer Android, consumer iOS, agent Android, agent iOS,
merchant Android, merchant iOS, etc.), they provide you 10 `package_id`s — each with
an optional `signing_cert_sha256` if they want attestation-level protection. You
register 10 rows. But the bank still gets **one** `client_id`/`client_secret`
from `tenant_configurations` — all 10 apps use the same credential, and you tell them
apart by `package_id` at the token endpoint:

```csharp
// In IssueClientCredentialsTokenCommandHandler — this already exists from FR-AUTH-002:
if (!string.IsNullOrWhiteSpace(request.PackageId))
{
    var allowed = await _applications
        .IsAllowedAsync(active.TenantId, request.PackageId, ct);

    if (!allowed)
    {
        // → 401 invalid_client, audit reason: package_mismatch
    }
}
```

During enrollment, your `EnrollDeviceCommandHandler` additionally checks that the
**verdict's `package_name` + `signing_cert_sha256`** match the registered
`tenant_applications` row. This is what stops a repackaged APK (modified code, different
signing cert) from enrolling — FR-AUTH-002's `package_id`-only check can't catch that.

### What about apps that only run on one platform?

The admin creates **one row per platform they actually ship**. You never create
empty placeholder rows for platforms you don't use:

| Bank ships | Admin creates |
|---|---|
| Android only (`com.dhakabank.app`) | 1 row: `(ANDROID, com.dhakabank.app, sha=9e3a1b…)` |
| iOS only (`com.dhakabank.app`) | 1 row: `(IOS, com.dhakabank.app, sha=f7c8d2…)` |
| Both platforms | 2 rows: one ANDROID, one IOS (same `package_id`, different `signing_cert_sha256`) |

The API enforces this — `platform` is a **required** field on `POST .../applications`:

```csharp
// The admin must explicitly choose ANDROID or IOS at registration:
{ "platform": "ANDROID", "package_id": "com.dhakabank.app" }
```

If the bank forgets to register the iOS row and their iOS app tries to enroll,
the attestation verifier looks up `(tenant_id, IOS, com.dhakabank.app)` → no row
found → **401 enrollment rejected**. This is intentional: it prevents the bank
from shipping an app that can't pass attestation.

### What about "our SDK"?

There is **no SDK in this repository.** The QR generation/verification and attestation
endpoints here are your **backend HTTP APIs** — the server side. But your platform team
will also build a **mobile SDK** (a separate artifact, published to the FIs) that wraps
these HTTP calls so FIs don't write raw `curl`-equivalent networking code in their app.

Here is what that SDK looks like and how it fits:

#### What the SDK is

A **native library** (Android `.aar` + iOS `.xcframework`) that your team builds and
ships to each FI. The FI drops it into their mobile app project. It is NOT in this
`.NET` repo — it's a separate `Android`/`iOS` project your mobile team owns.

#### What the SDK does (the 4 steps from FR-AUTH-003 §4.6)

```
FI's mobile app                        Your mobile SDK                        Your .NET backend
   │                                      │                                      │
   │  SDK.init(clientId, clientSecret,    │                                      │
   │           tenantId)                   │                                      │
   │                                      │                                      │
   │  sdk.generateQr(name, account, ...) ─────────────────►                        │
   │                                      │                                      │
   │                                      ├── GET /v1/oauth/attestation-nonce      │
   │                                      │   (with client_id + client_secret)     │
   │                                      │◄──────── { nonce: "..." }              │
   │                                      │                                      │
   │                                      ├── Ask Google Play Integrity API        │
   │                                      │   (or Apple App Attest) for a verdict   │
   │                                      │   using the nonce                      │
   │                                      │   (This is platform code, not your API) │
   │                                      │                                      │
   │                                      ├── POST /v1/oauth/device-enrollment     │
   │                                      │   { verdict, device_public_key, ... }  │
   │                                      │◄── { device_id, access_token, ... }     │
   │                                      │                                      │
   │                                      ├── POST /v1/oauth/token                 │
   │                                      │   (PoP-signed with device private key) │
   │                                      │◄── { access_token (with cnf claim) }   │
   │                                      │                                      │
   │                                      ├── POST /v1/qr/generate/static           │
   │                                      │   Authorization: Bearer <token>        │
   │                                      │   X-Signature: v1=<device-signed-challenge>│
   │                                      │   X-Timestamp: <unix-seconds>          │
   │                                      │   { name, account, amount }            │
   │                                      │◄── { qr_payload: "..." }               │
   │                                      │                                      │
   │◄──  { qr_payload: "..." }           │                                      │
```

#### What the SDK handles internally (the FI never sees this)

1. **Device keypair generation** — calls Android Keystore / iOS Secure Enclave to generate
   an asymmetric key pair. The private key never leaves the secure hardware.
2. **Attestation flow** — fetches the nonce from your `GET /v1/oauth/attestation-nonce`,
   passes it to Google/Apple's attestation API, then sends the verdict to your
   `POST /v1/oauth/device-enrollment`.
3. **PoP signing** — on every API call, signs `METHOD + PATH + TIMESTAMP + BODY_HASH`
   with the device private key and adds the `X-Signature` header. The FI's app code just
   calls `sdk.generateQr(...)` — it has no idea about signatures.
4. **Token refresh** — silently re-attests after 7 days and gets a fresh token.
5. **Certificate pinning / CA blocking** — configures the HTTP client to only trust your
   backend's certificate, blocking user-installed CAs on rooted devices.

#### What the FI provides

- The `client_id` + `client_secret` (from your `tenant_configurations` table) — they
  embed it in their app binary (same as FR-AUTH-002 today).
- The `package_id` + optionally `signing_cert_sha256` (from the Play Console /
  App Store Connect) — they provide this to your ops team during onboarding.
  Today (FR-AUTH-002), only `package_id` is required. The `signing_cert_sha256`
  can be added later during app registration (it's an optional column).
- The app code that calls `sdk.generateQr(...)` / `sdk.validateQr(...)`.

#### What the SDK is NOT

- It is **not a .NET library.** It's native Android (Kotlin/Java) + iOS (Swift/Obj-C).
- It does **not** verify attestations — that's your .NET backend's
  `IAttestationVerifier` implementations.
- It is **not in this repository.** This repo is only the .NET backend. The SDK lives in
  a separate mobile project that your mobile team builds and distributes to FIs.

The only SDK-like piece in **this** repo is the `SimulatorAttestationVerifier` and the
QA verdict-generator helper — these exist solely so QA can simulate the mobile SDK's
behavior from Postman, without a real device.

#### The "one authenticator" in this picture

Your backend API is the **single authenticator** for every FI's app. Each FI integrates
your SDK, and the SDK calls your centralized HTTP endpoints. There is no per-FI backend
service — Dhaka Bank's app and BRAC Bank's app both talk to the **same** `/v1/qr/generate`
and `/v1/oauth/device-enrollment` endpoints. The `tenant_id` (derived from the
`client_id`/`client_secret` the FI embedded) keeps their data separate. The `package_id`
(and, if the admin registered it, the `signing_cert_sha256`) ensures Dhaka Bank's
app can't enroll using BRAC Bank's credentials.

---

## Who does what — mobile side vs. backend side

This is the crucial split. **The mobile SDK does the hard part** (talking to Google/Apple,
generating device keys in secure hardware). **Your .NET backend verifies the result** and
enforces the token binding. Here's who owns each step:

### At a glance

| Step | Mobile app (the SDK) | Your .NET backend |
|---|---|---|
| 0. Setup | *(your admin team registers the app; signing cert is optional)* | *(your admin endpoints)* |
| 1. Get nonce | Calls `GET /v1/oauth/attestation-nonce` | Issues a single-use random nonce |
| 2. Attestation | Calls Google/Apple API with the nonce → gets a signed verdict | *(nothing — the SDK holds this until step 3)* |
| 3. Enroll | Sends the verdict + device public key to `POST /v1/oauth/device-enrollment` | Verifies Google's signature, checks app match, saves device, issues token |
| 4. Use API | Sends the token + a signed `X-Signature` header on every call | Verifies the token AND the device-key signature, then runs the QR logic |
| 5. Re-attest | Every 7 days, repeats steps 1–3 | Updates the existing device row |

### Step by step — who touches what

#### Setup (admin registers the app)

```
Admin team
   │  POST /v1/admin/tenants/{id}/applications
   │  { platform, package_id, signing_cert_sha256? }
   ▼
Your .NET backend stores it in the tenant_applications table.
```

- **Mobile side:** Nothing. (The bank's ops team does this, not the app.)
- **Backend side:** Your `TenantApplicationsController` stores the app row. The
  `signing_cert_sha256` is optional — the admin can supply it (from the Play Console
  → App Integrity page) or leave it null for FR-AUTH-002-level protection. When
  present, this fingerprint becomes the "golden record" the attestation verifier
  compares against Google/Apple's verdict.

#### Step 1 — Get a nonce

```
Mobile SDK                  Your .NET backend
   │                              │
   ├── GET /v1/oauth/attestation-nonce (with client_id + client_secret) ──►
   │                              │
   │                              ├── Verify tenant credential (Argon2id)
   │                              ├── Generate random nonce string
   │                              ├── Store nonce (Redis/table, TTL 5 min, used=false)
   │                              └── Returns { "nonce": "x7y9..." }
   │◄───                          │
   │  { "nonce": "x7y9..." }
```

- **Mobile side:** The SDK calls your nonce endpoint with the same `client_id`/`client_secret`
  it uses for the token endpoint. It **holds onto the nonce** — it doesn't send it yet.
- **Backend side:** Same credential check as the existing token endpoint. Generate a
  cryptographically random string, store it with a TTL, return it. **Your code.**

#### Step 2 — Talk to Google / Apple (mobile side only)

```
Mobile SDK
   │
   ├── Calls Google Play Integrity API (Android) or Apple App Attest (iOS)
   │   Sends the nonce as the "challenge" / requestHash
   │
   │   Google/Apple does all this internally:
   │   - Checks the device isn't rooted/jailbroken
   │   - Confirms the app's package name matches
   │   - Confirms the app's signing certificate digest matches
   │   - Signs a verdict saying "this is the genuine app on this device"
   │
   │◄─── Returns a signed verdict (JWT on Android, CBOR on iOS)
```

- **Mobile side:** **This is entirely the SDK's job.** You do NOT write this code.
  The SDK library (that the FI integrates) handles it. It uses platform APIs (Google
  Play Integrity Task Manager, Apple DeviceCheck/App Attest framework).
- **Backend side:** Nothing. Your backend doesn't know this step happened. The SDK holds
  the signed verdict and sends it in the next step.

#### Step 3 — Enroll the device

```
Mobile SDK                       Your .NET backend
   │                                 │
   ├── POST /v1/oauth/device-enrollment ─────────────────────────►
   │  {
   │    "platform": "android",
   │    "package_id": "com.dhakabank.app",
   │    "attestation_verdict": "<base64 signed JWT from Google>",
   │    "device_public_key": "<PEM from device Keystore/Secure Enclave>"
   │  }
   │                                 │
   │                                 ├── Verify tenant credential (Argon2id — same as token endpoint)
   │                                 ├── Download Google's public key from the internet
   │                                 ├── Verify the verdict JWT signature with Google's key
   │                                 ├── Decode the JWT payload → AttestationVerdict record
   │                                 ├── Check: nonce matches + not expired + not reused
   │                                 ├── Check: package_name matches the request
   │                                 ├── Check: signing_cert_sha256 matches the row
   │                                 │   (FR-AUTH-003 — skipped if the column is NULL,
   │                                 │    i.e. app registered for package_id-only)
   │                                 ├── Check: app_recognition_verdict = PLAY_RECOGNIZED
   │                                 ├── If Mode=Soft and any check failed → log but allow
   │                                 ├── If Mode=Enforced and any check failed → 401
   │                                 ├── Create enrolled_devices row (device_id, public_key, verdict JSON)
   │                                 ├── Issue a JWT with cnf claim (device key thumbprint)
   │                                 └── Returns { device_id, access_token, token_type, expires_in }
   │◄──────────────────────────────── │
   │  { device_id, access_token (has cnf claim), ... }
```

- **Mobile side:** The SDK sends the Google-signed verdict (as a base64 string) plus the
  device's public key. The SDK generated the keypair in the device's secure hardware
  (Android Keystore / iOS Secure Enclave) — the private key **never leaves the device**.
  The SDK does NOT do verification — it just passes Google's verdict to your backend.
- **Backend side:** **This is your core work.** Your `IAttestationVerifier` does the
  cryptographic verification (download Google's public key, verify the JWT signature,
  check all fields). Your enrollment handler matches the verdict against the registered
  app, validates the nonce, saves the device, and issues the token. The key difference
  from the existing token endpoint: **the token now carries a `cnf` claim** (the device
  key thumbprint), and the `sub` is `device:{guid}` instead of `client:{client_id}`.

#### Step 4 — Use the API (every subsequent call)

```
Mobile SDK (each request)        Your .NET backend (each request)
   │                                │
   ├── POST /v1/qr/generate/static ─────────────────►
   │  Authorization: Bearer <token-with-cnf>
   │  X-Signature: v1=<sig>
   │  X-Timestamp: 1694726412
   │  Body: { name, account, amount }
   │                                │
   │                                ├── JWT validation (signature, expiry, issuer)
   │                                ├── Token has "cnf" claim → PoP required
   │                                ├── Look up device public key by thumbprint
   │                                ├── Reconstruct signed payload:
   │                                │   "POST /v1/qr/generate/static 1694726412 <body-hash>"
   │                                ├── Verify X-Signature with the device's PUBLIC key
   │                                ├── If invalid/missing → 401
   │                                ├── Run the QR generation logic
   │                                └── Returns { qr_payload: "..." }
   │◄─────────────────────────────── │
   │  { qr_payload: "..." }
```

- **Mobile side:** The SDK signs every request with the device's private key. It
  constructs the payload (`METHOD + PATH + TIMESTAMP + BODY_HASH`), signs it, and sends
  the signature in the `X-Signature` header. The private key is inside the phone's secure
  hardware — the SDK calls it via the OS keystore API, it never leaves the device.
- **Backend side:** Your PoP middleware (the `OnTokenValidated` event) runs on every
  request that carries a token with a `cnf` claim. It reconstructs the same payload,
  fetches the device's **public key** from `enrolled_devices`, and verifies the signature.
  If it fails → `401`. If it passes → the request proceeds to the QR generation handler.
  The existing `qr:generate` scope policy still applies — the PoP check is an **additional**
  layer on top.

#### Step 5 — Re-attestation (every 7 days)

```
Mobile SDK                  Your .NET backend
   │                              │
   ├── Same as steps 1-3 ─────────►
   │                              ├── Verifier checks the new verdict
   │                              ├── Confirms device_public_key matches
   │                              │   the existing enrolled_devices row
   │                              ├── Updates attestation_verified_at
   │                              │   + attestation_verdict on that row
   │                              └── Issues a fresh token
   │◄──────────────────────────── │
```

- **Mobile side:** After 7 days, the SDK silently re-runs the attestation flow
  (steps 1–3) to prove the app is still genuine on the same device.
- **Backend side:** Instead of creating a new `enrolled_devices` row, your handler finds
  the existing row by `device_id` (or by public key match) and **updates** the
  `attestation_verified_at` and `attestation_verdict` fields.

---

### The boundary: what the mobile SDK owns vs. what you own

```
┌──────────────────────────┬─────────────────────────────────────────────┐
│     Mobile SDK           │     Your .NET Backend                       │
├──────────────────────────┼─────────────────────────────────────────────┤
│ Android Keystore /       │ Your code verifies the Google-signed JWT    │
│ iOS Secure Enclave       │ (downloads Google's public key, checks      │
│ (generates + holds the   │ signature + all fields). This is the ONLY    │
│ private key — never       │ place you need Google/Apple knowledge.      │
│ leaves the device)       │                                             │
│                          │                                             │
│ Calls Google Play        │ Your code issues nonces, verifies verdicts, │
│ Integrity / App Attest   │ saves devices, issues tokens, validates     │
│ APIs with the nonce      │ PoP signatures.                             │
│                          │                                             │
│ Signs each request with  │ Your PoP middleware checks the signature    │
│ the device private key   │ on every protected request.                 │
│ (in secure hardware)     │                                             │
└──────────────────────────┴─────────────────────────────────────────────┘
```

**Memory trick:** The mobile SDK is like a **passport holder** — it has the physical
credentials (the device key in secure hardware) and gets them stamped (Google's verdict).
Your backend is the **border agent** — it checks the passport's authenticity (Google's
signature), looks the holder up in the guestbook (`enrolled_devices`), and stamps their
boarding pass (`access_token` with `cnf`) so every future entry requires both the passport
and the biometric scan (PoP signature).

---

