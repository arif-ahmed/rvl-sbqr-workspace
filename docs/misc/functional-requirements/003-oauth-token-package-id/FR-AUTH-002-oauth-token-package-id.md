# FR-AUTH-002 — OAuth Token Endpoint: Base64 Credentials & Tenant App Package Allow-Listing

> **SUPERSEDED (2026-10-03).** The token flow moved to server-to-server (FI
> backend gateway -> platform over mTLS + client-credentials; mobile apps never
> call the token endpoint directly). The `tenant_applications` allow-list table
> was dropped (`013_drop_tenant_applications.sql`), and the `package_id` form
> field / JWT claim / `package_mismatch` audit reason were removed. See
> `docs/misc/extra/0001-fi-direct-oauth-to-bff-model.md` (task T5). This
> document is retained as historical design record only.

| Field   | Value                                                                              |
|---------|------------------------------------------------------------------------------------|
| Area    | Identity & Access                                                                   |
| Status  | Superseded — see banner above                                                       |
| Updated | 2026-10-03                                                                          |

## 1. Summary

This requirement captures a change request from the Head of Engineering for
`POST /v1/oauth/token` and records the architectural review behind the proposed
design. The change has two parts:

1. **Credential encoding** — the request carries the client credential as
   `Base64(client_id:client_secret)` instead of two separate fields (the
   RFC 6749 §2.3.1 client-authentication encoding).
2. **`package_id`** — the caller identifies the client it claims to be:
   a mobile app's Android `applicationId` / iOS bundle ID, or a synthetic
   identifier such as `com.<tenant>.backend` for a server-to-server
   integration. The platform keeps a per-tenant allow-list of registered
   clients in `tenant_applications` and rejects token requests whose
   claimed `package_id` does not match. The field is **mandatory for
   every tenant credential** (mobile app or backend); the only exemption
   is the platform bootstrap (admin) credential, which MUST NOT carry
   `package_id`.

The architectural position taken in this document:

- The Base64 change is **spec conformance only** — it neither adds nor removes
  security (Base64 is an encoding, not encryption).
- `package_id` is an **identification (allow-listing) mechanism, not an
  authentication mechanism**. It is valuable for allow-listing, audit and as a
  future attestation hook, and it must never be treated as proof that the
  caller is the genuine app.
- A single `package_id` column on `tenants` (as literally requested) cannot
  model reality; a `tenant_applications` table is proposed instead.
- The change implies the **mobile-app-direct-to-platform** call model, which
  makes the embedded client secret a de-facto public value. That risk is
  flagged for HoE together with the attestation (Play Integrity / App Attest)
  roadmap question — see §6.
- **`package_id` is mandatory for every tenant credential** — every
  tenant-credential call to `POST /v1/oauth/token` (mobile app *or*
  server-to-server backend integration) MUST carry `package_id` alongside
  `client_secret`. Requests from any tenant credential that omit
  `package_id` are rejected with `401 invalid_client`. The only exemption
  is the **platform bootstrap (admin) credential** — it cannot be used
  with `package_id` at all. Every tenant, including backends, must
  register at least one row in `tenant_applications` (mobile apps use
  their store identifier; backend integrations use a synthetic identifier
  such as `com.<tenant>.backend`, see §5.4 onboarding note).

## 2. Background: HoE feedback and its interpretation

Verbatim feedback:

> update /v1/oauth/token
> parameters will be as follows
> { package_id: client_secret: }
> client_secret -> client_id:client_secret base64 enco...
> package_id: mobile app package id, unique to each tenant, add this property in your tenants table

Decoded, this is two separate instructions:

1. **`client_secret` field carries `Base64(client_id:client_secret)`.** The two
   values are joined with `:` and the pair is Base64-encoded, e.g.
   `Base64("tenant-a:s3cret")`. This is exactly the OAuth 2.0
   client-authentication encoding of RFC 6749 §2.3.1 — normally transported in
   an `Authorization: Basic <base64>` header; the HoE note describes it as a
   body parameter instead.
