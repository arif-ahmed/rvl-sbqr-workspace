# FR-AUTH-001 — Jira Story & Task Breakdown

Reference spec: [FR-AUTH-001 — Platform Admin: Obtain Access Token](FR-AUTH-001-platform-admin-token.md)

## Story

**Title:** Platform admin can obtain a short-lived admin token for institute onboarding and key management

> **As a** platform administrator (machine client),
> **I want** to exchange the platform bootstrap credential for a 10-minute admin-scoped token,
> **so that** I can register institutes, issue their client credentials, and create/rotate their signing keys without a long-lived secret in flight.

**Spec:** FR-AUTH-001 (`docs/functional-requirements/001-platform-admin-token/FR-AUTH-001-platform-admin-token.md`)

**Scope:** Nothing is implemented yet — this story builds the token endpoint and everything it needs. The capabilities it unlocks (FR §7 C1–C4) are separate stories under their own requirements.

## Acceptance Criteria (= FR-AUTH-001 §8)

1. Valid credential → `200`, `expires_in = 600`, scope `admin`, no institute id, `auth.token.issued` audited.
2. Wrong secret → `401 invalid_client`, byte-identical to every other authentication failure.
3. Institute credential → exactly `qr:generate` + `qr:validate` plus its `tenant_id` claim, never `admin`.
4. Client-sent `scope` field → ignored.
5. `grant_type=password` → `400 unsupported_grant_type`.
6. Requests beyond the configured per-minute limit → `429` (default 10 → 11th; limit 2 → 3rd).
7. Admin token on a QR generate/validate endpoint → `403`.
8. Token older than 10 minutes → `401`.

## Subtasks

| # | Subtask | Type | Constraints |
|---|---|---|---|
| 1 | Bootstrap credential configuration — client-id `platform-bootstrap`, secret as Argon2id hash, operator CLI to generate it, Production startup refuses plaintext. | Code | C3, C9 |
| 2 | Credential store — tenant configurations (client-id unique, secret hash, status, expiry) via external SQL migration. | Code | C20 |
| 3 | Secret hashing — Argon2id (OWASP parameters), constant-time verify, fail closed on malformed hash. | Code | C2 |
| 4 | Token endpoint — `POST /v1/oauth/token`, form or JSON, three fields, request validation, RFC 6749 error mapping. | Code | A6 |
| 5 | Token issuance logic — bootstrap path (scope `admin`, no institute id) and institute path (uniform `invalid_client`, scopes `qr:generate` + `qr:validate`, `tenant_id` claim). | Code | C5, C15 |
| 6 | JWT issuance — signed 10-minute JWT; fails loudly if the signing key is missing or weak. | Code | A1 |
| 7 | Authorization policies — JWT bearer validation plus named policies so downstream endpoints are protected from day one. | Code | C15 |
| 8 | Rate limiting — configurable requests-per-minute per source IP (default 10, no code change to retune) → `429`. | Code | C7 |
| 9 | Audit — `auth.token.issued` on every successful mint (actor, client-id, scopes; never the secret). | Code | C8, A5 |
| 10 | Tests — unit (guards, JWT claim shape, hash round-trip) + integration for all 8 acceptance criteria including failure cases; AC6 must exercise a non-default limit. | Tests | A1, DoD |
| 11 | Docs — dev guide for obtaining the admin token; PR description names the touched constraint ids. | Docs | C22 |

Sizing note: two increments — bootstrap path first (AC1, 2, 4, 5, 6, 8), institute credential path second (AC3, 7).

## Notes for Jira

- Link the FR doc as source of truth; paste §8 into the story.
- Definition of Done: PR names the touched constraint ids (C2, C3, C5, C7, C8, C9, C15, C20, A1, A5, A6) and includes failure-case tests.
