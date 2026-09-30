# Metering ও Billing — v1.1 Design (Minimal, Production-grade)

- **তারিখ:** 2026-09-30
- **অবস্থা:** Design চূড়ান্ত; কিছু বিষয় HoE ও Product team-এর confirm বাকি (§১৫.৩); implementation বাকি
- **Target repo:** `rvl-secure-bqr-manager` (branch `feature/mtls-server`)
- **নতুন module:** **Metering** ও **Billing** (+ shared **Outbox** infrastructure)
- **মূলনীতি:** DDD mindset (পুরো ceremony নয়), Event-driven Modular Monolith, Clean Architecture। হিসাব নির্ভুল ও audit-যোগ্য; accounting system নয়।

> **v1.1-এ কী বদলেছে:** v1 সরাসরি `qr_generations` / `qr_validations` টেবিল পড়ত (module boundary ভাঙত) এবং রোজ একটা aggregation job চালাত। পুরনো design (`docs/misc/metering-billing-design.md`) থেকে তার **Transactional Outbox + Metering-এর নিজস্ব `usage_events`** নেওয়া হয়েছে, আর তার ভারী commercial অংশ (plan/subscription, invoice number, VAT, paid/void, tenant API) বাদ রাখা হয়েছে।

---

## ১. Scope

FI-দের বিল করা হবে **শুধু দুটো জিনিসের জন্য**, প্রতি successful request হিসেবে:

1. QR generation (static + dynamic)
2. QR validation

সিস্টেম মাস শেষে প্রতিটি FI-র **Statement** (usage + হিসাব করা টাকা) দেবে। Invoice, VAT, payment collection finance team নিজেদের system-এ করবে।

দুটো report:
- **FI report** — FI-র নিজের usage ও টাকা (platform team তৈরি করে FI-কে পাঠাবে)
- **Platform report** — সব FI মিলিয়ে volume, revenue, trend

v1-এ কোনো portal নেই — সব কিছু **admin API** (scope `admin`) দিয়ে। FI-facing কোনো API নেই। **Customer Portal** ও **Admin Portal** ভবিষ্যৎ scope (§১৫ F6, F7)।

---

## ২. Billable নিয়ম

### ২.১ Generation

| পরিস্থিতি | Billable? |
|---|---|
| 201 — QR তৈরি হয়েছে | ✅ |
| 409 `DUPLICATE_IDEMPOTENCY_KEY` | ❌ (row নেই, event নেই) |
| 400 / 401 / 403 / 422 / 500 | ❌ (row নেই, event নেই) |

Static ও Dynamic-এর **রেট একই**; Metering দুটোকে আলাদা meter হিসেবে রাখে, report-এ আলাদা দেখায়।

> ⏳ **নিশ্চিত করা বাকি:** এই তিনটি বিষয় (একই রেট, আলাদা meter, report-এ আলাদা দেখানো) HoE ও Product team-এর সাথে কথা বলে confirm করতে হবে (§১৫ C15, প্রশ্ন ১৬)।

### ২.২ Validation

নিয়ম: platform কাজটা সম্পূর্ণ করে একটা **Conclusive verdict** দিলে billable — ফলাফল valid হোক বা invalid।

| Verdict | Billable? |
|---|---|
| `VALID` (historical key সহ) | ✅ |
| `INVALID_SIGNATURE` | ✅ |
| `STRUCTURAL_INVALID` | ✅ (Q-B-তে নিশ্চিত) |
| `KEY_NOT_FOUND` | ✅ |
| `KEY_SUSPENDED` | ✅ |
| `KEY_REVOKED` | ✅ |
| `KEY_NOT_ACTIVE` | ✅ |
| `NON_P2P` | ✅ (Q-B-তে নিশ্চিত) |
| `REQUEST_STALE` | ❌ Protocol rejection |
| `REQUEST_REPLAYED` | ❌ (row নেই, event নেই) |
| 400 / 401 / 403 / 5xx | ❌ |

`STRUCTURAL_INVALID` ও `NON_P2P` billable — garbage input-ও platform-এর resource খরচ করে, আর free রাখলে abuse-এর পথ খোলে। এগুলো platform-এ পাঠানোর আগেই client-side-এ আটকানো যায় কিনা, সেটা আলাদা প্রশ্ন (§১৫ C14)।

**এই নিয়মের মালিক Metering।** Verification শুধু "verdict X দিয়ে validate হয়েছে" — এই business fact প্রকাশ করে; billable কিনা জানে না। নিয়ম বদলালে শুধু Metering বদলায়।

### ২.৩ কাকে বিল করা হবে (Paying FI)

- **Generation:** যে tenant QR তৈরি করেছে।
- **Validation:** **যে FI validate call করেছে** (verifying tenant), QR-এর issuer নয়।
- FI পরিচয় = token-এর `tenant_id`। প্রতিটি FI-র **আলাদা BFF/gateway deployment** (নিজস্ব `client_id`)।

### ২.৪ Duplicate / retry — তিন স্তরের সুরক্ষা

| স্তর | Duplicate-এর উৎস | সুরক্ষা |
|---|---|---|
| ১. HTTP (FI retry) | একই generation আবার | `Idempotency-Key` **বাধ্যতামূলক** → UNIQUE `(tenant_id, idempotency_key)` → 409; row/event নেই |
| | একই validation আবার | `requestId` **বাধ্যতামূলক** → UNIQUE `(tenant_id, request_id)` → `REQUEST_REPLAYED`; row/event নেই |
| ২. Delivery (outbox redelivery) | একই event দুবার handle | `usage_events` UNIQUE `(source_type, source_id)` + `ON CONFLICT DO NOTHING` |
| ৩. Billing (দুবার চাপ, concurrent run) | Draft/finalize দুবার | `(tenant_id, period)` UNIQUE, row lock, finalize idempotent |

প্রয়োজনীয় code পরিবর্তন (এখনো কেউ integrate করেনি, তাই breaking নয়):
- `QrGenerationController.cs` — static ও dynamic দুই endpoint-এ `Idempotency-Key` required।
- `QrValidationController.cs` — server-side requestId generate বন্ধ; validator-এ required।

### ২.৫ Billable FI

**যে FI-র ওই billing period-এ rate card কার্যকর আছে, শুধু সে billable।** Rate card না থাকলে (test, emulator, internal tenant) Metering usage ঠিকই রেকর্ড করে, কিন্তু Statement হয় না; platform report-এ "non-billable tenant" হিসেবে দেখায়।

---

## ৩. Pricing ও হিসাব

- প্রতি FI একটা **Rate card**: `generation_rate`, `validation_rate` (BDT প্রতি unit)।
- Tier / slab / monthly fee / included units **নেই**।
- রেট শুধু **কোনো মাসের ১ তারিখ থেকে** কার্যকর; শুধু **ভবিষ্যৎ মাসের** জন্য নতুন রেট যোগ করা যায় (ব্যতিক্রম: §১৫ C2)।
- **Billing period:** calendar month, **Asia/Dhaka (UTC+6, DST নেই)**। সময় UTC `timestamptz`-এ রাখা হয়; period-এর সীমা Dhaka-তে হিসাব করে UTC-তে query।
- **কোন period-এ পড়বে:** `occurred_at` (business time — upstream row-এর সময়) ঠিক করে; Metering কখন রেকর্ড করল (`recorded_at`) তা নয়।
- **টাকা:** C#-এ `decimal`, SQL-এ `numeric` — কখনো `float`/`double` নয়। Rate `numeric(18,4)`, amount `numeric(18,2)`।
- **Rounding:** প্রতিটি line-এ একবার, ২ দশমিক, `MidpointRounding.AwayFromZero`; তারপর rounded line যোগ।

```
generation_amount  = round((static_count + dynamic_count) × generation_rate, 2)
validation_amount  = round(billable_validation_count × validation_rate, 2)
adjustments_total  = Σ এই statement-এ যুক্ত adjustment
total              = generation_amount + validation_amount + adjustments_total
```

`total` ঋণাত্মক হতে পারে (Credit) — যেমন আছে তেমনই দেখানো হবে।

---

## ৪. Architecture

### ৪.১ নীতি → সিদ্ধান্ত

