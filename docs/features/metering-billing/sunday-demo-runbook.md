# Sunday demo runbook — QR generation, validation, metering, billing

Target: `rvl-secure-bqr-manager` (branch `feature/institution-onboarding`), Windows, PowerShell 7, Docker Desktop.
Story: Alpha Bank (031101) generates QRs, Beta Bank (031102) validates them, usage is metered, September is billed.
Run the **T-1 day** block on Saturday and rehearse once. Everything runs from the `rvl-secure-bqr-manager/` folder.

> Migrations on this branch are `001`–`011` (`008_outbox`, `009_metering`, `010_billing`). The older guide's `010`–`012` numbers are stale.

## T-1 day: setup (once)

```powershell
# 1. Throwaway lab DB. Finalized billing data cannot be wiped, so never demo on sbqr_app.
pwsh ./scripts/dev-stack-up.ps1 -Services postgres
docker exec -i sbqr.postgres psql -U postgres -c "DROP DATABASE IF EXISTS sbqr_lab;"
docker exec -i sbqr.postgres psql -U postgres -c "CREATE DATABASE sbqr_lab;"
Get-ChildItem db/migrations/*.sql | Sort-Object Name | ForEach-Object {
  Write-Host "applying $($_.Name)"
  Get-Content $_.FullName -Raw | docker exec -i sbqr.postgres psql -U postgres -d sbqr_lab -v ON_ERROR_STOP=1 -q }
docker exec -i sbqr.postgres psql -U postgres -d sbqr_lab -c "\dt public.outbox_messages public.usage_events public.billing_*"   # expect 7 rows

# 2. Admin secret. The plaintext in .env is not stored anywhere, so generate a new one.
dotnet run --project src/Host/SBQR.Api -- --generate-bootstrap-secret    # prints client_secret once + hash
```

Edit `.env`. It wins over environment variables, so change the file itself:

```ini
ConnectionStrings__sbqr_app=Host=localhost;Port=5432;Database=sbqr_lab;Username=postgres;Password=postgres;Include Error Detail=true
Auth__Bootstrap__ClientSecretHash=<hash from the command above>
Crypto__VaultProvider=Local
Messaging__Provider=InProcess
Outbox__MaxAttempts=2
```

Keep `ConnectionStrings__sbqr_key_vault` as it is. Note `Mtls__Enabled`: if it is `true`, plain `curl` to `:5001` will fail, so set `false` for the demo.

## T-0: start

```powershell
dotnet run --project src/Host/SBQR.Api --launch-profile SBQR.Api        # terminal 1, leave the log visible
curl.exe -s http://localhost:5001/health/ready                         # {"status":"ready"}
```

Optional trust-store mock (terminal 2: `dotnet run --project src/Host/BB.TrustStoreMock`, then `pwsh ./scripts/dev-seed-trust-store.ps1`). It is not needed for the demo, because activation writes the tenant's own key to the trust directory.

Paste the helper and set the secret from step 2:

```powershell
$Base="http://localhost:5001"; $BOOTSTRAP_SECRET="<client_secret>"
function Api { param([string]$Method,[string]$Path,$Body=$null,[string]$Token=$null,[hashtable]$Headers=@{})
  $h=@{}+$Headers; if($Token){$h["Authorization"]="Bearer $Token"}
  $r=@{Method=$Method;Uri="$Base$Path";Headers=$h;SkipHttpErrorCheck=$true}
  if($Body){$r.Body=$Body;$r.ContentType="application/json"}
  $x=Invoke-WebRequest @r; Write-Host "HTTP $($x.StatusCode)" -ForegroundColor Cyan
  $global:Last=$null; if($x.Content){ try{$global:Last=$x.Content|ConvertFrom-Json;$global:Last|ConvertTo-Json -Depth 12}catch{$x.Content} } }
function Psql { param([string]$Sql) docker exec -i sbqr.postgres psql -U postgres -d sbqr_lab -c $Sql }
$ADMIN=(Invoke-RestMethod -Method Post -Uri "$Base/v1/oauth/token" -Body @{grant_type="client_credentials";client_id="platform-bootstrap";client_secret=$BOOTSTRAP_SECRET}).accessToken
```

The token lasts 10 minutes. Re-run the last line on any `401`.

## Demo steps

**1. Onboard both banks** (`contactName` and `contactEmail` are now required). Repeat for Beta (`031102`) into `$BETA` and `$BETA_TOKEN`:

