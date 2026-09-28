# VAPT Compliance Report — Public Endpoints

| Field | Value |
|---|---|
| Report date | 2026-09-21 |
| Audited commit | `b9aa1c7c7634404188c2f9bc53e60e575bf386f5` |
| Scope | public (`v1.public` OpenAPI group + anonymously reachable infra endpoints) |
| Client population | FI mobile apps only (`package_id` allow-listed); no browser clients declared today |
| Mode | static + dynamic |
| Rule sets | OWASP API Security Top 10 (2023); OWASP ASVS 4.0.3 (selected chapters) |
| Auditor | Agent-run `vapt-audit` skill |
| Probe output | `C:\Users\Arif\AppData\Local\Temp\claude\D--Workspace-Sources-RVL-rvl-secure-bqr-manager\fb2c5472-520f-4933-a1c7-687a43514ebf\scratchpad\vapt-probe.json` (26 probes, see Appendix) |

## 1. Executive summary

The core payment-security logic (BOLA prevention, tenant fail-closed
resolution, Argon2id secret hashing with timing-equalized dummy verify,
Ed25519 signature verification with fail-closed trust-store gating, TLV
parsing bounded by length caps, hash-chained audit trail) is well built and
passed every check run against it. The findings that surfaced are almost
entirely at the HTTP edge: **no security-header middleware**, **the
internal-admin OpenAPI document and its Scalar UI are reachable without
authentication** (an attack-surface/inventory-management disclosure), and
**unhandled exceptions on two public-adjacent code paths leak raw .NET stack
traces** to unauthenticated callers. Rate limiting and TLS termination gaps
are real but charter-deferred by `AGENTS.md`.

| Verdict | Count |
|---|---|
| FAIL | 7 |
| WARN | 8 |
| PASS | 20 |
| N/A | 2 |

Failures annotated `[charter-deferred: …]` are real violations of the
standard that the project charter (`AGENTS.md`, "Explicitly out of scope")
consciously defers — they are reported as FAIL to keep this report valid for
external auditors.

## 2. Scope — endpoint inventory

| # | Method | Path | Auth | Policy / scope | OpenAPI group | File:line |
|---|---|---|---|---|---|---|
| 1 | POST | `/v1/oauth/token` | `[AllowAnonymous]` (secret IS the auth) | rate-limited (`TokenEndpoint`) | `v1.public` | `OAuthController.cs:35-39` |
| 2 | POST | `/v1/qr/generate/static` | Bearer JWT | `QrGenerate` (`scope=qr:generate`) | `v1.public` | `QrGenerationController.cs:25-48` |
| 3 | POST | `/v1/qr/generate/dynamic` | Bearer JWT | `QrGenerate` | `v1.public` | `QrGenerationController.cs:81` |
| 4 | POST | `/v1/qr/validate` | Bearer JWT | `QrValidate` (`scope=qr:validate`) | `v1.public` | `QrValidationController.cs:22-49` |
| 5 | GET | `/health/live` | `AllowAnonymous` | — | infra (minimal API) | `Program.cs:494` |
| 6 | GET | `/health/ready` | `AllowAnonymous` | — | infra (minimal API) | `Program.cs:497` |
| 7 | GET | `/` | `AllowAnonymous` (302 → public doc) | — | infra (minimal API) | `Program.cs:500` |
| 8 | GET | `/openapi/v1.public.json` | `AllowAnonymous` | — | infra (minimal API) | `Program.cs:570` |
| 9 | GET | `/openapi/v1.internal-admin.json` | `AllowAnonymous` (⚠ finding surface — admin doc, anonymous) | — | infra (minimal API) | `Program.cs:571` |
| 10 | GET | `/docs/public` | `AllowAnonymous` | — | infra (Scalar UI) | `Program.cs:574` |
| 11 | GET | `/docs/internal-admin` | `AllowAnonymous` (⚠ finding surface) | — | infra (Scalar UI) | `Program.cs:578` |
| 12 | GET | `/admin/_routes` | `AllowAnonymous`, **Development-only** (`IsDevelopment()` gate) | — | excluded from description | `Program.cs:517-547` |

