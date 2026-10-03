# Manual Dev-Testing — `POST /v1/oauth/token`

> **SUPERSEDED (2026-10-03).** FR-AUTH-002 is deprecated — `package_id` and the
> `tenant_applications` allow-list no longer exist. Retained as historical
> record; only the plain client-credentials cases remain applicable.

Companion to **FR-AUTH-002**. Cases are form-urlencoded; JSON works the
same way because the DTO uses `[JsonPropertyName]`. Labels: `[TODAY]`
passes on current code; `[§5]` is the target behaviour once §7 lands.

## Setup

```bash
# Stack
docker compose -f docker/docker-compose.yml up -d
for f in db/migrations/*.sql; do
  docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
    psql -U postgres -d sbqr_app -v ON_ERROR_STOP=1 -f - < "$f" || break
done

# Bootstrap secret (only credential out of the box)
dotnet run --project src/Host/SBQR.Api/SBQR.Api.csproj -- --generate-bootstrap-secret
dotnet user-secrets set "Auth:Bootstrap:ClientSecretHash" "<PHC>" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj

# Start the API
dotnet run --project src/Host/SBQR.Api/SBQR.Api.csproj
export HOST=http://localhost:5080   # use whatever Kestrel logs

# Variables
export BOOTSTRAP_ID="platform-bootstrap"
export BOOTSTRAP_SECRET="<plaintext from --generate-bootstrap-secret>"
export TENANT_ID="00000000-0000-0000-0000-000000000001"
export PACKAGE_ANDROID="com.dhakabank.app"
export PACKAGE_IOS="com.dhakabank.ios"
export PACKAGE_BACKEND="com.dhakabank.backend"

# Seed tenant_applications (no admin endpoint yet — SQL only)
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app <<SQL
INSERT INTO tenancy.tenant_applications
  (tenant_application_id, tenant_id, platform, package_id, status, is_active, created_by)
VALUES
  (gen_random_uuid(), '${TENANT_ID}', 'ANDROID', '${PACKAGE_ANDROID}', 'ACTIVE', TRUE, 'dev-test'),
  (gen_random_uuid(), '${TENANT_ID}', 'IOS',     '${PACKAGE_IOS}',     'ACTIVE', TRUE, 'dev-test'),
  (gen_random_uuid(), '${TENANT_ID}', 'ANDROID', '${PACKAGE_BACKEND}', 'ACTIVE', TRUE, 'dev-test');
SQL
```

The endpoint sits behind `{client_id}|{remoteIp}` rate limiting — bursts
from one shell will return `429`. Run cases one at a time, or restart the
API between bursts.

The repo ships no fixture tenant credential. Provision one yourself
(insert into `tenancy.tenant_configurations` + Argon2id PHC via the
`--generate-bootstrap-secret` flag) before the §3.2 cases. Assume:

```bash
export TENANT_CLIENT_ID="acme-test-client"
export TENANT_CLIENT_SECRET="acme-test-secret-plaintext"
export BACKEND_CLIENT_ID="acme-backend-client"
export BACKEND_CLIENT_SECRET="acme-backend-secret-plaintext"
```

## 1. Bootstrap (admin) — `package_id` MUST be absent

### 1.1 Happy path `[TODAY]`
```bash
curl -i -X POST "${HOST}/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "client_id=${BOOTSTRAP_ID}" \
  --data-urlencode "client_secret=${BOOTSTRAP_SECRET}"
```
**→** `200` `{"access_token":"<JWT>","token_type":"Bearer","expires_in":600}`
JWT carries `sub: "platform:bootstrap-admin"`, `scope: "admin"`.

### 1.2 Edge cases `[TODAY]`

| Case | Body change | Expected |
|---|---|---|
| Wrong secret | `client_secret=definitely-not-the-secret` | `401 invalid_client` (≈ same latency as 1.1) |
| Unknown client_id | `client_id=nobody`, `client_secret=anything` | `401 invalid_client` |
| Missing fields | omit `client_id` and `client_secret` | `400 invalid_request` |
| Wrong grant_type | `grant_type=password` | `400 unsupported_grant_type` |
| Bootstrap + `package_id` `[§5]` | add `package_id=${PACKAGE_ANDROID}` | today: `200` · §5: `401 invalid_client` (`package_not_allowed_for_bootstrap`) |

## 2. Tenant credential — `package_id` MANDATORY

### 2.1 Mobile happy path `[§5]`
```bash
curl -i -X POST "${HOST}/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "client_id=${TENANT_CLIENT_ID}" \
  --data-urlencode "client_secret=${TENANT_CLIENT_SECRET}" \
  --data-urlencode "package_id=${PACKAGE_ANDROID}"
```
**Today:** `200` (field informational).
**§5 target:** `200`, audit contains `package_id`.

### 2.2 Mobile, no `package_id` — `package_missing` `[§5]`
Drop the `package_id` line. **Today:** `200`. **§5:** `401 invalid_client` (`package_missing`).

### 2.3 Mobile, wrong `package_id` — `package_mismatch` `[§5]`
```bash
--data-urlencode "package_id=com.unknown.app"
```
**Today:** `200`. **§5:** `401 invalid_client` (`package_mismatch`).

