# SBQR Metering ও Billing — Implementation Planning Document

| | |
|---|---|
| **Status** | Draft — implementation-এর আগে review-র জন্য |
| **Target repo** | `rvl-sbqr-workspace/rvl-secure-bqr-manager/` (এই ডকুমেন্টে পথ না বুঝালে সেগুলো ওই submodule-এর ভেতরের পথ) |
| **Target** | .NET 10 modular monolith (`SBQR.Api` host), PostgreSQL 16, single `public` schema |
| **Reader** | এমন .NET developer যার আগে fintech application বানানোর অভিজ্ঞতা নেই |
| **Date** | 2026-09-29 |

---

## 0. এক নজরে (TL;DR)

দুটি নতুন bounded-context module যোগ হবে — **Metering** আর **Billing**। একটি নতুন shared infrastructure টেবিল যোগ হবে — **`integration_outbox`**।

```
QrGeneration / Verification          Metering                    Billing
(বিদ্যমান module)                     (নতুন)                      (নতুন)
┌─────────────────────┐   outbox    ┌──────────────────┐         ┌────────────────────┐
│ POST /qr/generate/* │──event────▶ │ usage_records    │         │ plans              │
│ POST /qr/validate   │  (at-least- │ (immutable, 1 row│ ◀─pull──│ plan_prices        │
│                     │   once +    │  per successful  │   calc  │ subscriptions      │
│ fact: qr_generations│   dedup)    │  business op)    │   time  │ invoices          │
│ fact: qr_validations│             └──────────────────┘         │ invoice_lines     │
└─────────────────────┘                usage reporting           └────────────────────┘
                                        (tenant)                  invoice reporting
                                                                  (tenant + admin)
```

মূল সিদ্ধান্তগুলো:

1. **Billable হওয়ার ঠিক মুহূর্ত** — যেই মুহূর্তে `qr_generations` / `qr_validations` fact row ডাটাবেসে commit হয়। এই টেবিল দুটোতে শুধুই completed operation ঢোকে, তাই "success" এর সংজ্ঞা প্রস্তুত আছে।
2. **নোটিফিকেশনের মাধ্যম** — producer module নিজের fact insert-এর **একই transaction-এ** `integration_outbox`-এ একটি event লেখে; একটি background worker সেটা publish করে; Metering consume করে। Producer কখনো Metering-কে সরাসরি call করে না।
3. **Duplicate ঠেকানো** — `usage_records`-এ `UNIQUE (source_type, source_id)` constraint; একই business operation যতবারই event re-deliver হোক, বিলে সর্বদা ১ unit।
4. **Pricing** — দাম কখনো UPDATE হয় না; নতুন দাম = `plan_prices`-এ নতুন row, নিজের `effective_from/to` উইন্ডো সহ। পুরনো invoice তাই কখনো বদলায় না।
5. **Invoice** — finalize-এর সময় line item **হিসেব করে সেভ হয়** (`invoice_lines`); পরে dynamically recalculate করা হয় না। `FINALIZED` invoice অপরিবর্তনীয় (immutable)।
6. **Reporting** — আলাদা module নেই; Metering/Billing-এর query endpoint-ই reporting। Tenant দেখবে current usage + invoice; admin দেখবে সব tenant-এর usage, plan, invoice status।

---

## 1. শব্দকোষ (এই ডকুমেন্টে যেভাবে শব্দ ব্যবহৃত হয়েছে)

| শব্দ | অর্থ |
|---|---|
| **Business operation** | একটি সফল QR generate বা QR validate — এটিই বিলের একক। Raw HTTP request নয়। |
| **Meter** | বিলযোগ্য সার্ভিসের নাম — `QR_STATIC_GENERATE`, `QR_DYNAMIC_GENERATE`, `QR_VALIDATE` |
| **Usage record** | Metering module-এর টেবিলে একটি সফল business operation-এর এক লাইন রেকর্ড (quantity = 1) |
| **Integration event** | Module-এর বাইরে অন্য module যে event consume করে (contract: producer-এর `Contracts` project-এ থাকে) |
| **Outbox** | একই DB transaction-এ লেখা হওয়া একটি "message queue টেবিল" — crash-এও event হারায় না |
| **At-least-once** | Event কমপক্ষে একবার deliver হবে; কিন্তু একাধিকবারও হতে পারে — তাই consumer-এ dedup বাধ্যতামূলক |
| **Minor unit** | টাকার ক্ষুদ্রতম একক — এখানে **poisha**। সব টাকার হিসাব `bigint` পূর্ণসংখ্যায়, কখনো `float`/`decimal`-এ নয় |
| **Billing period** | একটি পঞ্জিকা মাস, **Asia/Dhaka** timezone অনুযায়ী (`2026-09-01` থেকে `2026-09-30`) |
| **FI / Tenant** | Financial Institution — আমাদের গ্রাহক ব্যাংক/MFS; platform-এর ভেতরে `tenants` টেবিলের একটি row |

---

## 2. Module / Sub-module Boundary

### 2.1 নতুন দুটি module

দুটোই হবে সম্পূর্ণ নতুন bounded context, submodule-এর `.claude/skills/module-scaffold` skill দিয়ে scaffold হবে (sibling module-দের মতোই 5-project layout: `Domain / Application / Infrastructure / Api / Contracts`)। দুটোরই নিজস্ব persistence, HTTP surface আর cross-module contract আছে — তাই 5-project split justified (skill-এর যাচাই-প্রশ্ন ৫-এর উত্তর হ্যাঁ)।

```
rvl-secure-bqr-manager/src/Modules/Metering/
  SBQR.Modules.Metering.Domain          UsageRecord aggregate
  SBQR.Modules.Metering.Application     RecordUsage command + usage query-গুলো
  SBQR.Modules.Metering.Infrastructure  MeteringDbContext + configurations
  SBQR.Modules.Metering.Api             GET /v1/usage/*
  SBQR.Modules.Metering.Contracts       (কেউ এখনো consume করে না — ভবিষ্যতের জন্য)

rvl-secure-bqr-manager/src/Modules/Billing/
  SBQR.Modules.Billing.Domain           Plan, PlanPrice, Subscription, Invoice aggregates
  SBQR.Modules.Billing.Application      catalog/calc/finalize commands + invoice query-গুলো
  SBQR.Modules.Billing.Infrastructure   BillingDbContext + configurations
  SBQR.Modules.Billing.Api              /v1/billing/* + /v1/admin/billing/*
  SBQR.Modules.Billing.Contracts        InvoiceFinalized integration event
```

### 2.2 কে কী owns করে (এবং কে কী করতে পারবে না)

| Data / দায়িত্ব | Owner | অন্যরা যা করতে পারবে |
|---|---|---|
| `qr_generations`, `qr_validations` fact টেবিল | QrGeneration, Verification | Metering/Billing শুধু event consume করবে; টেবিল **পড়বে না**, লিখবে না |
| Billable usage record (`usage_records`) | **Metering** | শুধু Metering-ই INSERT করবে; কেউ UPDATE/DELETE করবে না |
| Plan, price version, subscription | **Billing** | Metering জানবেও না দাম কত |
| Invoice + line items | **Billing** | Finalize-এর পরে সবার জন্য read-only |
| `integration_outbox` টেবিল | SharedKernel infrastructure (host-এর worker চালায়) | Producer module-রা শুধু INSERT করে; worker-ই status বদলায় |
| Usage reporting | Metering (query) | — |
| Invoice/billing reporting | Billing (query) | — |

**Reporting আলাদা module কেন নয়:** reporting-এর দরকার শুধু read-only query + join — নতুন কোনো business rule বা consistency boundary নেই। Metering/Billing-এর `Queries/` ফোল্ডারেই endpoint বসবে। ভবিষ্যতে reporting ভারী হলে (dashboard, export) সেই সময়ে read-model আলাদা করা যাবে — এখন করলে অযথা জটিলতা।

