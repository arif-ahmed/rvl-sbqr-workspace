# Metering ও Billing — v1 Design (Minimal, Production-grade)

- **তারিখ:** 2026-09-30
- **অবস্থা:** Design চূড়ান্ত (grilling session-এ সিদ্ধান্ত নেওয়া), implementation বাকি
- **Target repo:** `rvl-secure-bqr-manager` (branch `feature/mtls-server`) — নতুন **Billing module**
- **মূলনীতি:** হিসাব নির্ভুল ও audit-যোগ্য; accounting system নয়। যা দরকার নেই তা v1-এ নেই।

---

## ১. Scope

FI-দের বিল করা হবে **শুধু দুটো জিনিসের জন্য**, প্রতি successful request হিসেবে:

1. QR generation (static + dynamic)
2. QR validation

সিস্টেম মাস শেষে প্রতিটি FI-র **usage statement + হিসাব করা টাকা** দেবে। Invoice, VAT, payment collection finance team নিজেদের system-এ করবে।

দুটো report:
- **FI report** — FI-র নিজের usage ও টাকা (platform team তৈরি করে FI-কে পাঠাবে)
- **Platform report** — সব FI মিলিয়ে volume, revenue, trend

কোনো portal নেই — সব কিছু **admin API** (scope `admin`) দিয়ে। ভবিষ্যতে portal বানালে একই API ব্যবহার করবে।

---

## ২. Billable নিয়ম

### ২.১ Generation

| পরিস্থিতি | Billable? |
|---|---|
| 201 — QR তৈরি হয়েছে (`qr_generations`-এ row) | ✅ |
| 409 `DUPLICATE_IDEMPOTENCY_KEY` | ❌ |
| 400 / 401 / 403 / 422 / 500 | ❌ |

Static ও Dynamic-এর **রেট একই**; report-এ সংখ্যা আলাদা দেখানো হবে।

### ২.২ Validation

নিয়ম: platform কাজটা সম্পূর্ণ করে একটা নির্দিষ্ট verdict দিলে billable — ফলাফল valid হোক বা invalid। Invalid QR ধরাও service।

| Verdict | Billable? |
|---|---|
| `VALID` (historical key সহ) | ✅ |
| `INVALID_SIGNATURE` | ✅ |
| `STRUCTURAL_INVALID` | ✅ |
| `KEY_NOT_FOUND` | ✅ |
| `KEY_SUSPENDED` | ✅ |
| `KEY_REVOKED` | ✅ |
| `KEY_NOT_ACTIVE` | ✅ |
| `NON_P2P` | ✅ |
| `REQUEST_STALE` | ❌ (FI-র protocol ভুল) |
| `REQUEST_REPLAYED` | ❌ (duplicate) |
| 400 / 401 / 403 / 5xx | ❌ |

### ২.৩ কাকে বিল করা হবে

- **Generation:** যে tenant QR তৈরি করেছে (`qr_generations.tenant_id`)।
- **Validation:** **যে FI validate call করেছে** (`qr_validations.tenant_id`), QR-এর issuer নয়।
- FI পরিচয় = JWT claim `tenant_id`। প্রতিটি FI-র জন্য **আলাদা fi-gateway deployment** (নিজস্ব `ClientId`), তাই attribution নির্ভুল।

### ২.৪ Duplicate / retry

একই request একবারই গোনা হবে। এর জন্য **idempotency key বাধ্যতামূলক** (এখনো কেউ integrate করেনি, তাই breaking নয়):

- Generation: `Idempotency-Key` header **required** — না থাকলে 400। (Gateway-এর derived key fallback হিসেবে থাকবে।)
- Validation: body-র `requestId` **required** — না থাকলে 400। Server-side requestId generate করা বন্ধ।
- বিদ্যমান UNIQUE constraint-ই dedup নিশ্চিত করে: `(tenant_id, idempotency_key)` ও `(tenant_id, request_id)`।

প্রয়োজনীয় code পরিবর্তন:
- `src/Modules/QrGeneration/SBQR.Modules.QrGeneration.Api/Controllers/QrGenerationController.cs` — static ও dynamic দুই endpoint-এ key required।
- `src/Modules/Verification/SBQR.Modules.Verification.Api/Controllers/QrValidationController.cs` — requestId auto-generate সরানো; validator-এ required।

### ২.৫ Billable tenant

**যে tenant-এর ওই মাসে active rate card আছে, শুধু সে billable।** Rate card না থাকলে (test, emulator, internal tenant) statement হবে না, তবে platform report-এ "non-billable tenant" হিসেবে usage দেখাবে। আলাদা flag লাগবে না।

---

## ৩. Pricing ও হিসাব