### 2.4 Mobile Base64-blob form, matching `package_id` `[§5]`
The RFC 6749 §2.3.1 form (`client_secret = base64(client_id:client_secret)`,
`client_id` empty):
```bash
BLOB=$(printf '%s:%s' "${TENANT_CLIENT_ID}" "${TENANT_CLIENT_SECRET}" | base64 -w0)

curl -i -X POST "${HOST}/v1/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "client_id=" \
  --data-urlencode "client_secret=${BLOB}" \
  --data-urlencode "package_id=${PACKAGE_ANDROID}"
```
**Today:** `400 invalid_request` (blob not decoded yet).
**§5:** `200`, audit contains `package_id`.

### 2.5 Mobile Base64-blob, no `package_id` `[§5]`
Drop the `package_id` line from 2.4. **Today:** `400 invalid_request`. **§5:** `401 invalid_client` (`package_missing`).

### 2.6 `Authorization: Basic` header + `package_id` `[§5]`
```bash
curl -i -X POST "${HOST}/v1/oauth/token" \
  -u "${TENANT_CLIENT_ID}:${TENANT_CLIENT_SECRET}" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "package_id=${PACKAGE_ANDROID}"
```
**Today:** `200` or `401` depending on whether the handler accepts Basic for this credential class.
**§5:** `200`, audit contains `package_id`.

### 2.7 Blob with `:` in secret — first colon splits `[§5]`
```bash
BLOB=$(printf '%s:%s' "${TENANT_CLIENT_ID}" "weird:secret:with:colons" | base64 -w0)
```
Same body as 2.4. **Today:** `400 invalid_request`. **§5:** `200`.

### 2.8 Malformed Base64 blob `[§5]`
```bash
--data-urlencode "client_secret=***not-base64***"
```
Same body as 2.4 with bad blob. **Today:** `400 invalid_request`. **§5:** `400 invalid_request`.

### 2.9 Backend happy path — synthetic `package_id` `[§5]`
Same shape as 2.1 but with `BACKEND_CLIENT_*` and `PACKAGE_BACKEND`:
```bash
--data-urlencode "client_id=${BACKEND_CLIENT_ID}" \
--data-urlencode "client_secret=${BACKEND_CLIENT_SECRET}" \
--data-urlencode "package_id=${PACKAGE_BACKEND}"
```
**Today:** `200`. **§5:** `200`, audit contains `package_id`.

### 2.10 Backend, no `package_id` — `package_missing` `[§5]`
Drop the `package_id` line from 2.9. **Today:** `200`. **§5:** `401 invalid_client` (`package_missing`). **The server-to-server exemption is gone.**

## 3. Allow-list states

```bash
# 3.1 Suspended row → all §5 mobile calls for it fail with package_mismatch
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "UPDATE tenancy.tenant_applications SET status='SUSPENDED', is_active=FALSE
   WHERE tenant_id='${TENANT_ID}' AND package_id='${PACKAGE_ANDROID}';"

# 3.2 Duplicate registration → DB unique-index violation (BR7)
docker compose -f docker/docker-compose.yml exec -T sbqr.postgres \
  psql -U postgres -d sbqr_app -c \
  "INSERT INTO tenancy.tenant_applications
     (tenant_application_id, tenant_id, platform, package_id, status, is_active, created_by)
   VALUES (gen_random_uuid(), '${TENANT_ID}', 'ANDROID', '${PACKAGE_ANDROID}',
           'ACTIVE', TRUE, 'dev-test');"
# Expect: ERROR: duplicate key value violates unique constraint
# "ix_tenant_applications_platform_package_id"
```

## 4. Sanity matrix

| Case | Today | §5 target |
|---|---|---|
| Bootstrap, valid creds | `200` | `200` |
| Bootstrap, wrong secret | `401 invalid_client` | `401 invalid_client` |
| Bootstrap, unknown client_id | `401 invalid_client` | `401 invalid_client` |
| Bootstrap, missing fields | `400 invalid_request` | `400 invalid_request` |
| Bootstrap, wrong grant_type | `400 unsupported_grant_type` | `400 unsupported_grant_type` |
| Bootstrap + `package_id` | `200` | `401 invalid_client` (`package_not_allowed_for_bootstrap`) |
| Mobile, valid + matching `package_id` | `200` | `200` |
| Mobile, valid, no `package_id` | `200` | `401 invalid_client` (`package_missing`) |
| Mobile, valid, wrong `package_id` | `200` | `401 invalid_client` (`package_mismatch`) |
| Mobile Base64-blob, matching `package_id` | `400 invalid_request` | `200` |
| Mobile Base64-blob, no `package_id` | `400 invalid_request` | `401 invalid_client` (`package_missing`) |
| Mobile Base64-blob, wrong `package_id` | `400 invalid_request` | `401 invalid_client` (`package_mismatch`) |
| Backend, valid + synthetic `package_id` | `200` | `200` |
| Backend, valid, no `package_id` | `200` | `401 invalid_client` (`package_missing`) |
| Suspended app row | `200` | `401 invalid_client` (`package_mismatch`) |
| Duplicate app registration | DB unique violation | `409 Conflict` (via §5.4 endpoint) |
