# FR-AUTH-003 — Security Architecture & VAPT Compliance

> **Part of the [FR-AUTH-003 dev guide](./FR-AUTH-003-dev-guide.md) series.**
> - [← Back to main guide](./FR-AUTH-003-dev-guide.md)
> - [QA Testing Guide](./FR-AUTH-003-qa-testing.md)
> - [Local Dev Guide](./FR-AUTH-003-local-dev.md)
> - [Production & Operations](./FR-AUTH-003-production-operations.md)
> - [Appendix: Payloads](./FR-AUTH-003-appendix-payloads.md)

---

## Who calls OAuth — and which endpoint they call

This is the single most confusing thing when reading this guide. There are
**two** OAuth token endpoints on `sbqr.service`, and they serve different flows:

| Endpoint | Used by | What it returns | When it's called |
|---|---|---|---|
| `POST /v1/oauth/token` | **Any** client with `client_id` + `client_secret` | Bearer token (no device binding) | **FR-AUTH-002 only** — legacy flow. Still exists for backward compatibility. |
| `GET /v1/oauth/attestation-nonce` → `POST /v1/oauth/device-enrollment` | The **SDK** embedded in the FI's app | JWT with `cnf` claim (device-bound) | **FR-AUTH-003 only** — the new flow. The enrollment endpoint **replaces** `POST /v1/oauth/token`. |

**The key fact:** In FR-AUTH-003, the app **does NOT call** `POST /v1/oauth/token`.
The `POST /v1/oauth/device-enrollment` endpoint handles credential verification
+ attestation verification + token issuance **in a single call**. The app
calls the nonce endpoint first (to get a challenge for Google/Apple), then calls
the enrollment endpoint (which returns the JWT). The old `POST /v1/oauth/token`
is only for apps that haven't migrated to attestation yet.

### Who calls OAuth, by architectural pattern