- প্রতি FI একটা **rate card**: `generation_rate`, `validation_rate` (BDT প্রতি unit)।
- Tier / slab / minimum commitment **নেই**।
- রেট শুধু **কোনো মাসের ১ তারিখ থেকে** কার্যকর; শুধু **ভবিষ্যৎ মাসের** জন্য নতুন রেট যোগ করা যায়। চলতি বা অতীত মাসের রেট বদলানো যায় না → প্রতিটি statement-এ একটাই রেট।
- **Billing period:** calendar month, **Asia/Dhaka (UTC+6)**। Usage-এর তারিখ = `(created_at AT TIME ZONE 'Asia/Dhaka')::date`।
- **টাকা:** সব `numeric`/`decimal` — কখনো `float` নয়। Rate `numeric(18,4)`, amount `numeric(18,2)`।
- **Rounding:** শুধু line total-এ একবার, ২ দশমিক, half-up (away from zero)।

```
generation_amount  = round(generation_count  × generation_rate, 2)
validation_amount  = round(validation_count  × validation_rate, 2)
adjustments_total  = Σ adjustments (এই statement-এ প্রযোজ্য)
total              = generation_amount + validation_amount + adjustments_total
```

`total` ঋণাত্মক হতে পারে (credit) — যেমন আছে তেমনই দেখানো হবে; finance সমন্বয় করবে। Credit carry-forward logic v1-এ নেই।

---

## ৪. Architecture

### ৪.১ মূল ধারণা: নতুন capture লাগবে না

Usage ledger **ইতিমধ্যে DB-তে আছে**, দুটোই append-only:

| Source table | কী থাকে |
|---|---|
| `qr_generations` | প্রতি সফল generation-এ এক row (`tenant_id`, `qr_type`, `created_at`, …) |
| `qr_validations` | প্রতি processed validation-এ এক row (`tenant_id`, `verdict`, `request_id`, `created_at`, …) |

Billing module এই টেবিলগুলো **শুধু read** করবে (read-only SQL)। Request path-এ কোনো পরিবর্তন বা latency যোগ হবে না।

> এটা module boundary-র বাইরে read — ইচ্ছাকৃত ও documented dependency। Billing কখনো এই টেবিলে write বা delete করবে না।

### ৪.২ Flow

```
qr_generations ─┐
                ├─► [Daily Aggregation Job] ─► usage_daily ─► [Draft Statement Builder] ─► billing_statements (DRAFT)
qr_validations ─┘        (০০:৩০ Dhaka)                              (মাসের ১ তারিখ থেকে)             │
                                                                                                  ▼
                                                        POST .../periods/{period}/finalize ─► FINALIZED (immutable)
```

### ৪.৩ Daily Aggregation Job

- সাধারণ `BackgroundService` (নতুন framework নয়)। প্রতিদিন **০০:৩০ Asia/Dhaka**-তে চলবে, এবং API start হলে একবার।
- প্রতিবার **সব non-finalized মাসের সব দিন** নতুন করে হিসাব করে `usage_daily`-তে **upsert** করবে (idempotent)। কোনোদিন job miss হলে বা server down থাকলে পরের run নিজেই ঠিক করে দেবে।
- Finalized মাস কখনো ছোঁয়া হবে না।
- একাধিক API instance থাকলে **Postgres advisory lock** (`pg_try_advisory_lock`) — lock না পেলে ওই run skip।
- শেষে `billing_job_state.last_aggregated_at` update।
- Job শেষে: আগের মাস যদি এখনো finalized না হয়, তার সব billable tenant-এর **DRAFT statement নতুন করে তৈরি** (পুরনো draft replace)।

### ৪.৪ Data model — `db/migrations/010_billing.sql`

হাতে লেখা, idempotent SQL (বিদ্যমান convention অনুযায়ী)।