```powershell
Api POST /v1/admin/tenants '{"institutionName":"Alpha Bank","institutionCode":"031101","contactName":"Demo","contactEmail":"demo@example.com"}' $ADMIN; $ALPHA=$Last.tenantId
Api POST "/v1/admin/tenants/$ALPHA/tenant-configuration" '{"isQrGenerationAllowed":true,"isQrValidationAllowed":true}' $ADMIN; $AC=$Last
Api POST /v1/crypto-keys "{`"tenantId`":`"$ALPHA`",`"mode`":`"Generate`"}" $ADMIN
Api POST "/v1/admin/tenants/$ALPHA/activate" $null $ADMIN
$ALPHA_TOKEN=(Invoke-RestMethod -Method Post -Uri "$Base/v1/oauth/token" -Body @{grant_type="client_credentials";client_id=$AC.clientId;client_secret=$AC.clientSecret}).accessToken
```

**2. Generate, then watch the pipeline** (say: "QR row and usage note are written in one transaction")

```powershell
Api POST /v1/qr/generate/static '{"recipientName":"Arif Mahmood","recipientCity":"Dhaka","recipientPan":"01711111111"}' $ALPHA_TOKEN @{"Idempotency-Key"="demo-1"}; $QR=$Last.qrPayload
Api POST /v1/qr/generate/dynamic '{"transactionAmount":"150.00","recipientName":"Arif Mahmood","recipientCity":"Dhaka","recipientPan":"01711111111"}' $ALPHA_TOKEN @{"Idempotency-Key"="demo-2"}
Start-Sleep 2
Psql "SELECT event_type,status FROM outbox_messages ORDER BY created_at;"
Psql "SELECT meter_code,billable,client_reference FROM usage_events ORDER BY occurred_at;"
```

**3. Idempotency:** the same call again returns `409`, and `usage_events` is unchanged.

```powershell
Api POST /v1/qr/generate/static '{"recipientName":"Arif Mahmood","recipientCity":"Dhaka","recipientPan":"01711111111"}' $ALPHA_TOKEN @{"Idempotency-Key"="demo-1"}
```

**4. Validate: the verifier pays** (Beta checks Alpha's QR):

```powershell
Api POST /v1/qr/validate "{`"qrPayload`":`"$QR`"}" $BETA_TOKEN                       # VALID
Api POST /v1/qr/validate "{`"qrPayload`":`"$QR`",`"requestId`":`"r1`"}" $BETA_TOKEN   # VALID
Api POST /v1/qr/validate "{`"qrPayload`":`"$QR`",`"requestId`":`"r1`"}" $BETA_TOKEN   # REQUEST_REPLAYED, free, no row
Api POST /v1/qr/validate '{"qrPayload":"000201broken"}' $BETA_TOKEN                  # STRUCTURAL_INVALID, still billed
Start-Sleep 2
Psql "SELECT t.institution_code,u.meter_code,u.billable,u.detail FROM usage_events u JOIN tenants t USING(tenant_id) ORDER BY u.occurred_at;"
```

If `Psql` fails on the join, use `SELECT tenant_id,meter_code,billable,detail FROM usage_events;`. Beta's `tenant_id` is on the `VALIDATION` rows and Alpha's is on `GENERATION_*`.

**5. Seed September and a rate card (SQL)** (rate cards cannot be back-dated through the API):

```powershell
Psql @"
INSERT INTO billing_rate_cards (rate_card_id,tenant_id,effective_from,generation_rate,validation_rate,created_by)
VALUES (gen_random_uuid(),'$ALPHA','2026-09-01',0.5,0.3,'seed'),(gen_random_uuid(),'$ALPHA','2026-10-01',0.5,0.3,'seed');
INSERT INTO usage_events (usage_event_id,tenant_id,meter_code,billable,detail,source_type,source_id,source_event_id,client_reference,occurred_at)
SELECT gen_random_uuid(),'$ALPHA','GENERATION_STATIC',true,NULL,'qr_generation',gen_random_uuid(),gen_random_uuid(),'seed-s-'||g,timestamptz '2026-09-10 10:00+06'+g*interval '1 minute' FROM generate_series(1,4) g;
INSERT INTO usage_events (usage_event_id,tenant_id,meter_code,billable,detail,source_type,source_id,source_event_id,client_reference,occurred_at)
SELECT gen_random_uuid(),'$ALPHA','GENERATION_DYNAMIC',true,NULL,'qr_generation',gen_random_uuid(),gen_random_uuid(),'seed-d-'||g,timestamptz '2026-09-11 10:00+06'+g*interval '1 minute' FROM generate_series(1,2) g;
INSERT INTO usage_events (usage_event_id,tenant_id,meter_code,billable,detail,source_type,source_id,source_event_id,client_reference,occurred_at)
SELECT gen_random_uuid(),'$ALPHA','VALIDATION',true,'VALID','qr_validation',gen_random_uuid(),gen_random_uuid(),'seed-v-'||g,timestamptz '2026-09-12 10:00+06'+g*interval '1 minute' FROM generate_series(1,5) g;
"@
Api POST /v1/admin/billing/adjustments "{`"tenantId`":`"$ALPHA`",`"amount`":-1.00,`"reason`":`"SLA credit for the 12 Sept outage`",`"createdBy`":`"ops.lead`"}" $ADMIN
```