2. **`package_id` identifies the calling client**: the mobile app's
   Android `applicationId` / iOS bundle ID, or a synthetic identifier
   such as `com.<tenant>.backend` for a server-to-server integration.
   Stored in `tenancy.tenant_applications` per tenant and validated at
   token issuance. Purpose: the server learns *which client of which
   tenant* is calling, so a credential used from an unregistered/wrong
   client can be rejected, audited per client, and — later — attested.
   **The platform enforces its presence for every tenant credential**
   (mobile app or backend); a request that omits `package_id` fails
   closed with `401 invalid_client` (audit reason `package_missing`).

Implied architectural decision worth stating explicitly: the mobile-app-
direct-to-platform call model is **enforced** by this requirement — every
tenant credential, including server-to-server backends, MUST send a
`package_id`, so a backend integration that today has no package to send
must register a synthetic one (see §5.4 onboarding note) and include it on
every call. The direct-mobile model was previously blocked by the
mobile-plane blockers B1/B2 (`docs/requirements/2026-09-10-qr-generation.md`);
this change request reopens that decision and tightens it — §6 still
records the attestation follow-up.

## 3. Current state (as of 2026-09-13)

- **Endpoint contract**: `POST /v1/oauth/token` accepts
  `grant_type=client_credentials`, `client_id`, `client_secret` as three
  separate fields, form-urlencoded or JSON
  (`src/Modules/IdentityAccess/SBQR.Modules.IdentityAccess.Api/Contracts/TokenRequest.cs`,
  `.../Controllers/OAuthController.cs`).
- **Validation chain**: client_id lookup in `tenancy.tenant_configurations`
  (Argon2id-hashed secret, status/expiry checks), tenant admission re-check via
  the Tenancy `ITenantAdmissionDirectory` seam, uniform `invalid_client` on
  every rejection with a dummy Argon2id run to equalize timing, full audit
  (`auth.token.issued` / `auth.token.rejected` with reason discriminators)
  (`.../Commands/IssueToken/IssueClientCredentialsTokenCommandHandler.cs`).
  A config-held platform bootstrap client (`scope=admin`) resolves on the same
  endpoint.
- **Tokens**: custom HS256 JWT, subject `client:{client_id}`, `tenant_id`
  claim, scopes `qr:generate` + `qr:validate`, default TTL 10 minutes.
- **Tables**: `tenancy.tenants` (institution name, BB institution code, status,
  is_active, audit) and `tenancy.tenant_configurations` (client_id, secret
  hash, status, expiry, capability flags). No notion of mobile app identity
  exists anywhere in the schema.
- **Rate limiting**: fixed-window limiter partitioned by `{client_id}|{remoteIp}`
  (`src/Host/SBQR.Api/Infrastructure/RateLimitClientIdExtractor.cs`), designed
  for server-to-server clients with stable IPs.

## 4. Architectural analysis

### 4.1 Base64 credential encoding — conformance, not security

Base64 is an encoding, not encryption. The secret already crossed the wire in
clear (inside TLS); combining the two fields into one encoded field changes
nothing about confidentiality or attack surface. Its value is **OAuth 2.0
conformance and developer familiarity** — the same pattern PayPal-style APIs
use. Implementation must preserve the existing anti-enumeration properties:
malformed input yields the generic failure with no differential information,
and secrets containing `:` are split on the **first** colon only.

### 4.2 `package_id` — identification, not authentication

The package identifier is a public constant:

- It is fixed at build time by the app developer and identical on every
  install (every user, every device, forever) — `getPackageName()` / bundle
  identifier just report the OS-installed name.
- Anyone can send the same string: a fake app does not even need to call
  `getPackageName()` — it can hardcode `com.dhakabank.app` into the request
  body. Even a repackaged APK (decompiled, modified, same package name)
  still reports the same string.