```sql
-- রেট কার্ড: প্রতি tenant, মাসের ১ তারিখ থেকে কার্যকর
CREATE TABLE IF NOT EXISTS billing_rate_cards (
    rate_card_id     uuid PRIMARY KEY,
    tenant_id        uuid          NOT NULL REFERENCES tenants(tenant_id),
    effective_from   date          NOT NULL CHECK (EXTRACT(DAY FROM effective_from) = 1),
    generation_rate  numeric(18,4) NOT NULL CHECK (generation_rate >= 0),
    validation_rate  numeric(18,4) NOT NULL CHECK (validation_rate >= 0),
    currency         char(3)       NOT NULL DEFAULT 'BDT',
    created_by       text          NOT NULL,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, effective_from)
);

-- দৈনিক aggregate (Dhaka date অনুযায়ী)
CREATE TABLE IF NOT EXISTS usage_daily (
    tenant_id   uuid        NOT NULL,
    usage_date  date        NOT NULL,
    metric      text        NOT NULL,   -- যেমন 'generation.STATIC', 'generation.DYNAMIC', 'validation.VALID', 'validation.REQUEST_STALE' …
    count       bigint      NOT NULL CHECK (count >= 0),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, usage_date, metric)
);

-- মাসের অবস্থা
CREATE TABLE IF NOT EXISTS billing_periods (
    period        char(7)     PRIMARY KEY,     -- 'YYYY-MM'
    status        text        NOT NULL CHECK (status IN ('OPEN','DRAFT','FINALIZED')),
    finalized_by  text,
    finalized_at  timestamptz
);

-- Statement (finalize-এর পর immutable)
CREATE TABLE IF NOT EXISTS billing_statements (
    statement_id       uuid PRIMARY KEY,
    tenant_id          uuid          NOT NULL,
    period             char(7)       NOT NULL REFERENCES billing_periods(period),
    status             text          NOT NULL CHECK (status IN ('DRAFT','FINALIZED')),
    generation_count   bigint        NOT NULL,
    validation_count   bigint        NOT NULL,
    generation_rate    numeric(18,4) NOT NULL,
    validation_rate    numeric(18,4) NOT NULL,
    adjustments_total  numeric(18,2) NOT NULL,
    total              numeric(18,2) NOT NULL,
    currency           char(3)       NOT NULL DEFAULT 'BDT',
    generated_at       timestamptz   NOT NULL,
    UNIQUE (tenant_id, period)
);

CREATE TABLE IF NOT EXISTS billing_statement_lines (
    statement_id  uuid          NOT NULL REFERENCES billing_statements(statement_id),
    line_no       int           NOT NULL,
    kind          text          NOT NULL CHECK (kind IN ('USAGE','ADJUSTMENT')),
    description   text          NOT NULL,
    quantity      bigint,
    unit_rate     numeric(18,4),
    amount        numeric(18,2) NOT NULL,
    PRIMARY KEY (statement_id, line_no)
);

-- সংশোধন: পরের draft statement-এ আলাদা line হয়ে যায়
CREATE TABLE IF NOT EXISTS billing_adjustments (
    adjustment_id         uuid PRIMARY KEY,
    tenant_id             uuid          NOT NULL,
    amount                numeric(18,2) NOT NULL,   -- ঋণাত্মক = credit
    reason                text          NOT NULL,
    created_by            text          NOT NULL,
    created_at            timestamptz   NOT NULL DEFAULT now(),
    applied_statement_id  uuid REFERENCES billing_statements(statement_id)
);

CREATE TABLE IF NOT EXISTS billing_job_state (
    id                  int PRIMARY KEY CHECK (id = 1),
    last_aggregated_at  timestamptz
);
```

Finalized statement ও তার lines-এ UPDATE/DELETE একটা trigger দিয়ে block করা হবে (বিদ্যমান `audit_logs` trigger-এর মতো)।

---

## ৫. মাস close ও সংশোধন

1. **১ তারিখ** — Job আগের মাসের DRAFT statement তৈরি করে। Period status `DRAFT`।
2. যতক্ষণ finalize হয়নি, প্রতিদিনের job draft নতুন করে তৈরি করে (late data থাকলে ধরা পড়ে)।
3. **Finalize** — platform team `POST .../periods/{period}/finalize` call করে, body-তে বাধ্যতামূলক `finalizedBy` (নাম)। এক transaction-এ:
   - সব DRAFT statement → `FINALIZED`; lines ও total স্থায়ীভাবে সংরক্ষিত
   - প্রযোজ্য adjustment-গুলোর `applied_statement_id` set
   - Period → `FINALIZED`
   - `audit_logs`-এ `billing.period.finalized` event
4. **Finalize-এর পর কিছুই বদলায় না।** যেকোনো সংশোধন → `billing_adjustments`-এ entry (পরিমাণ, কারণ, `createdBy`) → পরের মাসের statement-এ `ADJUSTMENT` line।
5. **Dispute window:** finalize-এর পর **৩০ দিন**। প্রমাণ হিসেবে ওই মাসের **raw usage CSV** দেওয়া হবে।

> **Known limitation:** Admin API-তে এখন একটাই platform credential, আলাদা user identity নেই। তাই `finalizedBy` / `createdBy` হলো free-text নাম + audit log। User-ভিত্তিক login পরবর্তী কাজ।

---

## ৬. Admin API

সব endpoint-এ scope `admin` লাগবে (বিদ্যমান admin route convention অনুযায়ী prefix)। FI-দের জন্য কোনো usage API **নেই** — FI report platform থেকে তৈরি হয়ে পাঠানো হবে।