| নীতি | এই design-এ কীভাবে |
|---|---|
| **Modular Monolith** | কোনো module অন্য module-এর টেবিল পড়ে না; cross-module FK নেই; `tenant_id` শুধু weak reference |
| **Event-driven** | QR module-গুলো business fact **integration event** হিসেবে প্রকাশ করে; Metering সেটা consume করে |
| **Distributed-ready** | Outbox + versioned event নাম → পরে broker (RabbitMQ/Kafka) বসালে handler বদলাতে হয় না |
| **Clean Architecture** | প্রতিটি module-এ Domain / Application / Infrastructure / Api / Contracts (module-scaffold skill) |
| **DDD mindset** | Ubiquitous language (`CONTEXT.md` → Billing), aggregate + invariant, policy — কিন্তু event sourcing, CQRS read store, saga নেই |
| **Minimal** | Broker নেই, নতুন framework নেই; একটা outbox টেবিল + একটা `BackgroundService` |
| **Production-grade fintech** | Atomic write, at-least-once + idempotent consumer, immutable fact, fail-closed, audit |

### ৪.২ Module map

```
 QrGeneration ──┐  (একই transaction: business row + outbox row)
 Verification ──┤
                ▼
        outbox_messages ──► OutboxDispatcher (Host, BackgroundService)
                                   │ in-process publish (MediatR)
                                   ▼
                              Metering ── মালিক: usage_events, Billability policy, usage query
                                   ▲
                                   │ IMeteringQueries (Contracts, sync pull)
                               Billing ── মালিক: rate card, billing period, statement, adjustment, report
```

| Module | যা লেখে | যা জানে | যা জানে না |
|---|---|---|---|
| QrGeneration / Verification | নিজের row + outbox row | নিজের business fact | meter, দাম, statement |
| **Metering** | `usage_events` | producer-দের event contract; Billability policy | দাম, rate card, statement |
| **Billing** | rate card, period, statement, adjustment | `IMeteringQueries` থেকে পাওয়া সংখ্যা | verdict, QR, outbox |

Reporting আলাদা module নয় — Billing-এর read endpoint (Metering থেকে pull করে)।

### ৪.৩ Billable মুহূর্ত

```
FI → POST /v1/qr/generate/dynamic   (Idempotency-Key: K)
  → validate → sign
  → এক transaction: INSERT qr_generations + INSERT outbox_messages(qr-generation.qr-generated.v1)   ← BILLABLE মুহূর্ত (commit)
  → 201

Dispatcher (~১ সেকেন্ড পরে) → Metering.QrGeneratedHandler
  → Billability policy → INSERT usage_events(... source_id = qr_generation_id, occurred_at = row.created_at)
    ON CONFLICT (source_type, source_id) DO NOTHING
```

| পরিস্থিতি | ফল |
|---|---|
| Commit হলো কিন্তু HTTP response হারাল | একবার বিল; একই K দিয়ে retry → 409, আবার বিল নয় |
| ৩০ সেপ্টেম্বর ২৩:৫৯:৫৯-এ তৈরি, Metering-এ পৌঁছাল ১ অক্টোবর ০০:০০:০২ | **সেপ্টেম্বরে** বিল (`occurred_at`) |
| Metering handler সাময়িক ব্যর্থ | Outbox retry করে; usage **দেরিতে আসে, হারায় না** |
| Dispatcher-এর আগে process crash | Message PENDING থাকে, পরে যায় |

### ৪.৪ Outbox (shared infrastructure)

- **Producer:** `QrIssuancePipeline` ও `ValidateQrCommandHandler.RecordAsync`-এ বিদ্যমান `SaveChangesAsync`-এর **ঠিক আগে** outbox row যোগ। Business row ও event একসাথে commit বা একসাথে rollback। Publish কখনো commit-এর আগে নয়।
- **Dispatcher** (`src/Host/SBQR.Api/Outbox/OutboxDispatcher.cs`, `BackgroundService`):
  1. Lease দিয়ে batch claim (`FOR UPDATE SKIP LOCKED`, `locked_until = now() + 60s`) — একাধিক API instance-এ নিরাপদ।
  2. `event_type` → CLR type **explicit registry** (dictionary) দিয়ে; DB থেকে পড়া string-এ `Type.GetType` কখনো নয়।
  3. নতুন DI scope-এ `IPublisher.Publish`।
  4. সফল → `PROCESSED`। ব্যর্থ → backoff `min(2^attempts s, 300 s)`। `attempts ≥ 10` → `DEAD` + `LogCritical`।
  5. ব্যাচ ভরা থাকলে সাথে সাথে আবার, নইলে ১ সেকেন্ড অপেক্ষা। ঘণ্টায় একবার ৭ দিনের পুরনো `PROCESSED` মুছে ফেলা (outbox plumbing; আসল record `usage_events`)।
- **Dead letter:** admin API দিয়ে তালিকা ও requeue (audited)। Dead letter থাকলে ওই period **finalize হয় না** (§৫)।
- Config: `Outbox:PollIntervalMs`, `Outbox:BatchSize`, `Outbox:MaxAttempts`, `Outbox:RetentionDays`।

> **প্রতিটি handler idempotent হতে হবে** — একই message একাধিকবার আসতে পারে।

### ৪.৫ Integration events

Producer-এর `.Contracts` project-এ, versioned। SharedKernel-এ নতুন `IIntegrationEvent : INotification { Guid EventId; DateTimeOffset OccurredAt; }`।

| `event_type` | Producer | Payload | Consumer |
|---|---|---|---|
| `qr-generation.qr-generated.v1` | QrGeneration | EventId, QrGenerationId, TenantId, QrType, IdempotencyKey, OccurredAt | Metering → `GENERATION_STATIC` / `GENERATION_DYNAMIC` |
| `verification.qr-validated.v1` | Verification (প্রতিটি রেকর্ড হওয়া row) | EventId, QrValidationId, TenantId (verifying), Verdict, RequestId, OccurredAt | Metering → `VALIDATION` (billable flag policy অনুযায়ী) |

Billing কোনো event প্রকাশ করে না (notification নেই); তার সিদ্ধান্তগুলো সরাসরি `audit_logs`-এ যায়।

### ৪.৬ Metering

- **লেখা:** একটাই internal command `RecordUsage` — event → meter code + `billable` flag (Billability policy) → `INSERT … ON CONFLICT DO NOTHING`।
- Non-billable verdict (`REQUEST_STALE`)-ও রেকর্ড হয় `billable = false` দিয়ে — যাতে FI report-এ verdict breakdown ও raw extract সম্পূর্ণ থাকে। Billing শুধু `billable = true` গোনে।
- `billable` রেকর্ড করার মুহূর্তে ঠিক হয় এবং আর বদলায় না — পরে নিয়ম বদলালে পুরনো ইতিহাস বদলায় না।
- **`IMeteringQueries`** (Contracts, Billing ব্যবহার করে):

| Query | কাজ |
|---|---|
| `GetBillableQuantities(tenantId, fromUtc, toUtc)` | `{ meter_code, quantity }[]` |
| `GetUsageBreakdown(tenantId?, fromUtc, toUtc, groupBy = day \| meter \| verdict \| tenant)` | Report-এর জন্য |
| `StreamUsageEvents(tenantId, fromUtc, toUtc, cursor)` | Raw usage extract (keyset-paged) |
| `IsUsageComplete(untilUtc)` | `untilUtc`-এর আগের কোনো QR-event PENDING বা DEAD নেই → true |

- **Daily rollup টেবিল নেই।** Covering index দিয়ে সরাসরি গোনা — মাসে কয়েক কোটি row পর্যন্ত যথেষ্ট। ধীর হলে তখন partition বা rollup (§১৫ F11)।

### ৪.৭ Billing

- Rate card, billing period, statement, adjustment-এর মালিক।
- সংখ্যা **pull** করে `IMeteringQueries` থেকে — usage event-এ subscribe করে না (statement একটা batch কাজ; batch-এ pull সহজ ও নির্ভুল)।
- **Period closer** (`BackgroundService`, ঘণ্টায় একবার): আগের মাস Open থাকলে এবং `now ≥ period_end + 2 ঘণ্টা` (grace) **ও** `IsUsageComplete(period_end)` true হলে → সব Billable FI-র Draft statement তৈরি, period → `DRAFT`। শর্ত পূরণ না হলে পরের ঘণ্টায় আবার চেষ্টা।

### ৪.৮ Data model

হাতে লেখা idempotent SQL, `public` schema, UUID v7 PK, UTC `timestamptz`। তিনটি migration।

#### `010_outbox.sql`