| Pattern | Who calls the OAuth endpoint? | Which endpoint? |
|---|---|---|
| **0 — Legacy (current)** | The SDK (in the FI's app) | `POST /v1/oauth/device-enrollment` directly |
| **1 — PKCE** | The SDK (in the FI's app) | `POST /v1/oauth/token` with PKCE code_verifier |
| **2 — BFF** | The FI's BFF (server-side) | `POST /v1/oauth/token` (with client_secret) |
| **3 — Gateway** | Your Gateway service | `POST /v1/oauth/token` (with client_secret) |

**What matters for your implementation:** You must support **both** endpoints:
- `POST /v1/oauth/token` — for BFF and Gateway patterns (server-to-server, client_secret is safe)
- `POST /v1/oauth/device-enrollment` — for direct SDK patterns (attestation + PoP)

The FI's choice of Pattern 0/1 vs. Pattern 2/3 determines which endpoint the
FI's infrastructure calls. Your backend supports both.

---

## The multi-app model — one tenant, many apps, one credential

This is the part that confuses most developers new to this domain. Let's make it concrete.

### The three entities

```
Tenant (bank)         Tenant Configuration         Tenant Applications
                                                         (one row per app)

Dhaka Bank             client_id: dhb-android-app    1. (ANDROID, "com.dhakabank.app")
                       client_secret: <argon2id hash>
                                                     2. (IOS, "com.dhakabank.app.ios")
                                                     3. (ANDROID, "com.dhakabank.agent")
                                                     4. (IOS, "com.dhakabank.agent.ios")

> ⚠️  The `signing_cert_sha256` column shown in some diagrams below is a
> FR-AUTH-003 addition (for attestation-level app integrity checking).
> The `tenant_applications` table (`008_tenant_applications.sql`) includes it
> as a **nullable** column — existing rows from FR-AUTH-002 don't need it.
> When FR-AUTH-003 is built, the registration endpoint populates it per app.
```

### Key rules

1. **One credential per tenant (in this system).** A tenant gets exactly one
   `client_id` + `client_secret` (stored in `tenant_configurations`, Argon2id-hashed).
   **All of the tenant's apps share that same credential.** The credential is baked
   into each app's binary.
   > **Why this is okay despite being non-standard — see the box below.**

2. **Multiple apps per tenant.** Each app the tenant ships gets its own row in
   `tenant_applications` (different `package_id`, different `platform`).
   `signing_cert_sha256` is an optional column — the tenant's admin provides it
   when they want attestation-level protection (FR-AUTH-003). If they leave it
   blank, you get FR-AUTH-002-level protection (package_id-only).

3. **The `package_id` is the discriminator.** When the app calls any of your endpoints,
   the `package_id` tells you **which app** it is. The `client_id`/`client_secret` tells
   you **which tenant** it is. Together they tell you: "Dhaka Bank's consumer Android app."

4. **Platform matters.** The same `package_id` string on Android and iOS is **two rows**
   (same string, different platform). A real FI often has both, and the iOS bundle ID
   might even differ from the Android `applicationId`.

#### ⚠️ Standard fintech practice vs. what this system does

| Industry standard (most banks today) | This system (single credential per tenant) |
|---|---|
| **Separate `client_id`+`client_secret` per app** (one for Android, one for iOS, one for web, etc.) | **One shared `client_id`+`client_secret` per tenant** — all apps share it |
| App identity comes from the client credentials | App identity from `package_id` (today) + optional `signing_cert_sha256` (FR-AUTH-003) in `tenant_applications` |
| Per-app secret rotation (rotate Dhaka Bank's Android app secret without touching iOS) | Rotate the shared tenant credential once — affects all apps at once |
| Fine-grained audit logs ("the Android app called X") via separate client_ids | Audit logs attribute by tenant + `package_id` (you read `package_id` from the PoP-signed request header) |
| Mobile apps typically use **PKCE** instead of `client_secret` (since the secret isn't secret in a binary) | Mobile apps send `client_id` + `client_secret` in the token request body (same as FR-AUTH-002) |

**Why this system gets away with one credential:** The security boundary here is **NOT** the
`client_secret`. It's the **PoP device signature + attestation verification**. Even if an
attacker extracts the shared `client_secret` from one app's binary, they still cannot:
- Generate a valid PoP signature (the device private key never leaves the secure hardware),
- Pass attestation verification (the verdict is tied to the specific `package_id` +
  `signing_cert_sha256` registered in `tenant_applications`).

So a stolen `client_secret` is useless without the device key. This is why a single
tenant-level credential is acceptable here. **If you later decide to follow the industry
norm**, you can split `tenant_configurations` into a per-app credential table — the
`package_id` allow-list in `IssueClientCredentialsTokenCommandHandler` already does the
app-level checks, so only the credential lookup would change.

> 📐 **See "Direct vs. Intermediary: The architectural choice" below** for the
> three standard fintech patterns (Direct + PKCE, BFF, Gateway) and a recommendation
> matrix based on your operational capacity and risk appetite.

#### Direct vs. Intermediary: The architectural choice

> **Important context:** Your team does **not** build the FI's mobile app. You
> build an **SDK** (Android `.aar` + iOS `.xcframework`) that the FI embeds into
> **their existing app**. The FI's app already exists in the Play Store / App
> Store. Your SDK is a library they drop into their project — you control its
> code, but the **app binary is theirs**. This means: **whatever secrets your SDK
> causes to end up in the binary are your responsibility for VAPT purposes.**

This section answers the question you asked: **"Should the SDK embed
`client_id` + `client_secret` and call `sbqr.service` directly — or should there
be an intermediary layer (BFF, gateway, etc.)?"**

Here are the **three standard patterns** used in production fintech today, plus
the **legacy pattern** that this system currently uses. Each is shown with its
security properties and trade-offs.

##### Pattern 0 — Legacy: Direct call with embedded `client_secret`

*(This is what the system currently does. Read this first, then the others.)*

```
┌──────────────────┐       ┌──────────────────────┐
│  FI's App        │       │  sbqr.service        │
│  (binary)        │       │  (.NET backend)      │
│  ─ embeds ─       │       │                      │
│  client_id        │       │                      │
│  client_secret    │       │                      │
│  ──── calls ────►│       │                      │
│  POST /oauth/token│       │  verifies            │
│  POST /device-enroll│    │  secret via Argon2id  │
│  POST /qr/generate│      │  runs QR logic       │
└──────────────────┘       └──────────────────────┘
    │
    │  (your SDK is linked in here)
```

- **What's embedded in the FI's binary:** both `client_id` **and** `client_secret`
  — because your SDK tells the FI to put them there.
- **The VAPT catch:** When VAPT decompiles the FI's app (which contains your SDK),
  they find the `client_secret` in the strings. The finding goes on the FI's
  report, but the root cause is your SDK's design. The FI will push back on you
  to fix it.
- **Why the industry moved away from this:** OWASP MASVS explicitly lists
  "hardcoded credentials" as a **critical** issue for mobile apps. The secret
  in a binary is not secret.
- **Security properties:** The `client_secret` is extractable from the binary
  (disassemblers, memory dumps). Anyone who decompiles the APK can read it.
- **Why the industry moved away from this:** OWASP MASVS explicitly lists
  "hardcoded credentials" as a **critical** issue for mobile apps. The secret
  in a binary is not secret.
- **Why *this* system tolerates it:** The `client_secret` is the **tenant
  identifier**, not the app authenticator. The real protection is the **attestation
  verdict** (proves the app is genuine and unmodified) + the **PoP device key**
  (proves the request comes from a specific enrolled device). A stolen secret
  is useless without both of those.

| Pro | Con |
|---|---|
| Simplest architecture — no extra layer | `client_secret` is extractable from the APK |
| Zero additional infrastructure | Can't rotate per-app (only per-tenant) |
| QA can test everything with curl | App can't be revoked without rotating tenant secret |

##### Pattern 1 — SDK with PKCE (the current industry standard for mobile apps)

```
┌──────────────────┐       ┌──────────────────────┐
│  FI's App        │       │  sbqr.service        │
│  (public client) │       │  (.NET backend)      │
│  ─ embeds ─       │       │                      │
│  client_id ONLY   │       │                      │
│  ──── calls ────►│       │                      │
│  1. GET /authorize?response_type=code&code_challenge=X │  issues auth code |
│  2. POST /oauth/token  │  │  verifies code_challenge |
│    (PKCE code_verifier)│  │  issues access_token   |
│  3. POST /qr/generate │  │  validates token       |
└──────────────────┘       └──────────────────────┘
    │  (your SDK handles PKCE + attestation + PoP internally)
```

- **What's embedded in the FI's binary:** only `client_id` (a public identifier,
  like a username — it's fine to be in the binary). **Your SDK** handles the PKCE
  code challenge/verifier exchange internally — the FI doesn't even know about it.
- **The `client_secret` never lives in the app.** Instead, the app proves its
  identity by demonstrating it can produce the PKCE code verifier that matches
  the code challenge it sent earlier. The SDK generates the `code_verifier`/`code_challenge`
  pair per request.
- **Security properties:** Even if an attacker decompiles the FI's APK, they get
  only the `client_id`. The `code_challenge` changes every request, so a stolen
  challenge is useless. **VAPT will pass this check.**
- **What this means for your SDK design:** Your SDK's initialization call
  takes `Sdk.init(clientId: "dhb-android-app")` — no secret parameter. The SDK
  itself enforces PKCE on every token request. You must build this into the SDK
  before the next app release.
- **Where `client_id` is registered:** In `tenant_configurations`, you'd mark
  the client as `"client_type": "public"` so the token endpoint knows not to
  require a `client_secret` and instead enforces PKCE.

##### Pattern 2 — Backend-for-Frontend (BFF) — used by most Tier-1 banks

```
┌──────────────────┐       ┌──────────────────┐       ┌──────────────────────┐
│  FI's App        │       │  BFF (your FI's  │       │  sbqr.service        │
│  (no secret)     │       │  backend)        │       │  (central .NET API)  │
│  ── calls ─────►│       │  ── has secret ──►│       │                      │
│  POST /bff/      │       │  POST /oauth/    │       │  POST /oauth/        │
│    generate-qr    │       │    token          │       │    token             │
│                  │       │  POST /qr/       │       │  POST /qr/           │
│                  │       │    generate        │       │    generate          │
└──────────────────┘       └──────────────────┘       └──────────────────────┘
    │  (your SDK talks to the FI's BFF, never sees the client_secret)
```

- **What's in the FI's binary:** **nothing secret.** Your SDK talks to the FI's
  own BFF (Backend-for-Frontend). The BFF holds the `client_id` + `client_secret`.
- **Your SDK's role:** The SDK is configured at init with a BFF endpoint URL
  (e.g., `Sdk.init(bffUrl: "https://api.dhakabank.com/bff")`). The SDK handles
  attestation and PoP signing internally, but delegates token management to the
  BFF. The BFF exchanges the user's session for a `client_id`/`client_secret`
  call to `sbqr.service`.
- **The FI's app authenticates to the BFF** via a session cookie, a mutual-TLS
  certificate, or a pinned API key that the BFF issues after the user logs in.
- **VAPT perspective:** The FI's binary has no extractable secret. VAPT shifts to
  testing the FI's BFF — but that's the FI's own infrastructure, not your SDK.
- **This is the pattern used by:** JPMorgan Chase, Bank of America, Wells Fargo,
  and most large banks. It's also the pattern behind the PSD2 "redirect-based
  SCA" flows in Europe.
- **Trade-off:** Adds a layer of infrastructure. The FI must operate + secure a BFF.
  But you (the SDK provider) are off the hook — VAPT finds no secrets in the
  app binary because your SDK doesn't embed them.

| Pro | Con |
|---|---|
| No secrets in the mobile binary at all | Each FI must operate + secure a BFF |
| Fine-grained per-app credentials | Adds latency (one extra hop) |
| Auditable server-to-server calls | More infrastructure for the FI to maintain |
| Standard for Tier-1 banks | The FI's BFF becomes an attack surface |

##### Pattern 3 — API Gateway / Token Exchange (platform-as-a-service model)

```
┌──────────────────┐       ┌──────────────────────┐       ┌──────────────────────┐
│  Mobile App      │       │  Gateway / Token     │       │  sbqr.service        │
│                  │       │  Exchange Service    │       │  (.NET backend)      │
│  ── no secret ──►│       │  (runs YOUR infra)   │       │                      │
│  POST /api/      │       │  ── has secret ──►  │       │  POST /qr/generate   │
│    generate-qr    │       │  POST /oauth/token   │       │                      │
│  (PKCE or mTLS)   │       │  (exchanges for       │       │                      │
│                  │       │   tenant credential) │       │                      │
└──────────────────┘       └──────────────────────┘       └──────────────────────┘
```

- **The gateway runs in YOUR infrastructure** (not the FI's). The mobile app
  authenticates to your gateway using PKCE (Pattern 1) or a device certificate.
- The gateway **exchanges** the app's proof-of-possession for the tenant's
  `client_id` + `client_secret`, then calls `sbqr.service` internally.
- **Security properties:** Secrets are centralized in your infrastructure,
  not embedded in any mobile binary. You can rotate, revoke, and monitor at the
  gateway level. This is the pattern used by Plaid, Stripe, and other financial
  API providers.
- **Trade-off:** Requires you to operate the gateway. But it's the most
  scalable pattern for a multi-tenant SaaS platform.

| Pro | Con |
|---|---|
| No secrets in any mobile binary | You must operate + secure the gateway |
| Central credential management | Additional infrastructure to build/test |
| Works for ALL FIs automatically | Latency depends on gateway location |

---

##### Which pattern does *this* system use, and which should you pick?

**Currently:** Pattern 0 (Legacy — embedded `client_secret`). This was chosen
because the system already existed as FR-AUTH-002 before FR-AUTH-003 was
designed. The user's constraint was "don't change code."

**Recommended for a new implementation:** It depends on your risk appetite
and operational capacity:

| If your situation is… | Choose… |
|---|---|
| You are a small platform with many small FIs who can't operate their own backends | **Pattern 1 (Direct + PKCE)** — it's the simplest fix: just stop embedding the secret, enable PKCE on the token endpoint, and mark clients as `public`. The SDK handles the code challenge/verifier. |
| You serve Tier-1 banks that have their own security teams | **Pattern 2 (BFF)** — hand them a BFF template; they run it, you document the contract. |
| You want a multi-tenant SaaS platform (like Stripe/Plaid) | **Pattern 3 (Gateway / Token Exchange)** — build the gateway once, serve everyone. |
| You have an existing app that already embeds the secret and can't do an app-store release immediately | **Keep Pattern 0 temporarily**, but migrate. The PoP + attestation layer you're building *is* the mitigation for the leaked secret. Just don't ship new apps with this pattern. |

**Bottom line:** Embedding `client_secret` in a mobile binary is **not** standard
fintech practice today. The industry standard is PKCE for public clients (Pattern
1). For a platform serving multiple FIs, a gateway or BFF layer (Patterns 2/3)
centralizes secrets where they can be properly secured. This system's current
Pattern 0 works *only because* the attestation + PoP layers make a stolen secret
harmless — treat that as a transitional state, not a final design.

##### VAPT compliance implications

Every pattern above must pass **VAPT testing** (Vulnerability Assessment and
Penetration Testing). VAPT firms will test every endpoint you expose, try to
extract secrets from the mobile binary, replay requests, bypass attestation,
and forge PoP signatures. Here is how each architectural choice maps to the
VAPT report you want to avoid:

| Pattern | Highest-risk VAPT finding | Mitigation | Is this enough for VAPT sign-off? |
|---|---|---|---|
| **0 — Embedded secret** | *"Critical: client_secret extractable from APK/IPA via static analysis"* | PoP device binding + attestation verdict | **No.** VAPT decompiles the FI's app (which contains your SDK), finds the `client_secret` in the strings dump, and flags it as critical. You'll get a "needs remediation" with 90-day deadline. |
| **1 — PKCE** | *"Informational: public client identifier present (expected)"* | PKCE per RFC 7636; code_challenge rotation per request | **Yes, for the auth flow.** VAPT accepts PKCE as a control. VAPT will still test the attestation + PoP layers for bypass. |
| **2 — BFF** | *"High: no secrets in mobile binary; BFF lacks mTLS to gateway"* | Mutual TLS between FI's BFF and sbqr.service; IP allow-listing | **Yes**, if the FI enforces mTLS on their BFF. Your SDK never touches a secret. |
| **3 — Gateway** | *"High: gateway token-exchange endpoint lacks rate limiting"* | Rate limiting + nonce single-use + token TTL | **Yes,** but the gateway (which runs in your infra) is in-scope. VAPT will spend most time here. |

> **SDK-specific note:** In Patterns 1–3, the secret is NOT in the FI's app binary.
> But VAPT will still inspect your SDK's published `.aar`/`.xcframework` — if your
> SDK's source code contains a hardcoded `client_secret` string, they'll find it
> in the compiled library too. The rule: **your SDK must NOT contain a static secret.**

**What VAPT testers actually do (and how your SDK design determines the outcome):**

1. **Static analysis of the mobile app.** The tester decompiles the FI's APK with
   `apktool` or `jadx` and searches for strings matching `client_secret`,
   `secret`, `password`, or base64-encoded blobs longer than 32 chars.
   - **Pattern 0 (your SDK embeds the secret):** They find it **in your SDK's
     code, compiled into the FI's binary.** The report says "Critical: hardcoded
     credential in mobile binary, found in [your SDK library name]." The FI will
     refuse to ship your SDK.
   - **Pattern 1 (SDK uses PKCE):** They find only `client_id`. VAPT records
     this as "Informational: public client identifier present" — expected for
     native apps, not a finding.
   - **Pattern 2 (SDK uses BFF):** Nothing secret in the binary at all. The tester
     notes "No secrets found in static analysis" — clean sign-off for the mobile
     side.

2. **Token replay / PoP bypass.** The tester captures a valid access token (via
   proxy or memory dump) and tries to reuse it from a different device or without
   the `X-Signature` header.
   - All patterns: **Your PoP middleware blocks them** (the signature won't
     verify without the device's private key). This is the control VAPT wants
     to see working.

3. **App attestation bypass.** The tester tries to craft a fake attestation
   verdict, reuse a nonce, or enroll a device without passing through
   Google/Apple.
   - Your `IAttestationVerifier` must verify the Google/Apple signature, and
     your `EnrollDeviceCommandHandler` must enforce nonce single-use.
   - **VAPT will write a test case for each of these — they must return 401.**

4. **Signing certificate mismatch.** The tester repackages the FI's APK with
   their own signing key and tries to enroll.
   - Your enrollment handler must compare the verdict's
     `signing_cert_sha256` against the registered `tenant_applications` row.
   - If the `signing_cert_sha256` column is NULL (FR-AUTH-002 mode), enrollment
     succeeds — VAPT will note this as a **medium finding**: "attestation
     integrity not enforced for this app."

5. **SDK library inspection (Patterns 1–3 only).** VAPT may also decompile your
   published `.aar`/`.xcframework` to check for:
   - Hardcoded URLs, keys, or credentials
   - Missing certificate pinning (the SDK should pin your `sbqr.service` cert)
   - Weak random number generation (use `SecureRandom` on Android, `SecRandomCopyBytes` on iOS)
   - Missing attestation on non-hardware-backed devices

**What VAPT compliance means for your roadmap:**

- **If you keep Pattern 0:** You will **not pass VAPT.** The FI's VAPT report
  will flag the `client_secret` as a critical finding, and the FI will refuse to
  ship your SDK. Every major auditor (PCI DSS, ISO 27001, SOC 2, local
  central-bank compliance) treats embedded secrets in mobile binaries as
  critical. The PoP + attestation layer mitigates the *impact* of the stolen
  secret, but the *finding itself* (static secret in binary) remains.
- **If you move to Pattern 1 (PKCE):** Your SDK passes VAPT's static analysis.
  The remaining scope is the attestation + PoP layers — which is where the real
  security battle is (and where your Ed25519 signing keys and
  `IAttestationVerifier` implementation get tested).
- **If you move to Pattern 2 (BFF):** You produce a clean SDK that contains
  no secrets at all. VAPT scope for the mobile side is zero findings. The FI
  is responsible for securing their own BFF.
- **If you move to Pattern 3 (Gateway):** You produce a clean SDK + you operate
  a hardened gateway. VAPT will focus on the gateway's token-exchange logic,
  rate limiting, and mTLS enforcement.

**Recommendation for VAPT:** Regardless of which pattern you ultimately choose,
the following are **non-negotiable** for compliance sign-off:

| # | Requirement | What it means for your code |
|---|---|---|
| 1 | **No static secrets in your SDK** | Your SDK must NOT contain a hardcoded `client_secret`. Move to PKCE (Pattern 1) or BFF/Gateway (Patterns 2/3) before the next SDK release. VAPT will decompile your `.aar` and the FI's app. |
| 2 | **Nonce single-use** | The `IssueAttestationNonceCommandHandler` must mark the nonce as `used=true` after it's consumed by enrollment. VAPT will test by replaying a used nonce. |
| 3 | **Attestation signature verification** | Your `IAttestationVerifier` implementations must verify the Google/Apple signature using a certificate fetched from Google/Apple's official endpoints (not a hardcoded key). |
| 4 | **PoP signature on every API call** | The `OnTokenValidated` middleware must reject any request that has a `cnf` claim but no valid `X-Signature`. VAPT will test by dropping the header. |
| 5 | **Signing cert verification** | If `signing_cert_sha256` is populated for an app, the enrollment handler **must** compare it against the verdict. VAPT will test by repackaging the app. |
| 6 | **Token TTL + refresh bounds** | Access tokens must expire (e.g., 10 minutes). Device re-attestation must occur at a bounded interval (e.g., 7 days). VAPT will test token validity beyond these bounds. |

> 💡 **Pro tip from the VAPT world:** When you engage your testing firm, give
> them the **QR code payloads** from the spec's Annex A test vectors. VAPT firms
> charge by the day — if they can immediately verify that your CRC computation
> matches the spec's byte-for-byte example, they'll sign off on the payload
> integrity section in hours instead of days. Likewise, give them a test
> `client_id`/`client_secret` and a test attestation verdict from the simulator
> so they don't have to figure out your auth flow from scratch.



> "I have a consumer Android app (`com.dhakabank.consumer`), a consumer iOS app
> (`com.dhakabank.consumer-ios`), an agent Android app (`com.dhakabank.agent`), and a
> merchant app on both platforms. I want **one** credential for all four so my users
> see one integration — is that supported?"
> Answer: **"Yes — one tenant credential, multiple app registrations."** (One
> `client_id`/`client_secret` from `tenant_configurations`; a separate row in
> `tenant_applications` for each `package_id`.)