| Method | Path | কাজ |
|---|---|---|
| POST | `/v1/admin/billing/rate-cards` | নতুন রেট (শুধু ভবিষ্যৎ মাসের ১ তারিখ) |
| GET | `/v1/admin/billing/rate-cards?tenantId=` | রেট ইতিহাস |
| POST | `/v1/admin/billing/adjustments` | সংশোধন entry (`amount`, `reason`, `createdBy`) |
| GET | `/v1/admin/billing/periods` | সব মাস ও তার status |
| POST | `/v1/admin/billing/periods/{period}/finalize` | মাস finalize (`finalizedBy`) |
| GET | `/v1/admin/billing/statements/{tenantId}/{period}?format=json\|csv\|html` | FI statement |
| GET | `/v1/admin/billing/usage/{tenantId}/{period}/raw.csv` | Raw usage (dispute প্রমাণ) |
| GET | `/v1/admin/billing/reports/platform/{period}?format=json\|csv\|html` | Platform report |

Open (চলতি) মাসের জন্য statement endpoint **provisional** সংখ্যা দেয়: `usage_daily` + আজকের দিনের live count (source table থেকে সরাসরি)। Response-এ স্পষ্ট `"status": "PROVISIONAL"`।

HTML format = server-rendered, JS-ছাড়া, print-friendly পেজ। Browser-এ খুলে **"Save as PDF"** করে FI-কে পাঠানো হবে। কোনো PDF library লাগবে না।

---

## ৭. Report-এর বিষয়বস্তু

শুধু সংখ্যা ও টেবিল — v1-এ chart নেই।

### ৭.১ FI report (প্রতি FI, প্রতি মাস)

1. মাসের মোট: billable generation (static / dynamic আলাদা), billable validation, টাকা, status (PROVISIONAL / DRAFT / FINALIZED)
2. দৈনিক টেবিল (তারিখ × generation × validation)
3. Validation verdict অনুযায়ী ভাগ (VALID, INVALID_SIGNATURE, …) — কোনগুলো billable চিহ্নিত
4. Adjustment line (থাকলে)
5. আগের মাসগুলোর finalized statement-এর তালিকা

### ৭.২ Platform report (প্রতি মাস)

1. সব FI মিলিয়ে মোট volume ও revenue, **FI অনুযায়ী ভাগ** (বড় থেকে ছোট)
2. আগের মাসের তুলনায় পরিবর্তন (%) — প্রতি FI ও মোট
3. দৈনিক মোট টেবিল
4. Period status — কোন মাস OPEN / DRAFT / FINALIZED
5. Non-billable tenant-দের usage (rate card নেই এমন)

### ৭.৩ Raw usage CSV

কলাম: `created_at (Dhaka)`, `type` (GENERATION/VALIDATION), `idempotency_key` বা `request_id`, `qr_type` বা `verdict`, `billable` (true/false)। Recipient নাম/PAN বা অন্য ব্যক্তিগত তথ্য **থাকবে না**।

সব report-এ `lastAggregatedAt` থাকবে।

---

## ৮. Monitoring

আলাদা email/alert system নেই। API health check-এ নতুন **`billing` check**:

| শর্ত | ফল |
|---|---|
| `last_aggregated_at` ২৬ ঘণ্টার বেশি পুরনো | **Degraded** |
| আগের মাস ৫ তারিখের পরেও FINALIZED নয় | **Degraded** |
| অন্যথায় | Healthy |

Degraded হলে warning log যাবে।

---

## ৯. Data retention

- `usage_daily`, `billing_statements`, lines, adjustments — **স্থায়ী** (বা regulatory নিয়ম অনুযায়ী)।
- Raw usage CSV অন্তত **১৩ মাস** পাওয়া যাবে।
- Billing module source table (`qr_generations`, `qr_validations`) থেকে **কিছু মুছবে না**। ওগুলোর retention ওই module-এর দায়িত্ব, তবে ১৩ মাসের কম নয়।

---

## ১০. Security নোট

- সব billing endpoint শুধু `admin` scope — FI credential দিয়ে access অসম্ভব।
- HTML output-এ সব dynamic মান (tenant নাম, reason ইত্যাদি) HTML-encode করা হবে।
- CSV export-এ formula injection থেকে রক্ষা: `=`, `+`, `-`, `@` দিয়ে শুরু হওয়া text cell-এ `'` prefix।
- `reason`, `createdBy`, `finalizedBy` — length limit ও validation।
- সব টাকা-সংক্রান্ত write (`rate-card`, `adjustment`, `finalize`) → `audit_logs` entry।

---

## ১১. Acceptance criteria