```sql
CREATE TABLE IF NOT EXISTS public.outbox_messages (
  outbox_message_id uuid PRIMARY KEY,                 -- = integration event id
  source_module     varchar(50)  NOT NULL,            -- 'qr-generation' | 'verification'
  event_type        varchar(150) NOT NULL,            -- যেমন 'qr-generation.qr-generated.v1'
  payload           jsonb        NOT NULL,
  occurred_at       timestamptz  NOT NULL,
  status            varchar(15)  NOT NULL DEFAULT 'PENDING'
                    CHECK (status IN ('PENDING','PROCESSED','DEAD')),
  attempts          integer      NOT NULL DEFAULT 0,
  next_attempt_at   timestamptz  NOT NULL DEFAULT now(),
  locked_until      timestamptz,
  last_error        varchar(2000),                    -- PII নয়
  processed_at      timestamptz,
  created_at        timestamptz  NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_outbox_due       ON public.outbox_messages (next_attempt_at) WHERE status = 'PENDING';
CREATE INDEX IF NOT EXISTS ix_outbox_open      ON public.outbox_messages (occurred_at)     WHERE status IN ('PENDING','DEAD');
CREATE INDEX IF NOT EXISTS ix_outbox_processed ON public.outbox_messages (processed_at)    WHERE status = 'PROCESSED';

-- Generic append-only guard (বিদ্যমান forbid_mutation() শুধু audit_logs-এর জন্য লেখা)
CREATE OR REPLACE FUNCTION public.forbid_mutation_any() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'public.% is append-only: % is prohibited', TG_TABLE_NAME, TG_OP
    USING ERRCODE = 'check_violation';
END $$;
```

#### `011_metering.sql`

```sql
CREATE TABLE IF NOT EXISTS public.usage_events (
  usage_event_id   uuid PRIMARY KEY,
  tenant_id        uuid         NOT NULL,               -- weak ref
  meter_code       varchar(40)  NOT NULL
                   CHECK (meter_code IN ('GENERATION_STATIC','GENERATION_DYNAMIC','VALIDATION')),
  billable         boolean      NOT NULL,
  detail           varchar(40),                         -- VALIDATION-এর verdict; PII নয়
  source_type      varchar(30)  NOT NULL CHECK (source_type IN ('qr_generation','qr_validation')),
  source_id        uuid         NOT NULL,               -- qr_generation_id / qr_validation_id
  source_event_id  uuid         NOT NULL,               -- outbox_message_id (tracing)
  client_reference varchar(100) NOT NULL,               -- FI-র Idempotency key → FI-side reconciliation
  occurred_at      timestamptz  NOT NULL,               -- business time → period ঠিক করে
  recorded_at      timestamptz  NOT NULL DEFAULT now(),
  CONSTRAINT uq_usage_events_source UNIQUE (source_type, source_id)   -- dedup-এর মূল নিশ্চয়তা
);
CREATE INDEX IF NOT EXISTS ix_usage_tenant_time ON public.usage_events (tenant_id, occurred_at)
  INCLUDE (meter_code, billable, detail);
CREATE INDEX IF NOT EXISTS ix_usage_time ON public.usage_events (occurred_at)
  INCLUDE (tenant_id, meter_code, billable);

DROP TRIGGER IF EXISTS trg_usage_events_immutable ON public.usage_events;
CREATE TRIGGER trg_usage_events_immutable
  BEFORE UPDATE OR DELETE OR TRUNCATE ON public.usage_events
  FOR EACH STATEMENT EXECUTE FUNCTION public.forbid_mutation_any();
```

> **Dedup key কেন `(source_type, source_id)`, event id নয়?** Producer-এর bug-এ একই QR row-এর জন্য দুটো event গেলেও ফল একটাই unit। Dedup business fact-এর ওপর, message-এর ওপর নয়।

#### `012_billing.sql`

```sql
CREATE TABLE IF NOT EXISTS public.billing_rate_cards (
  rate_card_id     uuid PRIMARY KEY,
  tenant_id        uuid          NOT NULL,              -- weak ref
  effective_from   date          NOT NULL CHECK (EXTRACT(DAY FROM effective_from) = 1),
  generation_rate  numeric(18,4) NOT NULL CHECK (generation_rate >= 0),
  validation_rate  numeric(18,4) NOT NULL CHECK (validation_rate >= 0),
  currency         char(3)       NOT NULL DEFAULT 'BDT',
  created_by       varchar(200)  NOT NULL,
  created_at       timestamptz   NOT NULL DEFAULT now(),
  CONSTRAINT uq_rate_cards UNIQUE (tenant_id, effective_from)
);

CREATE TABLE IF NOT EXISTS public.billing_periods (
  period        char(7)      PRIMARY KEY,               -- 'YYYY-MM'
  status        varchar(15)  NOT NULL CHECK (status IN ('OPEN','DRAFT','FINALIZED')),
  drafted_at    timestamptz,
  finalized_at  timestamptz,
  finalized_by  varchar(200)
);

CREATE TABLE IF NOT EXISTS public.billing_statements (
  statement_id       uuid PRIMARY KEY,
  tenant_id          uuid          NOT NULL,
  period             char(7)       NOT NULL REFERENCES public.billing_periods(period),
  status             varchar(15)   NOT NULL CHECK (status IN ('DRAFT','FINALIZED')),
  rate_card_id       uuid          NOT NULL REFERENCES public.billing_rate_cards(rate_card_id),
  generation_rate    numeric(18,4) NOT NULL,            -- snapshot
  validation_rate    numeric(18,4) NOT NULL,            -- snapshot
  subtotal           numeric(18,2) NOT NULL,
  adjustments_total  numeric(18,2) NOT NULL,
  total              numeric(18,2) NOT NULL,
  currency           char(3)       NOT NULL,
  calculated_at      timestamptz   NOT NULL,
  CONSTRAINT uq_statements UNIQUE (tenant_id, period)
);

CREATE TABLE IF NOT EXISTS public.billing_statement_lines (
  statement_id  uuid          NOT NULL REFERENCES public.billing_statements(statement_id) ON DELETE CASCADE,
  line_no       integer       NOT NULL,
  line_type     varchar(15)   NOT NULL CHECK (line_type IN ('USAGE','ADJUSTMENT')),
  meter_code    varchar(40),                            -- USAGE-এ
  description   varchar(200)  NOT NULL,
  quantity      bigint        NOT NULL DEFAULT 0,
  unit_rate     numeric(18,4) NOT NULL DEFAULT 0,
  amount        numeric(18,2) NOT NULL,
  adjustment_id uuid,                                   -- ADJUSTMENT-এ
  PRIMARY KEY (statement_id, line_no)
);

CREATE TABLE IF NOT EXISTS public.billing_adjustments (
  adjustment_id         uuid PRIMARY KEY,
  tenant_id             uuid          NOT NULL,
  amount                numeric(18,2) NOT NULL CHECK (amount <> 0),   -- ঋণাত্মক = credit
  reason                varchar(500)  NOT NULL,
  created_by            varchar(200)  NOT NULL,
  created_at            timestamptz   NOT NULL DEFAULT now(),
  applied_statement_id  uuid REFERENCES public.billing_statements(statement_id)
);
```

**Immutability trigger (`012`-এ):**

| টেবিল | নিয়ম |
|---|---|
| `billing_rate_cards` | UPDATE নিষেধ; DELETE শুধু `effective_from > current_date` হলে (ভুল ভবিষ্যৎ রেট সরাতে) |
| `billing_statements` | `DRAFT` অবস্থায় সব চলে; `FINALIZED` হলে UPDATE/DELETE নিষেধ |
| `billing_statement_lines` | Parent `DRAFT` না হলে INSERT/UPDATE/DELETE নিষেধ |
| `billing_adjustments` | UPDATE শুধু `applied_statement_id` NULL → মান (একবার); DELETE নিষেধ |

**Grants:** `sbqr_app_runtime` non-owner convention অনুযায়ী — runtime role `usage_events`-এ শুধু `SELECT, INSERT`।

---

## ৫. মাস close ও সংশোধন

1. **Draft** — Period closer (§৪.৭) grace + usage completeness নিশ্চিত হলে Draft তৈরি করে। Draft statement-এ সব Pending adjustment যুক্ত হয়।
2. **Recalculate** — `POST .../periods/{period}/recalculate`: Draft ফেলে নতুন করে তৈরি (যেমন নতুন adjustment বা dead letter requeue-র পরে)।
3. **Finalize** — `POST .../periods/{period}/finalize`, body: `finalizedBy` (বাধ্যতামূলক), `expectedTotal` (Draft-এর সব statement-এর মোট)। এক transaction-এ:
   1. Period ইতিমধ্যে `FINALIZED` → আগের ফল ফেরত (idempotent)।
   2. `IsUsageComplete(period_end)` আবার যাচাই — false হলে 409 `USAGE_NOT_COMPLETE`।
   3. সংখ্যা আবার হিসাব; মোট `expectedTotal`-এর সাথে না মিললে 409 `DRAFT_CHANGED` (Draft নতুন সংখ্যায় replace হয়) — **যা review করা হয়েছে, ঠিক সেটাই finalize হয়।**
   4. সব statement → `FINALIZED`; adjustment-গুলোর `applied_statement_id` set; period → `FINALIZED`; `audit_logs`-এ `billing.period.finalized`।
