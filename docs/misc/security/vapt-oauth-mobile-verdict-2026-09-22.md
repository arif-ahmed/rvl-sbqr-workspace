# VAPT Compliance Verdict — Mobile App OAuth Flow (`POST /v1/oauth/token`)

| Field | Value |
|---|---|
| Report date | 2026-09-22 |
| Audited commit | `60d89fc41befd4d60554bd19a7dafadd1e5f1223` |
| Scope | `POST /v1/oauth/token` — mobile-app-direct usage only (client-credentials grant, `package_id` field) |
| Related report | `docs/security/vapt-report-2026-09-21.md` (full public API surface) |
| Rule sets | RFC 6749/8252/9700, OWASP ASVS 4.0.3, OWASP API Security Top 10 (2023), OWASP MASVS 2.0 / Mobile Top 10 (2024), FAPI 2.0 Security Profile (OpenID Foundation) |
| Auditor | Manual architecture + code review (this session) |
| Verdict | **CONDITIONAL — not good-to-go for direct mobile-to-BQR calls as currently designed; GOOD-TO-GO if mobile traffic is routed through an FI-owned backend (see §6)** |

---

## 1. Executive summary

The token-issuance *implementation* — Argon2id hashing, timing-equalized rejection, uniform error responses, privilege separation, rate-limit partitioning, audit logging — meets or exceeds ASVS 4.0.3 expectations for a **server-to-server** client-credentials endpoint (§4). The finding is not in the code quality; it is in the **topology**: the current design allows a mobile app to hold and present the same `client_secret` directly to `/v1/oauth/token` (compensated only by the self-asserted `package_id` field), which is a documented anti-pattern under RFC 6749 §10.1 and the primary subject of RFC 8252 / RFC 9700's mobile-specific guidance. §5 gives the control-by-control verdict; §6 gives the two remediation paths with effort/impact.

| Verdict | Count |
|---|---|
| FAIL | 1 |
| WARN | 4 |
| PASS | 6 |
| N/A | 1 |

---

## 2. Reference standards used (so every claim below is checkable)

| ID | Standard | Relevant part | Publisher |
|---|---|---|---|
| R1 | RFC 6749 — The OAuth 2.0 Authorization Framework | §2.1 (client types), §4.4 (client-credentials grant), §10.1 (client authentication), §5.2 (error codes) | IETF, Oct 2012 |
| R2 | RFC 8252 — OAuth 2.0 for Native Apps (BCP 212) | Whole document — native/mobile apps are "public clients" and MUST NOT use embedded secrets; mandates external user-agent + Authorization Code + PKCE | IETF, Oct 2017 |
| R3 | RFC 7636 — Proof Key for Code Exchange (PKCE) | Whole document | IETF, Sep 2015 |
| R4 | RFC 9700 — Best Current Practice for OAuth 2.0 Security | §2.1 (client authentication), §2.4 (mobile/native apps), §4.13 (sender-constrained tokens) | IETF, Jan 2025 |
| R5 | RFC 9449 — OAuth 2.0 Demonstrating Proof of Possession (DPoP) | Whole document | IETF, Sep 2023 |
| R6 | OWASP ASVS 4.0.3 | V2 (Authentication), V3 (Session Mgmt/token lifecycle), V6.2 (Cryptography) | OWASP, Mar 2021 — mirrored locally at `.claude/skills/vapt-audit/rules/asvs-checks.md` |
| R7 | OWASP API Security Top 10 (2023) | API2 (Broken Authentication), API4 (Unrestricted Resource Consumption), API6 (Unrestricted Sensitive Business Flows) | OWASP, 2023 — mirrored locally at `.claude/skills/vapt-audit/rules/owasp-api-top10.md` |
| R8 | OWASP Mobile Application Security Verification Standard (MASVS) 2.0 | MASVS-AUTH-1 (server-verified auth state), MASVS-STORAGE-1 (no sensitive data in unencrypted local storage) | OWASP Mobile, 2023 |
| R9 | OWASP Mobile Top 10 (2024) | M1 — Improper Credential Usage | OWASP Mobile, 2024 |
| R10 | FAPI 2.0 Security Profile | §5.3.1 (sender-constrained access tokens), §4 (client authentication requirements) | OpenID Foundation |
| R11 | NIST SP 800-63B | §5.1.7 (multi-factor cryptographic devices), general "shared secret" authenticator guidance | NIST, 2017 (rev. 3 draft in progress) |

---

## 3. Current flow — as implemented (evidence)

