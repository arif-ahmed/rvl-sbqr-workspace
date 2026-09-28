# FR-AUTH-001 — QA Test Cases (Token Endpoint)

Reference spec: [FR-AUTH-001 — Platform Admin: Obtain Access Token](FR-AUTH-001-platform-admin-token.md) ·
Story: [FR-AUTH-001-story.md](FR-AUTH-001-story.md)

## Prerequisites

- Service running over TLS; rate limiter active with a **configurable** per-minute limit
  (know the current setting; default 10) and the ability to change it in the test environment.
- Bootstrap credential configured; QA knows the plaintext (test env only).
- One provisioned institute credential (client-id + secret) — story increment 2.
- API client, JWT decoder, read access to the audit log.

---

**TC-01 — Valid bootstrap credential returns a correct admin token** *(AC1)*

1. `POST /v1/oauth/token` form-encoded: `grant_type=client_credentials`, `client_id=platform-bootstrap`, `client_secret=<valid>` → `200` with `access_token`, `token_type=Bearer`, `expires_in=600` — and nothing else in the body.
2. Decode the token → `sub=platform:bootstrap-admin`, scope exactly `admin`, `iss`/`aud` per config, `exp` ≈ now + 10 min, **no `tenant_id`**.
3. Repeat as JSON → same result.

**TC-02 — Every authentication failure is the same 401** *(AC2)*

1. Wrong secret → `401 invalid_client`.
2. Unknown client-id → `401 invalid_client`, body **byte-identical** to step 1.
3. Institute credential whose tenant is suspended → same identical `401`.
4. Institute credential past its expiry date → same identical `401`.

*No response may hint at which check failed or reveal tenant status.*

**TC-03 — Institute credential gets QR scopes only** *(AC3, BR1)*

1. Token request with institute client-id + secret → `200`.
2. Decode → scope exactly `qr:generate` + `qr:validate`, `sub=client:{client_id}`, `tenant_id` present and correct.
3. Use this token on an admin endpoint (e.g. `POST /v1/admin/tenants`) → `403`.

**TC-04 — Client-sent scope field is ignored** *(AC4, BR2)*

1. Valid bootstrap request with an extra `scope=qr:generate` field → `200`; decoded scope is still exactly `admin`.

**TC-05 — Malformed requests are rejected cleanly** *(AC5, error table)*

1. `grant_type=password` → `400 unsupported_grant_type`.
2. Omit each of the three fields in turn; empty-string values; 101+ char client-id; 513+ char secret → `400 invalid_request`.
3. Empty body, malformed JSON, broken form encoding → `400`, never `500`, no stack trace or echoed input.

**TC-06 — Rate limit is enforced and configurable** *(AC6, BR5)*

1. With the default limit (10), after a quiet minute: 10 valid requests → all `200`; the 11th within the same minute → `429`.
2. Change the configured limit to 2 (configuration change only, per deployment procedure) → the 3rd request within a minute → `429`.
3. After the window passes → request succeeds again.

**TC-07 — Admin token cannot use QR endpoints** *(AC7)*

1. Admin token (from TC-01) on a QR generate endpoint → `403`; on a QR validate endpoint → `403`.

**TC-08 — Token expires after 10 minutes** *(AC8, BR4)*

1. Fresh admin token used immediately on an admin endpoint → success.
2. Same token after ~11 minutes → `401`.
3. Request a new token → `200`; old token stays rejected.

**TC-09 — Audit and secret hygiene** *(BR6, C8, C19)*

1. Perform one bootstrap and one institute mint → audit log shows two `auth.token.issued` records with actor, client-id, scopes, timestamp, correlation id.
2. Search logs and audit entries for either secret's plaintext → zero hits.

---

**Out of scope:** load/stress beyond the functional rate-limit check, signature forgery (VAPT), TLS/network checks (C12 scan), and the C1–C4 downstream endpoints (own stories).