4. **Finalize-এর পর কিছুই বদলায় না** (DB trigger)। সংশোধন = নতুন **Adjustment** → পরের মাসের statement-এ `ADJUSTMENT` line।
5. **Late usage** (finalize-এর পরে আসা, আগের period-এর usage) — completeness gate থাকায় প্রায় অসম্ভব; ঘটলে platform report-এ দেখায়, finance Adjustment দিয়ে ধরে। স্বয়ংক্রিয় নয়।
6. **Dispute window:** finalize-এর পর ৩০ দিন; প্রমাণ = Raw usage extract।

> **Known limitation:** Admin API-তে একটাই platform credential; `finalizedBy` / `createdBy` free-text + audit log। Four-eyes check (finalize ≠ calculate ব্যক্তি) আসল user identity আসার পরে (§১৫ F9)।

---

## ৬. Admin API

সব endpoint-এ scope `admin`। Prefix বিদ্যমান admin route convention অনুযায়ী (placeholder `/v1/admin`)।

| Method | Path | কাজ |
|---|---|---|
| POST / GET | `/billing/rate-cards` (`?tenantId=`) | নতুন রেট / রেট ইতিহাস |
| DELETE | `/billing/rate-cards/{id}` | শুধু এখনো কার্যকর হয়নি এমন |
| POST | `/billing/adjustments` | `tenantId`, `amount`, `reason`, `createdBy` |
| GET | `/billing/periods` | সব period ও status |
| POST | `/billing/periods/{period}/recalculate` | Draft নতুন করে তৈরি |
| POST | `/billing/periods/{period}/finalize` | `finalizedBy`, `expectedTotal` |
| GET | `/billing/statements/{tenantId}/{period}?format=json\|csv\|html` | FI statement / report |
| GET | `/billing/usage/{tenantId}/{period}/raw.csv` | Raw usage extract |
| GET | `/billing/reports/platform/{period}?format=json\|csv\|html` | Platform report |
| GET | `/outbox/dead` | Dead letter তালিকা |
| POST | `/outbox/{id}/requeue` | Dead letter আবার চালানো (audited) |

Open period-এর statement endpoint **provisional** সংখ্যা দেয় (Metering থেকে live, কয়েক সেকেন্ড পিছিয়ে); response-এ `"status": "PROVISIONAL"` ও `asOf`।

HTML = server-rendered, JS-ছাড়া, print-friendly; browser-এ "Save as PDF"।

---

## ৭. Report-এর বিষয়বস্তু

শুধু সংখ্যা ও টেবিল — chart নেই। সব report-এ `asOf`।

### ৭.১ FI report (প্রতি FI, প্রতি মাস)

1. মাসের মোট: billable generation (static / dynamic), billable validation, টাকা, status (PROVISIONAL / DRAFT / FINALIZED)
2. দৈনিক টেবিল
3. Validation verdict অনুযায়ী ভাগ — billable চিহ্নিত
4. Adjustment line (থাকলে)
5. আগের মাসগুলোর finalized statement-এর তালিকা

#### নমুনা: FI report-এর PDF layout

HTML print page (`format=html` → browser-এ "Save as PDF") দেখতে যেমন হবে, তার ASCII নমুনা। A4 portrait, monospace, ৮০ column। সব নাম, ID ও সংখ্যা কাল্পনিক, তবে হিসাব মেলানো: §৩-এর সূত্রে যোগ করলে প্রতিটি মোট হুবহু আসে।

```text
================================================================================
 SBQR PLATFORM - USAGE STATEMENT                                    Page 1 of 2
================================================================================
 FI            : Example Bank PLC
 Tenant ID     : 7f3c9a2e-1b44-4d6a-9c0e-5a8b2d1e6f90
 Period        : 2026-08  (01-Aug-2026 to 31-Aug-2026, Asia/Dhaka)
 Status        : FINALIZED  (2026-09-02 11:40 by ops.finance)
 Statement ID  : 0d9b6e1a-52c7-4f38-a1d2-3e7c9b4f8a15
 Rate card     : in force from 2026-01-01
 As of         : 2026-09-30 14:00 (Asia/Dhaka)
 Currency      : BDT

--------------------------------------------------------------------------------
 1. MONTHLY SUMMARY
--------------------------------------------------------------------------------
 Ln  Description                          Quantity   Unit rate         Amount
 --  ------------------------------   ------------  ----------  -------------
  1  QR generation - static                 12,400      0.5000       6,200.00
  2  QR generation - dynamic                30,850      0.5000      15,425.00
  3  QR validation - billable              118,640      0.2500      29,660.00
                                                    ----------  -------------
                                                      Subtotal      51,285.00
  4  Adjustment a41f2c07 (see section 4)                            -1,250.00
                                                    ----------  -------------
                                                   TOTAL (BDT)      50,035.00
                                                    ==========  =============

--------------------------------------------------------------------------------
 2. DAILY USAGE
--------------------------------------------------------------------------------
 Date          Gen static   Gen dynamic   Val billable   Val non-billable (*)
 ----------   -----------  ------------  -------------  ---------------------
 2026-08-01           402         1,010          3,820                     14
 2026-08-02           388           978          3,741                     12
 2026-08-03           415         1,002          3,905                     15
 ...                  ...           ...            ...                    ...
 2026-08-30           376           941          3,512                     11
 2026-08-31           420         1,055          4,020                     16
 ----------   -----------  ------------  -------------  ---------------------
 TOTAL             12,400        30,850        118,640                    448
 (*) REQUEST_STALE / REQUEST_REPLAYED: recorded, never charged.

--------------------------------------------------------------------------------
 3. VALIDATIONS BY VERDICT
--------------------------------------------------------------------------------
 Verdict                   Billable        Count
 ----------------------    --------   ----------
 VALID                     Yes           112,900
 INVALID_SIGNATURE         Yes             1,210
 STRUCTURAL_INVALID        Yes             2,340
 KEY_NOT_FOUND             Yes             1,480
 KEY_SUSPENDED             Yes                95
 KEY_REVOKED               Yes                60
 KEY_NOT_ACTIVE            Yes                25
 NON_P2P                   Yes               530
                           --------   ----------
                           Billable      118,640
 REQUEST_STALE             No                410
 REQUEST_REPLAYED          No                 38
                           --------   ----------
                           Not billed        448
                           ALL           119,088

--------------------------------------------------------------------------------
                                                                    Page 2 of 2
 4. ADJUSTMENTS APPLIED TO THIS STATEMENT
--------------------------------------------------------------------------------
 Adjustment   Created           Amount   Reason
 ----------   ----------   -----------   ---------------------------------------
 a41f2c07     2026-08-19     -1,250.00   Refund: 5,000 KEY_NOT_FOUND verdicts on
                                         2026-07-14 (trust-store sync delay)
                           -----------
                           -1,250.00

--------------------------------------------------------------------------------
 5. PREVIOUS FINALIZED STATEMENTS
--------------------------------------------------------------------------------
 Period    Generations   Validations   Adjustments          Total    Finalized
 -------   -----------   -----------   -----------   ------------   ----------
 2026-07        40,110       110,920          0.00      47,785.00   2026-08-03
 2026-06        38,920       104,300          0.00      45,535.00   2026-07-02
 2026-05        36,480        99,860          0.00      43,205.00   2026-06-03

--------------------------------------------------------------------------------
 NOTES
 - Dispute window for 2026-08 is open until 2026-10-02 (30 days after
   finalization). Evidence: raw usage extract (CSV) from the platform team.
 - Every quantity above equals the billable rows in the raw usage extract.
 - This is a usage statement, not a tax invoice. Invoicing and VAT: Finance.
 - CONFIDENTIAL - prepared for Example Bank PLC only.
================================================================================
```