### 2.3 ভবিষ্যতে service আলাদা করলে (extraction path)

- Metering-কে আলাদা service বানাতে: producer-রা event পাঠানোর জায়গায় শুধু outbox worker-এর "publish" ধাপটা broker-এ (RabbitMQ / Azure Service Bus) পাঠানোর কোডে বদলাবে — handler, টেবিল, endpoint কিছুই বদলাবে না।
- Billing-কে আলাদা করতে: Billing কেবল Metering-এর **query/API**-র উপর নির্ভরশীল (calculation-এর সময় usage পড়ে), কোনো টেবিলে সরাসরি নির্ভরতা নেই (নিচের §4.4 দেখুন)।
- সব module একই DB-তে আছে বলে এখন একটাই deployment — কিন্তু কোনো module অন্যের টেবিলে SQL করে না, তাই ভবিষ্যতে DB split-ও সম্ভব।

---

## 3. Architecture: Transactional + Outbox — Combination, এবং কেন

### 3.1 যে প্যাটার্নের সম্মিলন আমরা বেছেছি

| জায়গা | Approach | কেন |
|---|---|---|
| এক module-এর ভেতরের একাধিক write (যেমন fact + outbox row) | **Fully transactional** (একই DB transaction) | Outbox pattern-এর মূল শর্তই এটা: fact আর event একসাথে commit হবে, নাহলে event হারানো/মিথ্যা হওয়ার ঝুঁকি |
| Module → Module communication | **Transactional Outbox + in-process dispatcher** | QR generate-এর hot path-এ billing-এর কোনো latency/failure ঢুকতে পারবে না; তবু কোনো event হারাবে না |
| Invoice calculation (usage পড়ে invoice লেখে) | **Fully transactional** | এক request-এর ভেতরে পড়া-লেখা; এখানে eventual consistency-র দরকার নেই |

### 3.2 কেন শুধু fully synchronous নয়

সবচেয়ে সহজ বিকল্প হতো: `QrIssuancePipeline`-এ fact insert-এর পাশে সরাসরি Metering-এর method call, একই transaction-এ usage record insert। বাদ দেওয়ার কারণ:

1. **Coupling** — QrGeneration module-এর ভেতরে Metering-এর dependency ঢুকে যায়; ভবিষ্যতে Metering আলাদা service হলে hot path ভেঙে পড়বে।
2. **Failure isolation** — Metering-এর DB timeout হলে গ্রাহকের **সফল QR generation-ও fail** হয়ে যাবে। এটা fintech-এ অগ্রহণযোগ্য: সার্ভিস দিয়ে দিয়ে বিলিং গোলমালের কারণে টাই করা চলে।
3. **Hot path latency** — প্রতিটি generate-এ দুটো aggregate write বাড়ানোর দরকার নেই।

### 3.3 কেন outbox ছাড়া শুধু in-process MediatR publish নয়

