# Metering & Billing — .NET developer onboarding and test guide

Audience: a .NET developer new to this codebase who will run, debug and test Metering and Billing on a laptop (Visual Studio, local PostgreSQL, PowerShell) as part of knowledge transfer, and then start working on them.

Target repo: `rvl-secure-bqr-manager` (branch `feature/metering-billing`, HEAD `472ec2e` when this was written, 2026-10-02).

> **How accurate are the sample responses?** Every request shape, route, status code, error code and SQL statement below was read from the code and the migrations. The sample **responses** are built from the response records in the code. GUIDs, timestamps and tokens are made up, and a decimal's trailing zeros (`0.5000` vs `0.5`) follow .NET `decimal` scale. The guide was not executed end to end when written, so if your output differs, trust your output, then check the code reference next to the step. A real end-to-end run of the same feature is in `rvl-secure-bqr-manager/docs/metering-billing-e2e-outcome.md` (107 checks).

---

## 0. The one-minute mental model

```
FI backend ── POST /v1/qr/generate/{static|dynamic} ─┐
FI backend ── POST /v1/qr/validate ──────────────────┤   (same DB transaction as the business row)
                                                     ▼
                                    public.outbox_messages  (PENDING)
                                                     │  OutboxDispatcher (BackgroundService, polls every 1 s)
                                                     ▼
                                    IMessageBroker.PublishAsync  (InProcess | Channels | RabbitMq)
                                                     ▼
              Metering: QrGeneratedHandler / QrValidatedHandler  → INSERT … ON CONFLICT DO NOTHING
                                                     ▼
                                    public.usage_events  (append-only ledger)
                                                     │  IMeteringQueries  (the only seam Billing may use)
                                                     ▼
 Billing: rate cards + usage + pending adjustments → StatementCalculator → statements per FI per Dhaka month
          GET (provisional) → POST draft → POST recalculate → POST finalize (frozen forever) → CSV export
```

Two modules, two jobs:

| Module | Job | Has HTTP endpoints? |
|---|---|---|
| **Metering** | Turn each QR generation/validation event into one row in `usage_events`, exactly once. Answer questions about that ledger. | One: `POST /v1/admin/outbox/{id}/requeue` |
| **Billing** | Price that usage per FI per month, let an operator review and approve (finalize) it, export it. | Yes, 4 controllers under `/v1/admin/billing/*` |

### Business rules you must know before touching the code

| Rule | Where it lives |
|---|---|
| Three meters: `GENERATION_STATIC`, `GENERATION_DYNAMIC`, `VALIDATION`. | `011_metering.sql` CHECK; `UsageEvent.cs` |
| A generation is always billable, charged to the **requesting** FI. | `QrGeneratedHandler.cs` |
| Every conclusive validation verdict is billable to the **verifying** FI (the issuer is not charged). Only `REQUEST_STALE` and `REQUEST_REPLAYED` are free. An unknown verdict throws, so the message retries and eventually goes DEAD. | `QrValidatedHandler.cs`, ADR 0002 |
| Requests that are refused (400/409/422) create no outbox row, so no usage. | outbox row and business row share one transaction |
| The billing month is the **Asia/Dhaka calendar month** (fixed +06:00), a half-open UTC range. The month is decided by `occurred_at`, not `recorded_at`. | `BillingMonth.cs` |
| A month can be drafted only after it ends **plus a 2-hour grace**. | `BillingMonth.Grace` |
| A month can be drafted only if the previous month is FINALIZED (rule "D2"). The very first month ever is exempt (no backfill). | `DraftPeriodCommandHandler.cs` |
| A draft is refused while any `QrGenerated`/`QrValidated` outbox row older than the month's end is `PENDING` or `DEAD` (`USAGE_NOT_COMPLETE`). | `MeteringQueries.IsUsageCompleteAsync` |
| Rate cards: effective from the 1st of a **future** Dhaka month, rates ≥ 0 with at most 4 decimals, one per tenant per month, can never be edited, can be deleted only before they take effect. | `RateCard.cs`, trigger `billing_rate_cards_guard` |
| An FI is billed from its newest rate card with `effective_from <= month start`. No card → no statement, and its adjustments stay Pending. | `RateCardRepository`, `StatementCalculator` |
| Statement line amount = `round(quantity × unit_rate, 2)` (away from zero). Total = subtotal + adjustments. | `StatementLine.Usage`, `ck_billing_statement_lines_shape` |
| Adjustments: manual credit (negative) or debit (positive), 2 decimals, never zero. Pending until a finalize applies them, exactly once. | `Adjustment.cs`, trigger `billing_adjustments_guard` |
| Finalize needs three totals to agree: stored draft, fresh recalculation, and the `expectedTotal` the operator reviewed. If not, the draft is rebuilt and the call returns `409 DRAFT_CHANGED`. | `BillingPeriod.Finalize` |
| Once FINALIZED, statements, lines and applied adjustments are frozen by DB triggers, not only by C#. | `012_billing.sql` |

---

## 1. Code map

Everything is in `rvl-secure-bqr-manager/`. It is a modular monolith on .NET 10 (`SBQR.slnx`), with one deployable host, `src/Host/SBQR.Api`.

```
src/Modules/Metering/
  SBQR.Modules.Metering.Api              MeteringModule (DI), OutboxController (requeue)
  SBQR.Modules.Metering.Application      Handlers/QrGeneratedHandler, QrValidatedHandler; Commands/RequeueDeadLetter
  SBQR.Modules.Metering.Domain           UsageEvent (record), repository interfaces
  SBQR.Modules.Metering.Contracts        IMeteringQueries  ← what Billing is allowed to see
  SBQR.Modules.Metering.Infrastructure   UsageEventRepository, MeteringQueries (raw SQL), DeadLetterRepository
src/Modules/Billing/
  …Api            BillingModule, controllers: RateCards, Adjustments, Periods, Reports
  …Application    RateCards/*, Adjustments/*, Periods/* (Draft, Recalculate, Finalize, GetPeriod, Export*), PeriodCloser/*, Reports/*
  …Domain         BillingPeriod (the only aggregate), BillingMonth, Statement, StatementLine, RateCard, Adjustment, DhakaTime
  …Infrastructure BillingDbContext, repositories, BillingTransactionRunner (row-lock transaction)
src/Host/SBQR.Api/Outbox/                OutboxDispatcher, OutboxOptions, event-type registry
src/Modules/QrGeneration/…/Commands/Common/QrIssuancePipeline.cs      ← writes the QrGenerated outbox row
src/Modules/Verification/…/Commands/ValidateQrCommandHandler.cs       ← writes the QrValidated outbox row
db/migrations/010_outbox.sql  011_metering.sql  012_billing.sql        ← schema is applied by hand, never by the app
tests/  SBQR.Modules.Metering.Tests  SBQR.Modules.Billing.Tests  SBQR.Messaging.IntegrationTests  SBQR.Billing.IntegrationTests  SBQR.ArchitectureTests
```