- **Status অনুযায়ী যা বদলায়:**
  - `PROVISIONAL` হলে ওপরে banner থাকে: `*** PROVISIONAL - figures may change until finalized ***`। তখন Statement ID ও Finalized লাইন থাকে না, আর দৈনিক টেবিল `As of`-এর দিন পর্যন্ত যায়।
  - `DRAFT` হলে banner থাকে: `*** DRAFT - under review, not final ***`।
- **Line:** প্রতি meter-এ একটা USAGE line (static, dynamic, validation), যাতে §৭.৩-এর reconciliation invariant সরাসরি মেলানো যায়।
- **দৈনিক টেবিল:** নমুনায় মাঝের দিনগুলো `...` দিয়ে বাদ রাখা হয়েছে; আসল report-এ মাসের প্রতিটি দিন থাকে, usage না থাকলে 0। টাকা শুধু মাসিক line-এ থাকে, দৈনিক টেবিলে নয়, কারণ rounding হয় line-এ।
- **Section বাদ পড়া:** Adjustment না থাকলে section 4-এ লেখা থাকে `None`। প্রথম মাসে section 5-এ লেখা থাকে `No previous statements`।

### ৭.২ Platform report (প্রতি মাস)

1. মোট volume ও revenue, **FI অনুযায়ী ভাগ** (বড় থেকে ছোট)
2. আগের মাসের তুলনায় পরিবর্তন (%)
3. দৈনিক মোট
4. Period status
5. Non-billable tenant-দের usage
6. Pending adjustment ও late usage (থাকলে)

#### নমুনা: Platform report-এর PDF layout

§৭.১-এর নমুনার মতোই ASCII layout; সংখ্যা ওই নমুনার সাথে মেলানো (Example Bank PLC-র সারি = §৭.১-এর statement)।

```text
================================================================================
 SBQR PLATFORM - MONTHLY PLATFORM REPORT                            Page 1 of 2
 INTERNAL - platform team only
================================================================================
 Period        : 2026-08  (01-Aug-2026 to 31-Aug-2026, Asia/Dhaka)
 Status        : FINALIZED  (2026-09-02 11:40 by ops.finance)
 As of         : 2026-09-30 14:00 (Asia/Dhaka)
 Currency      : BDT

--------------------------------------------------------------------------------
 1. VOLUME AND REVENUE BY FI (largest first)
--------------------------------------------------------------------------------
 FI                  Generations  Validations    Adjust.         Total   Share
 -----------------   -----------  -----------  ---------  ------------  ------
 Sample MFS Ltd           85,500      210,300       0.00     76,260.00  53.02%
 Example Bank PLC         43,250      118,640  -1,250.00     50,035.00  34.78%
 Demo Bank Ltd            15,000       40,200       0.00     17,550.00  12.20%
 -----------------   -----------  -----------  ---------  ------------  ------
 TOTAL (3 FIs)           143,750      369,140  -1,250.00    143,845.00 100.00%

 Generation amount     63,325.00   (static 37,500 + dynamic 106,250)
 Validation amount     81,770.00
 Subtotal             145,095.00
 Adjustments           -1,250.00
 TOTAL (BDT)          143,845.00

--------------------------------------------------------------------------------
 2. CHANGE VS PREVIOUS MONTH
--------------------------------------------------------------------------------
 Metric                          2026-07        2026-08      Change
 ----------------------   -------------  -------------   ---------
 Billable generations            131,200        143,750      +9.57%
 Billable validations            342,500        369,140      +7.78%
 Total (BDT)                  132,410.00     143,845.00      +8.64%
 Billable FIs                          3              3          --

--------------------------------------------------------------------------------
 3. DAILY TOTALS (all billable FIs)
--------------------------------------------------------------------------------
 Date          Generations   Val billable   Val non-billable
 ----------   ------------  -------------  -----------------
 2026-08-01          4,610         11,820                 51
 2026-08-02          4,488         11,602                 49
 2026-08-03          4,702         12,015                 58
 ...                   ...            ...                ...
 2026-08-30          4,390         11,410                 47
 2026-08-31          4,815         12,380                 60
 ----------   ------------  -------------  -----------------
 TOTAL             143,750        369,140              1,680

--------------------------------------------------------------------------------
                                                                    Page 2 of 2
 4. PERIOD STATUS
--------------------------------------------------------------------------------
 Period    Status      Drafted            Finalized          By
 -------   ---------   ----------------   ----------------   -------------
 2026-09   OPEN        -                  -                  -
 2026-08   FINALIZED   2026-09-01 02:00   2026-09-02 11:40   ops.finance
 2026-07   FINALIZED   2026-08-01 02:00   2026-08-03 10:05   ops.finance

 Usage complete up to  : 2026-09-30 13:59:58 (Asia/Dhaka)
 Outbox                : 0 dead letters, oldest pending 2 s

--------------------------------------------------------------------------------
 5. USAGE OF NON-BILLABLE TENANTS (recorded, not charged)
--------------------------------------------------------------------------------
 Tenant              Reason                    Generations   Validations
 -----------------   -----------------------   -----------   -----------
 Pilot FI (UAT)      No rate card in force           1,240         3,980

--------------------------------------------------------------------------------
 6. PENDING ADJUSTMENTS AND LATE USAGE
--------------------------------------------------------------------------------
 Pending adjustments (applied to the next draft, 2026-09)
 FI                Adjustment   Created          Amount   Reason
 ---------------   ----------   ----------   ----------   ----------------------
 Demo Bank Ltd     c19e7b3d     2026-09-12      +500.00   2,000 late validations
                                                          of 2026-07 (x 0.2500)

 Late usage (recorded after its period was finalized)
 Period    FI                 Events   Covered by
 -------   ---------------   -------   ----------
 2026-08   -                       0   -
 2026-07   Demo Bank Ltd       2,000   c19e7b3d

================================================================================
```

- **Share** = FI-র Total ÷ সব FI-র Total, ২ দশমিকে। কোনো FI-র Total ঋণাত্মক (Credit) হলে Share দেখায় `--`।
- **পরিবর্তন (%):** আগের মাস `FINALIZED` না হলে (বা প্রথম মাসে) Change কলামে `--`।
- **Section বাদ পড়া:** Non-billable tenant, pending adjustment বা late usage না থাকলে সংশ্লিষ্ট section-এ লেখা থাকে `None`।

### ৭.৩ Raw usage extract (CSV)

কলাম: `occurred_at (Dhaka)`, `meter_code`, `client_reference` (FI-র Idempotency key), `detail` (verdict), `billable`। কোনো PII নেই।

**Reconciliation invariant** (integration test-ও): প্রতিটি finalized statement-এর USAGE line-এর `quantity` = ওই FI, meter ও period-এর `usage_events`-এ `billable = true` row-এর সংখ্যা। Raw extract-এর billable row যোগ করলে statement-এর সংখ্যা হুবহু মেলে।

---

## ৮. Monitoring

আলাদা email/alert system নেই। API health check-এ দুটো check:

| Check | Degraded যখন |
|---|---|
| `outbox` | কোনো `DEAD` message আছে, অথবা সবচেয়ে পুরনো `PENDING` ৫ মিনিটের বেশি পুরনো |
| `billing` | আগের মাস ৫ তারিখের পরেও `FINALIZED` নয় |

Dead letter → `LogCritical` (EventId `OUTBOX_DEAD_LETTER`)।

---

## ৯. Data retention

- `usage_events` — **Metering-এর মালিকানা**; অন্তত ১৩ মাস (dispute ও বছর-তুলনা), regulatory নিয়ম থাকলে সেটা।
- `billing_*` টেবিল — স্থায়ী।
- `outbox_messages` — `PROCESSED` ৭ দিন পর মুছে যায়; `DEAD` থাকে যতক্ষণ না requeue।
- QR module-এর টেবিলের retention তাদের নিজের দায়িত্ব — Billing আর তাদের ওপর নির্ভর করে না।

---

## ১০. Security নোট

- সব billing ও outbox endpoint শুধু `admin` scope।
- HTML output-এ সব dynamic মান HTML-encode।
- CSV-তে formula injection প্রতিরোধ (`=`, `+`, `-`, `@` দিয়ে শুরু হলে `'` prefix)।
- `reason`, `createdBy`, `finalizedBy` — length limit ও validation।
- সব টাকা-সংক্রান্ত write (rate card, adjustment, recalculate, finalize, requeue) → `IAuditLogger` (কে, কী, আগে/পরে মোট)।
- `usage_events` বা billing টেবিলে কোনো PII নয় (নাম, PAN, MSISDN)।
- Outbox registry explicit — DB-র string থেকে type resolve নয়।