```
Mobile App                          BQR API
  │  client_id + client_secret         │
  │  (+ optional package_id) ─────────>│  POST /v1/oauth/token
  │                                     │  OAuthController.cs:64-132
  │                                     │  → IssueClientCredentialsTokenCommandHandler
  │                                     │    .IssueTenantTokenAsync (:215-344)
  │  <──────── Bearer JWT (10 min) ─────│
```

- Grant type: `client_credentials` only — `OAuthController.cs:14` ("the OAuth 2.1 client-credentials token endpoint"), `IssueClientCredentialsTokenCommandHandler.cs:113-132`.
- Credential: shared per-FI `client_id`/`client_secret` pair, Argon2id-hashed at rest (`Argon2idSecretHasher`), verified per request.
- Mobile-specific field: `package_id` (Android `applicationId` / iOS bundle ID), optional, checked against `tenant_applications` **only if present** — `TokenRequest.cs:33-41`, `IssueClientCredentialsTokenCommandHandler.cs:268-299`, `TenantApplicationDirectory.cs:25-38`.
- No Authorization Code, no PKCE, no redirect URI, no external user-agent — none of RFC 8252's mobile-specific mechanics are present, because the flow was built as M2M, not as a user-facing native-app flow.

---

## 4. Control-by-control verdict

### 4.1 Credential storage & verification — PASS
**Reference**: R6 ASVS V2.4 (`.claude/skills/vapt-audit/rules/asvs-checks.md` lines 13-24), R6 V6.2.1.
**Evidence**: `Argon2idSecretHasher` — PHC-format storage, documented work factors (m=64 MiB, t=3, p=1). Every rejection branch (unknown client, wrong secret, wrong grant, suspended tenant, package mismatch) runs a dummy Argon2id verify before returning — `IssueClientCredentialsTokenCommandHandler.cs:415-433` (`EqualizeTimingAgainst`). No plaintext secret reachable at runtime.
**Verdict**: PASS — meets ASVS V2.4 and R11 (NIST 800-63B shared-secret storage expectations) for the server side of the exchange.

### 4.2 Anti-enumeration / uniform error responses — PASS
**Reference**: R6 ASVS V2.2.1, R1 RFC 6749 §5.2.
**Evidence**: Every failure path — `unknown_client`, `wrong_secret`, `credential_inactive`, `credential_expired`, `tenant_suspended`, `package_mismatch`, `wrong_grant_type` — collapses to the identical `invalid_client` (or `unsupported_grant_type` for grant errors) response body with equalized timing (`IssueClientCredentialsTokenCommandHandler.cs:117-266`). An attacker cannot distinguish "your `client_id` doesn't exist" from "your `client_id` exists but the secret is wrong" from "your tenant is suspended."
**Verdict**: PASS.

### 4.3 Anti-automation / rate limiting on the token endpoint — PASS
**Reference**: R6 ASVS V2.2.1/V2.5.4, R7 API4:2023.
**Evidence**: `[EnableRateLimiting(RateLimitPolicyNames.TokenEndpoint)]` (`OAuthController.cs:39`), partitioned by `{client_id}|{remoteIp}` rather than IP-only (`RateLimitClientIdExtractor.cs:14-21` — explicitly documents closing the "/24 subnet gets `PermitLimit × 256`" gap), bounded 4 KiB body read, `PermitLimit`/`WindowSeconds` configurable (`Program.cs:410-424`), confirmed live via probe in the prior full audit (`docs/security/vapt-report-2026-09-21.md` line 180, P-06: 429 on 11th/12th of 12 rapid requests).
**Verdict**: PASS.

### 4.4 Privilege separation (tenant credential cannot reach admin scope) — PASS
**Reference**: R7 API5:2023 (Broken Function Level Authorization), R6 ASVS V4.2.
**Evidence**: `ResolveTenantScopes` (`IssueClientCredentialsTokenCommandHandler.cs:355-371`) only ever emits `qr:generate`/`qr:validate`; the bootstrap admin path is a structurally separate code branch (`IssueBootstrapTokenAsync`, lines 145-213) gated by exact `client_id` match against config, and explicitly rejects any `package_id` on that path (line 156 — "the bootstrap credential is the admin plane; it is never a mobile app").
**Verdict**: PASS.

