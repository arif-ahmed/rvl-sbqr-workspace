# Public vs Internal API docs — usage & testing guide

Companion to
[`docs/superpowers/specs/2026-09-04-public-vs-internal-api-docs-design.md`](superpowers/specs/2026-09-04-public-vs-internal-api-docs-design.md)
(the design). This doc is the practical "how do I actually use this"
reference for engineers and for the team onboarding institute clients.

## The two documents

| | `v1.public` | `v1.internal-admin` |
|---|---|---|
| OpenAPI JSON | `GET /openapi/v1.public.json` | `GET /openapi/v1.internal-admin.json` |
| Browsable UI | `GET /docs/public` | `GET /docs/internal-admin` |
| Auth to **view the docs** | none | HTTP Basic Auth (`Docs:InternalAdmin`) |
| Auth to **call the endpoints** | bearer token from `/v1/oauth/token`, per-endpoint scope | bearer token, `admin` scope |
| Endpoints | `POST /v1/oauth/token`, `POST /v1/qr/generate/static`, `POST /v1/qr/generate/dynamic`, `POST /v1/qr/validate`, `GET /health/live`, `GET /health/ready` | `POST /v1/admin/tenants`, `POST /v1/admin/tenants/{id}/tenant-configuration`, `POST /v1/admin/tenants/{id}/suspend`, `POST /v1/admin/tenants/{id}/reactivate`, `GET /v1/admin/tenants/{id}`, `POST /v1/admin/institutions` |
| Environments | every environment, always on | every environment including Production, always gated |

**Important:** being listed in `v1.public` does not mean an endpoint is
anonymous. `qr/generate/{static,dynamic}` and `qr/validate` still require
a valid bearer token with the matching scope — the public document just
means "every integrating tenant needs to know this endpoint exists." The
internal-admin Basic Auth credential only gates *reading the documentation
page*; it is never accepted as API authorization.

## For institute (tenant) clients — public API

1. Point them at `GET /docs/public` for the interactive reference, or hand
   them `GET /openapi/v1.public.json` for SDK/codegen.
2. Golden path:
   ```bash
   # 1. Exchange client credentials for a bearer token
   curl -X POST http://localhost:5080/v1/oauth/token \
     -d grant_type=client_credentials \
     -d client_id=<tenant client_id> \
     -d client_secret=<tenant client_secret>
   # -> { "access_token": "...", "token_type": "Bearer", "expires_in": 600 }

   # 2a. Generate a static QR (Tag 01 = "11"; no transactionAmount)
   curl -X POST http://localhost:5080/v1/qr/generate/static \
     -H "Authorization: Bearer <access_token>" \
     -H "Content-Type: application/json" \
     -H "Idempotency-Key: <client-generated uuid>" \
     -d '{
           "recipientName": "Example Merchant",
           "recipientCity": "Dhaka",
           "recipientPan": "0123456789012"
         }'

   # 2b. Or generate a dynamic QR (Tag 01 = "12"; transactionAmount required)
   curl -X POST http://localhost:5080/v1/qr/generate/dynamic \
     -H "Authorization: Bearer <access_token>" \
     -H "Content-Type: application/json" \
     -H "Idempotency-Key: <client-generated uuid>" \
     -d '{
           "transactionAmount": "150.00",
           "recipientName": "Example Merchant",
           "recipientCity": "Dhaka",
           "recipientPan": "0123456789012"
         }'

   # 3. Verify a scanned QR
   curl -X POST http://localhost:5080/v1/qr/validate \
     -H "Authorization: Bearer <access_token>" \
     -H "Content-Type: application/json" \
     -d '{ "qrPayload": "<raw QR string>" }'
   ```
3. `POST /v1/oauth/token` is rate-limited to 10 requests/minute per remote IP
   (`RateLimitPolicyNames.TokenEndpoint`) — a client hammering it gets `429`.

## For the platform team — internal-admin (client onboarding, trust-key mgmt)

### One-time setup: generate the shared docs credential

```bash
dotnet run --project src/Host/SBQR.Api -- --generate-docs-credential
```

Prints a random password **once** plus its Argon2id PHC hash. Put the
password in the team password manager; put the hash in config:

- Local dev: user-secrets —
  `dotnet user-secrets set "Docs:InternalAdmin:PasswordHash" "<phc>" --project src/Host/SBQR.Api/SBQR.Api.csproj`.
- Staging/Production: env var `Docs__InternalAdmin__PasswordHash`, sourced
  from Key Vault. **The host refuses to start in Production without this
  set** — see the boot guard in `Program.cs` (§10a).

`Docs:InternalAdmin:Username` defaults to `platform-team`; override via
config if you want a different name.

### Browsing the internal-admin docs

`GET /docs/internal-admin` — browser will prompt for HTTP Basic Auth;
enter the username/password from the step above. Same credential gates
`GET /openapi/v1.internal-admin.json` directly (e.g. for `curl -u`).

### Onboarding a new institute client

Requires a bearer token with the `admin` scope (see "Minting an admin
token" below). Two steps — registration and credential issuance are
deliberately separate endpoints.

```bash
# 1. Register the tenant — creates the row in Pending; NO configuration yet.
curl -X POST http://localhost:5080/v1/admin/tenants \
  -H "Authorization: Bearer <admin access_token>" \
  -H "Content-Type: application/json" \
  -d '{
        "institutionName": "Example Bank Ltd.",
        "institutionCode": "010101"
      }'
# -> 201 Created { "tenantId": "…", "status": "Pending", … }

# 2. Provision the FI client configuration (Pending and Active tenants only).
#    The client_id is shaped {institutionCode}-{8hex}.
curl -X POST http://localhost:5080/v1/admin/tenants/<tenantId>/tenant-configuration \
  -H "Authorization: Bearer <admin access_token>"
# -> 201 Created
#    { "tenantId": "…", "credentialId": "…",
#      "clientId": "010101-7c1b4d88", "clientSecret": "…",
#      "expiresAt": "…" }
```

Hand `clientId` + `clientSecret` to the institute — the plaintext secret is
shown **exactly once** (only its Argon2id hash is stored) and is never
recoverable afterward. The institute then authenticates at
`POST /v1/oauth/token` with just those two values; no institute code is needed
at token time because it is embedded in the `clientId`. Re-provisioning
while the configuration is active returns `409` (rotation is a separate flow).

Suspend / reactivate: `POST /v1/admin/tenants/{id}/suspend`,
`POST /v1/admin/tenants/{id}/reactivate` (same bearer token, optional
`{"reason": "..."}` body on suspend).

Register the institution's public key in the trust directory. There are
two paths; pick the one that fits the flow:

- **Auto-publish on crypto-create (recommended).** The handler behind
  `POST /v1/crypto-keys` now publishes the freshly minted public key
  into the trust directory as part of the same operation — a successful
  201 means `qr/validate` can resolve the tenant's signature
  immediately, no second call needed. See
  [docs/new-tenant-onboard-guide.md](./new-tenant-onboard-guide.md) for
  the full sequence.
- **Manual upsert** (still available, for explicit overrides and the
  failure-recovery path):

```bash
curl -X POST http://localhost:5080/v1/admin/institutions \
  -H "Authorization: Bearer <admin access_token>" \
  -H "Content-Type: application/json" \
  -d '{
        "institutionCode": "EXBK",
        "institutionName": "Example Bank Ltd.",
        "publicKeyPem": "-----BEGIN PUBLIC KEY-----..."
      }'
```

### Vault provider

The key store backing every tenant's signing key is selected by
`Crypto:VaultProvider` in `appsettings.json`:

| Value | Backend | Notes |
|---|---|---|
| `Local` (default) | `EncryptedFileSigningKeyStore` — AES-256-GCM blobs under `KeyCustody:KeyStoreDirectory`, sealed by `KeyCustody:VaultKek` | The Phase-1 software vault. Dev KEK is deterministic; Production requires an explicit 32-byte KEK from Key Vault. |
| `Azure` / `AWS` / `HSM` | (planned) | One `IKeyVaultProvider` implementation per backend. Select by name once a backend ships. |

The host refuses to start in Production with an unrecognised provider
name (see `Program.cs` §10a). See
[docs/new-tenant-onboard-guide.md](./new-tenant-onboard-guide.md) for
the full configuration table.

### Minting an admin token

The `admin` scope comes from the platform bootstrap credential (client
zero), not a tenant credential:

```bash
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret
# -> prints client_secret once + Auth:Bootstrap:ClientSecretHash to configure

curl -X POST http://localhost:5080/v1/oauth/token \
  -d grant_type=client_credentials \
  -d client_id=platform-bootstrap \
  -d client_secret=<bootstrap secret>
```

## Local testing checklist

Run these after any change touching `Program.cs`'s OpenAPI/auth wiring —
they catch the two real bugs found while building this feature (see
"Gotchas" below).

```bash
# 1. Public doc has exactly the 5 public routes, nothing from admin/*
curl -s http://localhost:5080/openapi/v1.public.json | jq '.paths | keys'

# 2. Internal doc, no credentials -> 401
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5080/openapi/v1.internal-admin.json

# 3. Internal doc, wrong credentials -> 401
curl -s -o /dev/null -w '%{http_code}\n' -u 'platform-team:wrongpass' \
  http://localhost:5080/openapi/v1.internal-admin.json

# 4. Internal doc, correct credentials -> 200, and only admin/* routes
curl -s -u 'platform-team:<password>' \
  http://localhost:5080/openapi/v1.internal-admin.json | jq '.paths | keys'

# 5. Docs UI: same auth behavior as the raw JSON
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5080/docs/public
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5080/docs/internal-admin
curl -s -o /dev/null -w '%{http_code}\n' -u 'platform-team:<password>' http://localhost:5080/docs/internal-admin

# 6. A publicly-documented endpoint still rejects anonymous calls
curl -s -o /dev/null -w '%{http_code}\n' -X POST http://localhost:5080/v1/qr/generate/static -d '{}'
# -> 401/500 depending on whether Jwt:SigningKey is configured; never 2xx
```

The dev-only route dump (`GET /admin/_routes`, Development only) lists
every route MVC actually registered, independent of OpenAPI grouping —
useful when a route's OpenAPI document placement is in doubt but you need
to confirm it's genuinely live.

### Automated tests

```bash
dotnet build SBQR.slnx
dotnet test tests/SBQR.ArchitectureTests/SBQR.ArchitectureTests.csproj
dotnet test tests/SBQR.Modules.Tenancy.Tests/SBQR.Modules.Tenancy.Tests.csproj
dotnet test tests/SBQR.Modules.KeyCustody.Tests/SBQR.Modules.KeyCustody.Tests.csproj
dotnet test tests/SBQR.Modules.Verification.Tests/SBQR.Modules.Verification.Tests.csproj
# SBQR.Qr.IntegrationTests / SBQR.Tenancy.IntegrationTests need a live
# Postgres (Testcontainers) — not exercised by this change, skip unless
# you're touching persistence.
```

## Gotchas found while building this (read before changing the split again)

1. **`[ApiExplorerSettings(GroupName = "internal")]` does NOT become an
   `OpenApiOperation` tag** in `Microsoft.AspNetCore.OpenApi`. Filtering
   post-hoc with an `IOpenApiDocumentTransformer` that inspects
   `operation.Tags` silently keeps/drops nothing — this is exactly the bug
   that shipped `v1.internal-admin` as `"paths": {}` for a while. The
   correct hook is `OpenApiOptions.ShouldInclude`, checked against
   `ApiDescription.GroupName` (see `GroupNameDocumentFilter.cs` remarks and
   `Program.cs` §8).
2. **A literal route string like `/openapi/v1.public.json` breaks
   `MapOpenApi`.** It resolves the document name from route-parameter
   capture, not by re-splitting the matched URL — a fixed path makes it
   look for a document named `"v1"` (split on the first `.`) and 404s. Use
   `/openapi/{documentName:regex(...)}.json` with a regex constraint
   pinning it to one document name instead.
3. Restarting the dev server locally: if you see
   `Address already in use` on port 5080, something from a previous run is
   still bound — `fuser -k 5080/tcp` (Linux/WSL) before retrying.