Repo-তে এখন `AggregateRoot.RaiseDomainEvent` আছে কিন্তু commit-এর পরে event dispatch করার কোনো infrastructure নেই (audit কোডে নিজেই লেখা: *"epic-9 stories revisit this with an outbox"*। যদি `SaveChangesAsync()`-এর **পরে** MediatR দিয়ে publish করি:

- publish-এর আগে process crash → fact commit হয়ে গেছে কিন্তু usage event **চিরতরে হারাল** → আয় হারানো, আর জানারও উপায় নেই।
- handler-এ সাময়িক error (DB timeout) হলে retry-র কোনো নিশ্চয়তা নেই।

Outbox এই দুটো গর্তই বন্ধ করে: event টেবিলে সেভ হয় (crash-safe), worker বারবার চেষ্টা করে (retry-safe), consumer dedup করে (duplicate-safe)।

### 3.4 Delivery guarantee: at-least-once

আমরা **exactly-once চাইব না** — সেটা অত্যন্ত দামী এবং distributed system-এ ব্যবহারিকভাবে অবিশ্বস্ত। বরং:

```
at-least-once delivery  +  consumer-side UNIQUE constraint dedup  =  effectively-once billing
```

Worker crash-এর মাঝখানে event দুবার deliver হলেও `usage_records`-এর `UNIQUE (source_type, source_id)` দ্বিতীয় insert-কে পথ দেবে না — ডেভেলপারের কিছু করার নেই, DB-ই নিশ্চয়তা দেয়।

---

## 4. Database Schema

মিলিয়ে রাখুন — এই repo-তে SQL migration-ই source of truth (EF model তার আয়না), runtime DB user-এর DDL অধিকার **নেই**, আর `SchemaModelDriftTests` SQL ↔ EF মিল ভেঙে গেলে test fail করে। নতুন ৩টা migration file (submodule-এর `rvl-secure-bqr-manager/db/migrations/`-এ, পরের খালি নম্বর 010 থেকে):

```
rvl-secure-bqr-manager/db/migrations/010_integration_outbox.sql
rvl-secure-bqr-manager/db/migrations/011_metering.sql
rvl-secure-bqr-manager/db/migrations/012_billing.sql
```

সব file idempotent, `BEGIN; ... COMMIT;` — repo convention অনুযায়ী।

### 4.1 `010_integration_outbox.sql` — shared event outbox

```sql
BEGIN;

-- Shared integration-event outbox. Producer modules INSERT (in the same
-- transaction as their fact row); the host's OutboxDispatcherWorker owns
-- all status transitions. Extraction path: the dispatcher's publish step
-- is the single seam to swap for a real broker later.
CREATE TABLE IF NOT EXISTS public.integration_outbox (
    message_id     uuid         NOT NULL,
    event_type     varchar(100) NOT NULL,   -- e.g. 'QrGenerated', 'QrValidationCompleted'
    payload        jsonb        NOT NULL,   -- serialized integration event
    occurred_at    timestamptz  NOT NULL DEFAULT now(),
    status         varchar(10)  NOT NULL DEFAULT 'PENDING',
    attempts       integer      NOT NULL DEFAULT 0,
    available_at   timestamptz  NOT NULL DEFAULT now(),  -- exponential-backoff gate
    last_error     text,
    dispatched_at  timestamptz,
    CONSTRAINT pk_integration_outbox    PRIMARY KEY (message_id),
    CONSTRAINT ck_integration_outbox_status
        CHECK (status IN ('PENDING', 'DISPATCHED', 'DEAD'))
);

-- Worker poll: oldest-first claim, only pending rows.
CREATE INDEX IF NOT EXISTS ix_integration_outbox_pending
    ON public.integration_outbox (available_at)
    WHERE status = 'PENDING';

-- Ops queries: "show me everything stuck / dead".
CREATE INDEX IF NOT EXISTS ix_integration_outbox_status
    ON public.integration_outbox (status);

COMMIT;
```

**কেন `SKIP LOCKED` লাগবে:** worker একাধিক instance-এ চালালে (production-এ API scale-out) একই row দুজন তুলতে পারে — `SELECT ... FOR UPDATE SKIP LOCKED` দিয়ে প্রতিটা row একবারই একজন claim করে।

### 4.2 `011_metering.sql` — billable usage facts

```sql
BEGIN;

-- One row per SUCCESSFUL billable business operation. APPEND-ONLY:
-- no modified_*/is_active columns on purpose (same philosophy as
-- qr_generations: a usage fact is an immutable financial event).
CREATE TABLE IF NOT EXISTS public.usage_records (
    usage_id       uuid         NOT NULL,
    tenant_id      uuid         NOT NULL,
    meter          varchar(30)  NOT NULL,
    quantity       integer      NOT NULL DEFAULT 1,   -- always 1 today; headroom for bulk
    source_type    varchar(30)  NOT NULL,             -- 'QR_GENERATION' | 'QR_VALIDATION'
    source_id      uuid         NOT NULL,             -- qr_generation_id / qr_validation_id
    event_id       uuid         NOT NULL,             -- outbox message that produced this row (trace)
    occurred_at    timestamptz  NOT NULL,             -- fact time (UTC)
    occurred_date  date         NOT NULL,             -- Asia/Dhaka date of occurred_at (period math)
    created_at     timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT pk_usage_records PRIMARY KEY (usage_id),
    CONSTRAINT fk_usage_records_tenant
        FOREIGN KEY (tenant_id) REFERENCES public.tenants (tenant_id) ON DELETE RESTRICT,
    CONSTRAINT ck_usage_records_meter
        CHECK (meter IN ('QR_STATIC_GENERATE', 'QR_DYNAMIC_GENERATE', 'QR_VALIDATE')),
    CONSTRAINT ck_usage_records_source_type
        CHECK (source_type IN ('QR_GENERATION', 'QR_VALIDATION')),
    CONSTRAINT ck_usage_records_quantity CHECK (quantity > 0)
);

-- THE dedup: one usage row per business operation, ever. Duplicate event
-- delivery / replayed retry raises 23505 which the handler swallows as success.
CREATE UNIQUE INDEX IF NOT EXISTS uq_usage_records_source
    ON public.usage_records (source_type, source_id);

-- Tenant "current usage" + invoice calculation (index-only scan).
CREATE INDEX IF NOT EXISTS ix_usage_records_tenant_meter_date
    ON public.usage_records (tenant_id, meter, occurred_date);

-- Tenant usage-over-time listing.
CREATE INDEX IF NOT EXISTS ix_usage_records_tenant_date
    ON public.usage_records (tenant_id, occurred_date DESC);

-- Admin cross-tenant per-meter reports.
CREATE INDEX IF NOT EXISTS ix_usage_records_meter_date
    ON public.usage_records (meter, occurred_date);

-- Traceability: from a usage row back to the raw fact.
CREATE INDEX IF NOT EXISTS ix_usage_records_source
    ON public.usage_records (source_type, source_id);

COMMIT;
```

**`occurred_date` আলাদা কলাম কেন:** billing period হলো Dhaka-র পঞ্জিকা মাস, কিন্তু `occurred_at` UTC timestamp। Query-তে `((occurred_at AT TIME ZONE 'Asia/Dhaka')::date)` লিখলে index ব্যবহৃত হয় না। তাই insert-এর সময় অ্যাপ একবার Dhaka-date হিসেব করে `occurred_date`-এ রাখে — সব period query তখন plain date comparison, index-friendly।

### 4.3 `012_billing.sql` — plan, price, subscription, invoice

```sql
BEGIN;

CREATE SEQUENCE IF NOT EXISTS public.seq_invoice_number;

-- Commercial plan (e.g. "SBQR Standard 2026").
CREATE TABLE IF NOT EXISTS public.plans (
    plan_id     uuid         NOT NULL,
    code        varchar(50)  NOT NULL,
    name        varchar(200) NOT NULL,
    status      varchar(20)  NOT NULL DEFAULT 'ACTIVE',
    created_by  varchar(200),
    created_at  timestamptz  NOT NULL DEFAULT now(),
    modified_by varchar(200),
    modified_at timestamptz,
    CONSTRAINT pk_plans PRIMARY KEY (plan_id),
    CONSTRAINT uq_plans_code UNIQUE (code),
    CONSTRAINT ck_plans_status CHECK (status IN ('ACTIVE', 'RETIRED'))
);

-- Price VERSIONS. Never UPDATE unit_amount_minor on an existing row that
-- any invoice_line references (FK RESTRICT below enforces it): a price
-- change is always a NEW row with its own effective window.
CREATE TABLE IF NOT EXISTS public.plan_prices (
    price_id          uuid        NOT NULL,
    plan_id           uuid        NOT NULL,
    meter             varchar(30) NOT NULL,
    unit_amount_minor bigint      NOT NULL,           -- poisha; integer money, never float
    currency          char(3)     NOT NULL DEFAULT 'BDT',
    effective_from    date        NOT NULL,
    effective_to      date,                            -- NULL = open-ended (current price)
    created_by        varchar(200),
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_plan_prices PRIMARY KEY (price_id),
    CONSTRAINT fk_plan_prices_plan
        FOREIGN KEY (plan_id) REFERENCES public.plans (plan_id) ON DELETE RESTRICT,
    CONSTRAINT ck_plan_prices_amount CHECK (unit_amount_minor >= 0),
    CONSTRAINT ck_plan_prices_window
        CHECK (effective_to IS NULL OR effective_to > effective_from),
    CONSTRAINT ck_plan_prices_meter
        CHECK (meter IN ('QR_STATIC_GENERATE', 'QR_DYNAMIC_GENERATE', 'QR_VALIDATE')),
    CONSTRAINT uq_plan_prices_plan_meter_from UNIQUE (plan_id, meter, effective_from)
);

-- One ACTIVE subscription per tenant; history preserved (ENDED rows stay).
CREATE TABLE IF NOT EXISTS public.subscriptions (
    subscription_id        uuid         NOT NULL,
    tenant_id              uuid         NOT NULL,
    plan_id                uuid         NOT NULL,
    billing_contact_email  varchar(320),
    valid_from             date         NOT NULL,
    valid_to               date,
    status                 varchar(20)  NOT NULL DEFAULT 'ACTIVE',
    created_by             varchar(200),
    created_at             timestamptz  NOT NULL DEFAULT now(),
    modified_by            varchar(200),
    modified_at            timestamptz,
    CONSTRAINT pk_subscriptions PRIMARY KEY (subscription_id),
    CONSTRAINT fk_subscriptions_tenant
        FOREIGN KEY (tenant_id) REFERENCES public.tenants (tenant_id) ON DELETE RESTRICT,
    CONSTRAINT fk_subscriptions_plan
        FOREIGN KEY (plan_id) REFERENCES public.plans (plan_id) ON DELETE RESTRICT,
    CONSTRAINT ck_subscriptions_status
        CHECK (status IN ('ACTIVE', 'SUSPENDED', 'ENDED'))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_subscriptions_one_active
    ON public.subscriptions (tenant_id)
    WHERE status = 'ACTIVE';

CREATE INDEX IF NOT EXISTS ix_subscriptions_tenant
    ON public.subscriptions (tenant_id);

-- Invoice header. DRAFT is recalculable; FINALIZED is an immutable
-- financial document (no UPDATE path exists in code after finalize).
CREATE TABLE IF NOT EXISTS public.invoices (
    invoice_id      uuid        NOT NULL,
    tenant_id       uuid        NOT NULL,
    subscription_id uuid        NOT NULL,
    period_from     date        NOT NULL,
    period_to       date        NOT NULL,
    status          varchar(20) NOT NULL DEFAULT 'DRAFT',
    currency        char(3)     NOT NULL DEFAULT 'BDT',
    total_minor     bigint      NOT NULL DEFAULT 0,
    invoice_number  varchar(30),
    calculated_at   timestamptz,
    finalized_at    timestamptz,
    finalized_by    varchar(200),
    created_by      varchar(200),
    created_at      timestamptz NOT NULL DEFAULT now(),
    modified_by     varchar(200),
    modified_at     timestamptz,
    CONSTRAINT pk_invoices PRIMARY KEY (invoice_id),
    CONSTRAINT fk_invoices_tenant
        FOREIGN KEY (tenant_id) REFERENCES public.tenants (tenant_id) ON DELETE RESTRICT,
    CONSTRAINT fk_invoices_subscription
        FOREIGN KEY (subscription_id) REFERENCES public.subscriptions (subscription_id) ON DELETE RESTRICT,
    CONSTRAINT ck_invoices_status CHECK (status IN ('DRAFT', 'FINALIZED', 'VOID')),
    CONSTRAINT ck_invoices_period CHECK (period_to >= period_from),
    CONSTRAINT ck_invoices_total  CHECK (total_minor >= 0)
);

-- One (non-void) invoice per tenant per period — duplicate calculation
-- races close here as 23505 -> HTTP 409.
CREATE UNIQUE INDEX IF NOT EXISTS uq_invoices_tenant_period
    ON public.invoices (tenant_id, period_from, period_to)
    WHERE status <> 'VOID';

CREATE INDEX IF NOT EXISTS ix_invoices_tenant_created
    ON public.invoices (tenant_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_invoices_status
    ON public.invoices (status);

-- Stored line items: the invoice is a SNAPSHOT, never recalculated.
CREATE TABLE IF NOT EXISTS public.invoice_lines (
    invoice_line_id   uuid        NOT NULL,
    invoice_id        uuid        NOT NULL,
    meter             varchar(30) NOT NULL,
    quantity          bigint      NOT NULL,
    unit_amount_minor bigint      NOT NULL,
    amount_minor      bigint      NOT NULL,
    price_id          uuid        NOT NULL,
    CONSTRAINT pk_invoice_lines PRIMARY KEY (invoice_line_id),
    CONSTRAINT fk_invoice_lines_invoice
        FOREIGN KEY (invoice_id) REFERENCES public.invoices (invoice_id) ON DELETE CASCADE,
    CONSTRAINT fk_invoice_lines_price
        FOREIGN KEY (price_id) REFERENCES public.plan_prices (price_id) ON DELETE RESTRICT,
    CONSTRAINT ck_invoice_lines_quantity CHECK (quantity >= 0),
    CONSTRAINT ck_invoice_lines_amount   CHECK (amount_minor >= 0),
    -- DB-level arithmetic invariant: line amount is exactly qty x unit price.
    CONSTRAINT ck_invoice_lines_math
        CHECK (amount_minor = quantity * unit_amount_minor),
    CONSTRAINT ck_invoice_lines_meter
        CHECK (meter IN ('QR_STATIC_GENERATE', 'QR_DYNAMIC_GENERATE', 'QR_VALIDATE')),
    CONSTRAINT uq_invoice_lines_invoice_meter UNIQUE (invoice_id, meter, price_id)
);

CREATE INDEX IF NOT EXISTS ix_invoice_lines_invoice
    ON public.invoice_lines (invoice_id);

COMMIT;
```

### 4.4 Relationships ও module boundary — একটা সূক্ষ্ম পয়েন্ট

- সব hard FK যায় `tenants` (repo-র প্রচলিত প্যাটার্ন: *"every hard FK points to `tenants`, all `ON DELETE RESTRICT`"*) অথবা নিজের module-এর ভেতরের টেবিলে।
- **`invoice_lines.price_id → plan_prices` FK আছে, কিন্তু Billing → Metering কোনো FK নেই** — Billing calculation চলাকালীন Metering-এর টেবিল শুধু query API দিয়ে পড়ে। `usage_records` কিন্তু `qr_generations`-এর দিকেও FK রাখে না — Metering `source_id` রাখে দুর্বল reference হিসেবে (repo-তে `signature_key_version`-এর মতোই "weak reference by design — no FK, module boundary")। এটাই ভবিষ্যতের DB-split-কে সহজ করে।

### 4.5 Index summary — কোন query কোন index খায়

| Query | Index |
|---|---|
| Tenant: চলতি মাসে per-meter count | `ix_usage_records_tenant_meter_date` (index-only scan) |
| Tenant: usage history / pagination | `ix_usage_records_tenant_date` |
| Admin: সব tenant-এ per-meter monthly | `ix_usage_records_meter_date` |
| Invoice calculation: period-এর usage group-by | `ix_usage_records_tenant_meter_date` |
| Outbox worker poll | `ix_integration_outbox_pending` (partial) |
| Invoice list by status (admin) | `ix_invoices_status` |
| এক usage row → fact trace | `ix_usage_records_source` |

---

## 5. Commands / Queries (Application layer)

নামকরণ repo convention অনুযায়ী — `Commands/{UseCase}/` ফোল্ডারে command + handler + FluentValidation validator, `Queries/`-তে query। সব handler `Result<T>` return করবে।

### 5.1 Metering

| Type | Name | Trigger | কাজ |
|---|---|---|---|
| Command | `RecordUsage` | integration event handler (internal) | dedup সহ `usage_records` INSERT (নিচে §10) |
| Query | `GetCurrentUsage` | `GET /v1/usage/current` | চলতি Dhaka মাসের per-meter quantity |
| Query | `GetUsageForPeriod` | `GET /v1/usage?from&to` | tenant-scoped period usage (invoice reconciliation-তেও ব্যবহৃত) |

### 5.2 Billing

| Type | Name | Trigger | কাজ |
|---|---|---|---|
| Command | `CreatePlan` | admin POST | নতুন plan |
| Command | `AddPlanPrice` | admin POST | **নতুন price version** row (overlap check সহ, §11.2) |
| Command | `CreateSubscription` | admin POST | tenant-কে plan-এ রাখা |
| Command | `CalculateInvoice` | admin POST (পরে scheduler) | period-এর usage থেকে DRAFT invoice + lines বানানো/রিফ্রেশ |
| Command | `FinalizeInvoice` | admin POST | DRAFT → FINALIZED (নম্বর, timestamp, event) |
| Command | `VoidInvoice` | admin POST | শুধু DRAFT void করা |
| Query | `GetInvoices` / `GetInvoice` | tenant + admin GET | list / header+lines |
| Query | `GetPlans` / `GetPlanPrices` / `GetSubscriptions` | admin GET | catalog |

### 5.3 QrGeneration / Verification-এ যা বদলাবে

নতুন command নেই — শুধু `QrIssuancePipeline` (generate) এবং `ValidateQrCommandHandler` (validate)-এ fact insert-এর **ঠিক পাশে** outbox row insert যোগ হবে (§7.1)। এটাই এই দুই module-এর একমাত্র পরিবর্তন।

---

## 6. Events — domain, integration, notification

### 6.1 Integration events (producer-এর `Contracts` project-এ থাকবে)

```csharp
// SBQR.Modules.QrGeneration.Contracts / IntegrationEvents / QrGenerated.cs
public sealed record QrGenerated(
    Guid   QrGenerationId,
    Guid   TenantId,
    string QrType,          // "STATIC" | "DYNAMIC"
    DateTimeOffset OccurredAt);

// SBQR.Modules.Verification.Contracts / IntegrationEvents / QrValidationCompleted.cs
public sealed record QrValidationCompleted(
    Guid   QrValidationId,
    Guid   TenantId,
    string Verdict,         // qr_validations.verdict vocabulary
    DateTimeOffset OccurredAt);

// SBQR.Modules.Billing.Contracts / IntegrationEvents / InvoiceFinalized.cs
public sealed record InvoiceFinalized(
    Guid   InvoiceId,
    string InvoiceNumber,
    Guid   TenantId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    long   TotalMinor,
    string Currency);
```

এগুলো MediatR `INotification` implement করবে (in-process dispatch-এর জন্য), আর outbox payload-তে JSON হয়ে থাকবে।

### 6.2 Domain events

নতুন module-গুলোর aggregate-রা (repo-র `AggregateRoot<T>` প্যাটার্ন মেনে) domain event raise করবে, যেগুলো এখন প্রয়োজনীয় সেগুলো অল্প:

- `InvoiceCalculated` (Billing domain) — DRAFT তৈরি/রিফ্রেশ হলে (audit log-এর জন্য)।
- `InvoiceFinalized` (Billing domain) → এটিই outbox-এর মাধ্যমে `InvoiceFinalized` integration event হয়ে বাইরে যায়।
- `UsageRecorded` (Metering domain) — আভ্যন্তরীণ; এখন কোনো subscriber নেই, তাই **এটা আত্মসংযমে বাদ** — দরকার পড়লে যোগ হবে। (অপ্রয়োজনীয় event বানানো এই repo-র minimalism-এর পরিপন্থী।)

### 6.3 Event handlers / listeners

| Handler | Module | Subscribe করে | কাজ |
|---|---|---|---|
| `QrGeneratedHandler` | Metering.Application | `QrGenerated` | `RecordUsage` (meter = `QR_STATIC_GENERATE` / `QR_DYNAMIC_GENERATE`) |
| `QrValidationCompletedHandler` | Metering.Application | `QrValidationCompleted` | verdict billable হলে `RecordUsage` (meter = `QR_VALIDATE`) |
| `InvoiceFinalizedHandler` | Billing.Application | `InvoiceFinalized` | `IInvoiceNotificationSender` call (নিচে) |
| `OutboxDeadLetterMonitor` | Host | — (worker-এর লুপে) | DEAD হলে critical log + metric — ops-কে দেখার পথ |

### 6.4 Notifications

Minimal রাখতে আলাদা Notifications module **হবে না**। দুটি notification touchpoint:

1. **Invoice ready (tenant-facing)** — `InvoiceFinalizedHandler` → `IInvoiceNotificationSender` port (Billing.Application) → dev-এ `LoggedInvoiceNotificationSender` (log + audit), production-এ SMTP adapter। ঠিকানা: `subscriptions.billing_contact_email`।
2. **Outbox poison (ops-facing)** — worker-এর DEAD detection → critical structured log + metric; দেখা হবে §9-এর admin outbox endpoint দিয়ে।

---

## 7. Outbox Design ও Worker

### 7.1 Producer-এর লেখা (একই transaction — এটাই প্যাটার্নের প্রাণ)

`QrIssuancePipeline`-এ (generate) এবং `ValidateQrCommandHandler`-এ (validate) বর্তমান code হলো:

```csharp
_db.Generations.Add(new QrGeneration { ... });
await _db.SaveChangesAsync(ct);
```

নতুন হবে:

```csharp
var aggregate = new QrGeneration { ... };
_db.Generations.Add(aggregate);

_db.IntegrationOutbox.Add(IntegrationOutboxEnvelope.From(new QrGenerated(
    QrGenerationId: aggregate.Id,
    TenantId: tenantId,
    QrType: qrType,
    OccurredAt: aggregate.CreatedAt)));

await _db.SaveChangesAsync(ct);   // fact + outbox: একই transaction, একসাথে commit
```

`IntegrationOutboxEnvelope` (SharedKernel.Persistence-এর ছোট entity) message_id পাবে `Guid.NewGuid()`, event_type পাবে contract-এর ধরন থেকে। প্রতিটি producer module-এর DbContext-এ এই একটি shared table map করা থাকবে (tenants-FK-এর মতোই একটি সচেতন shared-infrastructure ব্যবহার)।

> সচেতন সিদ্ধান্ত: aggregate-এ `RaiseDomainEvent` + স্বয়ংক্রিয় interceptor দিয়ে outbox লেখার বদলে **explicit দুই লাইনের লেখা** বেছে নেওয়া হয়েছে। কারণ: repo-তে এখন কোনো event-dispatch interceptor নেই; hot path-এ স্বচ্ছ-অদৃশ্য যাদু যোগ করার চেয়ে দৃশ্যমান, transactional, পরীক্ষাযোগ্য লেখা এখন সস্তা। consumer বেড়ে গেলে interceptor-এ উন্নয়নের পথ খোলা থাকে।

### 7.2 `OutboxDispatcherWorker` (Host-এ নতুন `BackgroundService`)

Repo-তে ইতিমধ্যে দুটি BackgroundService প্রচলিত (`DailyTrustSyncService`, `TenantCertificateThumbprintSyncService`) — একই ধাঁচে `rvl-secure-bqr-manager/src/Host/SBQR.Api/`-তে (বা SharedKernel থেকে register):

```
loop (প্রতি ২ সেকেণ্ড, configurable):
  1. tx begin
  2. SELECT * FROM integration_outbox
     WHERE status='PENDING' AND available_at <= now()
     ORDER BY occurred_at LIMIT 100
     FOR UPDATE SKIP LOCKED
  3. প্রতিটি row-এর জন্য:
       payload JSON → typed IIntegrationEvent (event_type → CLR type map)
       await mediator.Publish(event)
       → সফল: status='DISPATCHED', dispatched_at=now()
       → exception: attempts++, last_error=e.Message,
                    available_at = now() + min(2^attempts, 300) sec   // backoff
                    attempts >= 8 হলে status='DEAD' + critical log
  4. tx commit
```

দুটি সূক্ষ্ম বিষয়:

- **Mark-after-publish** — "publish সফল, status বদলানোর আগে crash" হলে event আবার deliver হবে। এটা বাগ নয়, at-least-once-এর চুক্তি; consumer dedup সামলায়।
- **Ordering নেই** — ইচ্ছাকৃত। Usage যোগাত্মক (additive), invoice চূড়ান্ত হয় period শেষে; কোনো business rule পুরোনো-আগে ক্রম চায় না। এ কারণেই sequence-number-এর জটিলতা এড়ানো গেছে।

### 7.3 Outbox-এর admin surface

- `GET /v1/admin/outbox?status=&page=` — stuck/DEAD দেখা।
- `POST /v1/admin/outbox/{messageId}/requeue` — DEAD কে আবার `PENDING`, `attempts=0` (নিজে নয়, `IAuditLogger`-এ অডিট সহ)।

---

## 8. API Endpoints

Routing convention: `v{version:apiVersion}/...`, tenant-surface আর admin-surface আলাদা (`TenantsController`-এর মতো `admin/` prefix)। Tenant endpoint-এ tenant identity আসবে **platform token-এর client_id** থেকে (বিদ্যমান mTLS + client_credentials middleware) — route-এ tenantId থাকবে না, ফলে অন্য tenant-এর ডেটা চাওয়ার পথই বন্ধ। Admin endpoint ব্যবহার করবে বিদ্যমান admin authentication policy।

### 8.1 Tenant surface

| Method + Path | Module | Response |
|---|---|---|
| `GET /v1/usage/current` | Metering | `{ period: {from,to}, meters: [{meter, quantity}] }` (task-এর উদাহরণের গড়ন) |
| `GET /v1/usage?from=2026-09-01&to=2026-09-30` | Metering | period usage — invoice-এর সাথে মেলানোর জন্য |
| `GET /v1/billing/invoices` | Billing | status সহ list |
| `GET /v1/billing/invoices/{id}` | Billing | header + `lines[]` (meter, quantity, unit price, amount) + period |

### 8.2 Admin surface

| Method + Path | Module | কাজ |
|---|---|---|
| `POST /v1/admin/billing/plans` | Billing | plan তৈরি |
| `GET  /v1/admin/billing/plans` | Billing | plan list (price versions সহ) |
| `POST /v1/admin/billing/plans/{planId}/prices` | Billing | নতুন price version (আগেরটার `effective_to` সেট হয়) |
| `POST /v1/admin/billing/tenants/{tenantId}/subscriptions` | Billing | tenant-কে plan-এ subscribe |
| `GET  /v1/admin/billing/tenants/{tenantId}/subscriptions` | Billing | subscription ইতিহাস |
| `POST /v1/admin/billing/tenants/{tenantId}/invoices/calculate?period=2026-09` | Billing | DRAFT invoice বানানো/রিফ্রেশ |
| `POST /v1/admin/billing/invoices/{invoiceId}/finalize` | Billing | DRAFT → FINALIZED |
| `POST /v1/admin/billing/invoices/{invoiceId}/void` | Billing | শুধু DRAFT |
| `GET  /v1/admin/billing/invoices?status=&tenantId=&page=` | Billing | সব tenant-এর invoice |
| `GET  /v1/admin/usage/summary?from&to` | Metering | tenant × meter ম্যাট্রিক্স |
| `GET  /v1/admin/outbox?status=` | Host/Ops | outbox পরিদর্শন |
| `POST /v1/admin/outbox/{messageId}/requeue` | Host/Ops | DEAD requeue (অডিটেড) |

**বাদ দেওয়া হলো** task-এর প্রস্তাবিত `POST /admin/billing/{tenantId}/calculate`-এর raw-body version — `?period=YYYY-MM` রূপটা সরল, validate করা সহজ, আর period boundary (মাসের শেষ তারিখ) নিয়ে ভুলের পথ বন্ধ করে।

---

## 9. Metering Flow (end-to-end)

```
FI backend          SBQR.Api                     Postgres                    Worker              Metering
    │  POST /qr/generate/dynamic    │                             │                          │                    │
    │──────────────────────────────▶│ validate + execute business │                          │                    │
    │                               │ (sign, build payload...)    │                          │                    │
    │                               │ BEGIN                       │                          │                    │
    │                               │  INSERT qr_generations      │                          │                    │
    │                               │  INSERT integration_outbox  │  ← একই transaction       │                    │
    │                               │ COMMIT                      │                          │                    │
    │◀── 200 QR payload ────────────│                             │                          │                    │
    │                               │                             │   poll (≤2s later)       │                    │
    │                               │                             │◀─────────────────────────│                    │
    │                               │                             │  PENDING row claim       │                    │
    │                               │                             │  (FOR UPDATE SKIP LOCKED)│                    │
    │                               │                             │─────────────────────────▶│ Publish(QrGenerated)│
    │                               │                             │                          │──────────────────▶ │ RecordUsage
    │                               │                             │                          │                    │  INSERT usage_records
    │                               │                             │                          │                    │  (UNIQUE dedup)
    │                               │                             │  status=DISPATCHED       │◀────────────────── │ 23505 → no-op ✓
    │                               │                             │◀─────────────────────────│                    │
```

**Validation-এর বিশেষত্ব — কোন verdict বিলযোগ্য:** `qr_validations`-এ প্রতিটি attempt-এর row যায় (verdict সহ)। সার্ভিস *delivery* হয়েছে কি না সেটাই বিলের মানদণ্ড:

| Verdict | Billable? | যুক্তি |
|---|---|---|
| `VALID`, `INVALID_SIGNATURE`, `STRUCTURAL_INVALID`, `KEY_*`, `NON_P2P` | **হ্যাঁ** | অনুরোধ well-formed ছিল, verification সম্পূর্ণ হয়ে নির্দিষ্ট উত্তর দেওয়া হয়েছে — সার্ভিস কনজিউম হয়েছে |
| `REQUEST_STALE`, `REQUEST_REPLAYED` | **না** | অনুরোধ সার্ভিসে পৌঁছানোর আগেই নীতি-প্রহারে প্রত্যাখ্যাত (replay guard / freshness) — delivery হয়নি |
| HTTP 4xx/5xx (row insert-ই হয়নি) | না | event-ই নেই |

এই verdict-সেট Metering.Application-এ একটি স্পষ্ট constant (`BillableVerdicts`) হিসেবে থাকবে — বাণিজ্যিক দল ভবিষ্যতে সেট বদলালে এক জায়গায় বদলাবে।

---

## 10. Billing ও Invoice Flow

### 10.1 মাসের চক্র (চিত্রে সেপ্টেম্বর ২০২৬)

```
সেপ্টেম্বর জুড়ে:   usage_records জমা হতে থাকে (মাসে লক্ষ লক্ষ row, স্বাভাবিক)
অক্টোবর ১-২:       admin: POST .../invoices/calculate?period=2026-09   → DRAFT invoice
অক্টোবর ২-৫:       finance verify করে (DRAFT যতবার চান recalculate হয়)
                   admin: POST .../invoices/{id}/finalize               → FINALIZED ✅
                   → InvoiceFinalized event → FI-র billing contact-এ email
পরে:               invoice + lines চিরকাল read-only; সংশোধন মানেই নতুন নথি (credit note, ভবিষ্যৎ)
```

### 10.2 CalculateInvoice — ধাপে ধাপে (handler-এর আসল algorithm)

```
ইনপুট: tenantId, period = 2026-09 → (from=2026-09-01, to=2026-09-30)

1. Safety gate: outbox-এ কোনো PENDING row আছে কি না দেখো যার occurred_at <= to-এর শেষ সময়।
   থাকলে → ব্যর্থ, স্পষ্ট error: "outbox backlog — পরে আবার চেষ্টা করুন"।
   (সামনে-পেছনে দেরিতে আসা usage যেন চুপচাপ বাদ না পড়ে।)

2. ACTIVE subscription খোঁজো যেটা পুরো period জুড়ে বলবত:
   valid_from <= from AND (valid_to IS NULL OR valid_to >= to)।
   না পেলে → ব্যর্থ: "এই period-এর subscription নেই"।

3. usage aggregate (Metering query API দিয়ে):
   SELECT meter, occurred_date, count(*) ... group by (meter, occurred_date)

4. দাম মেলানো — মাসের মাঝে দাম বদলালেও সঠিক রাখতে, প্রতিটি meter-এর জন্য
   ওই plan-এর price version-গুলো নাও যারা [from, to]-এর সাথে overlap করে।
   প্রতিটি (meter, price) জোড়ার জন্য:
       quantity = period-জুড়ে সেই price-এর কার্যকর উইন্ডোতে পড়া usage-এর count
       line     = (meter, quantity, unit_amount_minor, quantity * unit_amount_minor, price_id)

   ⚠ কোনো meter-এর usage আছে কিন্তু কভার করা price version নেই →
      হিসাব থামাও, স্পষ্ট error দাও ("2026-09-15-এর QR_DYNAMIC_GENERATE-এর দাম নেই")।
      কখনোই চুপচাপ 0 দামে line বানানো যাবে না।

5. এক transaction-এ:
   - আগের DRAFT থাকলে তার lines মুছে নতুন জোড়া (DRAFT recalculation)
   - invoice upsert (total = Σ lines.amount_minor), calculated_at = now()

   Race: দুজন admin একসাথে calculate করলে uq_invoices_tenant_period 23505 → 409 Conflict।
```

### 10.3 FinalizeInvoice

```
1. status অবশ্যই DRAFT।
2. Outbox safety gate আবার (calculate-এর পরেও দেরিতে event এসে থাকতে পারে)।
   নতুন usage এসে থাকলে → আগে recalculate করতে বাধ্য করো।
3. invoice_number = 'SBQR-' + yyyyMM + '-' + nextval(seq_invoice_number) (৮-ঘর প্যাডেড)
   (sequence ব্যবহারের কারণে rollback-এ ফাঁক হতে পারে — নথির শৃঙ্খলার জন্য গ্রহণযোগ্য)
4. status = FINALIZED, finalized_at/by সেট — এক transaction-এ,
   সাথে outbox row: InvoiceFinalized integration event।
5. Event → email notification (FI billing contact) + audit log।
```

### 10.4 Finalize-এর পরে কী immutable

| Record | নিয়ম |
|---|---|
| `invoices` (FINALIZED) | status/timestamp বাদে কোনো column-এর UPDATE path কোডেই নেই; VOID শুধু DRAFT-এ সম্ভব |
| `invoice_lines` | কোনো UPDATE/DELETE path নেই; `price_id` FK RESTRICT-এর কারণে রেফারেন্সকৃত দাম-ও অপরিবর্তনীয় থাকে |
| `plan_prices` (invoice-এ ব্যবহৃত) | অপরিবর্তনীয় (উপরের FK-ই জামিন) |
| `usage_records` | আগে থেকেই immutable (append-only) — usage অনুমত সংশোধনের পথই নেই |

**সংশোধনের বাস্তব পথ** (যদি কখনো দরকার হয়): credit note — নতুন নথি যা পুরনো invoice-এর রেফারেন্স সহ ঋণাত্মক মান ধরে। v1 scope-এর বাইরে; স্কিমা এটাকে আটকায় না (একই `invoices` টেবিলে নতুন ধরনের row হিসেবে যোগ করা যাবে)।

### 10.5 Reconciliation — tenant রিপোর্ট আর invoice কীভাবে মেলে

```
GET /v1/usage?from=2026-09-01&to=2026-09-30        ← Metering থেকে
    meters: [ QR_DYNAMIC_GENERATE: 324521, ... ]

GET /v1/billing/invoices/{id}                       ← Billing থেকে
    lines: [ QR_DYNAMIC_GENERATE: qty 324521 @ 2500 ... ]

মিল: প্রতিটি meter-এ দুই সংখ্যা সমান হতে হবে।
```

দুটো সংখ্যা আলাদা উৎস থেকে আসা সত্ত্বেও মেলে, কারণ: (ক) `usage_records` append-only — একবার লেখা usage পরে বদলায় না; (খ) finalize-এর শর্তই হলো outbox ফাঁকা ও period বন্ধ — অর্থাৎ calculation-এর পরে নতুন usage যোগ হওয়ার পথ বন্ধ। মিললে সবুজ; না মিললে (একমাত্র তাত্ত্বিক কারণ: finalize-এর পরে DEAD event requeue হয়ে দেরিতে এসেছে) — সেটা ops-এর জন্য স্পষ্ট তদন্তযোগ্য ঘটনা, সমাধান credit note।

---

## 11. Retry ও Idempotency — পূর্ণ চিত্র

| স্তর | বিপদ | প্রতিরোধ |
|---|---|---|
| FI → API retry (একই operation আবার) | দুইবার বিল | Producer-এর fact টেবিলে আগে থেকেই DB-enforced dedup: `uq_qr_generations_tenant_idempotency` (client `Idempotency-Key` দিলে), `uq_qr_validations_replay (tenant_id, request_id)` — দ্বিতীয় attempt এ নতুন fact row-ই হয় না, ফলে outbox event-ও নেই |
| Outbox → consumer redelivery (worker crash ইত্যাদি) | দুইবার usage row | `uq_usage_records_source (source_type, source_id)` — দ্বিতীয় insert 23505; handler সেটা **সফল ধরে নিয়ে no-op return করে** (ভুল করে exception করলে event চিরকাল retry হবে — এই ধারাটা কোড review-তে পরীক্ষা করতে হবে) |
| Handler-এ সাময়িক ব্যর্থতা (DB timeout) | Event হারানো | Worker-এর catch: attempts++, exponential backoff (`available_at`), সর্বোচ্চ 8 বারের পরে DEAD + alert; ঠিক হয়ে গেলে পরের poll-এ আপনা-আপনি সফল |
| একসাথে দুইবার calculate | দুই invoice | `uq_invoices_tenant_period` → 409 |
| DEAD event | usage স্থায়ীভাবে বাদ | Ops দেখবে (`GET /v1/admin/outbox?status=DEAD`), কারণ ঠিক করে **requeue**; কারণ ঠিক না হলে finalize-এর safety gate-ই আটকে দেবে — চুপচাপ কম বিল করার পথ নেই |

**মূল মন্ত্র:** idempotency ব্যবহার করবে না এমন কোনো consumer লেখা যাবে না, আর retry করা যাবে না এমন কোনো failure থাকবে না।

---

## 12. Tenant Reporting বনাম Admin Reporting

| দিক | Tenant (FI) | Admin (management/finance/ops) |
|---|---|---|
| Scope | **নিজের tenant** একমাত্র — client_id থেকে স্কোপড, spoofing অসম্ভব | সব tenant |
| Usage | current period, meter-wise; period history | tenant × meter × period summary |
| Plan/subscription | (v2-তে নিজের plan দেখা; এখন দরকার নেই) | plan-এর CRUD, price version ইতিহাস, subscription তথ্য |
| Invoice | list + detail (lines সহ) | status অনুযায়ী list, detail, historical |
| Reconciliation | `/v1/usage?from&to` vs invoice lines (§10.5) | একই তুলনা যেকোনো tenant-এ |
| Outbreak ops | — | outbox পরিদর্শন + requeue |

দুই দিকেই implementation একই: Dapper/EF read query, কোনো আলাদা projection store নেই। পরিমাণ বাড়লে (যেমন মাসে 50M+ usage row) তখন `usage_daily_rollups` টেবিল ভাবা হবে — এখন নয়।

---

## 13. Implementation Order (ধাপে ধাপে)

> ধরে নিচ্ছি তুমি VS Code-এ কাজ করছ, terminal PowerShell/Bash, আর local Postgres চলছে। সব command workspace root (`rvl-sbqr-workspace/`) থেকে — তাই প্রথমে submodule-এ ঢুকে নাও।

**Phase 0 — প্রস্তুতি (½ দিন)**

```bash
cd rvl-secure-bqr-manager
# Postgres + বাকি সব তোলা
docker compose -f docker/docker-compose.yml up --build
# API আলাদা করে চালালে দ্রুত iteration
dotnet run --project src/Host/SBQR.Api/SBQR.Api.csproj
# পড়ে ফেলো: README.md, docs/design/database-design.md, src/Modules/Tenancy/README.md
```
পড়ার সময় খেয়াল করো: migration apply করে **external tool** (runtime user-এর DDL নেই), EF configuration গুলো SQL-এর আয়না — নাহলে `SchemaModelDriftTests` কাঁদবে।

**Phase 1 — Outbox অবকাঠামো (১-১.৫ দিন)**

1. `010_integration_outbox.sql` + external tool দিয়ে apply।
2. SharedKernel-এ `IntegrationOutboxEnvelope` entity + `OutboxDispatcherWorker`। Host-এ register।
3. একটি fake event দিয়ে worker পরীক্ষা: হাতে outbox-এ row ঢুকে handler-এ log দেখো, তারপর psql-এ দেখো status `DISPATCHED`।

```bash
docker compose -f docker/docker-compose.yml exec sbqr.postgres \
  psql -U sbqr_app -d sbqr_app -c "SELECT status, count(*) FROM integration_outbox GROUP BY 1;"
```

**Phase 2 — Producer ইভেন্ট (১ দিন)**

4. `QrGeneration.Contracts` ও `Verification.Contracts`-এ integration event record-গুলো।
5. `QrIssuancePipeline` ও `ValidateQrCommandHandler`-এ §7.1-এর দুই লাইন।
6. Postman (`.postman/` collection) দিয়ে একটি generate + একটি validate call; psql-এ দেখো দুটো outbox row `DISPATCHED`।

**Phase 3 — Metering module (২ দিন)**

7. `module-scaffold` skill দিয়ে `Metering` scaffold (5 project, host ও `SBQR.slnx`-এ plug)।
8. `011_metering.sql` + EF mapping + drift test-এ নতুন টেবিল যোগ।
9. দুটো event handler + `RecordUsage` (23505 → no-op!) + `BillableVerdicts`।
10. Query দুটো + tenant endpoint। পরীক্ষা: একই `request_id` দিয়ে validate দুইবার — usage count অপরিবর্তিত।

**Phase 4 — Billing catalog (১-১.৫ দিন)**

11. `Billing` scaffold; `012_billing.sql` + EF mapping + drift test।
12. Plan/price/subscription command + admin endpoint; `IAuditLogger`-এ প্রতিটি admin action।
13. Seed: একটি plan, তিন meter-এ দাম, নিজের test tenant-এ subscription।

**Phase 5 — Invoice (২ দিন)**

14. `CalculateInvoice` (§10.2-এর algorithm) + endpoint; DRAFT recalculation + 409 path।
15. `FinalizeInvoice` + `VoidInvoice`; outbox-এ `InvoiceFinalized`।
16. Tenant invoice endpoint দুটি। Phase 3-এ তৈরি করা usage দিয়ে একটি সম্পূর্ণ মাসের dry-run: usage → calculate → verify → finalize → reconciliation মিলিয়ে দেখো।

**Phase 6 — Notification + polish (১ দিন)**

17. `IInvoiceNotificationSender` + logged adapter; `InvoiceFinalizedHandler`।
18. Admin outbox endpoint + requeue। Postman collection ও submodule-এর `docs/design/database-design.md` আপডেট।

মোট ~9-10 কর্মদিবস। প্রতিটি phase শেষে সিস্টেম deploy-যোগ্য অবস্থায় থাকে — লম্বা ছুটির ডালা নয়।

---

## 14. Fintech Rules — যা ভঙ্গ করা যাবে না

1. **টাকা কখনো floating point নয়।** সব মান `bigint` minor unit-এ (poisha)। `0.1 + 0.2 ≠ 0.3` — শত বছরের পুরনো পাঠ।
2. **FINALIZED invoice অপরিবর্তনীয়।** সংশোধনের একমাত্র পথ নতুন নথি (credit note)। কোডে FINALIZED row-র কোনো UPDATE path রাখা যাবে না।
3. **`usage_records` append-only।** কোনো UPDATE, কোনো DELETE, কোনো soft-delete column (repo-র `qr_generations`-দর্শনের ধারাবাহিকতা)।
4. **দাম বদলালে নতুন row।** ব্যবহৃত `plan_prices` row অপরিবর্তনীয় — DB FK RESTRICT এবং কোড, দুই স্তরেই।
5. **Exactly-once আস্থা নয়, dedup আস্থা।** নতুন কোনো consumer/event যোগ করলে প্রথম প্রশ্ন: "duplicate deliver-এ কী হবে?" উত্তর না থাকলে merge নয়।
6. **চুপচাপ নয়, জোরে ব্যর্থ।** দাম-না-পাওয়া meter, subscription-হীন period, backlog-যুক্ত outbox — সব স্পষ্ট error; কখনোই 0-দামে line বা অসম্পূর্ণ invoice নয়।
7. **PII নয়।** `usage_records`-এ শুধু tenant + meter + সময় + fact id। `user_sub`, MSISDN, PAN-এর কোনো ছায়া নেই — billing-এ প্রয়োজনও নেই।
8. **প্রতিটি admin action অডিটেড** — বিদ্যমান `IAuditLogger` দিয়ে (কে, কখন, কোন invoice/price/requeue)।
9. **Finalize মানুষের হাতে।** মাসিক calculation automate করা যাবে; FINALIZE সবসময় একজন finance-মানুষের স্পষ্ট action — আর্থিক নথি ব্যাকগ্রাউন্ড কর্মীর ঘুমের মধ্যে জন্মায় না।
10. **Timezone শৃঙ্খলা।** সব timestamp `timestamptz` (UTC); billing period-এর সীমানা Asia/Dhaka, `occurred_date`-এ হিমায়িত। দুই জায়গায় আলাদা ধারা চলবে না।

---

## 15. বিবেচিত বিকল্প এবং কেন বাদ

| বিকল্প | বাদ দেওয়ার কারণ |
|---|---|
| **সম্পূর্ণ synchronous metering** (একই transaction-এ usage insert) | Hot path-এ module coupling; Metering-এর সাময়িক ব্যর্থতায় সফল QR generation-ও fail হবে; ভবিষ্যৎ extraction অসম্ভব (§3.2) |
| **Metering module নেই — bill time-এ সরাসরি `qr_generations`/`qr_validations` count** | Billing তখন অন্য module-এর টেবিলের উপর ভবিষ্যৎ-নির্ভর ভিত্তিতে দাঁড়ায়; "current usage" reporting প্রতিবার বিশাল fact টেবিল scan করবে; verdict-billability-র মতো metering নীতি Billing-এ গড়িয়ে পড়বে |
| **Real message broker (RabbitMQ/Service Bus) এখনই** | এখনকার সব consumer একই process-এ; broker মানে নতুন infrastructure, নতুন failure mode, নতুন ops বোঝা — এখনকার একমাত্র সুবিধা হতো "exactly-once-এর অনুভূতি", যা আমরা পাচ্ছিই dedup দিয়ে। Outbox seam রেখে দিলে পরে যোগ করা এক দিনের কাজ |
| **Microservices-এ সরাসরি** | Timeline-এর সাথে অসঙ্গত; distributed transaction/saga-র জটিলতা ছাড়া লাভ শূন্য। Modular monolith-এর boundary-discipline-ই পরের সেতু |
| **Event sourcing / CQRS read-store** | একটি metering fact স্বাভাবিক টেবিলেই নিখুঁত বসে যায়; event store মানে replay/projection-এর গোটা নতুন প্রশ্ন — সমস্যাটা এতে সহজ হয় না, শুধু জাঁকজমক হয় |
| **Invoice dynamic calculation** (দেখার সময় হিসাব) | দাম বদলালে পুরনো invoice-ও বদলে যেত — সরাসরি চাহিদার উল্লঙ্ঘন। Snapshot (stored lines) একমাত্র সঠিক উত্তর |

---

## 16. দ্রুত উত্তর-তালিকা (task-এর গুরুত্বপূর্ণ প্রশ্নগুলোর সূচি)

| প্রশ্ন | উত্তরের জায়গা |
|---|---|
| সফল QR operation ঠিক কখন billable হয়? | §0.1, §9 — fact row commit-এর মুহূর্তে |
| Billable usage record-এর মালিক কে? | §2.2 — Metering |
| বিনা coupling-এ নোটিফিকেশন কীভাবে? | §7 — Contracts + outbox |
| Retry-এ duplicate বিল কীভাবে আটকাবে? | §11 — producer ও consumer দুই স্তরের DB dedup |
| Metering event কি immutable? | হ্যাঁ — §4.2, §14.3 (append-only) |
| Pricing version কীভাবে? | §4.3 `plan_prices` — নতুন row, কখনো UPDATE নয় |
| Invoice কি stored line রাখবে? | হ্যাঁ — §4.3 `invoice_lines`, §10.2 |
| Handler সাময়িকভাবে fail করলে? | §7.2 — backoff-সহ retry, পরে DEAD |
| Outbox-এর বকেয়া message-এর পুনরায় চেষ্টা? | §7.2–7.3 — worker loop + requeue endpoint |
| Invoice calculation কীভাবে চলে? | §10.2 — ধাপে ধাপে algorithm |
| Finalize-এর পরে কী হয়? | §10.3–10.4 — immutable + notification |
| কোন রেকর্ড immutable হয়? | §10.4-এর টেবিল |
| Tenant রিপোর্ট ↔ invoice মিলবে কীভাবে? | §10.5 — reconciliation |
| কোন query-র জন্য কোন index? | §4.5 |
