# Tactical DDD — Billing BC

- **তারিখ:** 2026-09-30
- **কী:** Billing context-এর সবচেয়ে ছোট tactical model। শুধু সেটুকু, যা ছাড়া implementation সঠিক থাকে না।
- **ভিত্তি:**
  - Design: [metering-billing-v1-bn.md](metering-billing-v1-bn.md) (v1.1)। এখানে "design ৪.৫"-এর মতো রেফারেন্স মানে এই doc-এর অংশ ৪.৫। বিরোধ হলে design-ই ঠিক; শুধু domain model-এর বেলায় (design ১৪) এই doc নতুন।
  - Strategic baseline: [bounded-context-discovery-bn.md](bounded-context-discovery-bn.md), Billing অংশ। Context-এর সীমানা বা নাম এখানে বদলানো হয়নি।
  - Code: `rvl-secure-bqr-manager/src`। বিশেষ করে `Tenant` aggregate, `ActivateTenantCommandHandler`, আর `SBQR.SharedKernel.Domain`।
  - সঙ্গী doc: [metering-tactical-ddd-bn.md](metering-tactical-ddd-bn.md)।

---

## মূল কথা

Billing-এ তিন ধরনের জিনিস আছে, আর তিনটের দরকার আলাদা:

| জিনিস | ধরন | Aggregate লাগে? |
|---|---|---|
| **Billing period** (তার statement-সহ) | অবস্থা বদলায় (Open → Draft → Finalized); একসাথে অনেক statement চূড়ান্ত হয় | **হ্যাঁ।** এটাই Billing-এর একমাত্র aggregate। |
| **Rate card** | একবার লেখা; বদলায় না, শুধু কার্যকর হওয়ার আগে মোছা যায় | না |
| **Adjustment** | একবার লেখা; শুধু একবার Pending → Applied হয় | না |

- যেখানে অবস্থা বদলায় আর একাধিক জিনিস একসাথে ঠিক থাকতে হয়, code সেখানে `AggregateRoot` + repository + `IUnitOfWork` ব্যবহার করে (`Tenant`)। Billing period-ও তাই করবে।
- যেখানে রেকর্ড একবার লেখা হয়, code সেখানে সাধারণ `sealed class` আর সরাসরি `DbContext` ব্যবহার করে (`QrGeneration`, `QrValidation`)। Rate card আর Adjustment-ও তাই করবে।

---

## Business Actions

| Action | Business Rules | State Changed | Atomic? |
|---|---|---|---|
| **Rate card যোগ** | <ul><li>এক FI-র জন্য দুটো দাম: প্রতি তৈরি আর প্রতি যাচাই, দুটোই ≥ 0।</li><li>কার্যকর হয় কোনো **ভবিষ্যৎ** মাসের ১ তারিখ থেকে (ব্যতিক্রমের প্রস্তাব: C2)।</li><li>এক FI-র এক মাসে একটাই rate card শুরু হতে পারে।</li><li>যোগ হওয়ার পর বদলায় না। নতুন দাম মানে নতুন rate card।</li></ul> | একটা নতুন rate card | এক row। অনন্যতা DB-তে। |
| **Rate card মোছা** | শুধু যেটা এখনো কার্যকর হয়নি (ভুল ভবিষ্যৎ রেট সরাতে)। | একটা rate card কমে | এক row |
| **মাস Draft করা** (Period closer, ঘণ্টায় একবার, স্বয়ংক্রিয়; design ৪.৭) | <ul><li>মাস শেষ + grace (২ ঘণ্টা) পেরোতে হবে।</li><li>`IsUsageComplete(period_end)` true হতে হবে।</li><li>Billable FI = যার ওই মাসে rate card কার্যকর। প্রতিটির জন্য একটা statement।</li><li>প্রতি line: সংখ্যা × সেই মাসের দাম, rounding line-এ একবার (design ৩)।</li><li>Statement-এ দামের **কপি** থাকে।</li><li>FI-র সব Pending adjustment line হয়ে আসে।</li><li>Metering থেকে শুধু billable সংখ্যা নেওয়া হয়; Billing নিজে billable কিনা বিচার করে না।</li></ul> | Period → Draft; সব statement তৈরি | **হ্যাঁ।** Period-এর অবস্থা আর সব statement একটা transaction-এ। |
| **Recalculate** (design ৫ item 2) | শুধু Draft অবস্থায়। Draft statement ফেলে বর্তমান সংখ্যা আর Pending adjustment দিয়ে নতুন করে তৈরি। | সব Draft statement বদলায় | **হ্যাঁ** |
| **মাস Finalize করা** (design ৫ item 3) | <ul><li>আগেই Finalized → আগের ফল ফেরত (idempotent), error নয়।</li><li>শুধু Draft থেকে।</li><li>Usage complete আবার যাচাই; না হলে 409 `USAGE_NOT_COMPLETE`।</li><li>সংখ্যা আবার হিসাব; মোট `expectedTotal`-এর সাথে না মিললে 409 `DRAFT_CHANGED`, আর Draft নতুন সংখ্যায় বদলে যায়।</li><li>`finalizedBy` বাধ্যতামূলক (v1-এ free-text, C5)।</li><li>সব statement Finalized; যুক্ত adjustment Applied; period Finalized।</li></ul> | Period, সব statement, যুক্ত adjustment | **হ্যাঁ।** সবকিছু একটা transaction-এ। |
| **Adjustment রেকর্ড** | <ul><li>FI, অঙ্ক (≠ 0; ঋণাত্মক = credit), `reason`, `createdBy` বাধ্যতামূলক।</li><li>Pending থাকে; পরের Draft-এ line হয়ে যায়, সেই statement Finalize হলে Applied।</li><li>বদলানো যায় না। ভুল হলে উল্টো অঙ্কের নতুন adjustment।</li><li>৩০ দিনের dispute window FI-র dispute তোলার সময়সীমা। Adjustment কোনো statement-এর সাথে বাঁধা নয়, তাই system এটা আটকায় না।</li></ul> | একটা নতুন adjustment | এক row। Period-এর কিছু বদলায় না। |
| **Provisional figures / report** | <ul><li>চূড়ান্ত না হওয়া মাস: Metering × rate card দিয়ে তখনই হিসাব হয়, বদলাতে পারে।</li><li>চূড়ান্ত মাস: **শুধু** রাখা statement থেকে পড়া হয়, কখনো নতুন করে হিসাব হয় না।</li></ul> | কিছুই না (শুধু পড়া) | প্রযোজ্য নয় |

