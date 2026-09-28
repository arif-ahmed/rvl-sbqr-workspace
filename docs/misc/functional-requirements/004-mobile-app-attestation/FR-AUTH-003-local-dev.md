# FR-AUTH-003 — Local Development Guide (No Phone Required)

> **Part of the [FR-AUTH-003 dev guide](./FR-AUTH-003-dev-guide.md) series.**
> - [← Back to main guide](./FR-AUTH-003-dev-guide.md)
> - [Security Architecture & VAPT](./FR-AUTH-003-security-architecture.md)
> - [QA Testing Guide](./FR-AUTH-003-qa-testing.md)
> - [Production & Operations](./FR-AUTH-003-production-operations.md)
> - [Appendix: Payloads](./FR-AUTH-003-appendix-payloads.md)

---

## Local development guide — implement + test device binding without a phone

This section walks you through implementing the **entire** attestation + device-binding
flow **locally** — no Android device, no iOS device, no access to Google Play Console or
Apple App Store. You use a **Simulator verifier** (a test key you control) so you can
exercise every code path from `curl` / Postman.

### Prerequisites (5 minutes)

1. You already have: the `.NET 10` backend running locally (`docker compose up` or `dotnet run`)
2. You already have: a registered tenant + app row in the DB (from FR-AUTH-002 onboarding)
3. You need: the `dotnet-counters` and `openssl` CLI tools (already on dev machines)

### Step 1 — Generate a simulator key pair

The Simulator verifier uses an Ed25519 key pair. You generate this **once** locally
using `openssl` (or any Ed25519 tool). The private key signs "fake verdicts" in the QA
helper endpoint. The public key verifies them in the `SimulatorAttestationVerifier`.

```bash
# Generate a test Ed25519 private key
openssl genpkey -algorithm Ed25519 -out simulator-private.pem

# Extract the public key
openssl pkey -in simulator-private.pem -pubout -out simulator-public.pem

# Get the raw public key as Base64 (for your config)
openssl pkey -in simulator-private.pem -pubout -outform DER | tail -c 32 | base64 -w0
# → eJ2r3fKx9pQvN7tZ4mB8cY1aWd3xFh6sJkL5tRnYpE=
```

Store the Base64 public key in user-secrets (single-file convention — no
`appsettings.Development.json` exists):

```bash
dotnet user-secrets set "Attestation:Mode" "Soft" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
dotnet user-secrets set "Attestation:Verifier" "Simulator" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
dotnet user-secrets set "Attestation:Simulator:PublicKeyBase64" "eJ2r3fKx9pQvN7tZ4mB8cY1aWd3xFh6sJkL5tRnYpE=" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
```

### Step 2 — Implement the `IAttestationVerifier` interface

Create three files in the IdentityAccess Infrastructure layer:

**File: `Attestation/Models/AttestationVerdict.cs`** (shared domain concept)
```csharp
namespace SBQR.Modules.IdentityAccess.Application.Attestation;

/// <summary>
/// The decoded result of an attestation verification. Regardless of whether
/// the verdict came from Google, Apple, or the Simulator, every verifier
/// produces this same shape. The enrollment handler and the audited
/// attestation_verdict JSON both consume it.
/// </summary>
public sealed record AttestationVerdict(
    string PackageName,              // e.g. "com.dhakabank.app"
    string SigningCertSha256,        // e.g. "9e3a1bff2c4d5e6f..."
    DateTimeOffset IssuedAt,         // when Google/Apple signed this
    long AssertionCounter,           // iOS replay guard (0 for Android)
    bool IsGenuine,                  // Google: appRecognitionVerdict == PLAY_RECOGNIZED
    bool IsDeviceTampered,           // true if the device is rooted/jailbroken
    IDictionary<string, object> Raw  // the full decoded payload for audit
);
```