Therefore the server-side check is only ever: *“is this string registered and
active for this tenant?”* — a boolean allow-list lookup that answers “is this
a **known** app?”, never “is this a **genuine** app?”. Genuine-app proof
requires tying the package name to the signing certificate, which is exactly
what Google Play Integrity / Apple App Attest do and what a plain string
field cannot do.

What the mechanism **is** worth:

1. **Allow-listing** — wrong/unregistered app claims fail closed immediately.
2. **Audit trail** — issuances are attributable to a specific app per tenant.
3. **A hook for real verification** — the natural place to attach attestation
   later.

What it **is not**: a secret, a password substitute, or protection against a
 attacker who possesses the tenant credential.

### 4.3 One tenant, multiple apps (the two-platform case)

A single `package_id` column — as literally requested — assumes one string
per tenant. That assumption breaks for the most ordinary case: an FI with one
Android app and one iOS app. The Android `applicationId` (registered with
Google Play) and the iOS bundle ID (registered with Apple) are **independent
registrations in different stores**; they are frequently the same reverse-DNS
string *by convention*, but nothing guarantees it, nothing syncs them, and the
FI may change either at any time. The day the two strings differ, a single
column can store only one — and the other platform's app is locked out.

The same column also cannot express an FI's second app (consumer / agent /
merchant). "Unique to each tenant" therefore describes a **set** of
identifiers per tenant, not a single value. Hence the `tenant_applications`
table in §5.4.

### 4.4 Direct-call model, fresh installs, and the flip side

Runtime call flow under this change:

```
FI mobile app (any install, fresh or old)
   │  grant_type = client_credentials
   │  client_secret = base64("cred-id:embedded-secret")   ← baked into the build
   │  package_id = com.dhakabank.app                       ← read from the OS
   ▼
POST /v1/oauth/token ─► decode → credential lookup → Argon2id verify
                        → tenant admission → package allow-list → JWT
```

The endpoint is **stateless with respect to devices and users**: there is no
device registry, no enrollment, no first-run handshake. Everything the request
needs is static and ships inside the app binary. Consequently:

- A **fresh install works immediately** — its first request is byte-identical
  to the millionth request from a two-year-old install. No per-device
  information is needed or knowable.
- The token means “Dhaka Bank's app” — never “this device” or “this user”.
  All installs of the FI's app are one client. User/account identity must
  come from the FI's own authentication, not from this token.

The flip side is the security-critical part: the statelessness that makes
fresh installs “just work” equally makes an **attacker's environment “just
work”**. A script on a server sending the same two extracted strings is
indistinguishable from a fresh genuine install. `package_id` does not close
this gap (spoofable string); only attestation does.

### 4.5 Failure-mode matrix (fresh install and friends)

| Situation | Result |
|---|---|
| Credential + app registration correct | `200` token, instantly, no enrollment |
| App shipped but admin never registered the app row | `401 invalid_client` (`package_mismatch`) — fail closed; the app is dead until registered |
| App row registered but package string mismatch (typo / differing iOS bundle ID) | `401 invalid_client` (`package_mismatch`) |
| Specific app build compromised; admin suspends that app row | `401` for that app only — per-app kill switch |
| Credential rotated; old builds still in the field | `401` — rotation must plan for shipped builds |
| Attacker extracts credential from an APK and replays the two strings | `200` — indistinguishable from a genuine fresh install |

### 4.6 Revocation granularity and mobile rate limiting

- Revocation ladder: tenant → credential → app. **Per-device revocation is
  impossible in this model** — that capability only arrives with
  attestation-based per-device enrollment.
- The `{client_id}|{remoteIp}` rate-limit partition was designed for
  server-to-server clients. Mobile traffic churns IPs and piles many users
  behind CGNAT; permit limits / partitioning will need a mobile-plane policy
  when mobile volume ramps. Not a blocker for this change; recorded as an
  operational follow-up.