Out-of-scope-but-noted (internal, `GroupName = "internal"`, only reachable via
admin/key-admin scopes — audited only as cross-reference, not deep-dived):
`InstitutionsController` (`/v1/admin/institutions`), `CryptoKeysController`
(`/v1/crypto-keys`), `TenantsController` (`/v1/admin/tenants`),
`TenantApplicationsController` (`/v1/admin/tenants/{id}/applications`). All
four correctly carry `[Authorize(Policy = PolicyNames.AdminCredentialTree |
KeyAdmin)]` plus `[ApiExplorerSettings(GroupName = "internal")]`.

## 3. Compliance scorecard

`D` = FAIL with `[charter-deferred]`. Endpoint columns: **Tok**=`/v1/oauth/token`,
**Gen**=`/v1/qr/generate/*`, **Val**=`/v1/qr/validate`, **Infra**=health/docs/OpenAPI.

| Rule | Rule name | Tok | Gen | Val | Infra | Overall |
|---|---|---|---|---|---|---|
| API1:2023 | BOLA | N/A | PASS | PASS | N/A | **PASS** |
| API2:2023 | Broken Authentication | PASS | PASS | PASS | N/A | **PASS** (WARN: HS256 design note) |
| API3:2023 | Object Property Level AuthZ | PASS | PASS | PASS | N/A | **PASS** |
| API4:2023 | Unrestricted Resource Consumption | PASS | D | D | N/A | **D** |
| API5:2023 | Function Level AuthZ | PASS | PASS | PASS | FAIL | **FAIL** |
| API6:2023 | Sensitive Business Flows | PASS | WARN | WARN | N/A | **WARN** |
| API7:2023 | SSRF | N/A | N/A | PASS | N/A | **PASS** |
| API8:2023 | Security Misconfiguration | FAIL | FAIL | FAIL | FAIL | **FAIL** |
| API9:2023 | Improper Inventory Management | PASS | PASS | PASS | FAIL | **FAIL** |
| API10:2023 | Unsafe Consumption of APIs | N/A | N/A | FAIL | N/A | **FAIL** |
| ASVS V2 | Authentication | PASS | PASS | PASS | N/A | **PASS** (WARN: public-client secret) |
| ASVS V3 | Session/token lifecycle | PASS | — | — | N/A | **PASS** (WARN: no revocation) |
| ASVS V4 | Access control | PASS | PASS | PASS | N/A | **PASS** (WARN: RLS deferred) |
| ASVS V5 | Validation & encoding | PASS | PASS | PASS | N/A | **PASS** |
| ASVS V6 | Cryptography | PASS | PASS | PASS | N/A | **PASS** (WARN: HS256) |
| ASVS V7 | Errors & logging | FAIL | — | — | — | **FAIL** |
| ASVS V8 | Data protection | D | PASS | PASS | D | **D** |
| ASVS V14 | Configuration | D | D | D | FAIL | **FAIL** |

## 4. Findings

### F-001: Internal-admin OpenAPI document and Scalar UI reachable anonymously

- **Rule(s)**: API5:2023 (function-level authZ, cross-ref); API8:2023 §5;
  API9:2023; ASVS V14.5
- **Severity**: High
- **Verdict**: FAIL
- **Endpoint(s)**: `GET /openapi/v1.internal-admin.json`, `GET /docs/internal-admin`
- **Description**: Both the machine-readable OpenAPI document describing
  every internal-admin endpoint (tenant lifecycle, crypto-key mint/rotate,
  institution trust-directory upsert — full request/response schemas) and
  its rendered Scalar UI are served with `.AllowAnonymous()` in every
  environment. This is a full inventory disclosure of the admin surface to
  any unauthenticated caller: an attacker gets the exact shape of every
  admin request before attempting anything against the (correctly)
  authenticated admin endpoints themselves.
- **Evidence**: `src/Host/SBQR.Api/Program.cs:571` (`app.MapOpenApi(...v1\.internal-admin$...).AllowAnonymous()`) and `:578-580` (`MapScalarApiReference("/docs/internal-admin", ...).AllowAnonymous()`). Dynamic probe P-08: `GET /openapi/v1.internal-admin.json` → `200` (expected `401/404`); `GET /docs/internal-admin` → `200` (expected `401/404`).
- **Remediation**: Gate both routes behind the same `AdminCredentialTree`/`KeyAdmin` policy used by the controllers they document, or at minimum require any authenticated bearer token — the public document (`/openapi/v1.public.json`, `/docs/public`) can stay anonymous since every caller class needs it.

