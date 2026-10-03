# FR-AUTH-003 — A Plain Guide for .NET Backend Developers

> **SUPERSEDED (2026-10-03):** FR-AUTH-003 was built on the FR-AUTH-002
> `tenant_applications` allow-list, which was removed when the token flow moved
> to server-to-server (see the banner on `FR-AUTH-003-mobile-app-attestation.md`).

> This is for .NET backend developers who want to know: **what do I actually build, and how
> does it work in production?** No fintech jargon. Just the code and the real-world flow.
>
> This complements the full spec: [FR-AUTH-003-mobile-app-attestation.md](FR-AUTH-003-mobile-app-attestation.md)

---

## The problem in one paragraph

Your backend has an API. A mobile banking app calls it. Right now, the app sends a
`client_id` and `client_secret` to `POST /v1/oauth/token` to get a bearer token. That
secret is **hardcoded inside the app binary** — it ships to every user's phone. Anyone who
decompiles the app (or just reads the APK) gets the secret and can call your API from
Postman as if they were the real app. There is **no way to tell** a real app from a script.

> **Two OAuth endpoints exist on `sbqr.service`:**
> - `POST /v1/oauth/token` — the **existing** FR-AUTH-002 token endpoint (client credentials
>   grant). Still used for non-device-bound clients (BFFs, gateways, server-to-server).
> - `POST /v1/oauth/device-enrollment` — the **new** FR-AUTH-003 token endpoint. It replaces
>   `POST /v1/oauth/token` for mobile apps. It verifies credentials + attestation verdict +
>   issues a JWT with a `cnf` (device-binding) claim — all in one call.
>
> **In FR-AUTH-003, the app calls `/attestation-nonce` first, then
> `/device-enrollment` — it does NOT call `/oauth/token`.**
> See [Security Architecture: Who calls OAuth](./FR-AUTH-003-security-architecture.md) for
> the full breakdown by pattern.

FR-AUTH-003 adds two layers of proof:

1. **"Are you the real app?"** — the app must prove Google/Apple verified it's the genuine,
   unmodified app on a real device.
2. **"Are you on the same phone?"** — once enrolled, the token is locked to that specific
   device. A stolen token can't be replayed from Postman because Postman doesn't have the
   device's private key.

---

## Think of it like a club bouncer

Imagine your API is a club. Right now:

- Every member gets the same password ("open sesame").
- To get in, you say the password at the door.
- **The problem:** anyone who overhears the password gets in. The password is printed
  inside the member's ID card, which is just a piece of plastic anyone can photocopy.

FR-AUTH-003 changes the system:

- **Step 1 (get a challenge number):** The bouncer gives you a random number. "Write this
  number on your application."
- **Step 2 (get the app to prove itself):** Google looks at your phone, sees you installed
  the real, unmodified banking app, and signs a piece of paper that says "yes, this phone
  is running com.yourbank.app, and the signing certificate matches, and the user wrote
  challenge number 12345." Google's signature can't be forged.
- **Step 3 (show the signed paper):** You hand the signed paper to the bouncer. The bouncer
  verifies Google's signature, checks the challenge number matches, checks the app name
  and signing certificate against the list of approved apps, and — if it all checks out —
  writes your name in the book and gives you a device-specific key fob.
- **Step 4 (every future request):** From now on, when you want to enter, you don't just
  say the password. You also use your key fob to sign today's date. The bouncer checks:
  "Is this signature from a registered key fob?" If yes, you're in. If someone stole your
  password but doesn't have your key fob, they're turned away.

---


---

> 📚 **This guide is split into 6 files.** Use the one that matches your
> current need:
>
> | File | Who is it for? | What's inside |
> |---|---|---|
> | **This file** | Everyone | Orientation, problem, checklist, quick reference |
> | [Security Architecture & VAPT](./FR-AUTH-003-security-architecture.md) | Architects, security, backend devs | Multi-app model, **who calls OAuth**, Pattern 0/1/2/3, VAPT compliance, onboarding rules |
> | [QA Testing Guide](./FR-AUTH-003-qa-testing.md) | QA engineers, test automation | 8 test scenarios, environment setup, config matrix |
> | [Local Dev Guide](./FR-AUTH-003-local-dev.md) | .NET backend implementers | 8-step implementation guide, code snippets, simulator usage |
> | [Production & Operations](./FR-AUTH-003-production-operations.md) | DevOps, FIs, onboarding teams | Production journey, onboarding flow, SDK model, who-does-what |
> | [Appendix: Payloads](./FR-AUTH-003-appendix-payloads.md) | Backend devs, auditors | Full HTTP request/response bodies for every endpoint |

