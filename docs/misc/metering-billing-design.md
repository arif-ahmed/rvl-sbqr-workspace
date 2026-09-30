# Metering & Billing — Implementation Design

**Target repo:** `rvl-secure-bqr-manager` · **Audience:** .NET developer new to fintech · **Status:** proposed

> Charter note: `AGENTS.md` says anything outside the BanglaQR spec needs a separate approved document. This is that document. It adds no QR or spec controls.

## Confirmed decisions

- **Billable operations:**
  - Successful static QR generation → meter `QR_STATIC_GENERATE`.
  - Successful dynamic QR generation → meter `QR_DYNAMIC_GENERATE`.
  - QR validation that reaches a cryptographic verdict → meter `QR_VALIDATE`. The billable verdicts are `VALID`, `INVALID_SIGNATURE`, `KEY_NOT_FOUND`, `KEY_SUSPENDED`, `KEY_REVOKED` and `KEY_NOT_ACTIVE`.
  - Free (not billed): `STRUCTURAL_INVALID`, `NON_P2P`, `REQUEST_STALE`, `REQUEST_REPLAYED`.
- **Pricing model:** a unit price per meter, plus an optional fixed monthly fee, plus an optional number of included (free) units per meter.
- **Billing period:** a calendar month in `Asia/Dhaka`.
- **Currency:** BDT.

## What exists today (the design builds on it)

| Fact | Where |
|---|---|
| One `qr_generations` row per successful QR, saved in a single `SaveChangesAsync` | `QrGeneration/.../Commands/Common/QrIssuancePipeline.cs` (~L211–233) |
| Optional `Idempotency-Key`, tenant-scoped unique index, duplicate → 409 | `db/migrations/003_generation.sql` |
| One `qr_validations` row per verdict; `(tenant_id, request_id)` is single-use | `Verification/.../ValidateQrCommandHandler.cs` `RecordAsync` (~L385–421), `004_verification.sql` |
| Domain events via MediatR `INotification`; **no outbox yet** | `SharedKernel/Domain/IDomainEvent.cs`, `AggregateRoot.cs` |
| Single `public` schema, hand-written SQL migrations, UUID v7 PKs, append-only trigger `forbid_mutation()` | `db/migrations/`, `007_audit.sql` |
| Scope-based auth policies (`admin`, `qr:generate`, `qr:validate`) | `IdentityAccess/.../IdentityAccessModule.cs` (~L200–240) |
| Testcontainers.PostgreSql integration tests, architecture tests | `tests/` |

---

## 0. TL;DR

- **Two new modules: `Metering` and `Billing`.** Scaffold them with the `module-scaffold` skill (5 projects each, the same layout as the existing modules). **Reporting is not a module.** It is a set of read endpoints: usage reports in Metering, invoice reports in Billing.
- **Hybrid consistency model:**
  - The QR modules write their business row **and** an outbox row in **one DB transaction** (Transactional Outbox).
  - A background **Outbox Dispatcher** delivers those events to Metering at least once.
  - Metering de-duplicates using a **unique constraint on the source record id**.
  - Billing commands (calculate, finalize) are **plain, fully transactional** operations inside the Billing module.
- **The billable moment** is the commit of the transaction that stores the `qr_generations` / `qr_validations` row. The outbox row commits in the same transaction.
- **Immutable facts:** usage events, plan versions and finalized invoices are never updated. Corrections are made by adding new rows.
- **Invoices store their calculated lines.** A finalized invoice is never recalculated.

---

## 1. Module boundaries

```
 QrGeneration ──┐  (same tx: business row + outbox row)
 Verification ──┤
                ▼
        outbox_messages ──► OutboxDispatcher (BackgroundService, host)
                                   │ in-process publish (MediatR)
                                   ▼
                              Metering  ── owns usage_events, meter rules, usage reports
                                   ▲ IMeteringQueries (Contracts, sync pull)
                                   │
                               Billing  ── owns plans, subscriptions, invoices, invoice reports
                                   │ outbox: InvoiceFinalized/Voided → notifications
```

| Module | Owns (writes) | Knows about | Must NOT know about |
|---|---|---|---|
| QrGeneration / Verification | `qr_generations`, `qr_validations`, and the outbox rows they emit | their own business facts | meters, prices, invoices |
| **Metering** | `usage_events`; the mapping "event → meter code / billable?" | the producers' integration-event contracts | prices, plans, invoices |
| **Billing** | `billing_plans`, `billing_plan_versions`, `billing_plan_prices`, `tenant_subscriptions`, `invoices`, `invoice_lines`, `invoice_number_sequences` | meter codes and usage quantities (only through `IMeteringQueries`) | QR, verdicts, outbox internals |
| Reporting | — (no module) | — | — |

**Rules**

- No module reads another module's tables, and there are no cross-module foreign keys. `tenant_id` is a *weak reference*, the same way `signature_key_version` is today.
- **Metering owns the billability rule** (which verdicts are billed). Producers publish plain business facts, such as "a QR was validated with verdict X". If the commercial rules change, only Metering changes.
- **Billing pulls** quantities from Metering when it calculates an invoice. It does not subscribe to usage events: an invoice run is a batch, and pulling in a batch is simpler and exact.

**Future extraction**

- **Metering → separate service:** the dispatcher publishes to a message broker instead of MediatR, the Metering handler stays unchanged, and `IMeteringQueries` becomes an HTTP client.
- **Billing → separate service:** only `IMeteringQueries` changes.
- In both cases no table joins have to be broken.

---

## 2. Architecture choice: Outbox between modules, plain transactions inside a module