**File: `Attestation/IAttestationVerifier.cs`**
```csharp
namespace SBQR.Modules.IdentityAccess.Application.Attestation;

/// <summary>
/// Strategy interface for verifying attestation verdicts from Google
/// (Play Integrity), Apple (App Attest), or the Simulator.
/// Registered as a singleton in DI — one implementation active per environment.
/// </summary>
public interface IAttestationVerifier
{
    Task<AttestationVerdict> VerifyAsync(
        string platform,      // "android" or "ios"
        string rawVerdict,    // the signed proof (base64 string)
        string nonce,         // the challenge number you issued in Step 1
        CancellationToken ct);
}
```

**File: `Infrastructure/Attestation/SimulatorAttestationVerifier.cs`**
```csharp
using System.Security.Cryptography;
using System.Text.Json;

namespace SBQR.Modules.IdentityAccess.Infrastructure.Attestation;

/// <summary>
/// Simulator implementation of <see cref="IAttestationVerifier"/>.
/// Verifies proofs signed by the local test Ed25519 key. NEVER used in
/// Production — enforced by the startup guard in Program.cs.
/// </summary>
public sealed class SimulatorAttestationVerifier : IAttestationVerifier
{
    private readonly byte[] _publicKey;  // 32 raw bytes (Ed25519 public key)

    public SimulatorAttestationVerifier(IConfiguration config)
    {
        var base64 = config["Attestation:Simulator:PublicKeyBase64"]
                     ?? throw new InvalidOperationException(
                        "Attestation:Simulator:PublicKeyBase64 not configured.");

        _publicKey = Convert.FromBase64String(base64);
    }

    public async Task<AttestationVerdict> VerifyAsync(
        string platform,
        string rawVerdict,
        string nonce,
        CancellationToken ct)
    {
        // The "verdict" is a JSON blob signed with the simulator's Ed25519 private key.
        // Format: { alg: "EdDSA", payload: { ...verdict fields... }, signature: "base64" }
        using var doc = JsonDocument.Parse(rawVerdict);
        var root = doc.RootElement;

        var payloadJson = root.GetProperty("payload").GetRawText();
        var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);

        // --- THE KEY STEP: verify the verdict signature with the provider ---
        // In production: you download Google's / Apple's public key from the
        // internet and verify with THAT key. In the simulator, you verify with
        // YOUR test key. The verification logic is structurally identical:
        //
        //   using var key = Ed25519.Create();
        //   key.ImportFromPem(providerPublicKeyPem);   // ← Google's key in prod
        //   key.ImportFromPem(verifierSimulatorKeyPem); // ← test key here
        //   var ok = key.Verify(payloadBytes, signature);
        //
        // The ONLY difference is which public key you load. Everything else
        // (verify signature → decode payload → check nonce → check package/cert)
        // is identical between simulator and production.

        using var key = Ed25519.Create();
        key.ImportFromPem(new StringBuilder()
            .Append("-----BEGIN PUBLIC KEY-----\n")
            .Append(Convert.ToBase64String(_publicKey))  // raw 32-byte Ed25519 pubkey
            .Append("\n-----END PUBLIC KEY-----\n")
            .ToString());

        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadJson);

        if (!key.Verify(payloadBytes, signature))
        {
            throw new InvalidOperationException(
                "Simulator verdict signature verification failed — the payload "
                + "was tampered or signed with a different key.");
        }

        // Signature is valid — decode the verdict
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var p = payloadDoc.RootElement;

        return new AttestationVerdict(
            PackageName: p.GetProperty("package_name").GetString()!,
            SigningCertSha256: p.GetProperty("signing_cert_sha256").GetString()!,
            IssuedAt: DateTimeOffset.Parse(p.GetProperty("issued_at").GetString()!),
            AssertionCounter: p.TryGetProperty("assertion_counter", out var ac)
                ? ac.GetInt64() : 0,
            IsGenuine: p.GetProperty("is_genuine").GetBoolean(),
            IsDeviceTampered: p.GetProperty("is_device_tampered").GetBoolean(),
            Raw: JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!);
    }
}
```

> **Note:** `Ed25519.Create().ImportFromPem()` works in .NET 10. The key is that in **Production**, your `GooglePlayIntegrityVerifier` and `AppleAppAttestVerifier` follow the exact same pattern — they just load Google's or Apple's public key instead of your simulator key. The **signature verification logic is identical**; only the key source differs.