### F-002: No global exception handler — raw .NET stack traces returned to unauthenticated callers

- **Rule(s)**: API8:2023 §4; ASVS V7.1/V7.3
- **Severity**: Medium-High
- **Verdict**: FAIL
- **Endpoint(s)**: `POST /v1/oauth/token` (anonymous by design — directly attacker-reachable with zero setup)
- **Description**: There is no `AddProblemDetails`/`UseExceptionHandler` registration in `Program.cs`. Two distinct unhandled-exception paths were reproduced by probes: (1) malformed JSON body → `System.Text.Json.JsonException` with full stack trace in the 500 response body; (2) an oversized (1 MiB) body → `System.IO.IOException: Buffer limit exceeded` from `FileBufferingReadStream`, also with a full stack trace. Both confirm the exception detail path is the ASP.NET default (dev-exception-page-shaped output), not a sanitized `ProblemDetails` response — and this is on the one endpoint that is unauthenticated by design, so any anonymous internet caller can trigger it.
- **Evidence**: `src/Host/SBQR.Api/Program.cs` — no `UseExceptionHandler`/`AddProblemDetails` call anywhere in the pipeline (confirmed by full-file grep). Probe P-11: `POST /v1/oauth/token` with `{not json` → `500`, body contains `"System.Text.Json.JsonException: 'n' is an invalid start of a property name..."` plus `   at ` stack frames. Probe P-07: `POST /v1/oauth/token` with 1 MiB JSON → `500`, body contains `"System.IO.IOException: Buffer limit exceeded.\r\n   at Microsoft.AspNetCore.WebUtilities.FileBufferingReadStream.ReadAsync..."`.
- **Remediation**: Register `builder.Services.AddProblemDetails()` + `app.UseExceptionHandler()` (or a custom middleware) ahead of the controller pipeline so every unhandled exception maps to a generic `ProblemDetails` body with a correlation id, mirroring the discipline `QrIssuancePipeline`'s fail-closed `catch` already applies (`reference={correlationId}`, no exception detail).

### F-003: No HTTP security-header middleware