| Option | Verdict | Why |
|---|---|---|
| Fully transactional: the QR handler inserts into `usage_events` directly | ✗ | Couples QR to Metering's schema and blocks extraction. A Metering bug would fail QR issuance, which is on the payment-critical path. |
| Publish an in-memory event after commit | ✗ | This is a **dual write**. If the process crashes between the commit and the publish, a QR was issued but never billed: lost revenue and reports that can't be reconciled. |
| **Outbox between modules + plain transactions inside Billing** | ✓ | The fact and its event are written atomically, without distributed transactions. A Metering outage only *delays* usage and never *loses* it. Retry is automatic. The design maps directly onto a message broker later. |

**Delivery guarantee:** at-least-once delivery plus an idempotent consumer gives **effectively-once billing**. Usage counts are commutative, so **ordering does not matter**, and no ordering machinery is needed.

---

## 3. Database schema

All tables go in `public` (repo convention), added as new migrations `010`–`012`. Use UUID v7 PKs (`Guid.CreateVersion7()`), UTC `timestamptz` for times, and `numeric` for money (never float).

### 3.1 `010_outbox.sql`: platform plumbing (mutable; not a financial record)

```sql
CREATE TABLE IF NOT EXISTS public.outbox_messages (
  outbox_message_id uuid PRIMARY KEY,               -- = integration event id
  source_module     varchar(50)  NOT NULL,          -- 'qr-generation' | 'verification' | 'billing'
  event_type        varchar(150) NOT NULL,          -- stable name, e.g. 'qr-generation.qr-generated.v1'
  payload           jsonb        NOT NULL,
  occurred_at       timestamptz  NOT NULL,          -- business time of the fact
  status            varchar(15)  NOT NULL DEFAULT 'PENDING'
                    CHECK (status IN ('PENDING','PROCESSED','DEAD')),
  attempts          integer      NOT NULL DEFAULT 0,
  next_attempt_at   timestamptz  NOT NULL DEFAULT now(),
  locked_until      timestamptz,
  last_error        varchar(2000),
  processed_at      timestamptz,
  created_at        timestamptz  NOT NULL DEFAULT now()
);
-- dispatcher poll
CREATE INDEX IF NOT EXISTS ix_outbox_due  ON public.outbox_messages (next_attempt_at) WHERE status = 'PENDING';
-- "is usage complete up to T?" + dead-letter list
CREATE INDEX IF NOT EXISTS ix_outbox_open ON public.outbox_messages (occurred_at)     WHERE status IN ('PENDING','DEAD');
-- retention cleanup
CREATE INDEX IF NOT EXISTS ix_outbox_processed ON public.outbox_messages (processed_at) WHERE status = 'PROCESSED';

-- Generic append-only guard (the existing forbid_mutation() hard-codes 'audit_logs' in its message).
CREATE OR REPLACE FUNCTION public.forbid_mutation_any() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'public.% is append-only: % is prohibited', TG_TABLE_NAME, TG_OP
    USING ERRCODE = 'check_violation';
END $$;
```

### 3.2 `011_metering.sql`

```sql
CREATE TABLE IF NOT EXISTS public.usage_events (
  usage_event_id   uuid PRIMARY KEY,
  tenant_id        uuid         NOT NULL,           -- weak ref (no cross-module FK)
  meter_code       varchar(40)  NOT NULL
                   CHECK (meter_code IN ('QR_STATIC_GENERATE','QR_DYNAMIC_GENERATE','QR_VALIDATE')),
  quantity         integer      NOT NULL DEFAULT 1 CHECK (quantity > 0),
  source_type      varchar(30)  NOT NULL CHECK (source_type IN ('qr_generation','qr_validation')),
  source_id        uuid         NOT NULL,           -- qr_generation_id / qr_validation_id
  source_event_id  uuid         NOT NULL,           -- outbox_message_id (tracing only)
  client_reference varchar(100),                    -- FI's Idempotency-Key / request_id → FI-side reconciliation
  detail           varchar(40),                     -- e.g. verdict for QR_VALIDATE; never PII
  occurred_at      timestamptz  NOT NULL,           -- business time → decides the billing period
  recorded_at      timestamptz  NOT NULL DEFAULT now(), -- when Metering stored it → invoice cutoff
  CONSTRAINT uq_usage_events_source UNIQUE (source_type, source_id)   -- THE dedup guarantee
);
CREATE INDEX IF NOT EXISTS ix_usage_tenant_time ON public.usage_events (tenant_id, occurred_at)
  INCLUDE (meter_code, quantity, recorded_at);      -- tenant reports + invoice calc (index-only)
CREATE INDEX IF NOT EXISTS ix_usage_time ON public.usage_events (occurred_at)
  INCLUDE (tenant_id, meter_code, quantity);        -- admin all-tenant reports

DROP TRIGGER IF EXISTS trg_usage_events_immutable ON public.usage_events;
CREATE TRIGGER trg_usage_events_immutable
  BEFORE UPDATE OR DELETE OR TRUNCATE ON public.usage_events
  FOR EACH STATEMENT EXECUTE FUNCTION public.forbid_mutation_any();
```

> **Why is the dedup key `(source_type, source_id)` and not the event id?** Even if a producer bug emits two events for the same QR row, the result is still one unit. Dedup follows the *business fact*, not the message.

### 3.3 `012_billing.sql`