1. একই `Idempotency-Key` / `requestId` দিয়ে দুবার request → usage-এ ১ বার গোনা।
2. Key ছাড়া generation বা validation request → 400।
3. `REQUEST_STALE` / `REQUEST_REPLAYED` / 4xx / 5xx → billable count-এ আসে না।
4. FI-A-র QR FI-B validate করলে → validation FI-B-র statement-এ।
5. রাত ২৩:৫৯ (Dhaka) ও ০০:০১-এর request আলাদা দিনে ও (মাস শেষে) আলাদা মাসে পড়ে।
6. Job দুবার চালালে বা দুই instance একসাথে চালালে সংখ্যা একই থাকে।
7. একদিন job না চললে পরের run-এ সংখ্যা ঠিক হয়ে যায়।
8. Rate card ছাড়া tenant-এর statement হয় না, কিন্তু platform report-এ দেখায়।
9. Finalize-এর পর statement ও lines UPDATE/DELETE করা যায় না (DB trigger)।
10. Finalize-এর পর যোগ করা adjustment পরের মাসের statement-এ line হিসেবে আসে; total ঋণাত্মক হলে সেটাই দেখায়।
11. চলতি বা অতীত মাসের রেট যোগ করতে চাইলে → 400।
12. Health check ২৬ ঘণ্টা stale বা ৫ তারিখের পর unfinalized হলে Degraded।
13. FI credential দিয়ে যেকোনো billing endpoint → 403।
14. একই statement বারবার download করলে একই সংখ্যা (JSON = CSV = HTML)।

---

## ১২. v1-এ ইচ্ছাকৃতভাবে যা নেই

- Tier / slab / minimum commitment / subscription
- Invoice number, VAT, due date, payment tracking
- FI portal, admin portal, FI-facing usage API
- Chart / graph
- মাসের মাঝে রেট পরিবর্তন
- Credit carry-forward logic
- Email / notification
- User-ভিত্তিক login ও সত্যিকারের maker-checker

---

## ১৩. Domain model

শব্দের সংজ্ঞা: `rvl-secure-bqr-manager/CONTEXT.md` → **Billing** অংশ। এখানে সেই শব্দগুলো দিয়ে model।

### ১৩.১ Context map

```
                 ┌──────────────┐
                 │   Tenancy    │  FI ও Tenant পরিচয় (upstream)
                 └──────┬───────┘
                        │ tenant_id (শুধু reference)
┌──────────────┐        ▼        ┌──────────────┐
│ QrGeneration │──► ┌─────────┐ ◄──│ Verification │
│  (upstream)  │    │ Billing │    │  (upstream)  │
└──────────────┘    └────┬────┘    └──────────────┘
   read-only usage       │  read-only usage
                         ▼
                 ┌──────────────┐        ┌───────────────────┐
                 │    Audit     │        │ Finance (বাইরের) │ ◄── Statement (হাতে পাঠানো)
                 └──────────────┘        └───────────────────┘
```

- **QrGeneration / Verification → Billing:** Billing **conformist** — upstream-এর record যেমন আছে তেমনই পড়ে, কিছু বদলাতে বলে না। Upstream Billing-এর অস্তিত্বই জানে না।
- **Tenancy → Billing:** শুধু `tenant_id` দিয়ে reference; FI-র তথ্য Billing কপি করে না।
- **Billing → Audit:** টাকা-সংক্রান্ত প্রতিটি সিদ্ধান্ত audit-এ লেখা হয়।
- **Billing → Finance:** সিস্টেমের বাইরে; Statement হাতে যায়।

### ১৩.২ Aggregates

#### RateCard
একটা FI-র একটা নির্দিষ্ট billing period থেকে কার্যকর দাম।

| Invariant |
|---|
| `effectiveFrom` সবসময় কোনো billing period-এর প্রথম দিন |
| শুধু ভবিষ্যৎ billing period-এর জন্য তৈরি করা যায় |
| তৈরির পর কখনো বদলায় না — নতুন দাম = নতুন RateCard |
| প্রতি FI, প্রতি billing period-এ সর্বোচ্চ একটা |
| দুটো দামই ≥ 0 |

**কোন period-এ কোন দাম:** period P-এর জন্য সেই RateCard, যার `effectiveFrom ≤ P` এবং সবচেয়ে সাম্প্রতিক। কোনোটা না থাকলে FI ওই period-এ billable নয়।

#### BillingPeriod
একটা calendar month (সব FI-র জন্য একটাই)।

```
Open ──(মাস শেষ)──► Draft ──(Finalize, finalizedBy)──► Finalized
```

| Invariant |
|---|
| মাস শেষ না হলে Draft হয় না |
| Finalize শুধু Draft থেকে; নাম (`finalizedBy`) বাধ্যতামূলক |
| Finalized চূড়ান্ত — আর কোনো অবস্থায় যায় না |
| Finalize হলে ওই period-এর সব Statement একসাথে Finalized |

#### Statement
একটা Billable FI-র একটা BillingPeriod-এর হিসাব। ভেতরে **StatementLine** (Usage line, Adjustment line)।

| Invariant |
|---|
| প্রতি FI, প্রতি BillingPeriod-এ একটাই |
| শুধু Billable FI-র জন্য |
| `total = Σ line.amount` |
| Usage line-এর `amount = round(quantity × unitRate, 2)` |
| Draft অবস্থায় যেকোনো সময় ফেলে দিয়ে নতুন করে তৈরি করা যায় |
| Finalized হলে কোনো line বা অঙ্ক কখনো বদলায় না |