### 4.7 Verdict

**Implement — with eyes open.** The change is cheap, OAuth-2.0-conformant,
adds allow-listing and per-app audit, and lays the hook for real
verification. Refusing it would be ego, not engineering. The conditions:

1. `package_id` is treated as an identifier, never an authenticator or secret.
2. Storage is a `tenant_applications` table (multi-app), not a single column.
3. The embedded-secret-in-app risk and the attestation roadmap are flagged to
   HoE in writing (§6) — that question is *bigger* than this parameter change.

## 5. Proposed design

### 5.1 Token request contract

`POST /v1/oauth/token` accepts, in addition to today's fields:

- `package_id` (≤200 chars, printable ASCII). **Mandatory for every
  tenant credential** — every call authenticated by a
  `tenant_configurations` row (mobile app or backend integration) MUST
  carry `package_id`, and the value MUST match a registered active
  `tenant_applications` row for the authenticated tenant. The only
  exemption is the platform bootstrap (admin) credential, which MUST
  NOT carry `package_id` at all.
- Credentials in any of three forms, resolved in this order:
  1. `Authorization: Basic <base64(client_id:client_secret)>` header (the
     RFC 6749 §2.3.1 standard form) — takes precedence when present;
  2. body fields `client_id` + `client_secret` (today's contract — unchanged,
     kept for tenant backends);
  3. body `client_secret` carrying `base64(client_id:client_secret)` with
     `client_id` empty (the HoE-described form).