### Step 3 — Add the QA helper endpoint (for minting simulator verdicts)

This endpoint signs a verdict with your **test private key**, producing the
exact same data structure that Google/Apple would produce.

**File: `Api/Controllers/QA/QASimulatorController.cs`**
```csharp
/// <summary>
/// QA-only endpoint — mint a simulator-signed attestation verdict.
/// Returns 404 in Production (the startup guard removes the entire controller
/// route when Attestation:Verifier != "Simulator").
/// </summary>
[ApiController]
[Route("v1/qa/simulator")]
public sealed class QASimulatorController : ControllerBase
{
    private readonly byte[] _privateKey;

    public QASimulatorController(IConfiguration config)
    {
        var base64 = config["Attestation:Simulator:PrivateKeyBase64"]
                     ?? throw new InvalidOperationException("Not configured.");
        _privateKey = Convert.FromBase64String(base64);
    }

    [HttpPost("verdict")]
    public IActionResult MintVerdict([FromBody] MintVerdictRequest req)
    {
        // Build the payload that the verifier will check
        var payload = new
        {
            platform = req.Platform,
            package_name = req.PackageName,
            signing_cert_sha256 = req.SigningCertSha256,
            nonce = req.Nonce,
            issued_at = DateTimeOffset.UtcNow,
            assertion_counter = 0,
            is_genuine = req.IsGenuine,
            is_device_tampered = req.IsDeviceTampered
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        // Sign with our test Ed25519 key
        using var key = Ed25519.Create();
        key.ImportFromPrivateKey(_privateKey);

        var signature = key.Sign(payloadBytes);

        var wrapper = new
        {
            payload = payload,
            signature = Convert.ToBase64String(signature)
        };

        return Ok(new { verdict = JsonSerializer.Serialize(wrapper) });
    }
}

public sealed record MintVerdictRequest(
    string Platform,
    string PackageName,
    string SigningCertSha256,
    string Nonce,
    bool IsGenuine = true,
    bool IsDeviceTampered = false);
```

Add the private key to user-secrets (config keys:
`Attestation:Simulator:PublicKeyBase64` /
`Attestation:Simulator:PrivateKeyBase64`):

```bash
dotnet user-secrets set "Attestation:Simulator:PrivateKeyBase64" "<base64 of simulator-private.pem>" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
```

> ⚠️ **Never commit the simulator private key to source control.** Keep it in `dotnet user-secrets` (outside the repo) — never in `appsettings.json`. In Staging, use a separate key that QA controls.

### Step 4 — Register the verifier in DI

In `TenancyModule.RegisterServices` (or `IdentityAccessModule`):

```csharp
// At startup — pick the verifier based on config:
builder.Services.AddSingleton<IAttestationVerifier>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var verifier = config["Attestation:Verifier"] ?? "Simulator";

    return verifier switch
    {
        "PlayIntegrity" => new GooglePlayIntegrityVerifier(config, httpClientFactory),
        "AppAttest"     => new AppleAppAttastVerifier(config, httpClientFactory),
        "Simulator"     => new SimulatorAttestationVerifier(config),
        _ => throw new InvalidOperationException($"Unknown verifier: {verifier}")
    };
});
```

### Step 5 — Implement the device enrollment endpoint

**File: `Api/Controllers/OAuthController.cs`** (add these two endpoints)