Expected for Alpha: 4 × 0.50 + 2 × 0.50 + 5 × 0.30 = 4.50, minus 1.00 = **3.50**.

**6. Billing walk:**

```powershell
Api GET /v1/admin/billing/periods/2026-10 $null $ADMIN                       # PROVISIONAL: live October usage from steps 2-4
Api POST /v1/admin/billing/periods/2026-10/draft $null $ADMIN                # 409 GRACE_NOT_OVER
Api GET /v1/admin/billing/periods/2026-09 $null $ADMIN                       # PROVISIONAL, total 3.50
Api POST /v1/admin/billing/periods/2026-09/draft $null $ADMIN                # 201 DRAFT
Api POST /v1/admin/billing/periods/2026-09/finalize '{"finalizedBy":"ops.lead","expectedTotal":9.99}' $ADMIN   # 409 DRAFT_CHANGED
Api POST /v1/admin/billing/periods/2026-09/finalize '{"finalizedBy":"ops.lead","expectedTotal":3.50}' $ADMIN   # FINALIZED
curl.exe -s -H "Authorization: Bearer $ADMIN" "$Base/v1/admin/billing/periods/2026-09/statements.csv"
Psql "UPDATE billing_statements SET total=0 WHERE period='2026-09';"        # trigger rejects: FINALIZED row is immutable
```

**7. Optional, dead letter** (needs `Outbox__MaxAttempts=2` from setup). Do it before step 6's draft if you want to show the guard:

```powershell
Psql @"
INSERT INTO outbox_messages (outbox_message_id,source_module,event_type,payload,occurred_at)
VALUES ('11111111-1111-1111-1111-111111111111','qr-generation','QrGenerated',
'{"EventId":"11111111-1111-1111-1111-111111111111","OccurredAt":"2026-09-20T06:00:00+00:00","QrGenerationId":"22222222-2222-2222-2222-222222222222","TenantId":"99999999-9999-9999-9999-999999999999","QrType":"BOGUS","ClientReference":"poison-1"}'::jsonb,'2026-09-20 12:00+06');
"@
Start-Sleep 8; Psql "SELECT status,attempts,last_error FROM outbox_messages WHERE outbox_message_id='11111111-1111-1111-1111-111111111111';"   # DEAD
Api POST /v1/admin/billing/periods/2026-09/draft $null $ADMIN                # 409 USAGE_NOT_COMPLETE
Psql "UPDATE outbox_messages SET payload=jsonb_set(payload,'{QrType}','""STATIC""') WHERE outbox_message_id='11111111-1111-1111-1111-111111111111';"
Api POST /v1/admin/outbox/11111111-1111-1111-1111-111111111111/requeue $null $ADMIN
```

## Do not demo, and fallbacks

- **Do not demo:** `reports/late-usage` (always empty, F1) and a validate request with an old `requestTimestamp` (500, F2). Don't promise a PDF statement or alerts.
- **Admin screens:** the portal usage screen looks like sample data. Check whether it is wired before showing it. Otherwise stay with the API and SQL.
- **Scalar UI:** `http://localhost:5001/docs/internal-admin` is a good visual fallback for browsing admin endpoints.
- **Validation returns `KEY_NOT_FOUND`:** the verifier looked up the issuer's key and found nothing, so activation didn't write it. Check `SELECT * FROM institution_keys;`. It is still billed, so the metering story is unaffected.
- **`usage_events` empty:** check `SELECT last_error FROM outbox_messages;` and the API log.
- **Reset:** drop and recreate `sbqr_lab`, re-run the migration loop, re-onboard. Budget about 5 minutes.
- **Dates:** the plan assumes a demo date after 2 Oct 2026 and that September is the last closed month. Shift every `2026-09` and `2026-10` if that changes.