### 4.5 Token validation strictness (issuer/audience/lifetime/algorithm) — PASS (with one WARN sub-item)
**Reference**: R6 ASVS V3.5.
**Evidence**: `IdentityAccessModule.cs:186-197` — `ValidateIssuer=true`, `ValidateAudience=true`, `ValidateIssuerSigningKey=true`, `ValidateLifetime=true`, `ClockSkew=1min`, `MapInboundClaims=false`.
**Gap**: `ValidAlgorithms` is not explicitly pinned on `TokenValidationParameters`. Not currently exploitable (only a symmetric key is registered, so classic RS256/HS256 algorithm-confusion doesn't apply), but it's a named ASVS V3.5 checklist item and a one-line fix.
**Verdict**: PASS / WARN sub-item — see Finding F-2 below.

### 4.6 Token lifetime & revocation — WARN (accepted per ASVS, documented)
**Reference**: R6 ASVS V3.2, V3.3.
**Evidence**: `Jwt:AccessTokenTtlMinutes = 10` (`appsettings.json:14`), well under ASVS's 60-minute FAIL threshold. No `jti`/revocation list, no refresh-token flow — by design, since client-credentials re-issues rather than refreshes.
**Verdict**: WARN, explicitly acceptable per R6 V3.3's own guidance for short-lived M2M tokens — this is a documented residual risk, not a defect.

### 4.7 Sender-constrained tokens (mTLS / DPoP) — WARN
**Reference**: R4 RFC 9700 §4.13, R10 FAPI 2.0 §5.3.1, R6 ASVS V3.5 (token binding).
**Evidence**: Tokens are bare bearer tokens (`JwtAccessTokenIssuer.cs`) — no `cnf` claim, no DPoP proof-of-possession, no mTLS client-certificate binding. A stolen token is usable by anyone until its 10-minute TTL expires.
**Verdict**: WARN — mitigated by the short TTL, but this is the exact control RFC 9700 and FAPI 2.0 call out as the modern replacement for "just use bearer tokens with a short TTL." Financial-grade profiles (FAPI 2.0) treat sender-constraining as close to mandatory, not optional.

### 4.8 Public-client secret exposure — **FAIL** (the headline finding)
**Reference**: R1 RFC 6749 §10.1 ("Clients incapable of maintaining the confidentiality of their credentials... SHOULD NOT be issued... credentials that are unnecessary to their type"), R2 RFC 8252 (entire document — native apps are public clients by definition), R4 RFC 9700 §2.1/§2.4, R8 MASVS-STORAGE-1, R9 Mobile Top 10 2024 M1 (Improper Credential Usage).
**Evidence**: `client_credentials` (a *confidential-client* grant per R1 §4.4.2) is the only grant this endpoint accepts, and the mobile-direct integration path documented in code (`TokenRequest.cs:11-17` — "mobile apps that talk directly to the platform send [`package_id`]") implies the same FI-wide `client_secret` is embedded in every install of that FI's mobile app. This secret is extractable via static/dynamic reverse engineering of the APK/IPA (a shared secret across the FI's entire customer base, not per-device). The only compensating control is `package_id`, which is a **self-asserted, unauthenticated string** — `IsValidClientId`/`IsAllowedAsync` validate its *format* and *allow-list membership*, not its *authenticity*. Nothing cryptographically proves the caller is the genuine app (no Play Integrity / App Attest / DeviceCheck verification anywhere in the codebase — confirmed via grep, no matches for `integrity`, `attest`, or `DeviceCheck` in `src/`).
**Verdict**: **FAIL** against R1/R2/R4/R9 for the mobile-direct topology specifically. This verdict does **not** apply to the server-to-server (FI-backend-mediated) use of the same endpoint, where `client_credentials` is the textbook-correct grant (R1 §4.4.1: "used when the client is... a confidential client").

### 4.9 CSRF / transport for mobile — N/A
**Reference**: R6 ASVS V14.4.
**Rationale**: Native mobile HTTP clients don't carry ambient browser credentials (cookies), so CSRF doesn't apply to this channel. N/A, not PASS — recorded for completeness per the existing rule catalog's own guidance (`owasp-api-top10.md` line 240).

### 4.10 Rate-limit / DoS resistance of the Argon2id verify path under legitimate mobile load — WARN
**Reference**: R7 API4:2023 (Unrestricted Resource Consumption), R6 ASVS V5.5.
**Evidence**: Argon2id costs ~100ms/verify by design (the anti-brute-force property). If every mobile app instance calls `/v1/oauth/token` per user action rather than caching the FI-backend-issued token, this scales linearly with concurrent user actions and becomes a self-inflicted load problem, independent of attacker behavior. Not applicable if the FI-backend-mediation model (§6) is adopted, since token minting becomes infrequent and backend-cached rather than per-user-action.
**Verdict**: WARN — conditional on which topology is chosen (§6).

---

## 5. Findings summary