## What you build — the .NET backend API developer's checklist

You write the "bouncer" code. Here is every piece, in order.

### 1. A database table for enrolled devices

**File: `db/migrations/009_device_attestation.sql`** — one new table:

```sql
-- One row per enrolled phone/tablet.
CREATE TABLE public.enrolled_devices (
  device_id              uuid PRIMARY KEY,           -- the device's unique id
  tenant_id              uuid NOT NULL,              -- which bank owns this app
  tenant_application_id  uuid NOT NULL,              -- which app registration
  platform               varchar(10) NOT NULL,       -- 'android' or 'ios'
  device_public_key      text NOT NULL,              -- the device's public key (PEM)
  attestation_verdict    jsonb NOT NULL,             -- the last verified Google/Apple proof
  attestation_verified_at timestamptz NOT NULL,
  assertion_counter      bigint NOT NULL DEFAULT 0,  -- iOS replay guard
  status                 varchar(20) NOT NULL DEFAULT 'ACTIVE',
  is_active              boolean      NOT NULL DEFAULT TRUE,
  created_by             varchar(100), created_at     timestamptz NOT NULL DEFAULT now(),
  modified_by            varchar(100), modified_at    timestamptz
);
```

The `signing_cert_sha256` column on `tenant_applications` was already added in
**migration `008_tenant_applications.sql`** (as a nullable column, so existing
rows don't break). When FR-AUTH-003 ships, the admin populates it during app
registration from the Play Console / App Store Connect.

### 2. Two new API endpoints

> 🔗 **Related:** [Who calls OAuth?](./FR-AUTH-003-security-architecture.md#who-calls-oauth)
> — `POST /v1/oauth/device-enrollment` replaces the old `POST /v1/oauth/token` for
> mobile apps. The old endpoint stays for BFF/gateway patterns.

You add these to `OAuthController.cs` (or a new controller in the same IdentityAccess
module):

```csharp
// Step 1: the app calls this to get a random "challenge number"
[HttpGet("v1/oauth/attestation-nonce")]
public async Task<IActionResult> GetNonce()

// Step 2 + 3: the app calls this with Google/Apple's signed proof + its public key
[HttpPost("v1/oauth/device-enrollment")]
public async Task<IActionResult> EnrollDevice(EnrollDeviceRequest request)
```

### 3. One verification interface, three implementations

You define ONE interface:

```csharp
public interface IAttestationVerifier
{
    Task<AttestationVerdict> VerifyAsync(
        string platform,      // "android" or "ios"
        string rawVerdict,    // the signed proof from Google/Apple (base64 string)
        string nonce,         // the challenge number you issued
        CancellationToken ct);
}
```

Then you write three implementations:

| Class | What it does | When it's used |
|---|---|---|
| `GooglePlayIntegrityVerifier` | Downloads Google's public signing key from the internet, verifies the proof is really from Google, checks the app name and signing certificate match, checks the challenge number is correct and recent. | Production (Android apps) |
| `AppleAppAttestVerifier` | Downloads Apple's root certificate, validates the proof, checks the assertion counter to prevent replays. | Production (iOS apps) |
| `SimulatorAttestationVerifier` | Verifies proofs signed by a test key that **you** control. Used only in QA/staging so testers can use Postman. | QA/staging only |

### 4. The enrollment handler — the bouncer's logic

File: `src/Modules/IdentityAccess/Application/Commands/EnrollDevice/EnrollDeviceCommandHandler.cs`

This handler does exactly what the bouncer does:

```csharp
public async Task<Result<EnrollDeviceResult>> Handle(EnrollDeviceCommand cmd, CancellationToken ct)
{
    // --- 4. Verify the app knows the tenant's secret (same as the token endpoint today) ---
    var credential = await _configurations.GetByClientIdAsync(cmd.ClientId, ct);
    if (!VerifySecret(cmd.ClientSecret, credential))
        return Failure("invalid_client");   // same 401 error as everything else

    // --- 5. Verify Google/Apple's signature on the proof ---
    AttestationVerdict verdict;
    try
    {
        verdict = await _verifier.VerifyAsync(cmd.Platform, cmd.AttestationVerdict, cmd.Nonce, ct);
    }
    catch
    {
        return Mode == AttestationMode.Soft
            ? LogAndAllow("verdict_invalid", cmd)    // QA: log it but let it through
            : Failure("invalid_client");              // Production: hard reject
    }

    // --- 6. Check the app name + signing cert against the registered list ---
    var app = await _applications.GetByPackageAndCertAsync(
        credential.TenantId, verdict.PackageName, verdict.SigningCertSha256, ct);
    if (app is null)
    {
        return Mode == AttestationMode.Soft
            ? LogAndAllow("verdict_app_mismatch", cmd)
            : Failure("invalid_client");
    }

    // --- 7. Check the nonce hasn't been used (single-use) and isn't expired ---
    if (!await NonceIsValidAsync(cmd.Nonce, ct))
    {
        return Mode == AttestationMode.Soft
            ? LogAndAllow("nonce_reused", cmd)
            : Failure("invalid_client");
    }

    // --- 8. Save the device, issue a token ---
    var device = new EnrolledDevice(
        tenantId: credential.TenantId,
        tenantApplicationId: app.Id,
        platform: cmd.Platform,
        devicePublicKeyPem: cmd.DevicePublicKeyPem,
        attestationVerdictJson: JsonSerializer.Serialize(verdict),
        verifiedAt: verdict.IssuedAt,
        counter: verdict.AssertionCounter);

    await _devices.AddAsync(device, ct);
    await _uow.SaveChangesAsync(ct);

    // Issue a token that carries the device's key thumbprint in a "cnf" claim.
    // The "cnf" claim is what makes the token PoP-bound (see step 5 below).
    var token = _issuer.Issue(new AccessTokenClaims(
        Subject: $"device:{device.Id}",
        TenantId: credential.TenantId,
        Scopes: new[] { "qr:generate", "qr:validate" },
        CnfThumbprint: ComputeThumbprint(cmd.DevicePublicKeyPem)));

    return Ok(token);
}
```

### 5. Proof-of-possession (PoP) validation on every protected request

After enrollment, the mobile app gets a token. But this token is **bound to the device's
private key** (which only lives on the user's phone). On every subsequent API call, the app
must also send a signed challenge header:

```
X-Signature: v1=<base64 signature>
```

The signature is over: `HTTP_METHOD + PATH + TIMESTAMP + BODY_HASH`, signed with the
device's private key.

You add this as a JWT validation event (in `IdentityAccessModule.cs`):

```csharp
options.Events = new JwtBearerEvents
{
    OnTokenValidated = ctx =>
    {
        var token = ctx.Principal;
        var cnfClaim = token.FindFirst("cnf")?.Value;

        // If the token has a "cnf" claim, the caller MUST prove they have the
        // device's private key by sending a valid X-Signature header.
        if (cnfClaim is not null)
        {
            var signature = ctx.HttpContext.Request.Headers["X-Signature"].FirstOrDefault();
            if (string.IsNullOrEmpty(signature))
            {
                ctx.Fail(new SecurityTokenException("PoP signature required")); // 401
                return Task.CompletedTask;
            }

            // Reconstruct what the app should have signed: method + path + timestamp + body hash
            var expectedPayload = $"{ctx.HttpContext.Request.Method} " +
                                  $"{ctx.HttpContext.Request.Path} " +
                                  $"{ctx.HttpContext.Request.Headers["X-Timestamp"]} " +
                                  $"{ComputeBodyHash()}";

            var devicePublicKey = ResolveDevicePublicKey(cnfClaim); // lookup from enrolled_devices
            if (!VerifySignature(devicePublicKey, expectedPayload, signature))
            {
                ctx.Fail(new SecurityTokenException("Invalid PoP signature")); // 401
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }
};
```

**Effect:** If someone steals a token and tries to use it from Postman:
- Postman can send the stolen token in the `Authorization` header.
- But Postman **doesn't have the device's private key**, so it can't produce a valid
  `X-Signature`.
- The request gets a `401`. The stolen token is useless.

### 6. Three configuration modes (so QA can still test)

The same code runs everywhere. Behavior is controlled by config:

```json
"Attestation": {
  "Mode": "Enforced",     // "Enforced" | "Soft" | "Off"
  "Verifier": "PlayIntegrity"  // "PlayIntegrity" | "AppAttest" | "Simulator"
}
```

| Mode | What happens | Who uses it |
|---|---|---|
| `Enforced` | Full verification. Invalid proofs are rejected. PoP is required. | Production |
| `Soft` | Verification is attempted. Failures are **logged** but **still allowed**. | QA / staging |
| `Off` | No attestation at all. No PoP. Behaves exactly like today. | Local development |

### 7. The startup guard (production safety net)

In `Program.cs`, in the existing `if (app.Environment.IsProduction())` block, add:

```csharp
if (app.Environment.IsProduction())
{
    // ... existing checks for signing key, bootstrap secret, etc. ...

    // FR-AUTH-003 guard: Production MUST use Enforced mode.
    var mode = app.Configuration["Attestation:Mode"];
    if (mode != "Enforced")
    {
        throw new InvalidOperationException(
            $"Production requires Attestation:Mode = 'Enforced'. Found '{mode}'.");
    }

    // FR-AUTH-003 guard: the Simulator verifier can never reach Production.
    var verifier = app.Configuration["Attestation:Verifier"];
    if (verifier == "Simulator")
    {
        throw new InvalidOperationException(
            "Attestation:Verifier 'Simulator' is not permitted in Production.");
    }
}
```

This means a misconfiguration can **never** accidentally weaken security in Production.
The app won't even start.

---


---

## What you DON'T build

Per the project rules (AGENTS.md §"Explicitly out of scope"), these are **not** your job:

- ❌ Mobile SDK (the app-side code that calls Google/Apple APIs).
- ❌ A replay-window or rate limiter (not in scope).
- ❌ TLS configuration, NTP syncing, or deny-by-default firewall rules.
- ❌ The QR-code signing/verification itself (that's the `VerificationModule` — a separate
  concern about validating QR payloads, not about authenticating callers).
- ❌ DPoP (RFC 9449) — the project uses a custom `X-Signature` scheme for consistency
  with the existing JWT issuer.
- ❌ mTLS — mentioned as Phase 3, not Phase 2.

---

## Quick reference — where each piece of code lives

| What | File / Location |
|---|---|
| DB migration | `db/migrations/009_device_attestation.sql` |
| Verifier interface | `src/Modules/IdentityAccess/Application/Abstractions/IAttestationVerifier.cs` |
| Google verifier impl | `src/Modules/IdentityAccess/Infrastructure/Attestation/GooglePlayIntegrityVerifier.cs` |
| Apple verifier impl | `src/Modules/IdentityAccess/Infrastructure/Attestation/AppleAppAttastVerifier.cs` |
| Simulator verifier | `src/Modules/IdentityAccess/Infrastructure/Attestation/SimulatorAttestationVerifier.cs` |
| EnrolledDevice aggregate | `src/Modules/IdentityAccess/Domain/Aggregates/EnrolledDevice.cs` |
| EnrolledDevice EF config | `src/Modules/IdentityAccess/Infrastructure/Persistence/Configurations/EnrolledDeviceConfiguration.cs` |
| EnrolledDevice repository | `src/Modules/IdentityAccess/Domain/Interfaces/IEnrolledDeviceRepository.cs` + repo impl |
| Nonce command + handler | `src/Modules/IdentityAccess/Application/Commands/IssueAttestationNonce/...` |
| Enrollment command + handler | `src/Modules/IdentityAccess/Application/Commands/EnrollDevice/...` |
| API endpoints | `src/Modules/IdentityAccess/Api/Controllers/OAuthController.cs` (or a new DeviceEnrollmentController) |
| PoP middleware | `src/Modules/IdentityAccess/Api/IdentityAccessModule.cs` (JWT OnTokenValidated event) |
| Token `cnf` claim | `src/Modules/IdentityAccess/Application/Abstractions/IAccessTokenIssuer.cs` (extend `AccessTokenClaims`) + `JwtAccessTokenIssuer.cs` |
| Startup guard | `src/Host/SBQR.Api/Program.cs` (Production block) |
| Unit tests | `tests/SBQR.Modules.IdentityAccess.Tests/...` (new or alongside existing) |
| Integration tests | `tests/SBQR.Qr.IntegrationTests/...` (extend `QrFlowHostBuilder`) |
| Schema drift test | `tests/SBQR.Tenancy.IntegrationTests/Schema/SchemaModelDriftTests.cs` (extend to `enrolled_devices`) |
| Production gate test | `tests/SBQR.Qr.IntegrationTests/ProductionDenylistTests.cs` (extend) |

**Pattern to copy:** the existing `TenantApplicationsController.cs` and its
`RegisterTenantApplicationCommand` / `RegisterTenantApplicationCommandHandler` /
`RegisterTenantApplicationValidator` triple in the Tenancy module. New admin endpoints
(device list / suspend / reinstate) follow the exact same shape.

---

**Next step:** Read the [Security Architecture & VAPT](./FR-AUTH-003-security-architecture.md)
guide to understand the credential model and VAPT requirements. Then pick
your role-specific file from the index above.