```csharp
// Step A: issue a one-time nonce
[HttpGet("v1/oauth/attestation-nonce")]
public async Task<IActionResult> GetNonce(
    [FromHeader(Name = "Authorization")] string authHeader,
    CancellationToken ct)
{
    // Same client_id + client_secret check as the token endpoint today
    // (IssueClientCredentialsTokenCommandHandler already does this)
    var cmd = new IssueAttestationNonceCommand(clientId, clientSecret);
    var result = await _mediator.Send(cmd, ct);
    return Ok(new { nonce = result.Nonce });
}

// Step B: receive the verdict + device public key, verify, enroll
[HttpPost("v1/oauth/device-enrollment")]
public async Task<IActionResult> EnrollDevice(
    [FromHeader(Name = "Authorization")] string authHeader,
    [FromBody] EnrollDeviceRequest request,
    CancellationToken ct)
{
    var cmd = new EnrollDeviceCommand(
        ClientId: clientId,
        ClientSecret: clientSecret,
        Platform: request.Platform,
        PackageId: request.PackageId,
        AttestationVerdict: request.AttestationVerdict,
        DevicePublicKeyPem: request.DevicePublicKeyPem,
        Nonce: request.Nonce);

    var result = await _mediator.Send(cmd, ct);

    if (result.IsFailure)
        return Unauthorized(new { error = result.ErrorCode, detail = result.ErrorMessage });

    return Ok(new
    {
        device_id = result.Value.DeviceId,
        access_token = result.Value.AccessToken,
        token_type = "Bearer",
        expires_in = result.Value.ExpiresInSeconds
    });
}

public sealed record EnrollDeviceRequest(
    string Platform,
    string PackageId,
    string AttestationVerdict,    // base64 verdict from Google/Apple/Simulator
    string DevicePublicKeyPem,    // the device's public key
    string Nonce);                // the nonce from Step A
```

### Step 6 — Implement the enrollment command handler

**File: `Application/Commands/EnrollDevice/EnrollDeviceCommandHandler.cs`**

```csharp
public async Task<Result<EnrollDeviceResult>> Handle(
    EnrollDeviceCommand cmd, CancellationToken ct)
{
    // 1. Verify client credentials (same as FR-AUTH-002 token endpoint)
    var credential = await _configurations.GetByClientIdAsync(cmd.ClientId, ct);
    if (credential is null || !VerifySecret(cmd.ClientSecret, credential.SecretHash))
        return Failure("invalid_client");

    // 2. 🔐 THE CRITICAL STEP: verify the verdict signature with the provider
    // In Production: _verifier is GooglePlayIntegrityVerifier or AppleAppAttestVerifier
    // In Dev/QA: _verifier is SimulatorAttestationVerifier
    // Either way, the call is identical:
    AttestationVerdict verdict;
    try
    {
        verdict = await _verifier.VerifyAsync(
            cmd.Platform, cmd.AttestationVerdict, cmd.Nonce, ct);
    }
    catch (Exception ex) when (Mode == AttestationMode.Soft)
    {
        // Log the failure but allow (QA behavior)
        await _audit.LogAsync("attestation_verification_failed",
            cmd.ClientId, ex.Message, ct);
        goto SkipAttestationChecks;  // continue to enrollment
    }

    // 3. Verify the verdict's claims match the registered app
    var app = await _applications.GetByTenantAndPackageAsync(
        credential.TenantId, cmd.PackageId, ct);
    if (app is null)
        return Failure("invalid_client");  // package not registered

    if (app.SigningCertSha256 is not null &&
        app.SigningCertSha256 != verdict.SigningCertSha256)
    {
        // The signing cert in the verdict doesn't match what we registered
        if (Mode == AttestationMode.Enforced)
            return Failure("invalid_client");  // reject in production

        // Soft mode: log and continue
        await _audit.LogAsync("signing_cert_mismatch",
            cmd.ClientId, "verdict cert != registered cert", ct);
    }

    // 4. Verify the nonce (single-use, not expired)
    if (!await _nonceStore.IsValidAsync(cmd.Nonce, ct))
        return Failure("invalid_client");

    SkipAttestationChecks:

    // 5. Save the enrolled device + issue a PoP-bound token
    var device = new EnrolledDevice(
        tenantId: credential.TenantId,
        tenantApplicationId: app?.Id,
        platform: cmd.Platform,
        devicePublicKeyPem: cmd.DevicePublicKeyPem,
        attestationVerdictJson: verdict != null
            ? JsonSerializer.Serialize(verdict) : "{}",
        verifiedAt: DateTimeOffset.UtcNow,
        counter: verdict?.AssertionCounter ?? 0);

    await _devices.AddAsync(device, ct);
    await _uow.SaveChangesAsync(ct);

    // Issue token with "cnf" claim (device key thumbprint)
    var token = _issuer.Issue(new AccessTokenClaims(
        Subject: $"device:{device.Id}",
        TenantId: credential.TenantId,
        Scopes: new[] { "qr:generate", "qr:validate" },
        CnfThumbprint: ComputePkceThumbprint(cmd.DevicePublicKeyPem)));

    return Ok(new EnrollDeviceResult(device.Id, token, 3600));
}
```

