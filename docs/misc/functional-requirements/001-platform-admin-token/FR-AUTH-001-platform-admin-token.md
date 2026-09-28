# FR-AUTH-001 — Platform Admin: Obtain Access Token

| Field   | Value            |
|---------|------------------|
| Area    | Identity & Access |
| Status  | Draft            |
| Updated | 2026-09-09       |

## 1. Summary

A platform administrator authenticates with the platform admin client credential and receives a short-lived OAuth 2.1 JWT (Bearer token) carrying the `admin` scope. The token authorizes platform administration: registering a new institute, saving the institute's tenant configuration (currently client-id and client-secret), and creating or rotating the institute's crypto signing key.

## 2. Actor

Platform administrator — a machine-to-machine client. No username/password login; authentication uses the platform bootstrap credential (client-id `platform-bootstrap`), whose secret is held in secure configuration.

## 3. Preconditions

- Bootstrap credential configured; the secret is stored only as an Argon2id hash. The service refuses to start in Production with a plaintext secret.
- Token-endpoint rate limit configured: requests-per-minute per source IP (default: 10).
- The request is sent over TLS.

## 4. Main Flow

1. The client sends `POST /v1/oauth/token` (form-encoded or JSON) with exactly three fields: `grant_type=client_credentials`, `client_id`, `client_secret`. There is no `scope` parameter — the server decides the scopes.
2. The system validates that all fields are present and within size limits.
3. The system verifies the secret against the stored hash, in constant time.
4. The system issues a signed JWT: subject `platform:bootstrap-admin`, scope `admin`, expiry **10 minutes**, no institute/tenant binding.
5. The system writes an audit record `auth.token.issued` with actor, client-id and granted scopes. The secret is never logged.
6. The client receives `200` with `access_token`, `token_type: Bearer`, `expires_in: 600`.
7. The client calls admin endpoints with `Authorization: Bearer <token>`. On expiry it requests a new token; there is no refresh token.

## 5. Error Handling

| Condition | Result |
|---|---|
| Unknown client-id, wrong secret, credential not configured, or inactive/suspended/expired institute credential | `401 invalid_client` — identical response in every case |
| `grant_type` missing or not `client_credentials` | `400 unsupported_grant_type` |
| Missing or oversized fields | `400 invalid_request` |
| More than the configured requests-per-minute from the same IP | `429` |
| Expired token used on an admin endpoint | `401` — client requests a new token |

## 6. Business Rules

- **BR1** Only the bootstrap credential can receive the `admin` scope. Credentials issued to institutes can never obtain it.
- **BR2** Scopes are decided by the server. A `scope` field sent by the client is ignored.
- **BR3** The admin token is platform-level: no institute binding, and it cannot generate or validate QR codes.
- **BR4** Token lifetime is 10 minutes. No refresh token and no revocation endpoint; expiry is the revocation mechanism.
- **BR5** Rate limit: token requests per source IP per minute are **configurable** (default 10). Retuning the limit is a configuration change, not a code change.
- **BR6** Every successful issuance is audited. The secret never appears in any log, any response after first provisioning, or the database.

## 7. Token Capabilities

| # | Capability | Endpoint |
|---|---|---|
| C1 | Register new institute | `POST /v1/admin/tenants` |
| C2 | Save tenant configuration (client-id, client-secret) | `POST /v1/admin/tenants/{id}/tenant-configuration` |
| C3 | Create crypto signing key (Ed25519) | `POST /v1/crypto-keys` |
| C4 | Rotate crypto signing key | `PUT /v1/crypto-keys/{tenantId}` |

Detailed behavior of these endpoints is specified in their own requirements. The `admin` scope also covers the other admin endpoints (institute lifecycle, trust registry).

## 8. Acceptance Criteria

1. Valid credential → `200`, `expires_in = 600`, token contains scope `admin` and no institute id; an `auth.token.issued` audit record exists.
2. Wrong secret → `401 invalid_client`, byte-identical to every other authentication failure (unknown client, suspended tenant, expired credential).
3. Institute credential (any tenant client-id) → its token contains exactly `qr:generate` + `qr:validate` plus its `tenant_id` claim, never `admin`.
4. Client sends a `scope` field → ignored; granted scopes unchanged.
5. `grant_type=password` → `400 unsupported_grant_type`.
6. Requests beyond the configured per-minute limit → `429` (with the default of 10, the 11th request; with the limit set to 2, the 3rd).
7. Admin token used on a QR generate/validate endpoint → `403 Forbidden`.
8. Token older than 10 minutes → `401`; a newly requested token succeeds.

## 9. Out of Scope

- Human user accounts and logins.
- Refresh tokens; token introspection / revocation.
- Institute self-service.
- Client-secret rotation (future requirement).
- Auditing of failed token mints (future hardening).