- **Rule(s)**: API8:2023 §1; ASVS V14.4
- **Severity**: Medium
- **Verdict**: FAIL
- **Endpoint(s)**: all public + infra endpoints
- **Description**: No response carries `X-Content-Type-Options`, `X-Frame-Options`, `Content-Security-Policy`, `Referrer-Policy`, or `Strict-Transport-Security` (HSTS is separately charter-deferred — see F-005). These are cheap, standard hardening headers with no functional cost for a JSON API.
- **Evidence**: Full-file grep of `Program.cs` for header middleware returns no hits. Probe P-03: `GET /health/live` → all 5 assessed headers absent.
- **Remediation**: Add a small middleware (or `UseSecurityHeaders`-style extension) setting `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, and a minimal `Content-Security-Policy: default-src 'none'` (this is a JSON API, not a page-serving app) ahead of `MapControllers()`.

### F-004: QR-validate reason codes form a distinguishable trust-store oracle

- **Rule(s)**: API6:2023
- **Severity**: Medium
- **Verdict**: WARN
- **Endpoint(s)**: `POST /v1/qr/validate`
- **Description**: `ValidateQrCommandHandler` returns a distinct `ReasonCode` for every rejection branch — `TRUST_DIRECTORY_MISS` (unknown institution), `KEY_SUSPENDED`, `KEY_REVOKED`, `KEY_NOT_ACTIVE`, `SIGNATURE_MALFORMED`, `SIGNATURE_MISMATCH` — directly in the `200 OK` response body (the verdict IS the answer, by design). An authenticated tenant credential (any FI's own token is sufficient — no cross-tenant access needed) can therefore enumerate `Institution_ID` values and learn which institutions exist in the BB trust store and whether their keys are suspended/revoked, without ever needing a valid signature. `/v1/qr/validate` carries no additional rate limiting beyond the bearer-token requirement (cross-ref API4/F-006), so probing at scale is not throttled beyond whatever limit applies to minting the token in the first place.
- **Evidence**: `src/Modules/Verification/SBQR.Modules.Verification.Application/Commands/ValidateQrCommandHandler.cs:233-235` (`ReasonCode: "TRUST_DIRECTORY_MISS"`), `:254-261` (`KEY_SUSPENDED`/`KEY_REVOKED`), `:336-338` (`SIGNATURE_MISMATCH`) — all reachable via the public `ValidateQrResponse.ReasonCode` field (`QrValidationController.cs:88-96`).
- **Remediation**: This is a judgment call for the product owner: the distinct codes are useful for legitimate integrators debugging their own QR issuance, so collapsing them may cost operability. If kept, consider adding request-level throttling on `/v1/qr/validate` scoped per tenant (ties into F-006) so bulk enumeration is at least rate-bounded even though it isn't eliminated.

### F-005: Rate limiting absent beyond the token endpoint `[charter-deferred: rate-limit breadth — AGENTS.md "Explicitly out of scope"]`

- **Rule(s)**: API4:2023; API6:2023
- **Severity**: Medium
- **Verdict**: FAIL `[charter-deferred: rate-limit breadth — AGENTS.md "Explicitly out of scope"]`
- **Endpoint(s)**: `POST /v1/qr/generate/static`, `POST /v1/qr/generate/dynamic`, `POST /v1/qr/validate`
- **Description**: `AddRateLimiter` in `Program.cs` defines exactly one policy (`RateLimitPolicyNames.TokenEndpoint`), applied only to `OAuthController`. Neither `QrGenerationController` nor `QrValidationController` carries `[EnableRateLimiting]`. Both endpoints do cryptographic work per call (Ed25519 sign / verify) gated only by a valid bearer token — a compromised or shared FI credential can drive unbounded generate/validate volume.
- **Evidence**: `src/Host/SBQR.Api/Program.cs:407-428` (only policy defined is `TokenEndpoint`); `QrGenerationController.cs` and `QrValidationController.cs` carry no `[EnableRateLimiting(...)]` attribute (confirmed by reading both files in full).
- **Remediation**: Per `AGENTS.md`, out of scope for this project unless a separate approved doc adds it. No action needed beyond keeping this annotation current if the charter changes.

### F-006: No transport-layer protection in-app `[charter-deferred: TLS at host — AGENTS.md "Explicitly out of scope"]`

- **Rule(s)**: API8:2023 §2; ASVS V8.2/V14.4
- **Severity**: Medium
- **Verdict**: FAIL `[charter-deferred: TLS at host — AGENTS.md "Explicitly out of scope"]`
- **Endpoint(s)**: all public + infra endpoints
- **Description**: No `UseHttpsRedirection()`/`UseHsts()` in the pipeline; `launchSettings.json` binds plain `http://localhost:5001`. Per the charter this is a deliberate deferral to the reverse-proxy/host layer, but the report must still grade the endpoint's own transport posture honestly.
- **Evidence**: `src/Host/SBQR.Api/Properties/launchSettings.json:9` (`"applicationUrl": "http://localhost:5001"`); full-file grep of `Program.cs` for `UseHttpsRedirection`/`UseHsts` returns no hits. Probe P-12: base URL scheme = `http`.
- **Remediation**: No in-app action required if the reverse proxy terminates TLS and forwards `X-Forwarded-Proto` correctly — verify that configuration lives in the deployment docs (`docs/deployments.md`) as the compensating control, and note it in the next audit.

### F-007: Trust-store sync channel is plain HTTP with no authentication or integrity check

- **Rule(s)**: API10:2023; ASVS V8.2
- **Severity**: High
- **Verdict**: FAIL (not charter-deferred — this is an outbound call the application itself makes, not inbound TLS termination)
- **Endpoint(s)**: N/A directly (server-to-server; feeds the trust data `/v1/qr/validate` relies on)
- **Description**: `HttpTrustStoreClient.FetchAllAsync` fetches institution public keys from `TrustStore:BaseUrl` over plain HTTP with no bearer/mTLS auth and no response signature check (observed hitting `http://localhost:5002` in the dev log). `docs/environments.md` documents this channel only as using "mock trust store" data in dev/Development and gives no explicit production security contract for it. Since `/v1/qr/validate`'s entire trust decision rests on this data (Annex B step 2), a MITM or compromised network path between the app and the real BB trust store could substitute attacker keys and flip `INVALID_SIGNATURE` verdicts to `VALID` for forged QR codes.
- **Evidence**: `src/Modules/InstitutionTrust/SBQR.Modules.InstitutionTrust.Infrastructure/TrustStore/HttpTrustStoreClient.cs:24-28` (plain `GetFromJsonAsync` on the injected `HttpClient`, base URL entirely config-driven); `src/Host/SBQR.Api/appsettings.json:37-39` (`TrustStore:BaseUrl` empty — injected via env); dev boot log shows the outbound call target `http://localhost:5002`; `docs/environments.md` has no stated production hardening for this channel.
- **Remediation**: Confirm and document the production channel's actual security contract (mTLS or signed responses ideally, since the whole verification chain roots here) before go-live; if BB's real trust-store contract defines transport security out-of-band, downgrade this to WARN with a citation to that contract in the next audit.