Patterns you will see everywhere:

- **MediatR** commands/queries with a **FluentValidation** pipeline behavior. A validator failure becomes `Result.Failure(ValidationFailed)`, which the controller maps to HTTP 400. Business refusals in the period handlers are *outcomes* (enum + a `409` with a `code` field), not exceptions.
- **No EF migrations.** The schema is raw SQL in `db/migrations`. The app only has `SELECT/INSERT/UPDATE/DELETE` rights.
- **Module boundaries are enforced by tests.** Billing may talk to Metering only through `Metering.Contracts` (`IMeteringQueries`). `SBQR.ArchitectureTests/ModuleBoundaryTests.cs` fails the build if you break that.
- **Raw SQL where it matters.** `MeteringQueries` and `OutboxDispatcher` use hand-written SQL. Billing uses EF Core plus triggers.
- **Audit.** Every billing and outbox action writes a row to `audit_logs` (`event_type` column = the action name).

### HTTP surface (all admin routes need the platform **admin** token; an FI tenant token gets 403)

| Method & route | Purpose | Handler |
|---|---|---|
| `POST /v1/qr/generate/static` · `/dynamic` | FI generates a QR → **creates usage** | `QrGenerationController` |
| `POST /v1/qr/validate` | FI validates a QR → **creates usage** | `QrValidationController` |
| `POST /v1/admin/billing/rate-cards` | add a price list | `RateCardsController` |
| `GET /v1/admin/billing/rate-cards?tenantId=` | list a tenant's price lists | |
| `DELETE /v1/admin/billing/rate-cards/{id}` | withdraw a not-yet-effective card | |
| `POST /v1/admin/billing/adjustments` | record a credit/debit | `AdjustmentsController` |
| `GET /v1/admin/billing/adjustments?tenantId=&pending=` | list a tenant's adjustments | |
| `GET /v1/admin/billing/periods/{yyyy-MM}` | stored period or live PROVISIONAL view | `PeriodsController` |
| `POST /v1/admin/billing/periods/{p}/draft` | draft the month | |
| `POST /v1/admin/billing/periods/{p}/recalculate` | rebuild a draft | |
| `POST /v1/admin/billing/periods/{p}/finalize` | approve and freeze | |
| `GET /v1/admin/billing/periods/{p}/statements.csv` | statement lines export | |
| `GET /v1/admin/billing/periods/{p}/usage.csv?tenantId=&all=` | raw usage export | |
| `GET /v1/admin/billing/reports/pending-adjustments?limit=` | all Pending adjustments | `ReportsController` |
| `GET /v1/admin/billing/reports/late-usage?month=&limit=` | usage that arrived after a month was finalized (see known issue F1) | |
| `POST /v1/admin/outbox/{id}/requeue` | put a DEAD outbox message back | `OutboxController` |

Background workers (no HTTP): `OutboxDispatcher` (always on) and `PeriodCloserService` (off by default, auto-drafts last month).

---

## 2. Local setup (Windows, Visual Studio, PowerShell 7)

### 2.1 Prerequisites

- Visual Studio 2022 17.13+ (needed for the `.slnx` solution format) with the **ASP.NET and web development** workload, or Rider. .NET SDK 10 (`dotnet --version` → `10.0.x`).
- Docker Desktop (for PostgreSQL 16 and for the Testcontainers-based integration tests).
- PowerShell 7 (`pwsh`). The helper function below uses `-SkipHttpErrorCheck`, which Windows PowerShell 5.1 lacks.
- A SQL client of your choice (DBeaver, pgAdmin, or `psql` inside the container, shown below).

All commands below run from the repo root `rvl-secure-bqr-manager/` unless stated.

### 2.2 Start PostgreSQL

```powershell
pwsh docker/start-db.ps1                 # starts only the sbqr.postgres container and waits until healthy
# or:  docker compose -f docker/docker-compose.yml up -d sbqr.postgres
```

Defaults: host `localhost`, port `5432`, user `postgres`, password `postgres`.

### 2.3 Use a throwaway database (strongly recommended)

Billing tables are protected by immutability triggers, and `TRUNCATE` is blocked too. Once you finalize a month, **you cannot wipe it.** So never learn on `sbqr_app`. Use a lab database you can drop and recreate:

```powershell
docker exec -i sbqr.postgres psql -U postgres -c "DROP DATABASE IF EXISTS sbqr_lab;"
docker exec -i sbqr.postgres psql -U postgres -c "CREATE DATABASE sbqr_lab;"

# apply migrations 001..012 in order, stop on the first error
Get-ChildItem db/migrations/*.sql | Sort-Object Name | ForEach-Object {
  Write-Host "applying $($_.Name)"
  Get-Content $_.FullName -Raw | docker exec -i sbqr.postgres psql -U postgres -d sbqr_lab -v ON_ERROR_STOP=1 -q
}

# sanity check: expect 7 rows (outbox_messages, usage_events and the 5 billing_* tables)
docker exec -i sbqr.postgres psql -U postgres -d sbqr_lab -c "\dt public.outbox_messages public.usage_events public.billing_*"
```

**Reset = repeat the first two lines + the migration loop.** Migrations are idempotent, so re-running them is safe.

### 2.4 Configure the API (`.env` at the repo root)

`Program.cs` loads a gitignored repo-root `.env` in Development only, and it wins over everything else. Create `rvl-secure-bqr-manager/.env`:

```ini
ConnectionStrings__sbqr_app=Host=localhost;Port=5432;Database=sbqr_lab;Username=postgres;Password=postgres;Include Error Detail=true
Jwt__SigningKey=dev-only-signing-key-change-me-0123456789abcdef
Auth__Bootstrap__ClientSecretHash=<paste the hash from step 2.5>
TrustStore__BaseUrl=http://localhost:5002
Crypto__VaultProvider=Local
Messaging__Provider=InProcess
```

Why each line:

- `TrustStore__BaseUrl` is required or the host refuses to boot. Nothing listens on 5002 and that is fine, because the trust sync only logs errors and we validate QRs issued by our own tenants (`OWN_CUSTODY`).
- `Crypto__VaultProvider=Local` keeps signing keys in an encrypted file under `%LOCALAPPDATA%\sbqr\key-vault`, so you don't need AWS S3. The template `.env.example` sets `S3`, so override it.
- `Messaging__Provider`: `InProcess` calls the handlers directly. `appsettings.json` defaults to `Channels` (buffered in-process queue), which also works. RabbitMQ is opt-in.
- The key `Auth__Bootstrap__ClientSecret` in `.env.example` and the `dev-only-bootstrap-secret` value in older docs are **stale**. The code accepts only an Argon2id **hash** (`ClientSecretHash`).

### 2.5 Generate your admin ("platform bootstrap") credential

```powershell
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret
```

It prints a `client_secret` once and a hash starting `$argon2id$…`. Put the hash in `.env` (step 2.4) and keep the secret. In the rest of the guide the secret is `$BOOTSTRAP_SECRET`.

### 2.6 Run it

**Visual Studio:** open `SBQR.slnx`, right-click `SBQR.Api` → *Set as Startup Project*, pick the launch profile **SBQR.Api** (Development, `http://localhost:5001`), press **F5** (or Ctrl+F5 for no debugger). The console should print `[dev] loading .env: …`.

**Command line:** `dotnet run --project src/Host/SBQR.Api --launch-profile SBQR.Api`

```powershell
curl.exe -s http://localhost:5001/health/ready
# {"status":"ready"}
```

Handy URLs: `http://localhost:5001/docs/internal-admin` (Scalar UI for the admin APIs, including all billing endpoints) and, in Development only, `http://localhost:5001/admin/_routes` (every live route).

### 2.7 A tiny PowerShell client for the rest of the guide

Paste this into the PowerShell session you will use. It prints the status code and pretty JSON, keeps going on 4xx/5xx (which you want to see), and stores the parsed body in `$Last` so you can reuse values (for example `$Last.qrPayload`).

```powershell
$Base = "http://localhost:5001"
function Api {
  param([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Headers = @{})
  $h = @{} + $Headers
  if ($Token) { $h["Authorization"] = "Bearer $Token" }
  $req = @{ Method = $Method; Uri = "$Base$Path"; Headers = $h; SkipHttpErrorCheck = $true }
  if ($Body) { $req.Body = $Body; $req.ContentType = "application/json" }
  $r = Invoke-WebRequest @req
  Write-Host "HTTP $($r.StatusCode)" -ForegroundColor Cyan
  $global:Last = $null
  if ($r.Content) {
    try { $global:Last = $r.Content | ConvertFrom-Json; $global:Last | ConvertTo-Json -Depth 12 }
    catch { $r.Content }
  }
}
function Psql { param([string]$Sql) docker exec -i sbqr.postgres psql -U postgres -d sbqr_lab -c $Sql }
```

---

## 3. Walkthrough A — live metering (QR traffic → usage rows)

### Step 1. Get the admin token

```powershell
$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/oauth/token" -Body @{
  grant_type = "client_credentials"; client_id = "platform-bootstrap"; client_secret = $BOOTSTRAP_SECRET }
$ADMIN = $resp.accessToken
$resp | ConvertTo-Json
```

```json
{ "accessToken": "eyJhbGciOiJIUzI1NiIs…", "tokenType": "Bearer", "expiresIn": 600 }
```

The token lives 10 minutes, so re-run this when you get `401`. The token endpoint is rate limited to 10 requests per minute per client and IP. A tenant token has scope `qr:generate qr:validate`. The admin token has scope `admin` and cannot generate QRs.

### Step 2. Onboard a test FI ("Alpha Bank")