```sql
CREATE TABLE IF NOT EXISTS public.billing_plans (
  plan_id    uuid PRIMARY KEY,
  code       varchar(50)  NOT NULL UNIQUE,
  name       varchar(200) NOT NULL,
  currency   char(3)      NOT NULL DEFAULT 'BDT',
  created_by varchar(200),
  created_at timestamptz  NOT NULL DEFAULT now());

-- A version is IMMUTABLE once created. Changing a price = creating a new version.
CREATE TABLE IF NOT EXISTS public.billing_plan_versions (
  plan_version_id uuid PRIMARY KEY,
  plan_id     uuid NOT NULL REFERENCES public.billing_plans(plan_id) ON DELETE RESTRICT,
  version_no  integer NOT NULL,
  monthly_fee numeric(18,2) NOT NULL DEFAULT 0 CHECK (monthly_fee >= 0),
  created_by  varchar(200),
  created_at  timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_plan_versions UNIQUE (plan_id, version_no));

CREATE TABLE IF NOT EXISTS public.billing_plan_prices (
  plan_version_id uuid NOT NULL REFERENCES public.billing_plan_versions(plan_version_id) ON DELETE RESTRICT,
  meter_code      varchar(40)   NOT NULL,
  unit_price      numeric(18,4) NOT NULL CHECK (unit_price >= 0),
  included_units  bigint        NOT NULL DEFAULT 0 CHECK (included_units >= 0),
  PRIMARY KEY (plan_version_id, meter_code));

-- Which plan version applies to a tenant from which month. No end date:
-- the row with the latest effective_from <= period_start wins.
-- plan_version_id NULL = "not billed from this month".
CREATE TABLE IF NOT EXISTS public.tenant_subscriptions (
  subscription_id uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL,
  plan_version_id uuid REFERENCES public.billing_plan_versions(plan_version_id) ON DELETE RESTRICT,
  effective_from  date NOT NULL CHECK (extract(day FROM effective_from) = 1),  -- whole months only
  created_by      varchar(200),
  created_at      timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_tenant_subscriptions UNIQUE (tenant_id, effective_from));  -- also serves the lookup

CREATE TABLE IF NOT EXISTS public.invoices (
  invoice_id        uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL,
  period_start      date NOT NULL,                  -- 1st of month (Asia/Dhaka)
  period_end        date NOT NULL,                  -- exclusive: 1st of next month
  status            varchar(15) NOT NULL CHECK (status IN ('DRAFT','FINALIZED','PAID','VOID')),
  invoice_number    varchar(30) UNIQUE,             -- assigned at finalization only
  plan_version_id   uuid REFERENCES public.billing_plan_versions(plan_version_id),
  currency          char(3)       NOT NULL,
  subtotal          numeric(18,2) NOT NULL,
  tax_rate          numeric(5,4)  NOT NULL,
  tax_amount        numeric(18,2) NOT NULL,
  total             numeric(18,2) NOT NULL,
  usage_cutoff_at   timestamptz   NOT NULL,         -- usage with recorded_at <= this is included
  calculated_at     timestamptz   NOT NULL,
  calculated_by     varchar(200)  NOT NULL,
  finalized_at      timestamptz,
  finalized_by      varchar(200),
  paid_at           timestamptz,
  payment_reference varchar(100),
  voided_at         timestamptz,
  voided_by         varchar(200),
  void_reason       varchar(500),
  created_at        timestamptz NOT NULL DEFAULT now());
-- one live invoice per tenant per period (a voided one can be re-issued)
CREATE UNIQUE INDEX IF NOT EXISTS uq_invoices_tenant_period
  ON public.invoices (tenant_id, period_start) WHERE status <> 'VOID';
CREATE INDEX IF NOT EXISTS ix_invoices_tenant_period ON public.invoices (tenant_id, period_start DESC); -- tenant list
CREATE INDEX IF NOT EXISTS ix_invoices_period_status ON public.invoices (period_start, status);         -- admin list

CREATE TABLE IF NOT EXISTS public.invoice_lines (
  invoice_line_id   uuid PRIMARY KEY,
  invoice_id        uuid NOT NULL REFERENCES public.invoices(invoice_id) ON DELETE CASCADE,
  line_no           integer NOT NULL,
  line_type         varchar(15) NOT NULL CHECK (line_type IN ('MONTHLY_FEE','USAGE','ADJUSTMENT')),
  meter_code        varchar(40),                        -- USAGE only
  description       varchar(200) NOT NULL,
  quantity          bigint NOT NULL DEFAULT 0,          -- metered units
  included_units    bigint NOT NULL DEFAULT 0,          -- free units applied
  billable_quantity bigint NOT NULL DEFAULT 0,
  unit_price        numeric(18,4) NOT NULL DEFAULT 0,   -- SNAPSHOT from the plan version
  amount            numeric(18,2) NOT NULL,             -- may be negative only for ADJUSTMENT
  reason            varchar(500),                       -- required for ADJUSTMENT
  CONSTRAINT uq_invoice_lines UNIQUE (invoice_id, line_no),
  CONSTRAINT ck_adjustment_reason CHECK (line_type <> 'ADJUSTMENT' OR reason IS NOT NULL));

-- Gapless invoice numbers: row-locked counter, incremented inside the finalize transaction.
CREATE TABLE IF NOT EXISTS public.invoice_number_sequences (
  year       integer PRIMARY KEY,
  last_value integer NOT NULL);
```

**Immutability triggers (in `012_billing.sql`)**