---

## ১১. Fintech নিয়ম — কখনো ভাঙা যাবে না

1. টাকা `decimal` / `numeric` — কখনো float নয়। প্রতি line-এ একবার rounding, তারপর যোগ।
2. **বিল শুধু `usage_events` থেকে** — HTTP log, metric বা `audit_logs` থেকে কখনো নয়।
3. `usage_events`, rate card ও finalized statement append-only। সংশোধন = নতুন record (Adjustment), কখনো edit নয়। Shared environment-এ হাতে UPDATE/DELETE নয়।
4. Finalized statement কখনো আবার হিসাব নয় — সংরক্ষিত line পড়ো।
5. দাম সবসময় ওই period-এ কার্যকর rate card থেকে — "বর্তমান দাম" থেকে নয়।
6. সময় UTC-তে রাখো; period Dhaka-তে হিসাব করো; `occurred_at` period ঠিক করে।
7. Dedup DB UNIQUE constraint দিয়ে — "আগে দেখো, তারপর insert" code দিয়ে নয়।
8. Outbox row business row-এর **একই `SaveChanges`-এ** — commit-এর আগে কখনো publish নয়।
9. প্রতিটি event handler idempotent।
10. **Fail closed** — usage অসম্পূর্ণ, draft বদলে গেছে বা rate card নেই এমন meter → থামো; কখনো আন্দাজ বা নিঃশব্দে ০ বিল নয়।
11. প্রতিটি billing command audit হয়।

---

## ১২. Acceptance criteria

1. একই Idempotency key দিয়ে দুবার request → `usage_events`-এ ১টা row।
2. Key ছাড়া generation বা validation → 400।
3. একই outbox message জোর করে দুবার deliver → `usage_events`-এর সংখ্যা বদলায় না।
4. QR row commit হলে outbox row-ও আছে; QR insert rollback হলে outbox row-ও নেই।
5. `REQUEST_STALE` → `billable = false`; `REQUEST_REPLAYED`, 4xx, 5xx → কোনো row নেই।
6. FI-A-র QR FI-B validate করলে → usage FI-B-র।
7. ২৩:৫৯:৫৯ (Dhaka)-র QR, Metering-এ পরের দিন পৌঁছালেও আগের দিনে/মাসে পড়ে।
8. Metering handler ১০ বার ব্যর্থ → `DEAD`, health Degraded, period finalize → 409 `USAGE_NOT_COMPLETE`; requeue-র পর সব ঠিক।
9. Rate card ছাড়া tenant-এর statement হয় না, platform report-এ দেখায়।
10. `expectedTotal` না মিললে finalize → 409 `DRAFT_CHANGED`।
11. Finalize দুবার চাপলে → একই ফল (idempotent)।
12. Finalize-এর পর statement / line / `usage_events`-এ UPDATE → DB error।
13. Finalize-এর পর দাম বদলালেও পুরনো statement অপরিবর্তিত।
14. Adjustment পরের মাসের statement-এ line; total ঋণাত্মক হলে সেটাই দেখায়।
15. চলতি/অতীত মাসের রেট যোগ → 400।
16. Reconciliation invariant (§৭.৩) সব finalized statement-এ সত্য।
17. FI credential দিয়ে যেকোনো billing/outbox endpoint → 403।
18. একই statement JSON = CSV = HTML — একই সংখ্যা।

---

## ১৩. v1-এ ইচ্ছাকৃতভাবে যা নেই

- Plan / plan version / subscription, monthly fee, included units, tier
- Invoice number, VAT, paid/void, payment tracking
- FI-facing usage/invoice API, Customer Portal, Admin Portal (§১৫ F6, F7)
- Message broker (RabbitMQ / Kafka)
- Daily rollup টেবিল
- স্বয়ংক্রিয় বিল পাঠানো ও notification (email / SMS / `IBillingNotifier`) — §১৫ F8
- Chart
- মাসের মাঝে রেট পরিবর্তন, credit carry-forward
- Four-eyes check, user-ভিত্তিক login
- Late usage স্বয়ংক্রিয়ভাবে পরের statement-এ

---

## ১৪. Domain model

শব্দের সংজ্ঞা: `rvl-secure-bqr-manager/CONTEXT.md` → **Billing** অংশ।

### ১৪.১ Context map

```
                     ┌──────────────┐
                     │   Tenancy    │  FI / Tenant পরিচয়
                     └──────┬───────┘
                            │ tenant_id (weak ref)
┌──────────────┐            ▼
│ QrGeneration │──event──► ┌──────────┐ ◄──event── ┌──────────────┐
└──────────────┘  (outbox) │ Metering │  (outbox)  │ Verification │
                           └────┬─────┘            └──────────────┘
                                │ IMeteringQueries (pull)
                                ▼
                           ┌──────────┐          ┌───────────────────┐
                           │ Billing  │ ───────► │ Finance (বাইরের) │  Statement (হাতে)
                           └────┬─────┘          └───────────────────┘
                                ▼
                           ┌──────────┐
                           │  Audit   │
                           └──────────┘
```

- **QrGeneration / Verification → Metering:** **Published Language** — versioned integration event। Producer শুধু business fact জানায়; billable কিনা জানে না।
- **Metering → Billing:** **Customer–Supplier** — `IMeteringQueries` contract।
- **Tenancy → সবাই:** শুধু `tenant_id` reference।

### ১৪.২ Metering

**UsageEvent** (immutable entity) — একটা metered operation ঘটেছে, এই fact। তৈরির পর কখনো বদলায় না; `(source_type, source_id)` দিয়ে unique।

**Billability policy** — `(MeteredOperation, verdict) → billable?` (§২.২)।

### ১৪.৩ Billing aggregates

#### RateCard
| Invariant |
|---|
| `effectiveFrom` সবসময় কোনো billing period-এর প্রথম দিন |
| শুধু ভবিষ্যৎ period-এর জন্য তৈরি (ব্যতিক্রম §১৫ C2) |
| তৈরির পর বদলায় না — নতুন দাম = নতুন RateCard |
| প্রতি FI, প্রতি period-এ সর্বোচ্চ একটা; দুটো দামই ≥ 0 |

Period P-এর দাম = `effectiveFrom ≤ P` এমন সবচেয়ে সাম্প্রতিক RateCard; না থাকলে FI billable নয়।

#### BillingPeriod
```
Open ──(grace + usage complete)──► Draft ──(Finalize: finalizedBy, expectedTotal)──► Finalized
```
| Invariant |
|---|
| মাস শেষ + grace + usage complete না হলে Draft হয় না |
| Finalize শুধু Draft থেকে; usage complete ও `expectedTotal` মিলতে হবে |
| Finalized চূড়ান্ত |
| Finalize হলে সব Statement একসাথে Finalized |

#### Statement (+ StatementLine)
| Invariant |
|---|
| প্রতি FI, প্রতি period-এ একটাই; শুধু Billable FI-র জন্য |
| `total = Σ line.amount`; Usage line `amount = round(quantity × unitRate, 2)` |
| Rate snapshot রাখে — পরে rate card বদলালেও প্রভাব নেই |
| Draft যেকোনো সময় ফেলে নতুন করে তৈরি করা যায়; Finalized কখনো বদলায় না |

#### Adjustment
```
Pending ──(যুক্ত statement Finalize)──► Applied
```
| Invariant |
|---|
| `reason`, `createdBy` বাধ্যতামূলক; অঙ্ক ≠ 0 |
| ঠিক একটা Statement-এ Applied; তারপর আর নড়ে না |
| বদলানো যায় না — ভুল হলে উল্টো অঙ্কের নতুন Adjustment |

### ১৪.৪ Value objects

| Value object | অর্থ |
|---|---|
| **Money** | BDT, ২ দশমিক, signed |
| **UnitRate** | BDT প্রতি unit, ৪ দশমিক, ≥ 0 |
| **Period** | `YYYY-MM`, Asia/Dhaka; UTC সীমা দেয় |
| **MeteredOperation** | `GENERATION_STATIC` \| `GENERATION_DYNAMIC` \| `VALIDATION` |

### ১৪.৫ Policies

| Policy | মালিক | নিয়ম |
|---|---|---|
| **Billability** | Metering | §২.২ |
| **Attribution** | Metering | event-এর `TenantId` (requesting tenant) |
| **Rating** | Billing | `round(count × unitRate, 2)`, AwayFromZero, শুধু line-এ |

### ১৪.৬ Domain events