## 5. Passed checks (summary)

- **BOLA (API1)**: tenant identity is always resolved server-side from the JWT `tenant_id` claim (`JwtClaimCurrentTenant.cs:32-49`), never from the request body; missing/invalid claim fails closed to `Guid.Empty`, and both `QrIssuancePipeline.IssueAsync` (`:93-99`) and `ValidateQrCommandHandler.Handle` (`:73-81`, throws on `Guid.Empty`) reject rather than default to any tenant.
- **Credential hashing (API2/ASVS V2.4/V6.2.1)**: `Argon2idSecretHasher` uses documented work factors and PHC-format output; unknown-client and every tenant-path rejection branch runs a dummy verify before returning, and every failure collapses to the same `InvalidClientFailure()` / `invalid_client` body (`IssueClientCredentialsTokenCommandHandler.cs`).
- **`package_id` allow-list enforcement (API2 §6, ASVS V2.2)**: enforced presence-based at token-mint time (`IssueClientCredentialsTokenCommandHandler.cs:268-297`), gated behind the same dummy-verify timing equalization.
- **JWT validation (API2/ASVS V3.5)**: issuer, audience, signature, lifetime all validated; `ClockSkew` bounded to 1 minute; `MapInboundClaims=false`; bearer scheme only registered when `Jwt:SigningKey` is configured and ≥32 chars (`IdentityAccessModule.cs:173-198`). Access-token TTL defaults to 10 minutes (ASVS V3.2 PASS).
- **Production boot guards (ASVS V14.8, API2 §3)**: all four guards present and verified live in code — `plain-file` signing provider blocked, missing `Jwt:SigningKey` blocked, literal dev-key blocked (`DevSigningKeyGuard`), unknown `Crypto:VaultProvider` blocked (`Program.cs:453-492`).
- **Input validation & payload bounds (API3/API4/ASVS V5.4)**: strict JSON (`JsonUnmappedMemberHandling.Disallow`, `Program.cs:265-268`); `RecipientQrFieldsValidator` caps every recipient field length matching the spec's TLV byte caps; `ValidateQrCommand`'s `QrPayload` capped at 2048 chars (`ValidateQrCommand.cs:83`); `TlvTokenizer.Tokenize` is a single forward-only pass bounded by the input's byte length — no unbounded loop on attacker-controlled QR strings.
- **Response minimality (API3/ASVS V8.3)**: token response is `access_token`/`token_type`/`expires_in` only; QR generate response is payload/hash/type/key-version only — no key material, no other-tenant data.
- **Rate limiting on the token endpoint (ASVS V2.2.1)**: partitioned by `{client_id}|{remoteIp}` (not IP-only), bounded 4 KiB form read, non-throwing fallback to per-IP partitioning — confirmed live via probe P-06 (`429` returned on the 11th/12th of 12 rapid bogus-credential requests).
- **CORS (API8 §3, ASVS V14.4)**: no `AddCors`/`UseCors` registered — deny-by-default, correct for the current mobile-only client population; probe P-09 confirms no `Access-Control-Allow-Origin` reflection for either hostile test origin.
- **Audit trail (ASVS V7.7/7.9)**: `AuditLogger` computes a SHA-256 hash-chained `entry_hash` per row under a `pg_advisory_xact_lock`-serialized scope, verified on read.
- **Error-body correlation (API8)**: `X-Correlation-Id` is server-minted, not reflected from a client-supplied header (probe P-10).
- **Internal-admin controller authorization (API5, cross-ref)**: all four internal controllers carry `[Authorize(Policy = ...)]` plus `[ApiExplorerSettings(GroupName = "internal")]` — the controllers themselves are correctly gated; only the OpenAPI document/UI exposing them anonymously is the finding (F-001).
- **SSRF (API7)**: the only outbound call from a public-endpoint-reachable code path is the trust-store fetch, and its target is entirely config-pinned (`TrustStore:BaseUrl`) — no request DTO on any public endpoint carries a URL/hostname field.
- **Dev-only surfaces properly gated**: `/admin/_routes` requires `app.Environment.IsDevelopment()` AND carries `.AllowAnonymous().ExcludeFromDescription()` — confirmed present only in Development (`Program.cs:517-548`).

