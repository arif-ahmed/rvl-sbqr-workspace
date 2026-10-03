# FR-AUTH-003 — Mobile App Attestation: Only Authorized Apps, No Ad-hoc Clients

> **SUPERSEDED (2026-10-03).** Built on the FR-AUTH-002 `tenant_applications`
> allow-list, which was deprecated when the token flow moved to server-to-server
> (see `docs/misc/extra/0001-fi-direct-oauth-to-bff-model.md`, task T5, and the
> FR-AUTH-002 banner). The table was dropped and no attestation column ever
> shipped. This document is retained as historical design record only.

| Field   | Value                                                       |
|---------|-------------------------------------------------------------|
| Area    | Identity & Access                                            |
| Status  | Superseded — see banner above                                |
| Updated | 2026-10-03                                                   |

## 1. Summary

This requirement implements the platform directive that **only authorized
mobile apps** may call the mobile-facing endpoints — ad-hoc HTTP clients
(Postman, Swagger UI, browsers, curl) must be unable to obtain or use tokens
in Production. It achieves this with **platform app attestation**
(Google Play Integrity / Apple App Attest) plus **per-device enrollment and
sender-constrained (proof-of-possession) tokens**, while keeping QA fully
testable from Postman/Swagger through environment-gated attestation modes.

This is Phase 2 of the roadmap established in FR-AUTH-002:

| Phase | Mechanism | What it stops |
|---|---|---|
| 1 — FR-AUTH-002 (drafted) | credential + `package_id` allow-list | wrong/unregistered app claims (identification only) |
| **2 — this document** | **attestation-gated enrollment + device-bound PoP tokens** | **ad-hoc clients (Postman/Swagger/browser/curl), replay of extracted credentials and tokens** |
| 3 — future hardening | cert pinning telemetry, anomaly detection, mTLS | determined attackers on rooted devices |

**Dependency:** FR-AUTH-002 must land first — this document builds on the
`tenant_applications` table it introduces. If both are implemented together,
merge the schema migrations accordingly.

## 2. Threat model — what this does and does not stop

Achievable (and achieved by this design):

- A request without a platform-signed attestation verdict cannot enroll or
  obtain mobile tokens — Postman/Swagger/browser/curl cannot fabricate one.
- A stolen/extracted `client_secret` alone is no longer sufficient for the
  mobile plane (enrollment requires attestation).
- A stolen bearer token cannot be replayed from another client — tokens are
  bound to an enrolled device key (proof-of-possession).
- Repackaged/modified APKs fail: the verdict ties package name **and signing
  certificate digest** to the registered app.

Not achievable (accepted): a fully compromised, rooted device running the
genuine binary with an attacker driving it from inside. Cost is raised, not
made infinite. Phase 3 addresses detection.

## 3. Design overview

### 3.1 First-run enrollment (replaces the stateless fresh-install model)

```
Fresh install (first launch)
   │ 1. SDK requests a nonce        GET /v1/oauth/attestation-nonce   (auth: tenant credential)
   │ 2. SDK runs Play Integrity / App Attest with the nonce as requestHash
   │ 3. SDK posts enrollment        POST /v1/oauth/device-enrollment
   │      { platform, package_id, attestation_verdict, device_public_key, credential }
   ▼
Server
   │ 4. Verify tenant credential (existing chain: Argon2id + admission)
   │ 5. Verify the verdict signature with the provider (Google / Apple / simulator)
   │ 6. Match verdict.package_name + verdict.signing_cert_sha256
   │      against the tenant's registered tenant_applications row
   │ 7. Verify nonce freshness + single use; verify device key possession proof
   │ 8. Create enrolled_devices row; issue device-scoped credential
   ▼
Steady state
   │ 9. Token requests signed with the device key (PoP);
   │    QR endpoints require a PoP-bound token;
   │    periodic re-attestation (configurable, e.g. every 7 days)
```

### 3.2 Proof-of-possession (sender-constrained) tokens

- At enrollment the device generates a keypair (Android Keystore /
  iOS Secure Enclave) and registers the public key.