---

## Business Invariants

1. **এক মাসে একটা period** (Dhaka সময়ের ক্যালেন্ডার মাস, `YYYY-MM`)।
2. **অবস্থা শুধু সামনে যায়:** Open → Draft → Finalized। Finalized থেকে কোথাও যাওয়া যায় না; আবার Finalize করলে একই ফল।
3. **এক period-এ এক FI-র একটাই statement।**
4. **Finalized period-এর কোনো statement বা line বদলায় না।** ভুল শোধরানো শুধু adjustment দিয়ে।
5. **Finalize-এর পূর্বশর্ত:** usage complete, আর নতুন করে হিসাব করা মোট = `expectedTotal`।
6. **Draft-এর পূর্বশর্ত:** grace পেরিয়েছে, আর usage complete।
7. **Statement-এর দাম draft-এর সময়ের কপি।** পরে কোনো rate card যোগ হলে আগের statement বদলায় না।
8. **Rounding line-এ একবার** (২ দশমিক, AwayFromZero)। Statement-এর মোট = rounded line-গুলোর যোগফল; আবার rounding হয় না।
9. **Rate card বদলায় না, আর শুধু ভবিষ্যৎ মাস থেকে কার্যকর।** মোছা যায় শুধু কার্যকর হওয়ার আগে। তাই কোনো মাস শুরু হওয়ার পর তার দাম আর বদলাতে পারে না।
10. **Adjustment বদলায় না, আর ঠিক একবার Applied হয়,** একটা statement Finalize হওয়ার সময়।
11. **Statement-এর মোট ঋণাত্মক হতে পারে (Credit)।** এটা ভুল নয়, বৈধ অবস্থা।

---

## Aggregates

| Aggregate | Root | Protects | Key Invariants |
|---|---|---|---|
| **BillingPeriod** | `BillingPeriod` (`AggregateRoot`) | Draft, Recalculate, Finalize | ২, ৩, ৪, ৫, ৬, ৭, ৮ |

কেন শুধু এটা:

