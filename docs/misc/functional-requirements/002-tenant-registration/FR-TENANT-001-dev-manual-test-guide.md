# FR-TENANT-001 — Dev Manual Test Guide (Tenant Registration 4-Step Gated Flow)

| Field   | Value                  |
|---------|------------------------|
| Area    | Tenancy + IdentityAccess + KeyCustody + InstitutionTrust |
| Status  | Live (FR-TENANT-001 implemented) |
| Updated | 2026-09-09             |

> **Reference spec.** Functional requirements, business rules, and the
> 4-step flow diagram: [`FR-TENANT-001-tenant-registration.md`](FR-TENANT-001-tenant-registration.md).
> This document is the **hands-on companion** — copy-pasteable `curl`
> payloads, the exact JSON shapes the wire DTOs accept, the expected
> status codes, and the SQL you can run after each step to confirm
> state. Test cases map back to FR-TENANT-001 §6 / §5 acceptance criteria.

---

## Table of contents

1. [Mental model](#1-mental-model)
2. [Prerequisites & environment](#2-prerequisites--environment)
3. [The 4-step happy path](#3-the-4-step-happy-path)
   - [Step 1 — Register the tenant](#step-1--register-the-tenant)
   - [Step 2 — Provision the tenant configuration](#step-2--provision-the-tenant-configuration)
   - [Step 3 — Mint the signing key](#step-3--mint-the-signing-key)
   - [Step 4 — Activate](#step-4--activate)
4. [Edge cases](#4-edge-cases)
   - [EC-1 Activate before tenant-configuration (no credential)](#ec-1-activate-before-tenant-configuration-no-credential)
   - [EC-2 Activate before crypto-keys (no signing key)](#ec-2-activate-before-crypto-keys-no-signing-key)
   - [EC-3 Activate before trust entry (recovery path)](#ec-3-activate-before-trust-entry-recovery-path)
   - [EC-4 Recovery: publish via `/admin/institutions` then re-activate](#ec-4-recovery-publish-via-admininstitutions-then-re-activate)
   - [EC-5 Reactivate after suspend with all rows intact](#ec-5-reactivate-after-suspend-with-all-rows-intact)
   - [EC-6 Reactivate after admin deletes a credential row](#ec-6-reactivate-after-admin-deletes-a-credential-row)
   - [EC-7 Activate an already-Active tenant (self-transition)](#ec-7-activate-an-already-active-tenant-self-transition)
   - [EC-8 Activate a Terminated tenant](#ec-8-activate-a-terminated-tenant)
   - [EC-9 Activate a tenant that does not exist](#ec-9-activate-a-tenant-that-does-not-exist)
   - [EC-10 Register with a duplicate `institutionCode`](#ec-10-register-with-a-duplicate-institutioncode)
   - [EC-11 Register with an invalid `institutionCode`](#ec-11-register-with-an-invalid-institutioncode)
   - [EC-12 Re-provision `tenant-configuration` while one is already active](#ec-12-re-provision-tenant-configuration-while-one-is-already-active)
   - [EC-13 Mint a second crypto-key while one is already Active](#ec-13-mint-a-second-crypto-key-while-one-is-already-active)
5. [Verification SQL](#5-verification-sql)
6. [Audit-trail expectations](#6-audit-trail-expectations)
7. [Troubleshooting](#7-troubleshooting)

---

## 1. Mental model

```
  operator (you)
      │
      │  ① register             → 201  tenant_id, status=Pending
      │  ② tenant-configuration → 201  client_id, client_secret (one-time)
      │  ③ crypto-keys          → 201  public_key_pem (auto-publish to BB)
      │  ④ activate             → 200  status=Active     (gate: ALL three rows ✓)
      ▼
  end state: tenant can mint verifiable QRs
```

**The gate at Step 4** is the new behaviour. Before
FR-TENANT-001, Step 4 was a no-op state transition; now it reads three
preconditions from cross-BC Contracts seams and refuses to activate if
any is missing:

| # | Precondition                       | Where it lives                              |
|---|------------------------------------|---------------------------------------------|
| 1 | ACTIVE row in `public.tenant_configurations` | IdentityAccess DbContext (via `ITenantConfigurationProvisioner.HasActiveAsync`) |
| 2 | ACTIVE row in `public.crypto_keys`         | KeyCustody DbContext       (via `HasActiveSigningKeyQuery`)        |
| 3 | ACTIVE row in `public.institution_keys` | InstitutionTrust DbContext (via `GetInstitutionPublicKeyQuery`) |

If any one is `false`, Step 4 returns `409 InvariantViolation` with the
readiness summary in the body — no state change, no audit row.

> **Why this matters in dev.** The local BbTrustStoreMock starts with an
> empty registry (the Annex A seed was removed). Auto-publish inside
> Step 3 therefore **always fails**, so the operator MUST run the
> recovery path (Step 3a → `POST /v1/admin/institutions`) before
> Step 4 will succeed. This is by design — it lets you exercise the
> upload flow end-to-end. Once BB publishes the real trust store,
> Step 3 will succeed inside, and the recovery path becomes a
> fallback for BB outages.

---

## 2. Prerequisites & environment

You need the docker-compose stack running (Postgres + SBQR.Api +
BbTrustStoreMock + LocalStack). From the repo root:

```bash
docker compose -f docker/docker-compose.yml up -d
# wait ~15s for healthchecks
docker compose -f docker/docker-compose.yml ps
```

| Service           | Host URL                       | Notes |
|-------------------|--------------------------------|-------|
| SBQR.Api          | `http://localhost:8080`        | Override with `SBQR_API_HOST_PORT` |
| BbTrustStoreMock  | `http://localhost:5002`        | User-local override; docker-compose default is `localhost:8082` (set via `BB_TRUST_STORE_MOCK_HOST_PORT`) |
| LocalStack (S3)   | `http://localhost:4566`        | Override with `LOCALSTACK_HOST_PORT` |
| Adminer (DB UI)   | `http://localhost:8081`        | Optional |

Confirm the trust store starts empty (it should — the dev seed was
removed; if you see `031008` listed, you're hitting a stale container):

```bash
curl -s http://localhost:5002/trust-store/institutions | jq 'length'
# expected: 0
```

You need an admin-scoped JWT for `POST /v1/admin/*`. Mint one with the
platform-bootstrap client (dev credentials: `platform-bootstrap` /
`dev-only-bootstrap-secret` from the docker-compose defaults):

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/v1/oauth/token \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  -d 'grant_type=client_credentials
&client_id=<platform-bootstrap-client-id>
&client_secret=<platform-bootstrap-client-secret>
&scope=admin' | jq -r .access_token)

export TOKEN
echo "$TOKEN" | cut -c1-40   # sanity
```

If `JWT__AllowInsecureForDev=true` is set (the docker default) you can
also pass the token via the `?dev_actor=…` query param — but using a
real JWT matches production and is preferred for manual testing.

> **Shell convention.** All curl examples below are POSIX (Git Bash on
> Windows). PowerShell users: replace `\` line continuations with
> backtick `` ` `` and `$()` works identically. `jq` is assumed in
> PATH; if not, strip the `| jq …` pipe.

---

## 3. The 4-step happy path

The example uses `institutionCode=031008` (Pathao Pay). Pick any
6-digit code that does not already exist in `public.tenants`.

### Step 1 — Register the tenant

**Endpoint.** `POST /v1/admin/tenants`

**Payload.**

```json
{
  "institutionName": "Pathao Pay (Dev)",
  "institutionCode": "031008"
}
```

**Curl.**

```bash
TENANT_JSON=$(curl -s -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "institutionName": "Pathao Pay (Dev)",
    "institutionCode": "031008"
  }')

echo "$TENANT_JSON" | jq .
TENANT_ID=$(echo "$TENANT_JSON" | jq -r .tenantId)
echo "TENANT_ID=$TENANT_ID"
export TENANT_ID
```

**Expected response — 201 Created.**

```json
{
  "tenantId": "7d2f6e9c-3b4a-4f1d-9c0a-1a2b3c4d5e6f",
  "institutionName": "Pathao Pay (Dev)",
  "institutionCode": "031008",
  "status": "Pending",
  "isActive": true
}
```

The response includes a `Location` header pointing at
`GET /v1/admin/tenants/{id}`. **`status` is `Pending`** — the tenant
is not Active yet; Steps 2-4 must follow.

**State after.** One row in `public.tenants` with `status='Pending'`.
Nothing in `public.tenant_configurations`, `public.crypto_keys`,
or `public.institution_keys` yet. One `tenant.created`
audit row.

### Step 2 — Provision the tenant configuration

**Endpoint.** `POST /v1/admin/tenants/{id}/tenant-configuration`

**Payload.** None — the request body is empty. The endpoint mints the
tenant's first (and only-until-revoked) client credential.

**Curl.**

```bash
CFG_JSON=$(curl -s -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN")

echo "$CFG_JSON" | jq .
CLIENT_ID=$(echo "$CFG_JSON" | jq -r .clientId)
CLIENT_SECRET=$(echo "$CFG_JSON" | jq -r .clientSecret)
export CLIENT_ID CLIENT_SECRET
echo "CLIENT_ID=$CLIENT_ID"
echo "CLIENT_SECRET=$CLIENT_SECRET    # save this NOW; never re-displayed"
```

**Expected response — 201 Created.**

```json
{
  "tenantId": "7d2f6e9c-3b4a-4f1d-9c0a-1a2b3c4d5e6f",
  "credentialId": "9b1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e",
  "clientId": "031008-7c1b4d88",
  "clientSecret": "RkFLq7nQp3...<256-bit base64url>...xZ2m",
  "expiresAt": "2027-09-09T00:00:00+00:00"
}
```

> **⚠ One-time secret.** `clientSecret` is the plaintext, returned here
> **exactly once**. Only its Argon2id hash is persisted; there is no
> re-display endpoint. Lose it → suspend the credential, then
> re-provision.

**State after.** One ACTIVE row in
`public.tenant_configurations` for this tenant (`status='Active'`,
`client_secret_hash` populated). One `tenant_configuration.provisioned`
audit row. Gate precondition 1 is now ✓.

### Step 3 — Mint the signing key

**Endpoint.** `POST /v1/crypto-keys`

**Payload (Generate mode — server mints the Ed25519 keypair).**

```json
{
  "tenantId": "7d2f6e9c-3b4a-4f1d-9c0a-1a2b3c4d5e6f",
  "mode": "Generate"
}
```

**Curl.**

```bash
KEY_JSON=$(curl -s -X POST http://localhost:8080/v1/crypto-keys \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{
    \"tenantId\": \"$TENANT_ID\",
    \"mode\": \"Generate\"
  }")

echo "$KEY_JSON" | jq .
PUBLIC_KEY_PEM=$(echo "$KEY_JSON" | jq -r .publicKeyPem)
export PUBLIC_KEY_PEM
```

**Expected response — 201 Created** (`CryptoKeySummary`, when BB trust store is reachable):

```json
{
  "cryptoKeyId": "b1e0f2c7-3a8d-4d9a-9c51-6f2c8b7e1a04",
  "tenantId": "7d2f6e9c-3b4a-4f1d-9c0a-1a2b3c4d5e6f",
  "keyId": "sbqr-signing",
  "keyVersion": 1,
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEAGb9ECWmEzf6FQbrBZ9w7lshQhqowtrbLDFw4rXAxZuE=\n-----END PUBLIC KEY-----",
  "status": "Active",
  "isActive": true,
  "createdAt": "2026-09-09T10:15:30+00:00"
}
```

The response also includes a `Location` header pointing at
`GET /v1/crypto-keys/{tenantId}/active`.

**Expected response — 409 Conflict** (in local dev, the BbTrustStoreMock
starts empty; auto-publish fails; `crypto_key` row commits but the
key is returned with `status='Active'` and a `crypto_key.trust_publish_failed`
audit row is written):

```json
{
  "title": "Signing key invariant violated",
  "status": 409,
  "detail": "trust publish failed: ..."
}
```

> **This is the recovery trigger.** When Step 3 returns 409, the
> `crypto_keys` row IS committed (`status='Active'`), `publicKeyPem` is
> in the response body before the 409 — copy it for the recovery
> step below (EC-4). The tenant is *not* in a broken state; only the
> trust-directory publish step failed. Continue with EC-4 instead of
> retrying.

**State after (in either case).** One ACTIVE row in
`public.crypto_keys` for this tenant (`status='Active'`,
`key_version=1`). Gate precondition 2 is now ✓.

> **Auto-publish path.** If `TrustStore__BaseUrl` points at a real BB
> trust store and the publish succeeds, you also get one ACTIVE row
> in `public.institution_keys` for `institution_code='031008'`
> (`status='Active'`, `public_key_pem=<the same PEM>`). Gate
> precondition 3 is then ✓ too — skip EC-4 and go straight to Step 4.

### Step 4 — Activate

**Endpoint.** `POST /v1/admin/tenants/{id}/activate`

**Payload.** None.

**Curl.**

```bash
ACT_JSON=$(curl -s -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN")

echo "$ACT_JSON" | jq .
```

**Expected response — 200 OK** (gate passes; auto-publish succeeded in
Step 3, OR you completed EC-4 first):

```json
{
  "tenantId": "7d2f6e9c-3b4a-4f1d-9c0a-1a2b3c4d5e6f",
  "institutionName": "Pathao Pay (Dev)",
  "institutionCode": "031008",
  "status": "Active",
  "isActive": true
}
```

**Expected response — 409 Conflict** (any precondition false; no state
change; no audit row):

```json
{
  "title": "Tenant invariant violated",
  "status": 409,
  "detail": "Tenant 7d2f6e9c-… cannot be activated: credential=True, signing_key=True, trust_entry=False"
}
```

The readiness summary in `detail` tells you exactly which precondition
failed. Map back to §4's edge cases for the matching recovery path.

**State after (200 case).** The row in `public.tenants` is now
`status='Active'`. One `tenant.activated` audit row. The tenant can
now mint verifiable QR codes.

---

## 4. Edge cases

Each edge case continues from the §3 happy-path setup, with the
relevant step **omitted or reordered** to provoke the failure. Use the
same `$TOKEN`, `$TENANT_ID`, `$CLIENT_ID`, `$CLIENT_SECRET`,
`$PUBLIC_KEY_PEM` variables from §3 (re-export them in your shell).

Map back to FR-TENANT-001 §6 test cases for the formal acceptance list.

### EC-1 Activate before tenant-configuration (no credential)

**Setup.** Run only Step 1, then jump to Step 4.

```bash
# Step 1
TENANT_ID=$(curl -s -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"institutionName":"EC-1 Bank","institutionCode":"100001"}' \
  | jq -r .tenantId)

# Skip Steps 2, 3. Jump to Step 4:
curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```json
{
  "title": "Tenant invariant violated",
  "status": 409,
  "detail": "Tenant <TENANT_ID> cannot be activated: credential=False, signing_key=False, trust_entry=False"
}
```

**Then verify** (state is unchanged):

```bash
curl -s "http://localhost:8080/v1/admin/tenants/$TENANT_ID" \
  -H "Authorization: Bearer $TOKEN" | jq .status
# expected: "Pending"
```

**Recovery.** Run Steps 2, 3 (or 3 + EC-4), then re-run Step 4.

> **Maps to.** FR-TENANT-001 §5 AC2, TC-01.

### EC-2 Activate before crypto-keys (no signing key)

**Setup.** Run Steps 1, 2; skip Step 3; run Step 4.

```bash
TENANT_ID=$(curl -s -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"EC-2 Bank","institutionCode":"100002"}' \
  | jq -r .tenantId)

curl -s -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN" >/dev/null   # Step 2

# Skip Step 3. Jump to Step 4:
curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```
detail: "Tenant <…> cannot be activated: credential=True, signing_key=False, trust_entry=False"
```

**Recovery.** Run Step 3 (and EC-4 if auto-publish fails), then
re-run Step 4.

> **Maps to.** FR-TENANT-001 §5 AC3, TC-02.

### EC-3 Activate before trust entry (recovery path)

**Setup.** Run Steps 1, 2, 3; **without EC-4**; run Step 4.

This is the realistic local-dev scenario — auto-publish fails because
the trust store is empty, so `trust_entry=false` is the only failing
flag.

```bash
TENANT_ID=$(curl -s -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"EC-3 Bank","institutionCode":"100003"}' \
  | jq -r .tenantId)

curl -s -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN" >/dev/null   # Step 2

KEY_RESP=$(curl -s -X POST http://localhost:8080/v1/crypto-keys \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"tenantId\":\"$TENANT_ID\",\"mode\":\"Generate\"}")
# In local dev this returns 409 — capture the PEM anyway:
PUBLIC_KEY_PEM=$(echo "$KEY_RESP" | jq -r .publicKeyPem // empty)

# Jump to Step 4 WITHOUT recovery:
curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```
detail: "Tenant <…> cannot be activated: credential=True, signing_key=True, trust_entry=False"
```

**Recovery.** Continue to **EC-4** to publish via `/admin/institutions`,
then re-run Step 4.

> **Maps to.** FR-TENANT-001 §5 AC4, TC-03.

### EC-4 Recovery: publish via `/admin/institutions` then re-activate

**Setup.** Continue from EC-3 (Step 3 has committed `crypto_keys`,
auto-publish failed, `publicKeyPem` captured in `$PUBLIC_KEY_PEM`).

**Step 3a — manual trust-directory publish.**

**Endpoint.** `POST /v1/admin/institutions` (the *Tenancy-side* admin
controller in `SBQR.Api`). For local dev with the BbTrustStoreMock
running separately, you also need to push the PEM into the mock so
`DailyTrustSyncService` (or `InstitutionTrustPublisher`) can see it.
Both options are shown below — pick the one matching your topology.

**Option A — push to SBQR.Api's admin route** (works whether or not the
mock is the only trust source; this is the canonical recovery path
documented in FR-TENANT-001 §1.2 / BR-Recovery-1):

```bash
curl -i -X POST http://localhost:8080/v1/admin/institutions \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{
    \"institutionCode\": \"100003\",
    \"institutionName\": \"EC-3 Bank\",
    \"publicKeyPem\": $(jq -nR --arg p "$PUBLIC_KEY_PEM" '$p'),
    \"instituteType\": \"03\"
  }"
```

**Option B — push to BbTrustStoreMock directly** (when the mock IS the
trust source for dev, e.g., via `TrustStore__BaseUrl=http://localhost:8082`):

```bash
curl -i -X PUT http://localhost:8082/trust-store/institutions/100003/public-key \
  -H 'Content-Type: application/json' \
  -d "{
    \"institutionName\": \"EC-3 Bank\",
    \"publicKeyPem\": $(jq -nR --arg p "$PUBLIC_KEY_PEM" '$p')
  }"
```

(Option A is the production-shaped path; Option B is a dev-only escape
hatch for when `TrustStore__BaseUrl` points at the mock and you want
to skip the sync cycle.)

**Expected (Option A).** `201 Created` with the upserted institution
summary. `DailyTrustSyncService` will mirror this into the mock on
its next tick (within `TrustStore__SyncIntervalHours`).

**Now re-run Step 4.**

```bash
curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `200 OK`, `status="Active"`.

> **If Step 4 still returns 409 after Option A:** the
> `DailyTrustSyncService` may not have ticked yet. Either wait for the
> next tick, or trigger a manual sync via the dev-only
> `POST /v1/admin/trust-store/sync` endpoint (see
> [`docs/dev-manual-test-guide.md` §6](../../dev-manual-test-guide.md)).
> Alternatively, use Option B to push straight into the mock so the
> next `GetInstitutionPublicKeyQuery` reads succeed immediately.

> **Maps to.** FR-TENANT-001 §5 AC5, TC-08.

### EC-5 Reactivate after suspend with all rows intact

**Setup.** Run the full §3 happy path (tenant ends `Active`); suspend
it (Step 5); reactivate (Step 6). The gate MUST pass — all three rows
still exist; only the tenant's own status flipped to Suspended.

```bash
# Step 5 — suspend
curl -s -X POST "http://localhost:8080/v1/admin/tenants/$TENANT_ID/suspend" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"reason":"EC-5 manual suspend"}' | jq .status
# expected: "Suspended"

# Step 6 — reactivate
curl -s -X POST "http://localhost:8080/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $TOKEN" | jq .status
# expected: "Active"
```

> **`suspend` cascades onto `tenant_configurations` and `crypto_keys`** —
> their rows go `Suspended` too. **`reactivate` reinstates both**.
> After EC-5, all three rows are `Active` again.

> **Maps to.** FR-TENANT-001 §5 AC6 (first half).

### EC-6 Reactivate after admin deletes a credential row

**Setup.** Run §3 + EC-5 (tenant re-`Active`). Now simulate the
"admin deletes a credential row directly in the DB" failure mode:

```bash
# Simulate the manual deletion (dev-only; never do this in prod):
PGPASSWORD=postgres psql -h localhost -U postgres -d sbqr_app -c "
  UPDATE public.tenant_configurations
  SET status='Revoked', revoked_at=now()
  WHERE tenant_id = '$TENANT_ID';
"

# Suspend the tenant (so the gate gets re-evaluated):
curl -s -X POST "http://localhost:8080/v1/admin/tenants/$TENANT_ID/suspend" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"EC-6 prep"}' >/dev/null

# Reactivate — gate must catch the missing credential:
curl -i -X POST "http://localhost:8080/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```
detail: "Tenant <…> cannot be activated: credential=False, signing_key=True, trust_entry=True"
```

**Recovery.** Re-provision the tenant configuration (which will return
a fresh `clientSecret` since the old one is `Revoked`), then
re-run reactivate:

```bash
curl -s -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN" | jq .clientSecret
# expected: new plaintext secret. Save it.

curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/reactivate" \
  -H "Authorization: Bearer $TOKEN"
# expected: 200, status="Active"
```

> **Maps to.** FR-TENANT-001 §5 AC6 (second half), TC-05.

### EC-7 Activate an already-Active tenant (self-transition)

**Setup.** Tenant is already `Active` (e.g., from §3 happy path).

```bash
curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```
detail: "Tenant <…> cannot be activated: already Active"
```

> **Why this matters.** The aggregate's own self-transition guard fires
> **before** the gate runs — this is BR-Activation-2 (gate is idempotent
> on the aggregate side). So a re-activate on an already-Active tenant
> fails the same way it did before FR-TENANT-001: with the same 409
> shape. No regression.

> **Maps to.** FR-TENANT-001 §5 AC7 (existing failure mode), TC-06.

### EC-8 Activate a Terminated tenant

**Setup.** Tenant is Terminated (`POST /…/terminate` was called).

```bash
curl -s -X POST "http://localhost:8080/v1/admin/tenants/$TENANT_ID/terminate" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"EC-8"}' | jq .status
# expected: "Terminated", isActive=false

curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```
detail: "Tenant <…> cannot be activated: terminal state (Terminated)"
```

> **Why.** `Tenant.Activate` throws on `Terminated` (terminal state),
> mapped to `InvariantViolation → 409`. Gate is not the failing
> layer here; this is the aggregate's own invariant.

> **Maps to.** FR-TENANT-001 §5 AC7 (existing failure mode).

### EC-9 Activate a tenant that does not exist

**Setup.** Use a random GUID that is not in `public.tenants`.

```bash
GHOST_ID=$(uuidgen)
curl -i -X POST "http://localhost:8080/v1/admin/tenants/$GHOST_ID/activate" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `404 Not Found`,

```json
{
  "title": "Tenant not found",
  "status": 404,
  "detail": "tenant <GHOST_ID> does not exist."
}
```

> **Why.** The handler short-circuits on `GetByIdAsync == null` and the
> gate is **not** evaluated on the NotFound path — there is no tenant
> to check readiness for.

> **Maps to.** FR-TENANT-001 §5 AC7 (existing failure mode).

### EC-10 Register with a duplicate `institutionCode`

**Setup.** Run Step 1 with `institutionCode='031008'`, then run Step 1
again with the same code.

```bash
curl -i -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"First 031008","institutionCode":"031008"}'
# expected: 201

curl -i -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"Dup 031008","institutionCode":"031008"}'
```

**Expected.** `409 Conflict`,

```json
{
  "title": "Tenant invariant violated",
  "status": 409,
  "detail": "tenant with institution_code '031008' already exists."
}
```

> **Why.** The DB-level UNIQUE constraint on `public.tenants.institution_code`
> is the authoritative guard (validator can't be pure, so uniqueness
> is not in the FluentValidation rule). Handler maps the constraint
> violation to `InvariantViolation → 409`.

### EC-11 Register with an invalid `institutionCode`

**Setup.** Send a malformed `institutionCode` (anything that is not
exactly six digits).

```bash
curl -i -X POST http://localhost:8080/v1/admin/tenants \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"institutionName":"Bad Code Bank","institutionCode":"abc123"}'
```

**Expected.** `400 Bad Request` (validation failure; surfaces via the
global `ValidationBehavior`),

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "InstitutionCode": ["'InstitutionCode' must match ^[0-9]{6}$."]
  }
}
```

Try also `institutionCode="12345"` (5 digits), `"1234567"` (7 digits),
`""` (empty) — all should produce the same `400` with the regex error.

> **Why.** `CreateTenantValidator` rejects any value that is not
> exactly six digits before the handler runs.

### EC-12 Re-provision `tenant-configuration` while one is already active

**Setup.** Tenant has an Active `tenant_configurations` row (after
Step 2).

```bash
curl -i -X POST \
  "http://localhost:8080/v1/admin/tenants/$TENANT_ID/tenant-configuration" \
  -H "Authorization: Bearer $TOKEN"
```

**Expected.** `409 Conflict`,

```json
{
  "title": "Tenant invariant violated",
  "status": 409,
  "detail": "tenant <…> already has an active credential; suspend or revoke it first."
}
```

> **Why.** `ProvisionTenantConfigurationCommandHandler` refuses to mint
> a second active credential while one exists — rotation is a separate
> future flow. Use the Suspend cascade to revoke the old one first if
> you need a fresh secret.

### EC-13 Mint a second crypto-key while one is already Active

**Setup.** Tenant has an Active `crypto_keys` row (after Step 3).

```bash
curl -i -X POST http://localhost:8080/v1/crypto-keys \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"tenantId\":\"$TENANT_ID\",\"mode\":\"Generate\"}"
```

**Expected.** `409 Conflict`,

```json
{
  "title": "Signing key invariant violated",
  "status": 409,
  "detail": "tenant <…> already has an active key (version 1); rotate via PUT /v1/crypto-keys/{tenantId} instead."
}
```

> **Why.** Same shape as EC-12: rotation is a separate flow. Use
> `PUT /v1/crypto-keys/{tenantId}` (no body) to retire the current
> ACTIVE row and mint `version+1`.

### EC-14 Adopt-mode happy path (institute supplies its own keypair)

**Setup.** A throwaway Ed25519 keypair generated locally; tenant is at
Step 2 (credential provisioned, no key yet).

```bash
# 1. Generate a throwaway Ed25519 pair. Production keys live in the
#    institute's HSM / air-gapped machine — never `openssl` for prod.
openssl genpkey -algorithm Ed25519 -out /tmp/inst-priv.pem
openssl pkey -in /tmp/inst-priv.pem -pubout -out /tmp/inst-pub.pem

# 2. Pre-seed the trust row so the drift guard has something to match
#    against. In production this is done by BB out-of-band; locally
#    we use the same public half we'll Adopt below.
curl -sS -X POST "http://localhost:8080/v1/admin/institutions" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H 'Content-Type: application/json' \
  -d "$(jq -n --arg code "$INSTITUTION_CODE" \
                --arg pub  "$(cat /tmp/inst-pub.pem)" \
       '{institutionCode: $code, institutionName: "ACME Bank", publicKeyPem: $pub}')"

# 3. POST /v1/crypto-keys in Adopt mode.
curl -i -X POST http://localhost:8080/v1/crypto-keys \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg tid "$TENANT_ID" \
                --arg pub  "$(cat /tmp/inst-pub.pem)" \
                --arg priv "$(cat /tmp/inst-priv.pem)" \
       '{tenantId: $tid, mode: "Adopt", publicKeyPem: $pub, privateKeyPem: $priv}')"
```

**Expected.** `201 Created` (`CryptoKeySummary`):

```json
{
  "cryptoKeyId": "b1e0f2c7-…",
  "tenantId": "8c4a…",
  "keyId": "sbqr-signing",
  "keyVersion": 1,
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n…canonical SPKI…\n-----END PUBLIC KEY-----",
  "status": "Active",
  "isActive": true,
  "createdAt": "2026-09-09T12:34:56Z"
}
```

> The wire shape does **not** include `publicKeySha256`. To verify the
> canonical sha-256 the platform computed, query the DB after the call:
> `SELECT public_key_sha256 FROM public.crypto_keys WHERE tenant_id=…;`.
> The PEM is canonicalised by BouncyCastle before hashing, so the value
> is stable across re-uploads that differ only in trailing newline or
> header casing. The S3 object key in LocalStack is
> `keys/<institutionCode>_v1_private.pem` — the same key Generate uses;
> Adopt overwrites in place at the same `(institution, version)`.

### EC-15 Adopt-mode drift rejection (409)

**Setup.** After EC-14 succeeds (trust row sha256=X), generate a
*second* Ed25519 pair and try to Adopt it on the same tenant.

```bash
openssl genpkey -algorithm Ed25519 -out /tmp/inst-priv-2.pem
openssl pkey -in /tmp/inst-priv-2.pem -pubout -out /tmp/inst-pub-2.pem

curl -i -X POST http://localhost:8080/v1/crypto-keys \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg tid "$TENANT_ID" \
                --arg pub  "$(cat /tmp/inst-pub-2.pem)" \
                --arg priv "$(cat /tmp/inst-priv-2.pem)" \
       '{tenantId: $tid, mode: "Adopt", publicKeyPem: $pub, privateKeyPem: $priv}')"
```

**Expected.** `409 Conflict`,

```json
{
  "title": "Signing key invariant violated",
  "status": 409,
  "detail": "trust_store public-key drift: institute <code> already has a trusted public key (sha256=9f2e…) which does NOT match the Adopt payload's canonical public key (sha256=ab12…). Either supply the matching public half or rotate the trust row explicitly via POST /v1/admin/institutions before retrying."
}
```

Confirm with the verification SQL in §5 that:

- No row was added to `public.crypto_keys`.
- No new object landed in the LocalStack `sbqr-dev/keys/` bucket.
- `public.audit_logs` has a `crypto_key.adopt.drift_rejected` row with
  the `existing_pk_sha256` and `supplied_pk_sha256` in its `metadata`.

### EC-16 Adopt-mode mismatched PEM halves (400)

**Setup.** Adopt the second priv against the FIRST pub — they don't
match.

```bash
curl -i -X POST http://localhost:8080/v1/crypto-keys \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg tid "$TENANT_ID" \
                --arg pub  "$(cat /tmp/inst-pub.pem)" \
                --arg priv "$(cat /tmp/inst-priv-2.pem)" \
       '{tenantId: $tid, mode: "Adopt", publicKeyPem: $pub, privateKeyPem: $priv}')"
```

**Expected.** `400 Bad Request`,

```json
{
  "title": "Signing key operation failed",
  "status": 400,
  "detail": "Supplied 'private_key_pem' does NOT match the supplied 'public_key_pem': the derived public SPKI differs from the supplied public SPKI."
}
```

---

## 5. Verification SQL

Run these against the docker Postgres (`localhost:5432`, `postgres` /
`postgres`, db `sbqr_app`) to confirm state after each step.

```bash
PG="PGPASSWORD=postgres psql -h localhost -U postgres -d sbqr_app -t -A"
```

| After step | Query | Expected |
|---|---|---|
| 1 | `SELECT id, institution_name, institution_code, status FROM public.tenants WHERE institution_code='031008';` | 1 row, `status='Pending'` |
| 2 | `SELECT id, tenant_id, client_id, status FROM public.tenant_configurations WHERE tenant_id='<TENANT_ID>';` | 1 row, `status='Active'` |
| 3 | `SELECT tenant_id, key_version, algorithm, status FROM public.crypto_keys WHERE tenant_id='<TENANT_ID>';` | 1 row, `status='Active'`, `key_version=1` |
| 3 (auto-publish OK) | `SELECT institution_code, key_version, status FROM public.institution_keys WHERE institution_code='031008';` | 1 row, `status='Active'` |
| 3 (auto-publish FAILED — local dev) | Same query | 0 rows; check `public.audit_logs` for `crypto_key.trust_publish_failed` |
| 4 (gate passes) | `SELECT status FROM public.tenants WHERE id='<TENANT_ID>';` | `status='Active'` |
| 4 (gate fails) | `SELECT status FROM public.tenants WHERE id='<TENANT_ID>';` | unchanged (`Pending` or `Suspended`) |

For the EC-4 recovery path, after pushing via `POST /v1/admin/institutions`:

```bash
# Wait for DailyTrustSyncService to tick (or trigger it manually per
# docs/dev-manual-test-guide.md §6) and then:
$PG -c "SELECT institution_code, key_version, status
        FROM public.institution_keys
        WHERE institution_code='031008';"
# expected: 1 row, status='Active'
```

---

## 6. Audit-trail expectations

Each step writes a row to `public.audit_logs`. Use this query to dump
the chain for a given tenant:

```bash
$PG -c "SELECT action, actor_id, resource_type, resource_id,
               metadata::text, created_at
        FROM public.audit_logs
        WHERE resource_id = '$TENANT_ID'::text
           OR metadata::text LIKE '%$TENANT_ID%'
        ORDER BY created_at;"
```

Expected chain for the §3 happy path (with auto-publish OK):

| # | `action`                          | Resource           |
|---|-----------------------------------|--------------------|
| 1 | `tenant.created`                  | `Tenant/<id>`      |
| 2 | `tenant_configuration.provisioned`| `TenantConfiguration/<credentialId>` |
| 3 | `crypto_key.created`              | `CryptoKey/<id>`   |
| 4 | `crypto_key.trust_published`      | `InstitutionKey/<id>` (only if auto-publish succeeded) |
| 5 | `tenant.activated`                | `Tenant/<id>`      |

For EC-4 (auto-publish FAILED then recovery), the chain looks like:

| # | `action`                              | Resource           |
|---------------------------------------|--------------------|---|
| 1 | `tenant.created`                      | `Tenant/<id>`      |
| 2 | `tenant_configuration.provisioned`    | `TenantConfiguration/<id>` |
| 3 | `crypto_key.created`                  | `CryptoKey/<id>`   |
| 4 | `crypto_key.trust_publish_failed`     | `CryptoKey/<id>`   |
| 5 | `institution.upserted`                | `Institution/<code>` (from `POST /v1/admin/institutions`) |
| 6 | `tenant.activated`                    | `Tenant/<id>`      |

For a gate-failure (§4 EC-1/2/3/6), there is **no** `tenant.activated`
row — the transaction rolls back before the audit call. Only the
pre-failure steps' audit rows exist.

---

## 7. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Step 4 returns 409 `credential=False` after Step 2 returned 201 | `ProvisionTenantConfigurationCommand` silently rolled back (validator failure not surfaced) | Re-run Step 2 with `curl -i` to see the actual status; check `public.audit_logs` for the most recent `tenant_configuration.*` row |
| Step 3 returns 409 `trust publish failed: ...` in local dev | The BbTrustStoreMock starts empty (the Annex A seed was removed) | This is **expected**. Continue with **EC-4** (Step 3a). |
| Step 4 returns 409 `trust_entry=False` after EC-4's `POST /v1/admin/institutions` returned 201 | `DailyTrustSyncService` hasn't ticked yet | Wait `TrustStore__SyncIntervalHours` (default 24h), or trigger a manual sync; alternatively use EC-4 Option B (push straight to mock) |
| Step 1 returns 409 `already exists` | `institutionCode` collision | Pick a new 6-digit code not in `public.tenants` |
| Token returns `401` | `$TOKEN` expired (default 60 min) | Re-run the OAuth step in §2 |
| `curl` returns `Connection refused` on `localhost:5002` | BbTrustStoreMock not running, or running on a different port | `docker compose ps`; check `BB_TRUST_STORE_MOCK_HOST_PORT` in `docker/.env` |
| `curl` returns `Connection refused` on `localhost:8080` | SBQR.Api not running | `docker compose up -d sbqr.api`; check `SBQR_API_HOST_PORT` |

For deeper issues, see:

- [`docs/dev-manual-test-guide.md`](../../dev-manual-test-guide.md) — broader dev manual covering OAuth, QR generation, verification.
- [`docs/new-tenant-onboard-guide.md`](../../new-tenant-onboard-guide.md) — the operator-runbook form of this flow (lighter on curl).
- [`docs/bootstrap-dev-guide.md`](../../bootstrap-dev-guide.md) — getting the docker stack up from a cold clone.
- [FR-TENANT-001-review.md](FR-TENANT-001-review.md) — the rejected larger designs (saga, readiness gate, mark-ready command).