- Access tokens issued to the mobile plane carry a `cnf` (confirmation) claim
  with the device key thumbprint.
- Every mobile-plane request carries a signed challenge header:
  `X-Signature: v1 = <sig>` over `method + path + timestamp + body-hash`,
  signed with the enrolled device key. The JWT bearer handler validates the
  signature whenever the token carries a `cnf` claim.
- Effect: a token replayed from Postman (which cannot produce the device
  key's signature) is rejected even if the token itself is valid.

*(RFC 9449 DPoP is the standards-track alternative to the custom signed
challenge; the custom scheme is chosen for in-house consistency with the
existing custom JWT issuer. Revisit if interop requirements appear.)*

### 3.3 Attestation modes — the QA testing contract

The same code runs everywhere; behavior is configuration:

| Mode | Behavior | Where |
|---|---|---|
| `Enforced` | verdict required and verified for enrollment; PoP required on mobile plane | **Production only** |
| `Soft` | verify attempted; failures logged + audited but **allowed** | QA / staging |
| `Off` | no attestation, no PoP (credential-only, i.e. today's behavior) | local dev |

**Startup guard (hard, code-enforced):** if `Environment == Production` and
`Mode != Enforced` or `Verifier == Simulator`, the host **refuses to start** —
the same pattern the platform already uses to refuse a plaintext bootstrap
secret in Production. The bypass cannot leak into Production through
misconfiguration.

### 3.4 Verifier pluggability

`IAttestationVerifier` implementations:

1. `GooglePlayIntegrityVerifier` — verifies Google's signed verdict; checks
   `appRecognitionVerdict = PLAY_RECOGNIZED`, `package_name`,
   `certificateSha256Digest`, device recognition, and `requestHash` (nonce).
2. `AppleAppAttestVerifier` — validates the attestation object against
   Apple's App Attest root, verifies assertions (counter strictly
   increasing), checks the app's App ID.
3. `SimulatorAttestationVerifier` — verifies tokens signed with a
   platform-held test key, producing the same normalized verdict shape.
   **Never registerable in Production** (startup guard). This is what lets
   QA drive the full enrollment flow from Postman.

## 4. Implementation changes

### 4.1 Configuration (`appsettings` + options class)

```jsonc
"Attestation": {
  "Mode": "Enforced",            // Enforced | Soft | Off
  "Verifier": "PlayIntegrity",   // PlayIntegrity | AppAttest | Simulator
  "VerdictMaxAgeMinutes": 10,
  "ReattestationPeriodDays": 7,
  "PlayIntegrity": { "...service account / verification keys..." },
  "AppAttest": { "...apple root / environment..." },
  "Simulator": { "SigningKey": "dev-only" }
}
```

New `AttestationOptions` + `AttestationOptionsStartupValidator` in
IdentityAccess (registered in `IdentityAccessModule`), executed at host
startup alongside the existing bootstrap-secret check in `Program.cs`.

### 4.2 Schema changes

Extend FR-AUTH-002's table (adds the attestation matching key):

```sql
ALTER TABLE public.tenant_applications
  ADD COLUMN signing_cert_sha256 varchar(64);  -- Android: APK signing cert digest;
                                               -- iOS: App Attest App ID hash. NULL = not yet registered.
```

New table (IdentityAccess-owned, `tenancy` schema, mirrors
`tenant_configurations` placement):

```sql
CREATE TABLE public.enrolled_devices (
  device_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES public.tenants(tenant_id),
  tenant_application_id uuid NOT NULL REFERENCES public.tenant_applications(tenant_application_id),
  platform varchar(10) NOT NULL,
  device_public_key text NOT NULL,             -- PEM / base64; device key thumbprint is derived
  attestation_verdict jsonb NOT NULL,           -- last verified verdict (audit/forensics)
  attestation_verified_at timestamptz NOT NULL,
  assertion_counter bigint NOT NULL DEFAULT 0,  -- iOS App Attest replay guard
  status varchar(20) NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED')),
  is_active boolean NOT NULL DEFAULT TRUE,
  created_by varchar(100), created_at timestamptz NOT NULL DEFAULT now(),
  modified_by varchar(100), modified_at timestamptz
);
CREATE INDEX ix_enrolled_devices_tenant_id ON public.enrolled_devices(tenant_id);
CREATE INDEX ix_enrolled_devices_active ON public.enrolled_devices(tenant_application_id) WHERE is_active = TRUE;
```

Migration: `db/migrations/009_device_attestation.sql` (or merged into 008 if
FR-AUTH-002 lands simultaneously). EF mappings mirror index names exactly
(schema-drift rule); extend `SchemaModelDriftTests` to both tables.

### 4.3 Domain + Application (IdentityAccess module)

- `Domain/Aggregates/EnrolledDevice.cs` — factory `Enroll(...)`, `Suspend`,
  `Reinstate`, `RecordReattestation(verdict, counter)`.
- `Domain/Interfaces/IEnrolledDeviceRepository.cs`.
- `Application/Abstractions/IAttestationVerifier.cs` — returns normalized
  `AttestationVerdict { Platform, PackageName, SigningCertSha256,
  DeviceRecognitionId, IsGenuine, RequestHash, IssuedAt }`.
- Commands/queries:
  - `IssueAttestationNonceCommand` — single-use nonce store (cache or table),
    TTL 5 minutes;
  - `EnrollDeviceCommand` — the §3.1 pipeline; rejections are uniform
    `invalid_client`-style errors with audit reasons (`verdict_invalid`,
    `verdict_expired`, `verdict_app_mismatch`, `nonce_reused`,
    `app_not_registered`, `device_suspended`);
  - token issuance: mobile-plane tokens gain `cnf` claim (device thumbprint)
    — extend `IAccessTokenIssuer`/`JwtAccessTokenIssuer`.
- PoP validation: a JWT bearer `OnTokenValidated` hook (or post-policy
  middleware) — if the token carries `cnf`, require and verify the
  `X-Signature` challenge header against the enrolled device key; reject
  with `401` otherwise. Active on `MobileJwt` policy endpoints.

### 4.4 API surface

| Endpoint | Auth | Notes |
|---|---|---|
| `GET  /v1/oauth/attestation-nonce` | tenant credential | single-use nonce |
| `POST /v1/oauth/device-enrollment` | tenant credential + verdict | enroll device; returns device credential id |
| `POST /v1/admin/tenants/{id}/devices` (list) / `.../devices/{deviceId}/suspend` / `.../reinstate` | `AdminCredentialTree` | per-device kill switch — the missing revocation rung |
| `POST /v1/oauth/token` | unchanged | now also accepts device-PoP requests for the mobile plane |

Tenancy admin endpoints follow the TenantsController + Contracts-seam pattern
used by tenant-configuration provisioning.

### 4.5 Host wiring

- Register `AttestationOptions` binding + startup validator.
- `Mode == Off` ⇒ no verifier, no PoP (dev parity with today).
- Rate limiter: keep `client_id|IP`; add optional `device_id|IP` partition
  for enrolled flows (follow-up, not launch-blocking).

### 4.6 SDK contract (out of this repo — published to the FI)

1. Generate device keypair in hardware-backed store; never export the key.
2. Fetch nonce → run Play Integrity (`requestHash` = nonce) / App Attest →
   enroll with verdict + public key.
3. Sign every mobile-plane request with the device key (`X-Signature`).
4. Certificate pinning; block user-installed CAs (Android network security
   config); re-attest on the configured period.

## 5. Testing guidelines

### 5.1 Environment × mode matrix

| Environment | Mode | Verifier | Postman/Swagger | Real device |
|---|---|---|---|---|
| Local dev | `Off` | — | everything works (today's behavior) | n/a |
| QA / staging | `Soft` | `Simulator` | **full flows incl. enrollment** (simulator verdicts; failures logged not blocked) | optional |
| Production | `Enforced` | `PlayIntegrity` + `AppAttest` | blocked by design | required |

### 5.2 Test pyramid

**L1 — Unit (CI, no I/O):**

| Case | Expect |
|---|---|
| Verifier fixtures: valid Google/Apple verdict samples | normalized verdict parsed |
| Wrong provider signature / tampered payload | `verdict_invalid` |
| Verdict older than `VerdictMaxAgeMinutes` | `verdict_expired` |
| Verdict `package_name` ≠ registered | `verdict_app_mismatch` |
| Verdict cert digest ≠ registered `signing_cert_sha256` | `verdict_app_mismatch` |
| Nonce reuse / unknown nonce | `nonce_reused` |
| App Attest assertion counter not increasing | reject |
| PoP signature: wrong key, wrong method/path/body-hash, stale timestamp | `401` |
| Startup validator: Production + `Soft`/`Off`/`Simulator` | host refuses to start |
| Simulator verifier registered in Production config | host refuses to start |

**L2 — Integration (CI, real Postgres — extend `SBQR.Qr.IntegrationTests` on
the existing `QrFlowHostBuilder`/`PostgreSqlFixture` harness):**

1. Enroll with simulator verdict → `enrolled_devices` row + audit.
2. Every L1 rejection branch end-to-end with uniform error + audit reason.
3. Enrolled device → token with `cnf` claim → QR generate with valid
  `X-Signature` → `200`.
4. Same token from "Postman" (no signature / wrong key) → `401`.
5. Suspended device → token refusal; reinstate → works again.
6. Re-attestation after `ReattestationPeriodDays` → verdict refresh path.
7. `Soft` mode: invalid verdict → request **allowed** + audit row recording
   the would-be rejection (the QA contract).
8. `Off` mode: plain legacy flow unchanged (all FR-AUTH-001/002 tests keep
   passing unmodified).
9. Schema-drift tests extended to `tenant_applications` +
   `enrolled_devices`.

**L3 — QA manual (QA env, Soft + Simulator; document as a
`dev-testing-guide` page, house style):**

- Postman collection: nonce → (verdict generator helper) → enroll → token →
  QR generate/validate, incl. negative cases.
- A **verdict generator** helper (QA-build-only endpoint or CLI) that mints
  simulator verdicts for arbitrary package/digest values — this is how QA
  exercises the matching rules from Postman without a device.
- Swagger remains available in QA; in Production it is disabled at the
  host or inert by enforcement (requests fail without PoP).

**L4 — Device E2E (small set, the only real-provider coverage):**

- Android: Play Integrity local testing / Firebase Test Lab — genuine binary
  enrolls; a modified build fails.
- iOS: App Attest sandbox environment — enroll + assertion flow.

**L5 — Production gates (CI, mirroring `ProductionDenylistTests`):**

1. Deployed config snapshot: `Mode == Enforced`, verifier not Simulator —
   else pipeline fails.
2. Negative smoke in Production: valid tenant credential + no
   attestation → `401`; enrolled-style token without signature → `401`.
3. Startup-guard exercised against a Production-like host configuration in
   CI (host boot must fail on `Soft`).

### 5.3 Rollout order (operational safety)

1. Deploy with `Soft` in Production first (shadow mode): no blocking, full
   audit of would-be rejections — watch for legitimate-user breakage and
   verdict failure rates.
2. Register `signing_cert_sha256` for each tenant's apps.
3. Flip `Enforced` after a clean shadow period; the flip is configuration,
   verified by the L5 gates.

### 5.4 Definition of done

- In `Enforced` mode: no path to a mobile-plane token without a valid
  provider verdict; no replay of tokens without the enrolled device key.
- Production misconfiguration cannot boot (guard tested).
- QA can execute every flow from Postman/Swagger in `Soft` mode (documented).
- Real-provider paths exercised on device at least once per release.

## 6. Out of scope

- Continuous in-app integrity monitoring and anomaly scoring (Phase 3).
- mTLS as an alternative PoP transport.
- DPoP (RFC 9449) adoption (noted as an alternative in §3.2).
- User-level identity/accounts — device identity only; user authorization
  remains the FI's responsibility (BB spec Annex C).
- Changes to the BB BanglaQR QR payload/verification surface.