- **BillingPeriod:** Finalize-এর নিয়ম (`expectedTotal`) সব statement জুড়ে একসাথে দেখে। তাই statement-গুলো এর ভেতরে থাকতে হবে। একটা মাসে FI-র সংখ্যা কম (দেশের ব্যাংক/MFS), তাই পুরো মাস একসাথে load করা সমস্যা নয়।
- **Rate card-এ aggregate নেই:** সব নিয়ম তৈরি বা মোছার মুহূর্তের (৯)। অনন্যতা DB দেখে।
- **Adjustment-এ aggregate নেই:** তার একমাত্র অবস্থা-পরিবর্তন (Applied) হয় Finalize-এর একই transaction-এ। Module-এর ভেতরে সাধারণ transaction যথেষ্ট (ADR 0001)। "ঠিক একবার" DB trigger রক্ষা করে (invariant ১০)।
- **Invariant ১** (এক মাসে এক period) DB-র primary key রক্ষা করে।

---

## Domain Objects

| Object | Type | Why Needed |
|---|---|---|
| `BillingPeriod` | Aggregate Root | Draft/Recalculate/Finalize-এর state machine আর সব statement-এর চূড়ান্ত হওয়া এক জায়গায় রক্ষা করে। `Tenant`-এর মতো: private constructor, নিয়ম ভাঙলে `InvalidOperationException`, handler সেটাকে 409 বানায়। |
| `Statement` | Entity (aggregate-এর ভেতরে) | এক FI-র এক মাসের হিসাব। নিজের পরিচয় আছে, কারণ report আর adjustment line একে নির্দিষ্ট করে দেখায়। |
| `StatementLine` | Value Object (statement-এর ভেতরে) | Usage line (meter, সংখ্যা, দামের কপি, অঙ্ক) বা adjustment line (adjustment id, অঙ্ক)। পরিচয় লাগে না; তৈরির সময় একবার rounding হয় (৮)। |
| `BillingMonth` | Value Object | বছর + মাস, Dhaka সময়ে। মাসের শুরু-শেষ, grace কবে শেষ, rate card কোন মাস থেকে কার্যকর: সব সময়ের সীমা এক জায়গায় হিসাব হয়। মাস-সীমার ভুল এখানে টাকার ভুল। (design ১৪.৪-এর `Period`; aggregate `BillingPeriod`-এর সাথে নাম না মেলাতে `BillingMonth`।) |
| `PeriodStatus` | Value Object (enum) | `Open`, `Draft`, `Finalized`। |
| `RateCard` | Entity (append-only; `AggregateRoot` নয়) | এক factory দিয়ে তৈরি, যেটা "ভবিষ্যৎ মাসের ১ তারিখ" আর "দাম ≥ 0" দেখে। কোনো update method নেই। |
| `Adjustment` | Entity (`AggregateRoot` নয়) | Factory `reason`, অঙ্ক ≠ 0 দেখে। একমাত্র পরিবর্তন: Finalize-এর সময় একবার Applied। |

**ইচ্ছাকৃতভাবে বানানো হয়নি:**

- **`Money` / `UnitRate` VO** (design ১৪.৪-এ ছিল): মুদ্রা একটাই (BDT), আর `decimal` যথেষ্ট। Rounding-এর নিয়ম শুধু `StatementLine`-এ, আর দাম ≥ 0 শুধু `RateCard`-এর factory-তে। আলাদা type-এ কিছু যোগ হয় না।
- **Rating service:** সংখ্যা × দাম আর rounding `StatementLine`-এর factory-তেই হয়।

আকার (শুধু বোঝানোর জন্য, চূড়ান্ত code নয়; column design ৪.৮ `012_billing.sql`-এ):

```csharp
public sealed class BillingPeriod : AggregateRoot<BillingMonth>
{
    public PeriodStatus Status { get; private set; }
    public IReadOnlyList<Statement> Statements { get; }
    public string? FinalizedBy { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }

    // usageComplete handler Metering থেকে এনে দেয়, যেমন Tenant.Activate(readiness)
    public void Draft(IEnumerable<Statement> statements, bool usageComplete, DateTimeOffset now) { ... }
    public void Recalculate(IEnumerable<Statement> statements) { ... }
    public FinalizeOutcome Finalize(IEnumerable<Statement> recalculated, decimal expectedTotal,
                                    bool usageComplete, string finalizedBy, DateTimeOffset now) { ... }
}
```

---

## Domain Events

**কোনো Domain Event নেই।**

- Billing-এর ভেতরের কেউ অন্য কোনো Billing ঘটনায় সাড়া দেয় না।
  - Draft নিজেই সব statement বানায়।
  - Adjustment পরের Draft-এ query করে তোলা হয়; event লাগে না।