| # | Title | Severity | Standard refs | Verdict |
|---|---|---|---|---|
| F-1 | Mobile app holds and presents a shared FI-wide `client_secret` directly; `package_id` is not a cryptographic proof of app authenticity | **High** | R1 §10.1, R2, R4 §2.4, R9 M1 | FAIL |
| F-2 | `ValidAlgorithms` not explicitly pinned on `TokenValidationParameters` | Low | R6 ASVS V3.5 | WARN |
| F-3 | No sender-constrained tokens (DPoP/mTLS) — bearer token usable by anyone who obtains it until TTL expiry | Medium | R4 §4.13, R10 §5.3.1 | WARN |
| F-4 | No app/device attestation (Play Integrity / App Attest) backing `package_id` | High (compounds F-1) | R8 MASVS-AUTH-1, R9 M1 | FAIL (same root cause as F-1) |
| F-5 | Token-minting cost (Argon2id) not bounded against legitimate high-frequency mobile calling patterns if tokens aren't cached client-side | Medium (conditional) | R7 API4:2023 | WARN |

F-1 and F-4 are the same underlying defect (no cryptographic binding between "this HTTP request" and "a genuine instance of the FI's real app") observed from two angles — protocol-level (F-1) and mobile-platform-level (F-4). Fix them together.

---

## 6. Is the current flow good-to-go? — Conditional verdict

**As a server-to-server credential exchange (FI backend ↔ BQR): GOOD TO GO.** Every control in §4.1–4.6 passes or is an explicitly-accepted WARN. This is the correct, spec-appropriate use of `client_credentials` per R1 §4.4.1.

**As a direct mobile-app-to-BQR credential exchange: NOT GOOD TO GO** — F-1/F-4 (FAIL) represent a real gap against the specific standards (RFC 8252, RFC 9700, OWASP Mobile Top 10) written to cover exactly this scenario. This is not a theoretical/paper finding: the practical consequence is that extracting the secret from one copy of the FI's mobile app (a routine mobile-pentest technique — static analysis of the APK/IPA, or runtime hooking with Frida) yields a credential usable to mint tokens for **every customer of that FI**, indistinguishable from the FI's legitimate own traffic, bounded only by rate-limiting and scope (never `admin`, so blast radius is capped at that FI's own `qr:generate`/`qr:validate` operations — not a cross-tenant or platform compromise).

### Two remediation paths

**Path A — Recommended, no BQR protocol change (matches this session's earlier architecture discussion).** Route mobile traffic through the FI's own backend, exactly as agreed for the web channel. `client_credentials` stays exactly as implemented; the mobile app never holds `client_id`/`client_secret`; `package_id` becomes an audit/telemetry field the FI backend forwards, not a security boundary. Effort: integration-contract and FI-onboarding-process change, not a BQR code change.

**Path B — If direct-mobile-to-BQR must remain supported for some FIs (no backend).** Requires new BQR-side work: a public-client flow (pre-registered `client_id` with no secret, PKCE per R3, app-attestation verification service checking Play Integrity/App Attest tokens server-side per R8/R9), likely paired with sender-constrained tokens (F-3, R4/R10) and tighter risk-based transaction limits for that trust tier. This is materially larger scope and — per `AGENTS.md`'s own charter rule ("do not invent controls" beyond the spec) — should be an explicit, separately-approved scope addition, not built ad hoc under the current client-credentials design.

---

## 7. Passed checks (not re-litigated above)

- Production boot guards refuse to start on missing/dev-literal `Jwt:SigningKey`, unrecognized `Crypto:VaultProvider`, or `plain-file` signing provider (`Program.cs:453-492`) — ASVS V14.8.
- Strict JSON binding (`JsonUnmappedMemberHandling.Disallow`) and FluentValidation length caps (`ClientId ≤ 100`, `ClientSecret ≤ 512` — `IssueClientCredentialsTokenValidator.cs:17-27`) — ASVS V5.4.
- Full audit trail (`auth.token.issued` / `auth.token.rejected` with `reason` discriminator) on every issuance and rejection — ASVS V7.9.

## 8. Methodology & limitations

Static code review of the audited commit only (no dynamic probing in this pass — see `docs/security/vapt-report-2026-09-21.md` for the dynamic-probe results against the broader surface, which already exercised the rate limiter live). No access to an actual FI mobile-app binary was available to confirm secret-extraction empirically; F-1/F-4 are graded from the server-side design (which accepts the vulnerable pattern), consistent with how RFC 8252/9700 grade this class of issue — the standards evaluate the *protocol choice*, not whether a specific binary happens to obfuscate the secret well.