| Table | Rule |
|---|---|
| `billing_plan_versions`, `billing_plan_prices` | `forbid_mutation_any` on UPDATE / DELETE / TRUNCATE |
| `tenant_subscriptions` | UPDATE is forbidden. DELETE is allowed only when `effective_from > current_date`, so a mistaken future row can be removed. |
| `invoices` | While `OLD.status = 'DRAFT'`, anything goes. After that, the only allowed transitions are `FINALIZED → PAID` (sets `paid_at` and `payment_reference`) and `FINALIZED → VOID` (sets the `voided_*` columns). Every amount, number and period column must stay equal. DELETE is allowed only for a DRAFT. |
| `invoice_lines` | INSERT, UPDATE and DELETE raise an error when the parent invoice's status is not `DRAFT`. |

**Grants.** Follow the `sbqr_app_runtime` non-owner convention described in `db/migrations/README.md`. The runtime role gets only `SELECT, INSERT` on `usage_events` and `SELECT` on the plan tables.

### 3.4 Relationships

```
billing_plans 1──* billing_plan_versions 1──* billing_plan_prices
billing_plan_versions 1──* tenant_subscriptions
billing_plan_versions 1──* invoices 1──* invoice_lines
usage_events, invoices, tenant_subscriptions ──(weak ref: tenant_id)──> tenants
```

---

## 4. Commands / actions

### Metering

**Internal command**

- `RecordUsage`, called only by the integration-event handlers:
  1. Map the event to a meter code.
  2. If the event is not billable, return without writing.
  3. Otherwise insert with:
     ```sql
     INSERT INTO usage_events (...) VALUES (...)
     ON CONFLICT (source_type, source_id) DO NOTHING;
     ```

**Queries**

- `GetCurrentUsage(tenant)`
- `GetUsage(tenant?, from, to, groupBy = meter | day | tenant)`
- `GetInvoiceUsageEvents(tenant, from, to, cutoff, cursor)`, keyset-paged

**Contracts** (in `Metering.Contracts`, used by Billing)

- `IMeteringQueries.GetBillableQuantities(tenantId, fromUtc, toUtc, recordedAtOrBefore)` returns `{ meter_code, quantity }[]`.
- `IMeteringQueries.IsUsageComplete(untilUtc)` is true when there are no `PENDING` or `DEAD` outbox messages from QR modules with `occurred_at < untilUtc`.

### Billing

Every Billing command writes an `IAuditLogger` entry recording who ran it, what it did, and the before/after state.

**Plans and subscriptions**

| Command | Rule |
|---|---|
| `CreatePlan(code, name, currency)` | — |
| `CreatePlanVersion(planId, monthlyFee, prices[])` | The version is immutable; `version_no = max + 1`. |
| `SetTenantSubscription(tenantId, planVersionId \| null, effectiveFrom)` | Rejected if a non-VOID invoice already exists for any period ≥ `effectiveFrom`. |
| `DeleteFutureSubscription(subscriptionId)` | Only for rows that are not yet effective. |

**Invoicing**

| Command | Rule |
|---|---|
| `CalculateInvoice(tenantId, period "YYYY-MM")` | Creates or replaces a **DRAFT** invoice (§8). |
| `RunBillingPeriod(period)` | Runs `CalculateInvoice` for every subscribed tenant. One tenant failing doesn't stop the others; the result reports each tenant's outcome. |
| `AddInvoiceAdjustment(invoiceId, amount, reason)` | DRAFT only. |
| `FinalizeInvoice(invoiceId)` | §8 |
| `VoidInvoice(invoiceId, reason)` | FINALIZED only. |
| `MarkInvoicePaid(invoiceId, paymentReference, paidAt)` | FINALIZED only. |

---

## 5. Events, handlers, notifications

**Integration events.** Each event lives in the producer's `.Contracts` project and is versioned. They implement a new SharedKernel interface: `IIntegrationEvent : INotification { Guid EventId; DateTimeOffset OccurredAt; }`.

| `event_type` → CLR type | Producer | Payload | Handler(s) |
|---|---|---|---|
| `qr-generation.qr-generated.v1` → `QrGeneratedIntegrationEvent` | QrGeneration | EventId, QrGenerationId, TenantId, QrType, IdempotencyKey?, OccurredAt | Metering `QrGeneratedHandler` → `RecordUsage` (STATIC → `QR_STATIC_GENERATE`, DYNAMIC → `QR_DYNAMIC_GENERATE`) |
| `verification.qr-validated.v1` → `QrValidatedIntegrationEvent` | Verification (every recorded row) | EventId, QrValidationId, TenantId (the verifying tenant), Verdict, RequestId, OccurredAt | Metering `QrValidatedHandler` → `QR_VALIDATE` only for the 6 billable crypto verdicts; any other verdict returns without writing |
| `billing.invoice-finalized.v1` | Billing (from the `InvoiceFinalized` domain event, same tx) | InvoiceId, TenantId, InvoiceNumber, Period, Total, Currency | `NotifyTenantInvoiceIssued` |
| `billing.invoice-voided.v1` | Billing | InvoiceId, TenantId, Reason | `NotifyTenantInvoiceVoided` |