#### Adjustment
একটা FI-র বিরুদ্ধে একটা signed অঙ্ক।

```
Pending ──(পরের Draft Statement-এ যুক্ত হয়ে সেটা Finalize)──► Applied
```

| Invariant |
|---|
| কারণ (`reason`) ও কে করেছে (`createdBy`) বাধ্যতামূলক |
| অঙ্ক শূন্য হতে পারে না |
| ঠিক একটা Statement-এ Applied হয়; তারপর আর নড়ে না |
| তৈরির পর বদলানো যায় না — ভুল হলে উল্টো অঙ্কের নতুন Adjustment |

### ১৩.৩ Value objects

| Value object | অর্থ |
|---|---|
| **Money** | BDT, ২ দশমিক, signed |
| **UnitRate** | BDT প্রতি unit, ৪ দশমিক, ≥ 0 |
| **Period** | `YYYY-MM`, Asia/Dhaka |
| **UsageDate** | Asia/Dhaka তারিখ |
| **MeteredOperation** | `Generation(Static \| Dynamic)` বা `Validation(verdict)` |

### ১৩.৪ Domain policies (stateless নিয়ম)

| Policy | প্রশ্ন | উত্তর |
|---|---|---|
| **Billability** | এই operation কি Billable usage? | Generation হলে হ্যাঁ; Validation হলে verdict Conclusive হলে হ্যাঁ, Protocol rejection হলে না |
| **Attribution** | Paying FI কে? | যে tenant operation-টা request করেছে |
| **Rating** | কত টাকা? | `round(count × unitRate, 2)`, half-up, শুধু line-এ |

### ১৩.৫ Read models (aggregate নয়)

- **UsageRecord** — upstream-এর একটা generation বা validation row। Billing-এর মালিকানা নেই, শুধু পড়ে।
- **DailyUsage** — `(FI, UsageDate, MeteredOperation) → count`। যেকোনো সময় UsageRecord থেকে আবার তৈরি করা যায়; তাই এটা হিসাবের "সত্য" নয়, সুবিধা মাত্র। সত্য হলো UsageRecord (open period) ও Finalized Statement (closed period)।

### ১৩.৬ Domain events (audit-এ লেখা হয়)

| Event | কখন |
|---|---|
| `billing.rate_card.added` | নতুন RateCard |
| `billing.adjustment.recorded` | নতুন Adjustment |
| `billing.period.drafted` | মাস শেষে Draft |
| `billing.period.finalized` | Finalize |

v1-এ এগুলো শুধু `audit_logs`-এ লেখা হয়; কোনো subscriber বা event bus নেই।

---

## ১৪. Open concern, ভবিষ্যৎ scope ও HoE-এর কাছে প্রশ্ন

Design ও domain modeling session-এ (2026-09-30) যা উঠে এসেছে কিন্তু v1-এ সমাধান হয়নি।

### ১৪.১ Open concern

প্রস্তাবিত সমাধান সহ; চূড়ান্ত সিদ্ধান্ত বাকি।