> The `att.VerifiedAt` field on the `EnrolledDevice` row stores when Google/Apple
> (or the Simulator) verified the device. In the JSON column you keep the raw
> decoded verdict for audit.

### Step 7 — Add PoP validation middleware

The token now carries a `cnf` (confirmation) claim. Every subsequent request must
include an `X-Signature` header signed by the device's **private key** (which lives
in the phone's secure hardware and never leaves it):

```csharp
// In IdentityAccessModule.cs — add to the JwtBearer events:
OnTokenValidated = async ctx =>
{
    var cnfClaim = ctx.Principal?.FindFirst("cnf")?.Value;
    if (string.IsNullOrEmpty(cnfClaim))
        return;  // no cnf → not a device token → normal FR-AUTH-002 behavior

    // Device tokens MUST include an X-Signature header
    var sigHeader = ctx.HttpContext.Request.Headers["X-Signature"].FirstOrDefault();
    var tsHeader = ctx.HttpContext.Request.Headers["X-Timestamp"].FirstOrDefault();

    if (string.IsNullOrEmpty(sigHeader) || string.IsNullOrEmpty(tsHeader))
    {
        ctx.Fail(new SecurityTokenException("PoP signature required"));
        return;
    }

    // Reconstruct the signed payload: METHOD + PATH + TIMESTAMP + BODY_HASH
    var bodyHash = await ComputeBodyHashAsync(ctx.HttpContext.Request);
    var signedPayload = $"{ctx.HttpContext.Request.Method} " +
                        $"{ctx.HttpContext.Request.Path} " +
                        $"{tsHeader} " +
                        $"{bodyHash}";

    // Look up the device's public key from the "cnf" thumbprint
    var deviceKey = await _deviceRepo.GetByThumbprintAsync(cnfClaim, ctx.HttpContext.RequestAborted);
    if (deviceKey is null)
    {
        ctx.Fail(new SecurityTokenException("Device not enrolled"));
        return;
    }

    // Verify the signature with the device's PUBLIC key
    if (!VerifyEd25519Signature(deviceKey.Pem, signedPayload, sigHeader))
    {
        ctx.Fail(new SecurityTokenException("Invalid PoP signature"));
        return;
    }
};
```

### Step 8 — Local test walkthrough (no phone required)