**Domain events** (inside Billing's `Invoice` aggregate): `InvoiceCalculated`, `InvoiceFinalized`, `InvoiceVoided`, `InvoicePaid`. Only Finalized and Voided are turned into outbox messages, because they are the only ones another party cares about.

**Notifications (v1)**

- **Tenant and finance** notifications go through a small `IBillingNotifier` interface.
  - The v1 implementation writes a structured log event with a stable EventId.
  - Tenants see new invoices through `GET /v1/billing/invoices`.
  - An email/SMS adapter can be plugged in later without changing any handler.
- **Ops** alerts:

  | Situation | Log | Level |
  |---|---|---|
  | An outbox message was dead-lettered | EventId `OUTBOX_DEAD_LETTER`, watched by a log alert rule | `LogCritical` |
  | `RunBillingPeriod` finished with failures | — | `LogError` |

> **Handler rule:** every handler must be **idempotent**. A message can be delivered more than once, and when one event has several handlers, all of them run again on retry.

---

## 6. Outbox writer and dispatcher worker

### Writing (producer side)

- **QrGeneration:** in `QrIssuancePipeline`, add `_db.OutboxMessages.Add(OutboxMessage.From(evt, "qr-generation"))` right **before** the existing `_db.SaveChangesAsync` (~L222). The QR row and the event then commit together.
- **Verification:** make the same change in `ValidateQrCommandHandler.RecordAsync`, before `SaveChangesAsync` (~L411).
- On a duplicate insert (`23505`), neither row is written. That means no event and no bill.
- Each producing `DbContext` maps `OutboxMessage` using a shared `OutboxMessageConfiguration` from SharedKernel. Update the schema-drift tests if they compare these contexts.

### Dispatcher

The dispatcher lives in `src/Host/SBQR.Api/Outbox/OutboxDispatcher.cs`. It is a `BackgroundService`, registered next to `TenantCertificateThumbprintSyncService`. Its loop:

1. **Claim a batch using a lease.** This avoids holding a long transaction and is safe when several API instances run:
   ```sql
   UPDATE outbox_messages
      SET locked_until = now() + interval '60 seconds', attempts = attempts + 1
    WHERE outbox_message_id IN (
      SELECT outbox_message_id FROM outbox_messages
       WHERE status = 'PENDING' AND next_attempt_at <= now()
         AND (locked_until IS NULL OR locked_until < now())
       ORDER BY occurred_at
       LIMIT 100
       FOR UPDATE SKIP LOCKED)
   RETURNING *;
   ```
2. **Process each message:**
   1. Resolve `event_type` to a CLR type through an **explicit registry**, which is just a dictionary. Never call `Type.GetType` on strings read from the database.
   2. Deserialize the payload and call `IPublisher.Publish` in a new DI scope.
   3. Record the outcome:

      | Outcome | Update |
      |---|---|
      | Success | `status='PROCESSED'`, `processed_at=now()`, `locked_until=NULL` |
      | Failure | `last_error` (truncated, no PII), `next_attempt_at = now() + min(2^attempts s, 300 s)`, `locked_until=NULL` |
      | Failure and `attempts >= 10` (≈ 18 min of retries) | `status='DEAD'`, plus `LogCritical` |
3. **Pace the loop.** If the batch was full, run again immediately; otherwise wait 1 s.
4. **Clean up.** Once an hour, delete `PROCESSED` rows older than 7 days. The outbox is plumbing; `usage_events` is the durable record.

Configuration keys: `Outbox:PollIntervalMs`, `Outbox:BatchSize`, `Outbox:MaxAttempts`, `Outbox:RetentionDays`.

### Crash and failure cases

| Case | Result |
|---|---|
| Crash after the handler committed but before the message was marked `PROCESSED` | The lease expires and the message is delivered again. Metering's `ON CONFLICT DO NOTHING` absorbs the duplicate. |
| Crash before the handler ran | The message is simply retried. |
| Handler fails temporarily (DB blip) | Retried with exponential backoff. |
| Handler keeps failing | Status becomes `DEAD` and an alert fires. |

**Dead letters**

- `GET /v1/admin/outbox/dead` lists them.
- `POST /v1/admin/outbox/{id}/requeue` resets the message (`status='PENDING'`, `attempts=0`, `next_attempt_at=now()`). The action is audited.
- Dead letters **block invoice finalization** for their period (§8), so they cannot be forgotten.

---

## 7. Metering flow: the exact billable moment

```
FI → POST /v1/qr/generate/dynamic   (Idempotency-Key: K)
  → validate → sign → finalize payload
  → ONE transaction: INSERT qr_generations + INSERT outbox_messages(qr-generated.v1)   ← BILLABLE MOMENT (commit)
  → 200 with QR

Dispatcher (~1 s later) → Metering.QrGeneratedHandler
  → INSERT usage_events(meter=QR_DYNAMIC_GENERATE, source_id=qr_generation_id, occurred_at=row.created_at)
    ON CONFLICT (source_type, source_id) DO NOTHING
```

| Situation | Billed? |
|---|---|
| Validation error, inactive tenant or key, signing failure, 500 | No: no row, no event |
| Commit succeeded but the HTTP response was lost | Yes, once. A retry with the same `K` gets 409 and is not billed again. |
| QR generated at 23:59:59 on 30 Sep, recorded at 00:00:02 on 1 Oct | Billed in **September**, because `occurred_at` is the producer's time |
| Validation with verdict `STRUCTURAL_INVALID` or `NON_P2P` | No: Metering ignores it |
| Validation with a replayed `request_id` | No: no row, no event |

---

## 8. Billing and invoice flow

**Period.** A calendar month in `Asia/Dhaka` (UTC+6, no DST; config `Billing:TimeZone`). The range runs from 00:00 (+06:00) on `period_start` up to, but not including, 00:00 (+06:00) on `period_end`. It is converted to UTC for queries.

### `CalculateInvoice(tenant, period)`

Everything runs in one DB transaction, with `SELECT … FOR UPDATE` on the existing invoice row if there is one.

1. **Is the period closed?** Require `now ≥ period_end + Billing:CloseGraceMinutes` (default 120). Otherwise return 409 `PERIOD_NOT_CLOSED`. Use the preview endpoint for open periods.
2. **Is usage complete?** Call `IMeteringQueries.IsUsageComplete(period_end_utc)`. If it returns false, return 409 `USAGE_NOT_COMPLETE`: there are pending or dead outbox messages.
3. **Check for an existing invoice.**
   - FINALIZED or PAID → 409 `ALREADY_FINALIZED`.
   - DRAFT → replace it: delete its fee and usage lines, **keep its ADJUSTMENT lines**.
4. **Find the subscription.** Take the row with the latest `effective_from ≤ period_start`. If there is none, or its plan version is null, skip the tenant with `NOT_SUBSCRIBED`.
5. **Get quantities.** Set `cutoff = now()` and call `GetBillableQuantities(tenant, start, end, recordedAtOrBefore: cutoff)`.
6. **Build the lines from the plan version.**
   - Add a `MONTHLY_FEE` line if the fee is greater than 0.
   - Add one `USAGE` line **per priced meter, even when usage is zero**:
     - `billable = max(0, quantity − included_units)`
     - `amount = round(billable × unit_price, 2, AwayFromZero)`
   - **If there is usage for a meter the plan doesn't price, the calculation fails** with `UNPRICED_METER`. Never bill 0 silently and never drop usage.
7. **Compute the totals.**
   - `subtotal = Σ line amounts`
   - `tax_amount = round(subtotal × tax_rate, 2)`, with `tax_rate` snapshotted from `Billing:VatRate`
   - `total = subtotal + tax_amount`
   - Store `usage_cutoff_at = cutoff` and `status = DRAFT`.

**Draft review.** Finance reviews the draft through the admin API and can add `ADJUSTMENT` lines, each with a reason. The totals are recomputed after each change.

### `FinalizeInvoice(id)`

One transaction, with the invoice row locked.

1. **Check the current status.** If it is already FINALIZED or PAID, return the invoice unchanged, so the call is idempotent. If it is VOID, return 409.
2. **Re-check freshness.** `IsUsageComplete` must still be true, and **no usage for this tenant and period may have `recorded_at > usage_cutoff_at`**. Otherwise return 409 `DRAFT_STALE`: recalculate first.
3. **Four-eyes check.** When `Billing:RequireFourEyes=true` (the default outside Development), `finalized_by` must differ from `calculated_by`.
4. **Assign the invoice number.** Run `INSERT … ON CONFLICT (year) DO UPDATE SET last_value = last_value + 1 RETURNING last_value`, giving a number like `SBQR-2026-000123`. Because it is in the same transaction, a rollback never leaves a gap.
5. **Finalize.** Set `status = FINALIZED` and `finalized_at` / `finalized_by`. Raise `InvoiceFinalized`, which writes an outbox row in the same `SaveChanges`.

### After finalization

- The invoice and its lines are frozen by DB triggers.
- Allowed actions:
  - `MarkInvoicePaid`.
  - `VoidInvoice(reason)`. The row and its number are kept, and the tenant sees it as VOID. A new DRAFT can then be calculated for the same period and gets a new number.
- **These records become immutable:**
  - finalized, paid and void invoices, and their lines
  - the plan versions and prices they reference (always immutable)
  - subscriptions (always immutable)
  - usage events (always immutable)

### Late usage

Late usage is usage for an already-finalized period that arrives after the cutoff. It should be very rare because of the completeness gate.

- It is **never** added to the old invoice.
- The reconciliation report shows it.
- Finance bills it as an `ADJUSTMENT` line on the next draft. v1 does not do this automatically.

---

## 9. API endpoints

**Tenant scoping**

- The tenant is **always** taken from the token (`ICurrentTenant`), never from the route.
- Asking for another tenant's invoice returns **404**, not 403.

**New scope policies** (add them in `IdentityAccessModule` next to the existing ones):

| Scope | Granted to | Allows |
|---|---|---|
| `billing:read` | every active tenant (minted with its token) | tenant endpoints |
| `admin` or `billing-admin` | platform admins, finance | admin writes |
| `admin`, `billing-admin` or `billing-read` | admins, finance, management | admin reads |

### Tenant

| Method | Path | Notes |
|---|---|---|
| GET | `/v1/usage/current` | Current month per meter, with `asOf`. Near-real-time (a few seconds of lag), labelled "not final". |
| GET | `/v1/usage?from=&to=&groupBy=meter\|day` | Range ≤ 93 days |
| GET | `/v1/billing/invoices?page=` | FINALIZED, PAID and VOID only. Drafts are never shown to tenants. |
| GET | `/v1/billing/invoices/{id}` | Header and lines |
| GET | `/v1/billing/invoices/{id}/usage?groupBy=day` | The usage behind the invoice, using its `usage_cutoff_at`, so it **sums exactly to the lines** |
| GET | `/v1/billing/invoices/{id}/usage-events?cursor=` | The individual facts (meter, occurred_at, client_reference), keyset-paged, for FI-side reconciliation |

Example `GET /v1/usage/current`:

```json
{
  "period": { "from": "2026-09-01", "to": "2026-09-30" },
  "asOf": "2026-09-29T10:15:02Z",
  "final": false,
  "meters": [
    { "meter": "QR_STATIC_GENERATE",  "quantity": 10422 },
    { "meter": "QR_DYNAMIC_GENERATE", "quantity": 324521 },
    { "meter": "QR_VALIDATE",         "quantity": 682115 }
  ]
}
```

### Admin (`/v1/admin/...`)

| Method | Path |
|---|---|
| POST / GET | `/billing/plans`, `/billing/plans/{id}` |
| POST | `/billing/plans/{id}/versions` |
| POST / GET | `/tenants/{tenantId}/subscriptions` (GET returns the history) |
| DELETE | `/tenants/{tenantId}/subscriptions/{id}` (future rows only) |
| GET | `/usage?from=&to=&tenantId=&groupBy=tenant\|meter\|day` |
| GET | `/billing/preview?tenantId=&period=` (calculated live, never stored) |
| POST | `/billing/runs` with body `{ "period": "2026-09" }` |
| POST | `/billing/tenants/{tenantId}/invoices/calculate` with body `{ "period": "2026-09" }` |
| GET | `/billing/invoices?period=&status=&tenantId=`, `/billing/invoices/{id}` |
| POST | `/billing/invoices/{id}/adjustments`, `/finalize`, `/void`, `/mark-paid` |
| GET | `/billing/reconciliation?period=` |
| GET / POST | `/outbox/dead`, `/outbox/{id}/requeue` |

---

## 10. Tenant vs admin reporting

| Need | Tenant | Admin |
|---|---|---|
| Live usage | Own tenant, current month | Any or all tenants, any range, grouped by tenant, meter or day |
| Invoices | Own invoices: finalized, paid, void | All statuses including DRAFT; filter by period, status, tenant |
| Calculation detail | Lines: quantity, included units, unit price, amount | The same, plus plan version, cutoff, and who calculated and finalized it |
| Plans | — (the lines show the unit price) | Plans, versions, subscription history |
| Reconciliation | The invoice usage endpoints sum to the lines | `/billing/reconciliation` |
| Ops | — | Dead letters, billing-run results |

### Reconciliation invariant

`/billing/reconciliation` runs this check, and it is also an integration test. For every non-void invoice and every USAGE line:

```sql
line.quantity == SUM(quantity) FROM usage_events
                 WHERE tenant_id = inv.tenant AND meter_code = line.meter
                   AND occurred_at >= start_utc AND occurred_at < end_utc
                   AND recorded_at <= inv.usage_cutoff_at

late_usage      = the same query with recorded_at > inv.usage_cutoff_at   -- must be 0, or adjusted
```

The live dashboard for a closed month can only be higher than the invoice by `late_usage`. The per-invoice usage endpoint always matches the invoice exactly.

### Query → index map

| Query | Index |
|---|---|
| Tenant current usage, invoice calculation | `ix_usage_tenant_time` (index-only) |
| Admin all-tenant usage | `ix_usage_time` |
| Tenant invoice list | `ix_invoices_tenant_period` |
| Admin invoice list by period and status | `ix_invoices_period_status` |
| Subscription lookup | `uq_tenant_subscriptions` |
| Dispatcher poll, completeness check | `ix_outbox_due` / `ix_outbox_open` |

**Scale note.** v1 counts raw rows through covering indexes. That is fine up to roughly tens of millions of rows per month. When admin reports slow down, add monthly partitioning of `usage_events` or a daily rollup table. Invoices keep counting from `usage_events`, which stays the source of truth.

---

## 11. Retry and idempotency: three layers

| Layer | Source of duplicates | Guard |
|---|---|---|
| 1. HTTP (FI retries) | The same generation request is sent again | `Idempotency-Key` → `uq_qr_generations_tenant_idempotency` → 409; no row, no event |
| | The same validation request is sent again | Mandatory `request_id` → `uq_qr_validations_replay` → REQUEST_REPLAYED; no row, no event |
| 2. Delivery (outbox redelivery) | A handler runs again | `uq_usage_events_source (source_type, source_id)` + `ON CONFLICT DO NOTHING` |
| 3. Billing (admin clicks twice, concurrent runs) | Calculate or finalize runs twice | Partial unique index `uq_invoices_tenant_period`, row lock, finalize is a no-op when already final, gapless counter in the same transaction |

> ⚠ **Known gap:** `Idempotency-Key` is **optional** on generation today. A retry **without** a key looks exactly like a new request. It produces a new, distinct QR, and that QR is billed. Document this in the FI integration guide ("always send Idempotency-Key"). Making the header mandatory is a separate v1.public contract decision (§15).

---

## 12. Implementation order

Each step leaves something you can run and check locally.

Apply migrations with the loop in `db/migrations/README.md`. To inspect data:

```bash
docker compose -f docker/docker-compose.yml exec sbqr.postgres psql -U postgres -d sbqr_app
```

1. **Scaffold the modules.**
   - Create `Metering` and `Billing` with the `module-scaffold` skill.
   - Run `dotnet build` and `dotnet test tests/SBQR.ArchitectureTests`.
2. **Build the outbox.**
   - Add `010_outbox.sql`.
   - Add the SharedKernel types `IIntegrationEvent`, `OutboxMessage` and `OutboxMessageConfiguration`.
   - Add the host `OutboxDispatcher` and its event-type registry.
   - *Check:* insert a message of a registered test type by hand and watch it become `PROCESSED`. Make its handler throw and watch `attempts` and `next_attempt_at` grow until the status is `DEAD`.
3. **Emit events from the producers.**
   - Add the event contracts to `QrGeneration.Contracts` and `Verification.Contracts`.
   - Add the outbox row before the existing `SaveChangesAsync` calls.
   - *Check:* generate and validate a QR, then run `SELECT * FROM outbox_messages;`. A request with a duplicate `Idempotency-Key` adds no outbox row.
4. **Metering write side.**
   - Add `011_metering.sql` and the two handlers that call `RecordUsage`.
   - *Check* each of these:
     - There is exactly one `usage_events` row per QR.
     - After a forced redelivery (dev only), the row count doesn't change:
       ```sql
       UPDATE outbox_messages SET status='PENDING', attempts=0 WHERE ...;
       ```
     - An `INVALID_SIGNATURE` validation is billed; a `STRUCTURAL_INVALID` one is not.
     - `UPDATE usage_events ...` fails.
5. **Metering read side.**
   - Add `/v1/usage/current`, `/v1/usage` and `/v1/admin/usage`.
   - Add `IMeteringQueries`.
   - Add the `billing:read` scope.
6. **Billing catalog.**
   - Add the plan, version, price and subscription tables from `012_billing.sql`.
   - Add the plan and subscription commands and endpoints.
   - *Check:* `UPDATE billing_plan_prices ...` fails.
7. **Invoicing.**
   - Add `CalculateInvoice`, `AddInvoiceAdjustment`, `FinalizeInvoice`, `VoidInvoice` and `MarkInvoicePaid`.
   - Add the invoice triggers and the number sequence.
   - *Check:* make the period count as closed (config or test clock), then calculate and finalize. `UPDATE invoice_lines ...` must fail afterwards.
8. **Reporting and reconciliation.**
   - Tenant invoice endpoints and the usage-behind-invoice endpoints.
   - Admin invoice list, `/billing/reconciliation`, `/billing/preview`.
9. **Notifications and ops.**
   - `InvoiceFinalized` / `InvoiceVoided` → outbox → `IBillingNotifier`.
   - `RunBillingPeriod`.
   - Dead-letter endpoints.
10. **Tests.** Use Testcontainers.PostgreSql, like the existing `*.IntegrationTests`. Cover:
    - A duplicate delivery produces 1 row.
    - A price change after finalization leaves the old invoice unchanged.
    - An event at the month boundary lands in the right period.
    - Rounding and included units.
    - An unpriced meter makes the calculation fail.
    - Finalize is idempotent.
    - The reconciliation invariant holds.
    - Tenant A gets 404 for tenant B's invoice.
11. **Documentation.**
    - Update `db/migrations/README.md` and `docs/design/database-design.md`.
    - Update the Postman collection (`postman-export` skill).
    - Update the v1.public OpenAPI artifact with the new tenant endpoints.

---

## 13. Fintech rules that must not be broken

1. **Money** is `decimal` in C# and `numeric` in SQL, **never** `double` or `float`. Round each line once (`MidpointRounding.AwayFromZero`, 2 dp), then sum the rounded lines.
2. **Bill only from `usage_events`.** Never bill from HTTP logs, metrics or `audit_logs`.
3. **Usage events, plan versions and finalized invoices are append-only.**
   - Corrections are *new records* (an adjustment line, or void + re-issue), never edits.
   - Never run manual `UPDATE` or `DELETE` in any shared environment.
4. **Never recalculate a finalized invoice.** Read its stored lines.
5. **Take the price from the plan version subscribed for that period**, never from "the current price".
6. **Time:**
   - Store UTC `timestamptz`.
   - Compute period boundaries in `Asia/Dhaka`.
   - `occurred_at` (business time) decides the period; `recorded_at` is only used for the cutoff.
7. **Enforce dedup with DB unique constraints**, not with "check, then insert" in code.
8. **Add the outbox row in the same `SaveChanges` as the business row.** Never publish before commit.
9. **Every event handler must be idempotent.**
10. **Fail closed.** An unpriced meter, incomplete usage or a stale draft stops the calculation. Never guess, and never bill 0 silently.
11. **Take the tenant id from the token only.** Reads across tenants return 404.
12. **Invoice numbers are gapless** and are assigned only at finalization.
13. **Audit every billing command** through `IAuditLogger`: actor, action, and before/after totals.
14. **No PII** in usage or billing tables: no names, PANs or MSISDNs. `client_reference` is the FI's own opaque key.

---

## 14. Alternatives considered

| Alternative | Why not |
|---|---|
| One combined "Billing" module | Metering (high-volume writes, near-real-time) and Billing (low volume, finance workflow) change and scale differently. Two modules keep extraction cheap for about the same effort. |
| A separate Reporting module / read model | Not needed yet; each module serves reads for the data it owns. Add a read replica or BI export when volume requires it. |
| Count usage directly from `qr_generations` / `qr_validations` | Couples Billing to the QR tables, puts commercial rules (billable verdicts) into SQL across modules, and breaks on extraction. |
| Metering subscribes to events **and** Billing keeps running counters | Creates two sources of truth that have to be reconciled. Pulling at invoice time is exact and simpler. |
| Recalculate invoices on demand | A price or data change would silently rewrite history. Stored lines are the legal record. |
| A mutable price table with `effective_to` edits | Editing a row rewrites history. Immutable versions plus month-aligned subscriptions are simpler and safe. |
| Tiered pricing, proration, multi-currency now | Not required yet. A tier table per plan version can be added later without migrating old invoices. |
| A message broker (RabbitMQ / Kafka) now | Adds operational cost with no benefit inside one process. The outbox and the event-type names are already broker-ready. |
| Bill late usage into the next invoice automatically | Needs cumulative-cutoff logic. The completeness gate makes late usage rare, and a manual adjustment line is explicit and auditable. |

---

## 15. Open decisions (product / finance)

- Make `Idempotency-Key` **mandatory** on `/v1/qr/generate/*`. This changes the public contract.
- The VAT rate, and whether VAT appears on the invoice (`Billing:VatRate`, default 0 until finance confirms).
- Whether a customer-facing notification channel (email) is required for v1, or log + API polling is enough.