| Event | কোথায় যায় |
|---|---|
| `qr-generation.qr-generated.v1`, `verification.qr-validated.v1` | Outbox → Metering (integration event) |
| `billing.rate_card.added`, `billing.adjustment.recorded`, `billing.period.drafted`, `billing.period.finalized` | শুধু `audit_logs` (কোনো subscriber নেই) |

---

## ১৫. Open concern, ভবিষ্যৎ scope ও HoE-এর কাছে প্রশ্ন

### ১৫.১ Open concern

| # | Concern | ঝুঁকি | প্রস্তাব |
|---|---|---|---|
| C1 | **"requestId" শব্দ** — glossary "request id" Correlation ID-র জন্য এড়াতে বলে; অথচ validation-এর dedup key `requestId`, generation-এ `Idempotency-Key`। | বিভ্রান্তি | Domain term **Idempotency key** (দুটোর জন্য); API field নাম অপরিবর্তিত |
| C2 | **মাসের মাঝে onboarding** — প্রথম আংশিক মাস বিনামূল্যে যায়। | Revenue leakage | FI-র **প্রথম** rate card চলতি open মাসের ১ তারিখ থেকে দেওয়া যাবে |
| C3 | **Platform-এর দোষে verdict** — trust store sync পিছিয়ে থাকায় `KEY_NOT_FOUND` billable। | Dispute | নিয়ম অপরিবর্তিত; dispute হলে Adjustment |
| C4 | **Offboarded FI-র Adjustment** চিরকাল Pending। | পাওনা হারায় | Pending Adjustment থাকলে usage ছাড়াই Statement; platform report-এ তালিকা |
| C5 | **আসল user identity নেই** — `finalizedBy` / `createdBy` free-text। | জবাবদিহি দুর্বল | v1-এ audit log; পরে user login + four-eyes |
| C6 | **দুটো প্রায় একই repo** — `rvl-sbqr-api` ও `rvl-secure-bqr-manager`। | ভুল repo-তে কাজ, drift | Canonical repo স্পষ্ট করা |
| C7 | **mTLS certificate tenant-এর সাথে বাঁধা নয়।** | Credential ফাঁসে অন্যের usage এই FI-র নামে | Certificate ↔ tenant binding (security backlog) |
| C8 | **`REQUEST_STALE` কোন পথে আসে অস্পষ্ট** (400 নাকি verdict)। | Metering-এ অসংগতি | Implementation-এ যাচাই; 400 হলে row/event নেই, verdict হলে `billable = false` |
| C9 | **Gateway-এর derived idempotency key** — একই সময়-খণ্ডে হুবহু দুটো বৈধ request → দ্বিতীয়টা 409। | ছোট leakage | Key বাধ্যতামূলক হলে derivation সরিয়ে FI-র key পাঠানো |
| C10 | **Degraded health কেউ না দেখলে অর্থহীন।** | Dead letter অজানা থাকে | বিদ্যমান monitoring-এ health check যুক্ত করা |
| C11 | **Admin route prefix placeholder।** | ছোট | Implementation-এর শুরুতে মেলানো |
| C12 | **Outbox QR-এর critical path-এ** — প্রতিটি QR transaction-এ একটা অতিরিক্ত INSERT; outbox insert ব্যর্থ হলে QR-ও ব্যর্থ। | সামান্য latency; নতুন failure mode | গ্রহণযোগ্য (একই DB, একই transaction); load test-এ মাপা |
| C13 | **Domain event raise হয় কিন্তু dispatch হয় না** (Tenancy, KeyCustody, IdentityAccess)। | বিদ্যমান dead code / ভুল ধারণা | Outbox আসার পর আলাদা কাজ হিসেবে দেখা; billing-এর scope নয় |
| C14 | **Client-side pre-validation** — FI-র SDK / app / BFF যদি QR payload-এর গঠন (TLV) ও CRC (`63`) নিজেই যাচাই করে, তাহলে যেগুলো `STRUCTURAL_INVALID` হতো সেগুলো platform-এ আসবেই না। | এখন: platform-এর CPU, DB row, outbox event ও bandwidth অকারণে খরচ হয়; FI-ও অকারণে টাকা দেয়। করলে: SDK-তে logic দুই জায়গায় থাকে, SDK version-এ ভিন্নতা (drift) আসে; BFF "1:1 reverse proxy" নীতির সাথে সাংঘর্ষিক হতে পারে। | Platform-এর যাচাই **সবসময় থাকবে** (কখনো client-কে বিশ্বাস করা নয়); শুধু একটা **ঐচ্ছিক, হালকা pre-check** (TLV parse + CRC) SDK/app-এ — signature verify নয়। Billing নিয়ম অপরিবর্তিত: platform-এ যা আসবে তা নিয়ম অনুযায়ী বিল হবে। HoE-এর সিদ্ধান্ত লাগবে (প্রশ্ন ৩)। |

| C15 | **Static ও Dynamic generation-এর রেট একই, কিন্তু meter ও report-এ আলাদা** — এটা এখনো HoE ও Product team confirm করেনি। দুটো line আলাদা rounding পায়, তাই §৩-এর যৌথ সূত্রের সাথে কখনো ১ পয়সা পার্থক্য হতে পারে। | ভুল pricing ধারণায় build; FI-র কাছে বিভ্রান্তিকর statement | HoE + Product-এর সাথে confirm (প্রশ্ন ১৬); confirm হলে §৩-এর সূত্র প্রতি meter-এ আলাদা line হিসেবে লেখা |

**সমাধান হয়ে গেছে (v1.1):** module boundary পেরিয়ে read (পুরনো C6), upstream retention নির্ভরতা (পুরনো C7), দৈনিক recompute-এর scale (পুরনো C14)।

### ১৫.২ ভবিষ্যৎ scope

| # | প্রস্তাব | কখন |
|---|---|---|
| F1 | Tier / slab, monthly fee, included units (plan/subscription model — পুরনো design-এ তৈরি আছে) | Commercial model বদলালে |
| F2 | Static ও Dynamic-এর আলাদা রেট | Pricing আলাদা হলে (meter আগে থেকেই আলাদা) |
| F3 | মাসের মাঝে রেট পরিবর্তন | চুক্তিতে লাগলে |
| F4 | Credit carry-forward | Credit ঘন ঘন হলে |
| F5 | Invoice number, VAT, paid/void (পুরনো design-এ তৈরি আছে) | Finance চাইলে |
| F6 | **Admin Portal** — platform team-এর জন্য: rate card, adjustment, draft review ও finalize-এর UI; FI ও platform report দেখা (chart সহ); dead letter দেখা ও requeue। v1-এর admin API-র ওপরেই বসবে। | Admin Portal-এর roadmap হলে |
| F7 | **Customer Portal** — FI-দের self-service: নিজের provisional usage, statement ও report দেখা, PDF/CSV ও raw usage extract নামানো, dispute তোলা। এর জন্য FI-facing read-only usage API (tenant-scoped) লাগবে। | FI self-service চাইলে |
| F8 | **স্বয়ংক্রিয় বিল পাঠানো ও Notification system** — Finalize হলে প্রতিটি FI-র statement (PDF + CSV) নির্দিষ্ট billing contact-এর কাছে স্বয়ংক্রিয়ভাবে email-এ যাবে। অন্যান্য notification: FI-কে dispute window শেষ হওয়ার reminder আর Adjustment যুক্ত হওয়ার খবর; platform team-কে Draft review-এর জন্য তৈরি, dead letter আর finalize দেরি হওয়ার alert। নকশা: Billing `billing.period.finalized` event outbox-এ প্রকাশ করবে, আর একটা আলাদা Notification module সেটা শুনে পাঠাবে। এতে Billing email-এর কিছু জানবে না, আর পাঠানো ব্যর্থ হলে retry হবে। প্রতিটি পাঠানোর রেকর্ড রাখা হবে (কাকে, কখন, কোন statement, delivered কিনা), যাতে dispute-এর সময় প্রমাণ থাকে। FI-র billing contact Tenancy-তে রাখতে হবে। | হাতে পাঠানো ঝামেলা হলে বা FI সংখ্যা বাড়লে |
| F9 | User-ভিত্তিক login + four-eyes finalize | C5 সমাধানে |
| F10 | Message broker (dispatcher → broker publish) | Metering আলাদা service হলে |
| F11 | `usage_events` monthly partition বা daily rollup | Report ধীর হলে |
| F12 | ~~ADR ০০০১~~ — **লেখা হয়েছে:** `rvl-secure-bqr-manager/docs/adr/0001-outbox-between-modules.md` | ✅ |
| F13 | ~~ADR ০০০২~~ — **লেখা হয়েছে:** `rvl-secure-bqr-manager/docs/adr/0002-bill-conclusive-verdicts-to-verifying-fi.md` | ✅ |
| F14 | Client-side pre-validation SDK / reference library (TLV + CRC) | C14-এ HoE হ্যাঁ বললে |
| F15 | **Subscription type ও সবার জন্য একটা Price list** — এখনকার ব্যবসায়িক ধারণা: সব FI-র জন্য প্রতি unit একই দাম, আর প্রতি FI তিনটের একটা subscription type নেয় — `GENERATION_ONLY`, `VALIDATION_ONLY` বা `GENERATION_AND_VALIDATION`। নকশা: (ক) Subscription type-এর জন্য নতুন টেবিল নয় — Tenancy-র বিদ্যমান `is_qr_generation_allowed` / `is_qr_validation_allowed` flag-দুটোই subscription type (access ও token scope এখনই এগুলো দিয়ে চলে); Billing দরকার হলে Tenancy-র contract query দিয়ে জানবে, টেবিল পড়বে না। (খ) প্রতি FI-র rate card-এর বদলে platform-জুড়ে একটা **Price list** (দুটো দাম + কবে থেকে কার্যকর; মাসের ১ তারিখ, append-only), আর প্রতি FI-তে শুধু **Billing account** (কোন মাস থেকে বিল শুরু; না থাকলে non-billable, যেমন pilot/UAT)। (গ) মাসের মাঝে subscription type বদলালে আলাদা হিসাব লাগে না — per-unit দামে যা ব্যবহার, তাই বিল। (ঘ) Statement ও report-এ FI-র subscription type দেখানো, আর শুধু নেওয়া service-এর line। (ঙ) পরে কোনো FI-র আলাদা দাম লাগলে FI-ভিত্তিক override যোগ করা যাবে; subscription type-ভিত্তিক monthly fee (F1) এর ওপরেই বসবে। বদলাবে: §৩, §৪.৮ `012_billing.sql`, §৬, §৭, §১৪; CONTEXT.md glossary ("Rate card" → "Price list", "Subscription type", "Billing account"); ADR 0003। | HoE + Product প্রশ্ন ১৭-এ হ্যাঁ বললে |