- `grant_type=client_credentials` remains required (RFC 6749).
- Malformed Base64/blob → decoded values empty → existing empty-field
  rejection → `400 invalid_request` (same behaviour as today's missing fields;
  leaks nothing beyond the caller's own input format).

Decoding splits on the **first** `:` so secrets containing `:` keep working.

### 5.2 `package_id` validation rule (v1)

`package_id` is mandatory for every tenant credential — there is no
server-to-server exemption. The rule has two legs:

- **Tenant-credential callers** (any `tenant_configurations` row that is
  not the platform bootstrap):
  - `package_id` is **required**. If absent → `401 invalid_client` with
    audit reason `package_missing`.
  - When present, it must match a registered, active
    `tenant_applications` row for the authenticated tenant; otherwise
    `401 invalid_client` with audit reason `package_mismatch`.
  - Rejection responses are byte-identical to every other
    `invalid_client`; the dummy Argon2id run equalizes timing with a
    wrong-secret rejection.
- **Bootstrap (admin) credential** + `package_id` → reject, audit reason
  `package_not_allowed_for_bootstrap` (the admin plane is never an app).
- **Issuance audit** metadata includes the claimed `package_id` when
  present.

#### 5.2.1 Caller-class detection

The class is determined by the credential, not by request shape:

- **Tenant credential.** Resolves to a `tenant_configurations` row that is
  not the bootstrap credential. `package_id` is mandatory regardless of
  the credential form in the request body
  (separate-field form, Base64-blob form, or `Authorization: Basic`
  header). All three forms are validated under §5.2's tenant-credential
  leg.

- **Bootstrap / admin credential.** Resolves to the config-held platform
  bootstrap client. `package_id` is forbidden.

There is no longer a "server-to-server may omit `package_id`" branch.
Tenant backends that today call the platform without a package must
register a synthetic `tenant_applications` row (§5.4) and start sending
`package_id` — they are tenant-credential callers like any other.

### 5.3 `tenant_applications` table

```sql
CREATE TABLE tenancy.tenant_applications (
  tenant_application_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenancy.tenants(tenant_id),
  platform varchar(10) NOT NULL CHECK (platform IN ('ANDROID','IOS')),
  package_id varchar(200) NOT NULL,
  status varchar(20) NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED')),
  is_active boolean NOT NULL DEFAULT TRUE,
  created_by varchar(100), created_at timestamptz NOT NULL DEFAULT now(),
  modified_by varchar(100), modified_at timestamptz
);
CREATE UNIQUE INDEX ix_tenant_applications_platform_package_id
  ON tenancy.tenant_applications(platform, package_id);
CREATE INDEX ix_tenant_applications_tenant_id ON tenancy.tenant_applications(tenant_id);
CREATE INDEX ix_tenant_applications_active
  ON tenancy.tenant_applications(tenant_id) WHERE is_active = TRUE;
```

Unique `(platform, package_id)` prevents two different tenants claiming the
same app on the same platform; the same string on both platforms for one
tenant (two rows) remains legal. `platform` is recorded for audit and for the
future attestation integration; it is not required in the token request.

### 5.4 Admin surface

Under `/v1/admin/tenants` (`AdminCredentialTree` policy, internal API doc):

- `POST /v1/admin/tenants/{id}/applications` — register `{platform, package_id}`;
  reverse-DNS format enforced; duplicate → `409`.
- `GET /v1/admin/tenants/{id}/applications` — list the tenant's apps.
- `POST /v1/admin/tenants/{id}/applications/{appId}/suspend` / `.../reinstate`
  — per-app kill switch.

Onboarding order rules (operational, documented):

- **Mobile apps** — register the app **before** shipping it. A shipped app
  whose row does not exist fails closed with `package_mismatch` on every
  device.
- **Backend integrations** — every server-to-server tenant integration
  must also register at least one `tenant_applications` row and start
  sending `package_id`. The platform accepts any printable reverse-DNS
  string the tenant picks; the recommended convention is
  `com.<tenant>.backend` (e.g. `com.dhakabank.backend`) so the row is
  recognisable as non-mobile in dashboards and audit. A backend that
  calls without `package_id` fails closed with `package_missing`.
- Suspended/terminated tenants may not register new apps (mirrors
  credential-provisioning admission).

### 5.5 Error handling (token endpoint additions)

| Condition | Result |
|---|---|
| Tenant-credential request (any form — separate-field, Base64-blob, or `Authorization: Basic`) with `package_id` absent | `401 invalid_client`, audit reason `package_missing` |
| Tenant-credential request with `package_id` that does not match a registered active app of the authenticated tenant (wrong string, unregistered app, suspended app, or backend integration that has not yet registered a synthetic `package_id` row) | `401 invalid_client`, audit reason `package_mismatch` |
| `package_id` present on the bootstrap credential | `401 invalid_client`, audit reason `package_not_allowed_for_bootstrap` |
| Malformed Base64 credential blob / blob missing `:` | `400 invalid_request` |

All rejections remain byte-identical to today's `invalid_client` responses;
timing equalization unchanged.

### 5.6 Business rules

- **BR1** `package_id` is an identifier. It is stored in clear, compared
  exactly, never hashed, never secret, never a substitute for the credential.
- **BR2** **Mandatory for every tenant credential.** Every token request
  authenticated by a `tenant_configurations` row (mobile app or backend
  integration) MUST include `package_id`, and the value MUST match a
  registered active `tenant_applications` row for the authenticated tenant;
  absence → `package_missing`, mismatch → `package_mismatch`. The only
  exemption is the platform bootstrap (admin) credential, which MUST NOT
  carry `package_id`.
- **BR3** *(removed — absorbed into BR7.)*
- **BR4** Per-app suspension must not affect sibling apps, other credentials,
  or the tenant.
- **BR5** The legacy separate-field credential form remains valid (no
  forced cutover) **as a credential form**, but every form — separate-field,
  Base64-blob, and `Authorization: Basic` — is now subject to the
  `package_id` rule of BR2. The credential form does not unlock any
  `package_id` exemption.
- **BR6** Every issuance/rejection audit row includes the claimed
  `package_id` when present.
- **BR7** A tenant registers a `tenant_applications` row for **every
  client that calls the platform**: each Android | iOS mobile app (with
  its store-issued `applicationId` / bundle identifier) and every
  server-to-server backend integration (with a synthetic identifier,
  recommended convention `com.<tenant>.backend`). On a given platform
  each `package_id` is unique platform-wide — the unique constraint
  `(platform, package_id)` in §5.3 enforces this at the database level.

### 5.7 Acceptance criteria (extract)

1. Blob-credential request (`client_secret` = `base64(id:secret)`, `client_id`
   empty) with a matching registered `package_id` → `200`, audit contains
   `package_id`.
2. Same request with a wrong/unknown `package_id` → `401 invalid_client`,
   audit reason `package_mismatch`, timing indistinguishable from
   wrong-secret rejection.
3. Suspended app row → same `401`/reason as (2).
4. Bootstrap credential with `package_id` → `401 invalid_client`.
5. Legacy three-field request without `package_id` → `401 invalid_client`,
   audit reason `package_missing`. Existing tests that exercise the
   legacy form **without** `package_id` must be updated to either send
   the field or be reclassified as bootstrap-only tests.
6. `Authorization: Basic` header request → `200` (body carries `grant_type`
   and `package_id`).
7. Secret containing `:` splits on the first colon and authenticates.
8. Malformed blob → `400 invalid_request`.
9. Duplicate app registration (same platform + package) → `409`.
10. Tenant-credential request (any credential form — separate-field,
    Base64-blob, or `Authorization: Basic`) with valid credential but
    `package_id` omitted → `401 invalid_client`, audit reason
    `package_missing`, timing indistinguishable from wrong-secret
    rejection.
11. Tenant-credential request (any credential form) with valid credential
    and `package_id` matching a registered active app → `200`, audit
    contains `package_id`.
12. Backend-integration request (separate-field form) with valid
    credential and a synthetic `package_id` (e.g. `com.dhakabank.backend`)
    matching a registered active app row → `200`, audit contains the
    `package_id`.
13. Backend-integration request (any credential form) with valid
    credential and **no** `package_id` → `401 invalid_client`, audit
    reason `package_missing`. There is no longer any server-to-server
    exemption — backends are tenant-credential callers like any other.

## 6. Clarification draft for HoE (ready to send)

> **Subject: /v1/oauth/token change — our understanding + a few confirmations**
>
> Our understanding of your request:
> 1. The request will carry `grant_type=client_credentials` (unchanged,
>    required per RFC 6749) plus `package_id` and `client_secret`, where
>    `client_secret` = Base64(`client_id:client_secret`) — the RFC 6749
>    §2.3.1 client-authentication encoding.
> 2. `package_id` is the calling client's identifier — the mobile app's
>    Android `applicationId` / iOS bundle ID, or a synthetic identifier
>    such as `com.<tenant>.backend` for a server-to-server integration.
>    It is registered per tenant and validated at token issuance.
> 3. **`package_id` is mandatory for every tenant credential** — both
>    mobile apps (Android | iOS) and server-to-server backend integrations
>    MUST send `package_id` on every token request. A request that omits
>    `package_id` is rejected with `401 invalid_client` (audit reason
>    `package_missing`). The only exemption is the platform bootstrap
>    (admin) credential, which MUST NOT carry `package_id`.
>
> Confirmations we need:
> 1. **Encoding**: we will accept both the standard
>    `Authorization: Basic <base64>` header and the body style you described
>    (`client_secret` carrying the base64 pair, `client_id` omitted). Which
>    one will the SDK use?
> 2. **Storage**: a single column on `tenants` cannot model reality — Android
>    and iOS identifiers are independent store registrations (often the same
>    string by convention, but not guaranteed), and a tenant may ship
>    multiple apps (consumer / agent). We propose a `tenant_applications`
>    table (tenant_id, platform, package_id, status; unique per
>    platform+package_id) with admin endpoints to register/suspend apps. OK?
> 3. **Scope (mandatory for every tenant credential)**: confirmed —
>    `package_id` is mandatory for **every** tenant credential, mobile app
>    or backend integration, with no per-tenant opt-out. Backend
>    integrations must register a synthetic `tenant_applications` row
>    (recommended `com.<tenant>.backend`) and start sending `package_id`.
>    A tenant-credential request without `package_id` fails closed with
>    audit reason `package_missing`.
> 4. **Backward compatibility**: existing tenant backends keep the current
>    separate `client_id`/`client_secret` fields — please confirm there is no
>    forced cutover date.
> 5. **Security framing (important)**: `package_id` is an identifier, not an
>    authenticator — every install of the genuine app sends the same constant
>    string, and anyone else can send that same string too. It gives us
>    allow-listing, audit, and a hook for real verification. Actual app
>    verification requires Google Play Integrity / Apple App Attest — is that
>    on the roadmap?
> 6. **Bigger risk we want to align on**: embedding `client_secret` inside a
>    mobile app makes it a public client — the secret is extractable from any
>    APK/IPA, and a matching `package_id` does not prevent its replay. What
>    is the intended flow: mobile app → platform directly, or mobile app →
>    FI backend → platform? If direct, we recommend attestation-gated token
>    issuance and short token TTLs before relying on this endpoint for
>    mobile traffic.

## 7. Implementation outline — DEFERRED, not started

Blocked on the §6 confirmations. Rough order when green-lit:

1. `db/migrations/008_tenant_applications.sql` (§5.3 DDL).
2. Tenancy domain: `TenantApplication` aggregate + repository + EF mapping
   (index names mirror SQL exactly — schema-drift test rule).
3. Tenancy Contracts seam `ITenantApplicationDirectory`
   (`HasActiveApplicationAsync`, `ListByTenantAsync`) implemented in
   Tenancy.Infrastructure, mirroring `ITenantAdmissionDirectory`.
4. Admin endpoints + commands/validators (§5.4).
5. IdentityAccess: `TokenRequest.package_id`, credential-resolution order
   (§5.1) in `OAuthController`, `PackageId` on
   `IssueClientCredentialsTokenCommand`, package check in the handler (§5.2).
6. Rate-limit extractor: resolve `client_id` from Basic header / blob so the
   `{client_id}|{IP}` partition keeps working.
7. Tests: aggregate + validator units; integration — happy path (mobile +
   backend), all `package_missing` and `package_mismatch` variants
   (mobile and backend), bootstrap-with-`package_id` rejection,
   blob/header/legacy paths, first-colon split, malformed blob; the
   backend-without-`package_id` regression (the case that proves there is
   no longer a server-to-server exemption); schema-drift extension.
8. API doc annotations; onboarding-order note in the tenant onboarding guide.

## 8. Open questions

1. Which credential form does the mobile SDK actually use (header vs body
   blob)? — HoE §6.1
2. Single-column vs `tenant_applications` sign-off. — HoE §6.2
3. Forced cutover date for the legacy contract, if any. — HoE §6.4
4. Attestation roadmap (Play Integrity / App Attest) and, with it, the
   mobile-plane trust model previously blocked by B1/B2. — HoE §6.5–6.6
5. Mobile-plane rate-limit policy (CGNAT, IP churn) — operational follow-up
   when mobile volume ramps.

## 9. Out of scope

- Play Integrity / App Attest integration (tracked as the follow-up that
  would turn identification into verification).
- Per-tenant opt-out from the `package_id`-mandatory rule. The rule is
  uniform for the tenant plane — no per-tenant flag, no credential-class
  exemption beyond bootstrap, no "trusted caller" allow-list.
- Credential rotation policy for shipped app builds.
- The mobile SDK itself.
- Per-device or per-user identity, enrollment and revocation.
- BB BanglaQR spec compliance changes — this change is platform-side only and
  does not touch the QR payload/verification surface
  (`docs/bb-banglaqr-p2p-specification.md` remains the source of truth there).