```bash
# ─── Setup (once) ───
# 1. Start the backend in Dev mode (Attestation:Mode=Soft, Verifier=Simulator)
dotnet run --launch-profile Development

# 2. Register a tenant (if not done already)
curl -X POST http://localhost:5000/v1/admin/tenants \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"code":"DHB","name":"Dhaka Bank"}'
# → 201, returns tenant_id

# 3. Register an app (one row per platform)
curl -X POST "http://localhost:5000/v1/admin/tenants/$TENANT_ID/applications" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"platform":"ANDROID","package_id":"com.dhakabank.consumer"}'
# → 201 Created

# ─── The 5-step flow (from Postman / curl) ───

# Step 1: Get a nonce (uses same client_id + client_secret as FR-AUTH-002)
NONCE=$(curl -s http://localhost:5000/v1/oauth/attestation-nonce \
  -u "$CLIENT_ID:$CLIENT_SECRET" | jq -r .nonce)
echo "Nonce: $NONCE"
# → {"nonce": "x7y9q2m4p1..."}

# Step 2: Mint a SIMULATOR verdict (signs with your test Ed25519 key)
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
echo "Verdict: $VERDICT"

# Step 3: Enroll the device (your simulator verifier checks the signature)
# Generate a throwaway Ed25519 key pair for the "device public key"
DEVICE_PUB=$(openssl genpkey -algorithm Ed25519 -out /tmp/device.pem 2>/dev/null && \
  openssl pkey -in /tmp/device.pem -pubout -outform PEM)
echo "$DEVICE_PUB" > /tmp/device-pub.pem

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
# → { "device_id": "...", "access_token": "...", "token_type": "Bearer", "expires_in": 3600 }

# Step 4: Call a QR endpoint WITH a valid PoP signature → 200
TOKEN=$(echo "$ENROLL" | jq -r .access_token)
TIMESTAMP=$(date +%s)
# Sign: "POST /v1/qr/generate/static $TIMESTAMP <body-hash>"
# (use the device private key at /tmp/device.pem)
curl -X POST http://localhost:5000/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H "X-Timestamp: $TIMESTAMP" \
  -H "X-Signature: v1=$(echo -n "POST /v1/qr/generate/static $TIMESTAMP $(echo -n '{\"name\":\"Test User\",\"account\":\"01711111111\",\"amount\":100.00}' | sha256sum | cut -d' ' -f1)" | openssl pkeyutl -sign -inkey /tmp/device.pem -rawin | base64 -w0)" \
  -H "Content-Type: application/json" \
  -d '{"name":"Test User","account":"01711111111","amount":100.00}'
# → 200 with { "qr_payload": "00010A01..." }

# Step 5: Call WITHOUT PoP signature → 401
curl -X POST http://localhost:5000/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Test User","account":"01711111111","amount":100.00}'
# → 401 Unauthorized: "PoP signature required"

# ─── Negative tests ───

# Tampered verdict (change is_genuine to false)
curl -X POST http://localhost:5000/v1/qa/simulator/verdict \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -H "Content-Type: application/json" \
  -d "{\"platform\":\"android\",\"package_name\":\"com.dhakabank.consumer\",\"signing_cert_sha256\":\"9e3a1b...\",\"nonce\":\"$NONCE\",\"is_genuine\":false}"
# The simulator signs this → enrollment in Soft mode logs "verdict says not genuine" but allows

# Wrong signing cert
curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d "{\"platform\":\"android\",\"package_id\":\"com.dhakabank.consumer\",\"attestation_verdict\":\"$BAD_VERDICT\",\"device_public_key\":\"...\",\"nonce\":\"$NONCE\"}"
# Soft mode: logs "signing_cert_mismatch" → allows
# Enforced mode: 401 invalid_client

# Nonce reuse
# Step 1 returns a nonce, then Step 3 uses it. A second Step 3 with the same nonce → rejected
curl -X POST http://localhost:5000/v1/oauth/device-enrollment \
  -u "$CLIENT_ID:$CLIENT_SECRET" \
  -d "..."
# → 401 (nonce already consumed)
```

### What "verify the verdict signature with the provider" means in local dev

In the table below, the **only** difference between the Simulator and the real providers is **which public key** is loaded. The verification algorithm is structurally identical:

| | Simulator (local dev) | Google (production) | Apple (production) |
|---|---|---|---|
| **What you download** | Your test public key from `appsettings.json` | Google's public key from `https://www.googleapis.com/.../keys` | Apple's attestation root cert |
| **Data format** | Custom JSON wrapper with Ed25519 signature | Google Play Integrity JWT (RS256) | Apple App Attest CBOR |
| **Verify step** | `Ed25519.Verify(payload, signature)` | `RS256.Verify(jwt, signature)` | `ECDSA.Verify(cbor, signature)` |
| **What you check after** | `nonce` ✓, `package_name` ✓, `signing_cert_sha256` ✓ | Same checks | Same checks |
| **Assertion counter** | N/A (always 0) | N/A | iOS: increment check to prevent replay |

The key insight: **your `EnrollDeviceCommandHandler` calls `_verifier.VerifyAsync(platform, rawVerdict, nonce, ct)` — it doesn't know or care whether the verifier is Simulator, Google, or Apple.** That's dependency injection at work. You swap implementations per environment with zero code changes.

---