| # | Concern | ঝুঁকি | প্রস্তাব |
|---|---|---|---|
| C1 | **"requestId" শব্দ** — glossary "request id" শব্দটা Correlation ID-র জন্য এড়াতে বলে; অথচ validation-এর dedup key-র নামই `requestId`, আর generation-এ একই ধারণা `Idempotency-Key`। | আলোচনা ও doc-এ বিভ্রান্তি। | Domain term **Idempotency key** (দুটোর জন্যই); API field নাম অপরিবর্তিত। |
| C2 | **মাসের মাঝে onboarding** — rate card শুধু ভবিষ্যৎ মাস থেকে, তাই মাসের মাঝে live হওয়া FI-র প্রথম আংশিক মাস বিনামূল্যে যায়। | Revenue leakage। | FI-র **প্রথম** rate card চলতি open মাসের ১ তারিখ থেকে দেওয়া যাবে; বাকি সব পরিবর্তন শুধু ভবিষ্যৎ মাস। |
| C3 | **Platform-এর দোষে verdict** — trust store sync পিছিয়ে থাকায় বৈধ QR-এ `KEY_NOT_FOUND` এলেও সেটা billable। | FI-র অসন্তোষ, dispute। | নিয়ম অপরিবর্তিত; dispute হলে Adjustment দিয়ে ফেরত। |
| C4 | **Offboarded FI-র Adjustment** — পরের statement না থাকায় Adjustment চিরকাল Pending। | ফেরত/পাওনা হারিয়ে যায়। | Pending Adjustment থাকলে usage না থাকলেও Statement তৈরি; platform report-এ Pending Adjustment তালিকা। |
| C5 | **আসল user identity নেই** — admin API-তে একটাই platform credential; `finalizedBy` / `createdBy` শুধু free-text। | টাকা-সংক্রান্ত সিদ্ধান্তে জবাবদিহি দুর্বল; maker-checker নামমাত্র। | v1-এ audit log দিয়ে চালানো; পরে user-ভিত্তিক login। |
| C6 | **Module boundary পেরিয়ে read** — Billing সরাসরি `qr_generations` / `qr_validations` পড়ে। | Upstream schema বদলালে billing নিঃশব্দে ভুল সংখ্যা দেখাতে পারে। | একটা SQL view (`billing_usage_source`) contract হিসেবে + integration test; ADR-এ লিখে রাখা। |
| C7 | **Upstream retention অন্যের হাতে** — raw CSV ≥ ১৩ মাসের নিশ্চয়তা নির্ভর করে source টেবিলের retention-এর ওপর। | কেউ purge চালালে dispute-এর প্রমাণ হারাবে। | Source module-এর retention policy-তে "≥ ১৩ মাস" লিখিতভাবে রাখা। |
| C8 | **দুটো প্রায় একই repo** — `rvl-sbqr-api` (পুরনো কপি) ও `rvl-secure-bqr-manager` (active)। | ভুল repo-তে implement, বা দুটোয় drift। | Canonical repo স্পষ্ট করা; অন্যটা archive বা sync নীতি। |
| C9 | **mTLS certificate tenant-এর সাথে বাঁধা নয়** — certificate শুধু allow-list-এ মেলানো হয়; token-এর `tenant_id`-র সাথে মেলানো হয় না। | কোনো FI-র credential ফাঁস হলে অন্য কারো usage ওই FI-র নামে বিল হবে। | Certificate ↔ tenant binding যাচাই (security backlog)। |
| C10 | **`REQUEST_STALE` কোন পথে আসে অস্পষ্ট** — stale timestamp কখনো 400 validation error, কখনো verdict হিসেবে দেখা যায়। | Billable/non-billable গণনায় অসংগতি। | Implementation-এর সময় যাচাই; যেভাবেই আসুক non-billable। |
| C11 | **Gateway-এর derived idempotency key** — একই সময়-খণ্ডে হুবহু একই দুটো বৈধ request এক key পায় → দ্বিতীয়টা 409, বিল হয় না। Key বাধ্যতামূলক হওয়ার পর এই fallback-এর দরকারও প্রশ্নসাপেক্ষ। | ছোট revenue leakage; অপ্রত্যাশিত 409। | Key বাধ্যতামূলক হলে gateway-এর derivation সরিয়ে FI-র key-ই পাঠানো। |
| C12 | **Alert আসলে কেউ দেখে না** — Degraded health check কেবল তখনই কাজের, যখন কোনো monitoring সেটা দেখছে। | Job থেমে থাকলেও কেউ জানবে না। | বিদ্যমান monitoring-এ health check যুক্ত করা। |
| C13 | **Admin route prefix placeholder** — `/v1/admin/billing` বিদ্যমান convention-এর সাথে মেলানো হয়নি। | ছোট, implementation-এ ঠিক করা যাবে। | Implementation-এর শুরুতে মিলিয়ে নেওয়া। |
| C14 | **পুরো open মাস প্রতিদিন recompute** — এখনকার volume-এ সমস্যা নেই। | Volume অনেক বাড়লে job ধীর। | প্রয়োজনে incremental aggregation; এখন নয়। |

### ১৪.২ ভবিষ্যৎ scope (v1-এর পরে)

| # | প্রস্তাব | কখন দরকার হবে |
|---|---|---|
| F1 | Tier / slab pricing, minimum commitment, subscription | Commercial model বদলালে |
| F2 | Static ও Dynamic-এর আলাদা রেট | Pricing আলাদা করার সিদ্ধান্ত হলে |
| F3 | মাসের মাঝে রেট পরিবর্তন | চুক্তিতে দরকার হলে |
| F4 | Credit carry-forward (ঋণাত্মক total পরের মাসে সমন্বয়) | Credit ঘন ঘন হলে |
| F5 | Invoice / VAT / payment — finance system-এর সাথে integration | Finance-এর চাহিদা অনুযায়ী |
| F6 | Admin portal-এ Billing অংশ (chart সহ) | Admin portal বানানো হলে |
| F7 | FI-facing usage API (`GET /v1/usage` BFF-এর মাধ্যমে) বা FI portal | FI self-service চাইলে |
| F8 | Email / notification (draft তৈরি, finalize, statement পাঠানো) | হাতে পাঠানো ঝামেলা হলে |
| F9 | User-ভিত্তিক login ও সত্যিকারের maker-checker | C5 সমাধানে |
| F10 | Usage source-এর জন্য SQL view contract | C6 সমাধানে (v1-এও নেওয়া যায়) |
| F11 | Incremental aggregation | C14 — volume বাড়লে |
| F12 | ADR ০০০১ — Billing upstream record থেকে usage নেয় (আলাদা capture নয়) | এখনই লেখা উচিত |
| F13 | ADR ০০০২ — Invalid verdict-ও billable; বিল verifying FI-র নামে | এখনই লেখা উচিত |