An FI needs four things before it can generate QRs: a tenant, credentials, a signing key, and Active status. (A Pending tenant's token request is refused with `invalid_client`.)

```powershell
# 2a. register the tenant
Api POST /v1/admin/tenants '{"institutionName":"Alpha Bank","institutionCode":"031101"}' $ADMIN
```
```json
{ "tenantId": "3f1c9d52-7a0e-4f55-9a43-0b6f2d1a8c10", "institutionName": "Alpha Bank",
  "institutionCode": "031101", "status": "Pending", "isActive": true }
```
```powershell
$ALPHA = "3f1c9d52-7a0e-4f55-9a43-0b6f2d1a8c10"      # use YOUR tenantId

# 2b. credentials: the secret is shown exactly once
Api POST "/v1/admin/tenants/$ALPHA/tenant-configuration" '{"isQrGenerationAllowed":true,"isQrValidationAllowed":true}' $ADMIN
```
```json
{ "tenantId": "3f1c9d52-…", "credentialId": "b2b8…", "clientId": "031101-7f3a91c2",
  "clientSecret": "k9Z…43 chars…", "expiresAt": null }
```
```powershell
# 2c. signing key (the server generates an Ed25519 pair)
Api POST /v1/crypto-keys "{`"tenantId`":`"$ALPHA`",`"mode`":`"Generate`"}" $ADMIN
```
```json
{ "cryptoKeyId": "…", "tenantId": "3f1c9d52-…", "keyId": "…", "keyVersion": 1,
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----…", "status": "ACTIVE", "isActive": true, "createdAt": "2026-10-02T04:11:09+00:00" }
```
```powershell
# 2d. activate
Api POST "/v1/admin/tenants/$ALPHA/activate" $null $ADMIN        # 200 { … "status":"Active" … }

# 2e. the FI's own token
$t = Invoke-RestMethod -Method Post -Uri "$Base/v1/oauth/token" -Body @{
  grant_type="client_credentials"; client_id="031101-7f3a91c2"; client_secret="<clientSecret from 2b>" }
$ALPHA_TOKEN = $t.accessToken
```

### Step 3. Generate QRs, then watch the pipeline

```powershell
# static QR, idempotency key e2e-gen-1
Api POST /v1/qr/generate/static '{"recipientName":"Arif Mahmood","recipientCity":"Dhaka","recipientPan":"01711111111"}' $ALPHA_TOKEN @{ "Idempotency-Key" = "e2e-gen-1" }
```
```json
{ "qrPayload": "00020101021126…6304A1B2", "payloadHash": "9f2c…64 hex…", "qrType": "STATIC", "signatureKeyVersion": 1 }
```
```powershell
# dynamic QR (amount required, string)
Api POST /v1/qr/generate/dynamic '{"transactionAmount":"150.00","recipientName":"Arif Mahmood","recipientCity":"Dhaka","recipientPan":"01711111111"}' $ALPHA_TOKEN @{ "Idempotency-Key" = "e2e-gen-2" }

# a third one; keep its payload for the validation steps (the helper stores the parsed body in $Last)
Api POST /v1/qr/generate/static '{"recipientName":"Arif Mahmood","recipientCity":"Dhaka","recipientPan":"01711111111"}' $ALPHA_TOKEN @{ "Idempotency-Key" = "e2e-gen-3" }
$QR = $Last.qrPayload
```

Now look inside the database. Within about a second the outbox rows go `PENDING → PROCESSED` and usage rows appear:

```powershell
Psql "SELECT event_type, status, attempts, left(payload::text,70) AS payload FROM outbox_messages ORDER BY created_at;"
Psql "SELECT meter_code, billable, detail, source_type, client_reference, tenant_id, occurred_at FROM usage_events ORDER BY occurred_at;"
```
```
 event_type  |  status   | attempts | payload
-------------+-----------+----------+--------------------------------------------------------------
 QrGenerated | PROCESSED |        0 | {"EventId":"…","OccurredAt":"2026-10-02T04:20:11…","QrGenerationId":"…
 …

 meter_code         | billable | detail | source_type   | client_reference | tenant_id
--------------------+----------+--------+---------------+------------------+----------
 GENERATION_STATIC  | t        |        | qr_generation | e2e-gen-1        | 3f1c9d52…
 GENERATION_DYNAMIC | t        |        | qr_generation | e2e-gen-2        | 3f1c9d52…
 GENERATION_STATIC  | t        |        | qr_generation | e2e-gen-3        | 3f1c9d52…
```

`client_reference` is your `Idempotency-Key`. If you send none, it is the generation id.

**Debugging this flow in Visual Studio** (set breakpoints, press F5, make the call):

1. `QrIssuancePipeline.cs:236`: the outbox row is added in the same `SaveChanges` as the `qr_generations` row.
2. `OutboxDispatcher.cs:102` `RunOnceAsync` → `:121` `DispatchOneAsync`: claim, deserialize, publish, mark processed. (A tick is every second, so set the breakpoint, then make the call.)
3. `QrGeneratedHandler.cs:30` `Handle`: maps `STATIC`/`DYNAMIC` to a meter code.
4. `UsageEventRepository.AddIfAbsentAsync`: the idempotent `INSERT … ON CONFLICT (source_type, source_id) DO NOTHING`. It returns `false` on a duplicate delivery.

### Step 4. Generate negative cases (no usage must be created)

| Try | Expect | Usage rows |
|---|---|---|
| Re-send the generate call with the same `Idempotency-Key: e2e-gen-1` | `409` `{"error":"DUPLICATE_IDEMPOTENCY_KEY",…}` | none added: the unique-key violation rolls back both the business row and the outbox row |
| `POST /generate/static` with a `transactionAmount` field | `400` (unknown JSON members are rejected) | none |
| Generate for a tenant with no active key | `422` `KEY_NOT_ACTIVE` | none |
| Call generate with `$ADMIN` | `403` | none |

### Step 5. Validate: the verifying FI is charged

```powershell
Api POST /v1/qr/validate "{`"qrPayload`":`"$QR`"}" $ALPHA_TOKEN
```
```json
{ "verdict": "VALID", "trustSource": "OWN_CUSTODY", "reasonCode": null, "institutionCode": "031101",
  "payloadHash": "9f2c…", "recipientName": "Arif Mahmood", "recipientPan": "01711111111", "qrClassification": "P2P" }
```

`/v1/qr/validate` accepts two optional fields, `requestId` (the replay-guard key; the server makes a GUID if you omit it) and `requestTimestamp`. Use them to test the free verdicts:

```powershell
$body = "{`"qrPayload`":`"$QR`",`"requestId`":`"replay-test-1`"}"
Api POST /v1/qr/validate $body $ALPHA_TOKEN      # 1st: VALID
Api POST /v1/qr/validate $body $ALPHA_TOKEN      # 2nd: 200 {"verdict":"REQUEST_REPLAYED","reasonCode":"REPLAY_DETECTED",…}
# broken CRC: flip the last character of the payload
Api POST /v1/qr/validate '{"qrPayload":"…broken…"}' $ALPHA_TOKEN   # 200 STRUCTURAL_INVALID / CRC_MISMATCH
```
```powershell
Psql "SELECT meter_code, billable, detail, client_reference FROM usage_events WHERE meter_code='VALIDATION' ORDER BY occurred_at;"
```
```
 meter_code | billable | detail            | client_reference
------------+----------+-------------------+------------------
 VALIDATION | t        | VALID             | 6b1f…            (auto request id)
 VALIDATION | t        | VALID             | replay-test-1
 VALIDATION | t        | STRUCTURAL_INVALID| 0c2e…
```

What to notice:

- A conclusive verdict such as `STRUCTURAL_INVALID` **is still billed.** Only the protocol rejections are free.
- The replayed call (2nd) **creates no row at all**, not even a `billable=false` one. (The design says it should record a `billable=false` row. See known issue F5.)
- **Extra exercise (issuer not charged):** onboard a second FI "Beta Bank" (`031102`), have Beta validate Alpha's QR, and confirm the `VALIDATION` row carries **Beta's** `tenant_id`, and Alpha gains no row.

### Step 6. Make the outbox fail on purpose (dead letters)

Failures are never swallowed: they retry with back-off `min(2^attempts s, 300 s)` and, after `Outbox:MaxAttempts` (default 10, ~8 minutes in total), the row becomes `DEAD`. For a quick demo add `Outbox__MaxAttempts=2` to `.env` and restart the API.

Insert a poison message that Metering will reject (the QR type is unknown). Use a **random, non-existent tenant id** so that it never affects Alpha's invoice later, and put its `occurred_at` in **last month** (use the month you will draft in section 5):

```powershell
Psql @"
INSERT INTO outbox_messages (outbox_message_id, source_module, event_type, payload, occurred_at)
VALUES ('11111111-1111-1111-1111-111111111111','qr-generation','QrGenerated',
 '{"EventId":"11111111-1111-1111-1111-111111111111","OccurredAt":"2026-09-20T06:00:00+00:00","QrGenerationId":"22222222-2222-2222-2222-222222222222","TenantId":"99999999-9999-9999-9999-999999999999","QrType":"BOGUS","ClientReference":"poison-1"}'::jsonb,
 '2026-09-20 12:00+06');
"@
Start-Sleep 8
Psql "SELECT status, attempts, last_error FROM outbox_messages WHERE outbox_message_id='11111111-1111-1111-1111-111111111111';"
```
```
 status | attempts | last_error
--------+----------+---------------------------------------------------------------
 DEAD   |        2 | Unknown QR type 'BOGUS' on QrGenerated event 11111111-…
```

The API log shows a `Warning` on the first failure (EventId 9002) and a `Critical` on moving to DEAD (9003). Leave it DEAD for now; section 5 shows that it **blocks drafting last month**. To fix it later:

```powershell
Psql "UPDATE outbox_messages SET payload = jsonb_set(payload,'{QrType}','""STATIC""') WHERE outbox_message_id='11111111-1111-1111-1111-111111111111';"
Api POST /v1/admin/outbox/11111111-1111-1111-1111-111111111111/requeue $null $ADMIN
```
```json
{ "outboxMessageId": "11111111-1111-1111-1111-111111111111" }
```

Requeueing resets `attempts` to 0 and sets the row `PENDING`; the dispatcher then processes it. Requeueing an id that is not DEAD returns `404` `{"title":"Dead-lettered message not found",…}`. The action is audited as `outbox.requeued`.

---

## 4. Walkthrough B — rate cards and adjustments

### Step 7. Rate cards

Today is **2026-10-02** in these examples, so the earliest allowed card is **2026-11-01**. (Adjust the dates to your own "today".)

```powershell
Api POST /v1/admin/billing/rate-cards "{`"tenantId`":`"$ALPHA`",`"effectiveFrom`":`"2026-11-01`",`"generationRate`":0.5,`"validationRate`":0.3,`"createdBy`":`"ops.lead`"}" $ADMIN
```
```json
{ "rateCardId": "5e0d7c6a-…", "tenantId": "3f1c9d52-…", "effectiveFrom": "2026-11-01",
  "generationRate": 0.5, "validationRate": 0.3, "currency": "BDT", "createdBy": "ops.lead" }
```
HTTP 201. The currency is always BDT and cannot be sent. The audit action is `billing.rate_entry`.

| Try | Result |
|---|---|
| Same tenant + same `effectiveFrom` again | `409` `{"title":"Rate card conflict","detail":"tenant 3f1c… already has a rate card for 2026-11.","status":409}` |
| `effectiveFrom` = `2026-10-15` (not the 1st) | `400` `Invalid rate card` |
| `effectiveFrom` = a date in the past, or today | `400`: a future month is required (decided in Dhaka time, not server time) |
| `generationRate` = `0.00005` (5 decimals) or `-1` | `400` |
| `GET /v1/admin/billing/rate-cards?tenantId=$ALPHA` | `200` list, newest effective month first |
| `DELETE /v1/admin/billing/rate-cards/{id}` of the 2026-11 card | `204` (audit `billing.rate_withdrawal`) |
| `DELETE` of a card that has already taken effect (see seeding in step 8) | `409` `Rate card conflict`; the DB trigger is the second line of defence |
| Call with `$ALPHA_TOKEN` | `403` |

> The API **cannot** create a card for a month that has already started. That is intended (prices are announced in advance), but it means you must seed history by SQL to test drafting (section 5).

### Step 8. Adjustments

```powershell
Api POST /v1/admin/billing/adjustments "{`"tenantId`":`"$ALPHA`",`"amount`":-1.00,`"reason`":`"SLA credit for the 12 Sept outage`",`"createdBy`":`"ops.lead`"}" $ADMIN
```
```json
{ "adjustmentId": "a4f2…", "tenantId": "3f1c9d52-…", "amount": -1.00, "reason": "SLA credit for the 12 Sept outage",
  "createdBy": "ops.lead", "createdAt": "2026-10-02T04:55:31.1+00:00", "appliedStatementId": null }
```
HTTP 201. `appliedStatementId: null` means **Pending**.

```powershell
Api GET "/v1/admin/billing/adjustments?tenantId=$ALPHA&pending=true" $null $ADMIN        # oldest first
Api GET "/v1/admin/billing/reports/pending-adjustments?limit=50" $null $ADMIN            # across all tenants
```

Rules: `amount` ≠ 0 with at most 2 decimals, `reason` ≤ 500 characters, `createdBy` ≤ 200. Violations return `400` `Invalid adjustment`. A Pending adjustment is picked up by **every** draft and provisional view of that tenant until a finalize applies it.

---

## 5. Walkthrough C — a full billing month (draft → recalculate → finalize → export)

### 5.1 Seed last month (the only way to test history)

The API cannot back-date rate cards and real QR traffic is always "now". So seed **last month (2026-09 here)** directly in SQL, exactly as the team's own end-to-end run did. (Drafting needs a month that has already ended plus the 2-hour grace. If you are running this in a different month, shift every date below, and in step 6, to your own "last month".) Open an interactive `psql` (so you can use variables):

```powershell
docker exec -it sbqr.postgres psql -U postgres -d sbqr_lab
```
```sql
\set tenant '3f1c9d52-7a0e-4f55-9a43-0b6f2d1a8c10'   -- YOUR Alpha tenantId

-- price list valid for September
INSERT INTO billing_rate_cards (rate_card_id, tenant_id, effective_from, generation_rate, validation_rate, created_by)
VALUES (gen_random_uuid(), :'tenant', '2026-09-01', 0.5, 0.3, 'seed');

-- usage: 3 static + 1 static at 23:59:30 on the 30th (still September in Dhaka)
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
SELECT gen_random_uuid(), :'tenant', 'GENERATION_STATIC', true, NULL, 'qr_generation', gen_random_uuid(), gen_random_uuid(), 'seed-s-'||g,
       timestamptz '2026-09-10 10:00+06' + g * interval '1 minute'
FROM generate_series(1,3) g;
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
VALUES (gen_random_uuid(), :'tenant', 'GENERATION_STATIC', true, NULL, 'qr_generation', gen_random_uuid(), gen_random_uuid(), 'seed-edge-in',  '2026-09-30 23:59:30+06');

-- the boundary row: 00:00:00 on 1 Oct Dhaka belongs to OCTOBER, so it must NOT be in the September bill
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
VALUES (gen_random_uuid(), :'tenant', 'GENERATION_STATIC', true, NULL, 'qr_generation', gen_random_uuid(), gen_random_uuid(), 'seed-edge-out', '2026-10-01 00:00:00+06');

-- 2 dynamic
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
SELECT gen_random_uuid(), :'tenant', 'GENERATION_DYNAMIC', true, NULL, 'qr_generation', gen_random_uuid(), gen_random_uuid(), 'seed-d-'||g,
       timestamptz '2026-09-11 10:00+06' + g * interval '1 minute'
FROM generate_series(1,2) g;

-- 5 billable validations + 1 free one (REQUEST_STALE, billable=false) that must be ignored
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
SELECT gen_random_uuid(), :'tenant', 'VALIDATION', true, 'VALID', 'qr_validation', gen_random_uuid(), gen_random_uuid(), 'seed-v-'||g,
       timestamptz '2026-09-12 10:00+06' + g * interval '1 minute'
FROM generate_series(1,5) g;
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
VALUES (gen_random_uuid(), :'tenant', 'VALIDATION', false, 'REQUEST_STALE', 'qr_validation', gen_random_uuid(), gen_random_uuid(), 'seed-free', '2026-09-13 10:00+06');
```

Expected September bill for Alpha (rates 0.50 generation, 0.30 validation, adjustment −1.00 from step 8):

| Line | Qty | Rate | Amount |
|---|---|---|---|
| Static QR generation | 4 | 0.5000 | 2.00 |
| Dynamic QR generation | 2 | 0.5000 | 1.00 |
| QR validation | 5 | 0.3000 | 1.50 |
| **Subtotal** | | | **4.50** |
| Adjustment: SLA credit… | | | −1.00 |
| **Total** | | | **3.50** |

### 5.2 Read it before it exists: the PROVISIONAL view

```powershell
Api GET /v1/admin/billing/periods/2026-09 $null $ADMIN
```
```json
{ "period": "2026-09", "status": "PROVISIONAL", "provisional": true,
  "draftedAt": null, "finalizedAt": null, "finalizedBy": null,
  "statements": [ {
    "tenantId": "3f1c9d52-…", "rateCardId": "…", "generationRate": 0.5000, "validationRate": 0.3000,
    "lines": [
      { "lineNo": 1, "lineType": "USAGE", "meterCode": "GENERATION_STATIC",  "adjustmentId": null, "description": "Static QR generation",  "quantity": 4, "unitRate": 0.5000, "amount": 2.00 },
      { "lineNo": 2, "lineType": "USAGE", "meterCode": "GENERATION_DYNAMIC", "adjustmentId": null, "description": "Dynamic QR generation", "quantity": 2, "unitRate": 0.5000, "amount": 1.00 },
      { "lineNo": 3, "lineType": "USAGE", "meterCode": "VALIDATION",         "adjustmentId": null, "description": "QR validation",         "quantity": 5, "unitRate": 0.3000, "amount": 1.50 },
      { "lineNo": 4, "lineType": "ADJUSTMENT", "meterCode": null, "adjustmentId": "a4f2…", "description": "SLA credit for the 12 Sept outage", "quantity": 0, "unitRate": 0, "amount": -1.00 } ],
    "subtotal": 4.50, "adjustmentsTotal": -1.00, "total": 3.50, "currency": "BDT", "calculatedAt": "2026-10-02T05:02:10+00:00" } ],
  "grandTotal": 3.50 }
```

Nothing is stored (`SELECT count(*) FROM billing_periods;` → 0). If the numbers differ, check the boundary rows and the free validation row first. A malformed period such as `2026-13` returns `404` `Billing period not found`.

**Debug here:** `StatementCalculator.BuildAsync` (`StatementCalculator.cs:28`) → `IMeteringQueries.GetBillableQuantitiesAsync` (raw SQL in `MeteringQueries.cs`) → `Statement.Create`.

### 5.3 Draft refusals worth seeing

```powershell
Api POST /v1/admin/billing/periods/2026-10/draft $null $ADMIN      # the current month has not ended
```
```json
{ "title": "Billing period conflict", "status": 409, "code": "GRACE_NOT_OVER",
  "detail": "2026-10 cannot be drafted before 2026-10-31T20:00:00.0000000+00:00 (two hours after the month ends).", "traceId": "…" }
```
```powershell
Api POST /v1/admin/billing/periods/2026-13/draft $null $ADMIN      # 400 "Invalid billing period"
```

Now draft September **while the poison outbox message from step 6 is still DEAD** (skip this refusal if you did not do step 6):

```powershell
Api POST /v1/admin/billing/periods/2026-09/draft $null $ADMIN
```
```json
{ "title": "Billing period conflict", "status": 409, "code": "USAGE_NOT_COMPLETE",
  "detail": "Usage for 2026-09 is not complete: QR events before 2026-09-30T18:00:00.0000000+00:00 are still pending or dead-lettered." }
```

That is the guard that stops an operator billing on an incomplete ledger. Fix and requeue it as shown at the end of step 6, wait a couple of seconds, and the draft works. (The poison event has an unknown tenant and does not change Alpha's bill.) The refusal codes are `PREVIOUS_PERIOD_NOT_FINALIZED`, `GRACE_NOT_OVER`, `USAGE_NOT_COMPLETE` and `PERIOD_FINALIZED`.

### 5.4 Draft

```powershell
Api POST /v1/admin/billing/periods/2026-09/draft $null $ADMIN
```
HTTP **201** and the same body as the provisional view, but `"status": "DRAFT"`, `"provisional": false`, `draftedAt` set. Run it again: HTTP **200** with the stored period (idempotent: it does not rebuild or re-audit). Audit action: `billing.period_draft`.

Rows now exist: `SELECT * FROM billing_periods; SELECT tenant_id,total FROM billing_statements; SELECT * FROM billing_statement_lines ORDER BY line_no;`. The adjustment is **still Pending**. It is only marked applied at finalize.

**Debug:** `DraftPeriodCommandHandler.Handle` (`DraftPeriodCommandHandler.cs:37`) shows the check order: exists → previous month → grace → usage complete → build → `BillingPeriod.Draft` → store.

### 5.5 Late usage arrives while the draft is open

```sql
INSERT INTO usage_events (usage_event_id, tenant_id, meter_code, billable, detail, source_type, source_id, source_event_id, client_reference, occurred_at)
VALUES (gen_random_uuid(), :'tenant', 'VALIDATION', true, 'VALID', 'qr_validation', gen_random_uuid(), gen_random_uuid(), 'seed-late-1', '2026-09-29 09:00+06');
```
```powershell
Api GET /v1/admin/billing/periods/2026-09 $null $ADMIN         # still total 3.50: a DRAFT shows STORED figures, never a live recalculation
Api POST /v1/admin/billing/periods/2026-09/recalculate $null $ADMIN
```
Recalculate returns HTTP 200 with the rebuilt draft: validation quantity 6 (1.80), **total 3.80**. Audit: `billing.period_refresh` with `total_before: 3.50, total_after: 3.80`. A month that was never drafted returns `404`.

### 5.6 Finalize (approve), including the safety check

Insert one more late validation (repeat the INSERT from 5.5 with `client_reference` `seed-late-2`) so the fresh total becomes **4.10**, then finalize with the **stale** total 3.80 that "the operator reviewed":

```powershell
Api POST /v1/admin/billing/periods/2026-09/finalize '{"finalizedBy":"ops.lead@rvl.example","expectedTotal":3.80}' $ADMIN
```
```json
{ "title": "Billing period finalize refused", "status": 409, "code": "DRAFT_CHANGED",
  "detail": "The reviewed total did not match a fresh recalculation. The draft was rebuilt; review and try again." }
```
The draft **was** rebuilt and committed, but the 409 body does not carry the new total (the code has a `NewTotal` value that the controller does not serialize). `GET /v1/admin/billing/periods/2026-09` now shows the stored draft with total **4.10**. Review it, then approve the right number:

```powershell
Api POST /v1/admin/billing/periods/2026-09/finalize '{"finalizedBy":"ops.lead@rvl.example","expectedTotal":4.10}' $ADMIN
```
```json
{ "alreadyFinalized": false, "appliedAdjustmentCount": 1,
  "period": { "period": "2026-09", "status": "FINALIZED", "provisional": false,
              "draftedAt": "…", "finalizedAt": "2026-10-02T05:20:44+00:00", "finalizedBy": "ops.lead@rvl.example",
              "statements": [ … ], "grandTotal": 4.10 } }
```
Run it a second time: HTTP 200 with `"alreadyFinalized": true` (the original result, no new writes, audit `billing.period_already_finalized`).

Validation: `expectedTotal` is **required** (at most 2 decimals, may be negative); blank `finalizedBy` or over 200 characters returns `400`; a month that was never drafted returns `404`; `USAGE_NOT_COMPLETE` returns `409`.

Everything happens in one transaction that holds the period row with `SELECT … FOR UPDATE`: recompute → compare → mark adjustments applied → set FINALIZED. **Debug:** `FinalizePeriodCommandHandler.FinalizeLockedAsync` (`:107`) and `BillingPeriod.Finalize` (`BillingPeriod.cs:95`).

### 5.7 Prove that it is frozen

```powershell
Api POST /v1/admin/billing/periods/2026-09/recalculate $null $ADMIN        # 409 code PERIOD_FINALIZED
Psql "UPDATE billing_statements SET total = 0 WHERE period='2026-09';"       # ERROR: billing period 2026-09 is FINALIZED
Psql "DELETE FROM billing_periods WHERE period='2026-09';"                    # ERROR: public.billing_periods: FINALIZED row is immutable (DELETE prohibited)
Api GET "/v1/admin/billing/adjustments?tenantId=$ALPHA&pending=false" $null $ADMIN   # the credit now has appliedStatementId set
```

A correction after approval is a **new** adjustment: record one and it stays Pending, lands as its own line on the **next** month's statement, and the approved statement never changes.

### 5.8 Exports

```powershell
curl.exe -s -H "Authorization: Bearer $ADMIN" http://localhost:5001/v1/admin/billing/periods/2026-09/statements.csv
curl.exe -s -H "Authorization: Bearer $ADMIN" "http://localhost:5001/v1/admin/billing/periods/2026-09/usage.csv?tenantId=$ALPHA"
curl.exe -s -H "Authorization: Bearer $ADMIN" "http://localhost:5001/v1/admin/billing/periods/2026-09/usage.csv?tenantId=$ALPHA&all=true"   # adds billable=false rows
```
```
period,tenant_id,statement_id,line_no,line_type,meter_code,adjustment_id,description,quantity,unit_rate,amount,statement_subtotal,statement_adjustments_total,statement_total,currency,status,calculated_at
2026-09,3f1c9d52-…,8d21…,1,USAGE,GENERATION_STATIC,,Static QR generation,4,0.5000,2.00,5.10,-1.00,4.10,BDT,FINALIZED,2026-10-02T05:19:02.0000000Z
…
usage_event_id,tenant_id,meter_code,billable,detail,source_type,source_id,client_reference,occurred_at,recorded_at
```
The statements CSV is long format: one row per statement line, with the statement totals repeated on each row. Files start with a UTF-8 BOM so Excel opens them cleanly. A month with no rows yields a header-only file. A malformed period gives `404` for statements and `400` for usage. Note: `occurred_at` in the CSV is **UTC** (known issue F4).

### 5.9 Reports and the auto-closer

- `GET /v1/admin/billing/reports/late-usage?month=2026-09` is meant to list usage recorded **after** a month was finalized. **Known defect F1:** it always returns `[]` (see section 8).
- `PeriodCloserService` drafts the previous Dhaka month automatically. It is **off by default**: enable it with `Billing__Scheduler__Enabled=true`. It ticks every hour, with the first tick one hour after startup, and only targets "last month", so it cannot be exercised quickly through the API. To debug it, run the unit tests (`PeriodCloserTests`) or put a breakpoint in `PeriodCloser.RunOnceAsync`. Log event ids 9401–9411.

### 5.10 Audit trail

```powershell
Psql "SELECT event_type, resource_type, resource_id, created_by FROM audit_logs WHERE event_type LIKE 'billing.%' OR event_type LIKE 'outbox.%' ORDER BY sequence;"
```
Expected actions: `billing.rate_entry`, `billing.rate_withdrawal`, `billing.adjustment_recorded`, `billing.period_draft`, `billing.period_refresh`, `billing.period_finalized`, `billing.period_already_finalized`, `outbox.requeued`. Billing audit rows are written **after** the commit (a known limitation noted in the handlers).

---

## 6. Automated tests

From the repo root:

```powershell
dotnet build SBQR.slnx -c Debug                       # warnings are errors (Directory.Build.props)

# fast unit tests: no Docker, no DB
dotnet test tests/SBQR.Modules.Metering.Tests
dotnet test tests/SBQR.Modules.Billing.Tests          # domain: BillingMonth, Statement, RateCard, BillingPeriod; handlers; controllers; CSV writers

# integration tests: need Docker running (Testcontainers starts postgres:16-alpine and applies db/migrations)
dotnet test tests/SBQR.Messaging.IntegrationTests     # outbox dispatcher, outbox→usage_events end to end, MeteringQueries
dotnet test tests/SBQR.Billing.IntegrationTests       # rate cards, adjustments, full period lifecycle, triggers

# architecture rules (module boundaries)
dotnet test tests/SBQR.ArchitectureTests

# one test or class
dotnet test tests/SBQR.Modules.Billing.Tests --filter "FullyQualifiedName~BillingMonthTests"
```

In Visual Studio use **Test Explorer**, and right-click a test → *Debug*. Good first reads: `BillingMonthTests` (the Dhaka boundary), `StatementTests` (rounding, e.g. 3 × 0.125 = 0.38 and 7 × 0.3333 = 2.33), `PeriodLifecycleTests` (the whole draft→finalize story against a real database), `OutboxToUsageEventEndToEndTests`.

Integration test database rule: billing tables cannot be truncated, so those tests use a fresh tenant id per test instead of resetting data.

---

## 7. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Host won't start: "Missing connection string 'sbqr_app'" | `.env` not at the repo root, or the key spelling is wrong (`ConnectionStrings__sbqr_app`, double underscores) |
| Host won't start: `TrustStore:BaseUrl is required` | add `TrustStore__BaseUrl=http://localhost:5002` |
| Host won't start on S3 / `Storage` errors | `.env` still says `Crypto__VaultProvider=S3`; set `Local` |
| `401 invalid_client` for a tenant's token | tenant still **Pending** (call `/activate`), wrong secret, or you used the secret from an old provisioning |
| `403` on admin endpoints | you used a tenant token; use the admin token |
| `403`/`401` after ~10 minutes | token expired, fetch a new one |
| `404` on every route | forgot the `/v1/` prefix |
| `relation "usage_events" does not exist` | migrations not applied to the database in your connection string |
| `outbox_messages` stay `PENDING` | dispatcher not running (check log), or you are inside a back-off window (`next_attempt_at`), or the message has an unregistered `event_type` |
| `usage_events` empty after a successful generate | look at `outbox_messages.last_error` |
| `ERROR: … FINALIZED row is immutable` / `append-only` | a trigger doing its job; reset your lab database instead of fighting it |
| `/v1/qr/validate` returns 500 for an old `requestTimestamp` | known issue F2 below |

---

## 8. Known gaps (from the team's own E2E run, `docs/metering-billing-e2e-outcome.md`, plus code reading)

| # | What | Status in code today |
|---|---|---|
| F1 | `GET …/reports/late-usage` never returns rows. `FindLateUsageAsync` requires `finalized_at` to fall *inside* the usage month, but a month is finalized after it ends. 233 unit tests still pass, so look here first if you pick this up. | Confirmed by reading `MeteringQueries.FindLateUsageAsync` and `LateUsageReportQueryHandler`. |
| F2 | `/v1/qr/validate` with a ~2-hour-old `requestTimestamp` returns HTTP 500 (unhandled `ValidationException`). Not charged. | Reported against Verification code; not re-checked. |
| F3 | Finalize audit action is `billing.period_finalized`; the plan said `billing.period_finalize`. | Confirmed in `PeriodAudit.cs`. |
| F4 | Usage CSV `occurred_at` is UTC; the PRD says Bangladesh time. The default export has no approval cut-off, so it stops summing to the approved statement once late usage exists. | Confirmed (`UsageEventsCsvWriter` writes UTC). |
| F5 | A replayed validation request is not charged, but also not recorded as a `billable=false` row (ADR 0002). | Observed in step 5. |
| F6 | `Idempotency-Key` on generation is optional, so a retry without a key creates a second charge. | By design for now. |
| F7 | Not built: printable/PDF statement, full statement content, management report, operational alerts (tickets 04–07 are only partly implemented). | See `tickets/`. |

## 9. Where to read next

Order that works: the PRD (`prd.md`, requirements R1–R32) → `metering-billing-v1-bn.md` (design, Bengali) → `database-design.md` → ADRs 0001 and 0002 → `metering-tactical-ddd-bn.md` and `billing-tactical-ddd-bn.md` → `traceability.md` (requirement → story → code → test) → `tickets/` → the E2E evidence report in `rvl-secure-bqr-manager/docs/`. For the QR side, `docs/misc/dev-manual-test-guide-qr-postman.md` has the full QR request/verdict reference, and `docs/misc/new-tenant-onboard-guide.md` covers tenant onboarding in depth.