## 6. Methodology & limitations

- Static analysis of the audited commit: `src/Host/SBQR.Api/Program.cs` (full file), all 7 public/internal controllers, `IdentityAccessModule.cs`, `JwtClaimCurrentTenant.cs`, `RateLimitClientIdExtractor.cs`, `QrIssuancePipeline.cs`, `ValidateQrCommandHandler.cs`, `RecipientQrFieldsValidator.cs`, `TlvTokenizer.cs`, `HttpTrustStoreClient.cs`, `Argon2idSecretHasher.cs`, `Directory.Build.props`, `Directory.Packages.props`, `appsettings.json`, `launchSettings.json`, `docs/environments.md`.
- Dynamic probes: `vapt_probe.py` P-01…P-12 against a locally running instance (`dotnet run --project src/Host/SBQR.Api`, `ASPNETCORE_ENVIRONMENT=Development`, `http://localhost:5001`) — anonymous-access, malformed bearer, security/fingerprint headers, verb tampering, rate-limit verification (12 bogus-credential requests under `client_id=vapt-probe`, burning only that probe id's window), oversized body, docs exposure, CORS, correlation-id trust, error leakage, transport scheme. All non-destructive, no valid credentials, no writes.
- `dotnet list package --vulnerable --include-transitive` was run (60s bound, succeeded): every shipped `src/` project (including `SBQR.Api` itself) reported **no vulnerable packages**. All flagged advisories (`AutoMapper 15.0.0` High, `Microsoft.OpenApi 2.0.0/2.4.0` High, `System.Drawing.Common 5.0.0` Critical, `Azure.Identity 1.3.0` Moderate/High) are confined to four **test-only** projects (`SBQR.ArchitectureTests`, `SBQR.BbTrustStoreMock.Tests`, `SBQR.InstitutionTrust.IntegrationTests`, `SBQR.Qr.IntegrationTests`, `SBQR.Tenancy.IntegrationTests`) — not part of the deployed `SBQR.Api` binary. Notably, `System.Drawing.Common` resolves to `5.0.0` in those test projects despite a central pin to `5.0.3` in `Directory.Packages.props:145` — worth a follow-up dependency-graph check, but non-shipping. `Directory.Build.props:76` mutes `NU1902/NU1903/NU1904` advisory noise at build time (WARN, evidence only).
- **Limitations**: safe probes only (no exploit verification, no authorized credential testing, no authenticated-endpoint probing beyond what a bearer-less/garbage-bearer caller can trigger); findings graded on code plus unauthenticated dynamic responses. Internal-admin surface was **not** deep-dived (per scope confirmation — public-only audit); its four controllers were read only far enough to confirm they carry the correct `[Authorize]`/`[ApiExplorerSettings(GroupName="internal")]` pair, feeding F-001's contrast with the anonymous OpenAPI document that describes them.

## Appendix: raw probe output

Full JSON (26 probe records, summary `{"PASS": 19, "FAIL": 4, "WARN": 2, "INCONCLUSIVE": 1}` at the probe-script's own verdict granularity — several probe FAILs/WARNs map to more than one report finding above, e.g. P-08's two internal-admin FAILs both feed F-001, and P-07/P-11 both feed F-002) is saved at:

`C:\Users\Arif\AppData\Local\Temp\claude\D--Workspace-Sources-RVL-rvl-secure-bqr-manager\fb2c5472-520f-4933-a1c7-687a43514ebf\scratchpad\vapt-probe.json`

Key records are quoted inline in each finding's Evidence above; no credentials or secrets appear anywhere in the probe output.
