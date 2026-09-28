# Slice 6 — Contract pinning + mTLS

## Context

Per AR-2026-09-23-01 §6.1 (contract conformance) and §6.2 (mTLS):

- **Contract pinning**: validate every outbound sbqr.api response body against
  the real OpenAPI contract at `rvl-secure-bqr-manager/contracts/v1.public.json`.
  Pin prevents silent shape drift between sbqr.api and the BFF.
- **mTLS**: the BFF authenticates itself to sbqr.api with a client certificate
  loaded from a PFX file at a config-bound path.

## Decisions

### 1. Validator library: JsonSchema.Net

`JsonSchema.Net` (Greg Dennis, MIT) over NJsonSchema. Reasons:

- Light (single assembly) vs NJsonSchema's NSwag dependency tree.
- Native JSON-Schema 2020-12 evaluation; works on the raw `JsonSchema`
  objects we extract from OpenAPI's `components/schemas` directly.
- MIT, actively maintained, .NET 8-compatible.

### 2. Pin response bodies, NOT request bodies

The BFF is a thin gateway: it forwards whatever the FI app sends. The FI app
is responsible for the inbound payload shape. So we validate only
**outbound responses**: `TokenResponse`, `GenerateQrResponse`, `ValidateQrResponse`.

### 3. Contract file is loaded at startup from a config-bound path

```csharp
public PlatformContractRegistry(IConfiguration config, ILogger<...> log)
{
    _contractPath = config["Platform:ContractPath"];
    // ... load + parse OpenAPI document
}
```

- `Platform:ContractPath` empty → pinning is disabled (dev convenience).
- Path set but file missing → log warning + pinning disabled (graceful).
- Path set + file valid → load schemas, log info, pinning enabled.
- Production deployment sets `Platform:ContractPath` to a stable path; the
  contract file is part of the deployment artifact.

### 4. Pinned response handling

`HttpPlatformQrClient` calls `_validator.Validate(json, schemaName)` AFTER
reading the response body, ONLY for 2xx responses. On failure:

1. Throw `ContractViolationException` with sanitized errors
   (no raw body — never echo PAN/PII).
2. `ContractViolationFilter` (MVC `IExceptionFilter`) catches it and returns
   a 502 ProblemDetails with `type`, `title`, `status`, `detail`, and a
   capped list of `errors`.

### 5. TokenResponse deserialization — camelCase per real contract

The real contract uses camelCase (`accessToken, tokenType, expiresIn`).
We updated `TokenResponse` accordingly; the fake-idp + fake-sbqr-api were
also updated to emit camelCase. (The fake-sbqr-api previously emitted
snake_case which would now fail validation — that was the desired behavior.)

### 6. GenerateQrResponse + ValidateQrResponse shapes

Fake now returns the real contract shapes:
- `GenerateQrResponse`: `{qrPayload, payloadHash, qrType, signatureKeyVersion}` with **201 Created** status.
- `ValidateQrResponse`: 8 required fields including `verdict, payloadHash, qrClassification`.

The fake's previous shape (`{qrPayload, status}` and `{valid, reason}`)
would fail validation — caught by the integration test
`Contract_violating_response_returns_502_with_sanitized_errors`.

### 7. mTLS via PFX

```json
"Mtls": {
  "Enabled": true,
  "CertPath": "/etc/sbqr/pfx/client.pfx",
  "CertPassword": "<bound via Mtls__CertPassword env var>"
}
```

`MtlsConfigurator.Apply(builder, config)` reads `MtlsOptions` from
`IConfiguration` at registration time (before the SP exists) and uses
`ConfigurePrimaryHttpMessageHandler` to attach an `X509Certificate2` to
the `HttpClientHandler.ClientCertificates` collection.

`Mtls:Enabled=false` is a no-op (dev convenience — no client cert needed
against the fake sbqr.api).

### 8. BootGuard enforcement in Production

In Production, if `Mtls:Enabled=true` AND either `CertPath` or
`CertPassword` is missing → fail-fast at startup. Slice 4's `BootGuard`
class gets a new block for this; no separate "MtlsBootGuardStep" needed.

## What got built

```
src/SBQR.FiGateway.Api/
├── Contracts/
│   ├── PlatformContractRegistry.cs        ← loads schemas from v1.public.json
│   ├── ContractValidator.cs              ← TryValidate(body, schemaName)
│   ├── ContractViolationException.cs     ← sanitized exception
│   └── ContractViolationFilter.cs        ← MVC filter -> 502 ProblemDetails
├── Mtls/
│   ├── MtlsOptions.cs                    ← Enabled, CertPath, CertPassword
│   └── MtlsConfigurator.cs               ← Apply(builder, config)
├── Platform/
│   ├── TokenResponse.cs                  ← MODIFIED: camelCase per real contract
│   ├── HttpPlatformQrClient.cs           ← MODIFIED: ContractValidator call
│   └── PlatformOptions.cs                ← MODIFIED: + ContractPath
├── Observability/
│   └── BootGuard.cs                      ← MODIFIED: + Mtls Production check
└── Program.cs                            ← MODIFIED: register registry + validator + filter + MtlsConfigurator

tmp/fake-sbqr-api.js                      ← MODIFIED: real contract shapes + /__contract__/invalid endpoint

tests/SBQR.FiGateway.Tests/
├── Contracts/
│   └── ContractValidatorTests.cs         ← 13 tests against real OpenAPI doc
├── Mtls/
│   └── MtlsConfiguratorTests.cs          ← 4 tests (loads PFX, missing-file, missing-pwd, noop)
├── Integration/
│   └── ContractPinningIntegrationTests.cs ← 2 tests (violation=502, conformant=200)
└── Observability/
    └── BootGuardTests.cs                 ← MODIFIED: +MtlsOptions parameter
```

## Tests

105/105 passing (was 86 in Slice 5; +19 new in Slice 6: 13 contract + 4 mTLS + 2 integration).

## Out-of-scope (deferred)

- Inbound request-body contract pinning (FI app is responsible).
- mTLS for `"PlatformToken"` and `"Idp"` named HttpClients (only `"PlatformQr"` this slice).
- Secret-management (Azure Key Vault / Vault integration).