### ১৫.৩ HoE-এর কাছে প্রশ্ন

**Governance**
1. Canonical repo কোনটা — `rvl-secure-bqr-manager` না `rvl-sbqr-api`? (C6)

**Commercial**
2. Invalid verdict (`INVALID_SIGNATURE`, `KEY_REVOKED` ইত্যাদি)-এর জন্যও চার্জ করা কি FI-দের কাছে গ্রহণযোগ্য, এবং চুক্তিতে থাকবে?
3. **Client-side pre-validation:** FI-র SDK / app / BFF কি QR-এর গঠন (TLV) ও CRC আগে নিজে যাচাই করবে, যাতে ভাঙা QR platform-এ পাঠানোই না হয়? এতে platform-এর resource বাঁচে, আর FI-ও `STRUCTURAL_INVALID`-এর জন্য অকারণে টাকা দেয় না। তবে এতে logic দুই জায়গায় থাকে, আর SDK version-এর ভিন্নতা সামলাতে হয়। আমাদের কি এমন SDK/reference library দেওয়ার পরিকল্পনা আছে, নাকি এটা FI-র integration guide-এ সুপারিশ হিসেবে থাকবে? (C14)
4. রেট কে ঠিক করবেন — প্রতি FI আলাদা, নাকি সবার জন্য একই? (এখনকার ধারণা: সবার জন্য একই — প্রশ্ন ১৭, F15)
5. মাসের মাঝে onboard হওয়া FI-র প্রথম আংশিক মাস বিল হবে? (C2)
6. Platform-এর দোষে আসা verdict-এর জন্য SLA credit নীতি লাগবে? (C3)
7. ৩০ দিনের dispute window কি চুক্তির সাথে মেলে?
8. সব FI কি BDT-তেই বিল হবে?

**Regulatory ও Finance**
9. QR validation/generation-এ fee নেওয়া নিয়ে Bangladesh Bank-এর কোনো নিয়ম বা fee cap আছে?
10. Billing / usage data-র retention নিয়ে regulatory নির্দেশনা আছে (১৩ মাসের বেশি)?
11. Platform fee কি VAT-যোগ্য? Finance system integration-এর timeline? (F5)

**Operations ও Security**
12. কে finalize ও Adjustment করতে পারবেন? v1-এ free-text নাম গ্রহণযোগ্য? (C5)
13. Admin Portal ও Customer Portal-এর roadmap আছে? (F6, F7)
14. mTLS certificate ↔ tenant binding কি অগ্রাধিকার পাবে? (C7)
15. Health check-এর Degraded অবস্থা কোন monitoring দেখবে, কাকে জানাবে? (C10)

**Product (HoE + Product team একসাথে)**
16. Static ও Dynamic QR generation-এর রেট কি একই থাকবে? দুটো কি আলাদা meter হিসেবে গোনা হবে, আর FI statement ও report-এ আলাদা line হিসেবে দেখানো হবে? (C15, F2)
17. **Subscription type ও সবার জন্য একই দাম:** এখনকার ধারণা — সব FI-র জন্য প্রতি unit একই দাম, আর প্রতি FI তিনটের একটা subscription type নেবে: শুধু QR generation, শুধু QR validation, বা দুটোই। (ক) তিনটে type-ই কি সত্যিই বিক্রি হবে — বিশেষ করে "শুধু validation"-এর বাস্তব চাহিদা আছে? (খ) Subscription type বদলানো কি চুক্তির বিষয় (Product / Finance-এর অনুমোদন লাগবে), নাকি admin নিজেই বদলাতে পারবে? (গ) "সবার জন্য একই দাম" কি দীর্ঘমেয়াদি, নাকি ভবিষ্যতে কোনো FI-র আলাদা দাম হতে পারে? (প্রশ্ন ৪, F15)

---

## পরিশিষ্ট: সিদ্ধান্তের তালিকা

| # | সিদ্ধান্ত |
|---|---|
| Q1 | Validation: Conclusive verdict দিলেই billable |
| Q2 | Validation-এর বিল verifying FI-র নামে |
| Q3 | প্রতি FI flat per-unit rate, gen ও validation আলাদা, effective date সহ |
| Q4 | Calendar month, Asia/Dhaka; finalize-এর পর immutable |
| Q5 | শুধু statement + টাকা; invoice/VAT/payment finance-এর |
| Q6 / Q14 | Duplicate একবার; idempotency key বাধ্যতামূলক |
| Q7 | BDT, decimal, line-এ AwayFromZero rounding |
| Q8 / Q9 | FI ও platform report-এর বিষয়বস্তু (§৭) |
| Q10 | Auto draft → নাম সহ manual finalize |
| Q11 | Adjustment, ৩০ দিনের dispute window, raw extract প্রমাণ |
| Q12 / Q24 | Billing স্থায়ী; usage ≥ ১৩ মাস (**v1.1:** Metering-এর মালিকানায়) |
| Q13 / Q-B | Billable verdict তালিকা (§২.২); `STRUCTURAL_INVALID` ও `NON_P2P` billable — নিশ্চিত |
| Q15 | প্রতি FI আলাদা gateway |
| Q16 | Static = Dynamic রেট |
| Q17 | Rate card নেই = non-billable |
| Q18 | ~~Billing module + daily job + সরাসরি read~~ → **v1.1:** Outbox + Metering + Billing module |
| Q19 / Q22 | শুধু admin API; FI API নেই |
| Q20 | রেট শুধু ভবিষ্যৎ মাসের ১ তারিখ থেকে |
| Q21 / Q27 | JSON / CSV / print-friendly HTML → browser PDF |
| Q23 | ~~Open মাস recompute, advisory lock~~ → **v1.1:** outbox lease + completeness gate |
| Q25 / Q30 | Notification নেই; health check Degraded (**v1.1:** outbox + billing check) |
| Q26 | ঋণাত্মক total দেখানো হবে |
| Q28 | Rate card, adjustment, finalize — admin API |
| Q29 | Finalized statement DB-তে সংরক্ষিত, সেখান থেকে render |
| R1 | **v1.1:** Finalize-এ `expectedTotal` check — যা review হয়েছে তাই finalize |
| R2 | **v1.1:** Metering non-billable verdict-ও `billable = false` দিয়ে রাখে |