### ১৪.৩ HoE-এর কাছে প্রশ্ন

**Governance**
1. Canonical repo কোনটা — `rvl-secure-bqr-manager` না `rvl-sbqr-api`? অন্যটার ভবিষ্যৎ কী? (C8)

**Commercial**
2. Invalid verdict (যেমন `INVALID_SIGNATURE`, `KEY_REVOKED`)-এর জন্যও চার্জ করা কি FI-দের কাছে গ্রহণযোগ্য, এবং চুক্তিতে লেখা থাকবে?
3. রেট কে ঠিক করবেন — প্রতি FI-র সাথে আলাদা আলোচনা, নাকি সবার জন্য একই?
4. মাসের মাঝে onboard হওয়া FI-র প্রথম আংশিক মাস কি বিল হবে, নাকি বিনামূল্যে? (C2)
5. Platform-এর নিজের দোষে (trust store sync ইত্যাদি) আসা verdict-এর জন্য কি কোনো SLA credit নীতি আছে বা লাগবে? (C3)
6. ৩০ দিনের dispute window কি চুক্তির সাথে মেলে?
7. সব FI কি BDT-তেই বিল হবে?

**Regulatory ও Finance**
8. QR validation বা generation-এর জন্য fee নেওয়ার বিষয়ে Bangladesh Bank-এর কোনো নিয়ম বা fee cap আছে কি?
9. Billing data-র retention নিয়ে কোনো regulatory নির্দেশনা আছে কি (১৩ মাসের বেশি)? (C7)
10. Platform fee কি VAT-যোগ্য? Finance-এর system-এর সাথে integration-এর timeline কী? (F5)

**Operations ও Security**
11. কে বা কোন ভূমিকা মাস finalize ও Adjustment তৈরি করতে পারবেন? v1-এ free-text নাম কি গ্রহণযোগ্য? (C5)
12. Admin portal বা FI portal-এর roadmap আছে কি? (F6, F7)
13. mTLS certificate ↔ tenant binding কি security backlog-এ অগ্রাধিকার পাবে? এটা billing-এর নির্ভুলতার সাথে সরাসরি যুক্ত। (C9)
14. Health check-এর Degraded অবস্থা কোন monitoring system দেখবে এবং কাকে জানাবে? (C12)

---

## পরিশিষ্ট: সিদ্ধান্তের তালিকা

| # | সিদ্ধান্ত |
|---|---|
| Q1 | Validation: নির্দিষ্ট verdict দিলেই billable (valid বা invalid) |
| Q2 | Validation-এর বিল caller FI-র নামে |
| Q3 | প্রতি FI flat per-unit rate, gen ও validation আলাদা, effective date সহ |
| Q4 | Calendar month, Asia/Dhaka; finalize-এর পর immutable |
| Q5 | শুধু statement + টাকা; invoice/VAT/payment finance-এর |
| Q6 / Q14 | Duplicate একবার; idempotency key বাধ্যতামূলক |
| Q7 | BDT, decimal, line total-এ half-up rounding |
| Q8 / Q9 | FI ও platform report-এর বিষয়বস্তু (§৭) |
| Q10 | Auto draft → নাম সহ manual finalize |
| Q11 | Adjustment entry, ৩০ দিনের dispute window, raw CSV প্রমাণ |
| Q12 / Q24 | Aggregate স্থায়ী; raw ≥ ১৩ মাস; billing source মুছবে না |
| Q13 | Billable verdict তালিকা (§২.২) |
| Q15 | প্রতি FI আলাদা gateway |
| Q16 | Static = Dynamic রেট |
| Q17 | Rate card নেই = non-billable |
| Q18 | Billing module + daily aggregation job + `010_billing.sql` |
| Q19 / Q22 | শুধু admin API; FI usage API নেই |
| Q20 | রেট শুধু ভবিষ্যৎ মাসের ১ তারিখ থেকে |
| Q21 / Q27 | JSON / CSV / print-friendly HTML → browser PDF |
| Q23 | Open মাস পুরো recompute, advisory lock |
| Q25 / Q30 | Notification নেই; health check Degraded |
| Q26 | ঋণাত্মক total দেখানো হবে |
| Q28 | Rate card, adjustment, finalize — admin API |
| Q29 | Finalized statement DB-তে সংরক্ষিত, সেখান থেকে render |