- `RateCardAdded`, `AdjustmentRecorded`, `BillingPeriodDrafted`, `BillingPeriodFinalized` (design ১৪.৬) শুধু audit-এ যায়। Code-এর নিয়মে handler নিজে `IAuditLogger` call করে (যেমন `ActivateTenantCommandHandler` লেখে `tenant.activated`)। তাই code-এ লেখা হবে শুধু **audit action** (`billing.*`)। আলাদা Domain Event class লাগবে না।
- `BillingPeriodFinalized` integration event হিসেবে শুধু ভবিষ্যৎ F8-এ। এখন বানানো হবে না।

---

## Persistence

- **`BillingPeriod`:** repository লাগে (`IBillingPeriodRepository`: মাস দিয়ে খোঁজা, যোগ করা) আর `IUnitOfWork`, `Tenant`-এর মতো। Statement আর line সবসময় period-এর সাথে একসাথে load ও save হয়।
- **`RateCard`, `Adjustment`:** repository নেই। Handler module-এর `DbContext` দিয়ে সরাসরি লেখে।
- **Report আর provisional figures:** সরাসরি query। Aggregate load করতে হয় না।
- **DB-র দায়িত্ব:** unique constraint আর immutability trigger (design ৪.৮-এ)। Finalized statement, line, rate card আর adjustment-এর invariant (৪, ৯, ১০) code-এর পাশাপাশি DB-ও রক্ষা করে।

---

## Production Risks / Decisions

1. **একই period-এ একসাথে দুটো কাজ** (Period closer আর Recalculate, বা দুজন Finalize)।
   - Recalculate আর Finalize period-এর row **lock** করে তারপর কাজ করে (design ২.৪ স্তর ৩)।
   - দ্বিতীয় Finalize idempotent, তাই একই ফল পায়।
   - Code-এ এখনো কোনো aggregate-এ lock বা concurrency token নেই (`Tenant`-এও নেই)। Billing-এ এটা বাধ্যতামূলক, কারণ ভুলটা টাকার।
2. **দুটো API instance একসাথে একই মাস Draft করছে।** Period-এর primary key-র কারণে একজন conflict পাবে। সেটাকে "আগেই হয়ে গেছে" ধরতে হবে, error নয়।
3. **Usage complete দেখা আর সংখ্যা নেওয়া একই কাজে।** Complete দেখার পরে নতুন event এলে ক্ষতি নেই। সেটা মাসের শেষের **পরের** সময়ের, তাই সংখ্যা বদলায় না। তবে সংখ্যা নিতে হবে শুধু `occurred_at` দিয়ে মাসের সীমায়।
4. **চূড়ান্ত মাসের পরে দেরিতে আসা ব্যবহার।** Statement বদলানো হয় না। Platform report-এ দেখায়, finance adjustment দেয় (design ৫ item 5)। এটা খুঁজে পাওয়ার query এখনো `IMeteringQueries`-এ নেই (discovery Open Question ৬)।
5. **টাকার হিসাব `decimal`-এ**, `double` নয় (design ১১)।
6. **Audit commit-এর পরে লেখা হয়,** code-এর বর্তমান নিয়মে (`ActivateTenantCommandHandler`)। Audit ব্যর্থ হলে কাজটা থেকে যায়, কিন্তু audit row হারায়। Billing-এও একই নিয়ম; এটা জানা সীমাবদ্ধতা।

---

## Open Questions

শুধু যেগুলো tactical model আটকে রাখে।

| # | প্রশ্ন | কেন আটকায় | প্রস্তাব |
|---|---|---|---|
| 1 | আগের মাস (M-1) Finalize হওয়ার আগেই M শেষ হলে Period closer কি M Draft করবে? | করলে একই Pending adjustment দুটো Draft-এ চলে যায়। M-1 Finalize-এ সেগুলো Applied হয়ে যায়, ফলে M-এর Draft ভুল থাকে আর M Finalize DB trigger-এ আটকে যায়। | না। M Draft হবে শুধু M-1 Finalized হওয়ার পরে। না হলে design ৮-এর `billing` health check তো Degraded দেখাচ্ছেই। |
| 2 | "Open" অবস্থার row কে বানায়? `billing_periods`-এ `OPEN` status আছে, কিন্তু কোনো কাজ সেই row তৈরি করে না। | Draft কি নতুন row insert করবে, নাকি আগে থেকে থাকা `OPEN` row update করবে, সেটা এর ওপর নির্ভর করে। | Row না থাকা মানেই Open। Draft প্রথমবার row insert করে; `OPEN` status বাদ দেওয়া যায়। এতে মাস আগে থেকে বানিয়ে রাখার কোনো job লাগে না। |
