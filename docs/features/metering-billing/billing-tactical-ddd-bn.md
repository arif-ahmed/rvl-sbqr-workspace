# Tactical DDD — Billing BC

- **তারিখ:** 2026-09-30
- **কী:** Billing context-এর সবচেয়ে ছোট tactical model। শুধু সেটুকু, যা ছাড়া implementation সঠিক থাকে না।
- **ভিত্তি:**
  - Strategic baseline: [bounded-context-discovery-bn.md](bounded-context-discovery-bn.md), Billing অংশ। Context-এর সীমানা বা নাম এখানে বদলানো হয়নি।
  - Code: `rvl-secure-bqr-manager/src`। বিশেষ করে `Tenant` aggregate, `ActivateTenantCommandHandler`, আর `SBQR.SharedKernel.Domain`।
  - সঙ্গী doc: [metering-tactical-ddd-bn.md](metering-tactical-ddd-bn.md)।

---

## মূল কথা

Billing-এ তিন ধরনের জিনিস আছে, আর তিনটের দরকার আলাদা:

| জিনিস | ধরন | Aggregate লাগে? |
|---|---|---|
| **Billing period** (তার statement-সহ) | অবস্থা বদলায় (Open → Draft → Finalized); একসাথে অনেক statement চূড়ান্ত হয় | **হ্যাঁ।** এটাই Billing-এর একমাত্র aggregate। |
| **Rate card** | একবার লেখা, আর বদলায় না | না |
| **Adjustment** | একবার লেখা, আর বদলায় না | না |

- যেখানে অবস্থা বদলায় আর একাধিক জিনিস একসাথে ঠিক থাকতে হয়, code সেখানে `AggregateRoot` + repository + `IUnitOfWork` ব্যবহার করে (`Tenant`)। Billing period-ও তাই করবে।
- যেখানে রেকর্ড একবার লেখা হয়, code সেখানে সাধারণ `sealed class` আর সরাসরি `DbContext` ব্যবহার করে (`QrGeneration`, `QrValidation`)। Rate card আর Adjustment-ও তাই করবে।

---

## Business Actions

| Action | Business Rules | State Changed | Atomic? |
|---|---|---|---|
| **Rate card যোগ** | <ul><li>এক FI-র জন্য দুটো দাম: প্রতি তৈরি আর প্রতি যাচাই।</li><li>কার্যকর হয় কোনো **ভবিষ্যৎ** মাসের ১ তারিখ থেকে; চলতি বা অতীত মাস থেকে নয়।</li><li>এক FI-র এক মাসে একটাই rate card শুরু হতে পারে।</li><li>দাম ঋণাত্মক হতে পারে না।</li><li>যোগ হওয়ার পর বদলায় না। নতুন দাম মানে নতুন rate card।</li></ul> | একটা নতুন rate card | এক row। অনন্যতা DB-তে। |
| **মাস Draft করা** (Open → Draft) | <ul><li>মাস শেষ হয়ে grace পেরোতে হবে।</li><li>Metering বলতে হবে, মাসের শেষ পর্যন্ত usage complete।</li><li>Billable FI = যার ওই মাসে rate card আছে। প্রতিটির জন্য একটা statement।</li><li>প্রতি line: সংখ্যা × সেই মাসের দাম, rounding line-এ একবার।</li><li>Line-এ দামের **কপি** থাকে; পরে rate card বদলালে line বদলায় না।</li><li>FI-র যে adjustment এখনো কোনো চূড়ান্ত statement-এ যায়নি, সেগুলো line হয়ে আসে।</li><li>Metering থেকে শুধু billable সংখ্যা নেওয়া হয়; Billing নিজে billable কিনা বিচার করে না।</li></ul> | Period → Draft; সব statement তৈরি | **হ্যাঁ।** Period-এর অবস্থা আর সব statement একটা transaction-এ। অর্ধেক Draft হওয়া মাস থাকতে পারবে না। |
| **মাস Finalize করা** (Draft → Finalized) | <ul><li>শুধু Draft অবস্থা থেকে।</li><li>Operator যে `expectedTotal` দেখে চূড়ান্ত করছেন, সেটা সব statement-এর বর্তমান মোটের সাথে মিলতে হবে।</li><li>কে চূড়ান্ত করলেন (নির্দিষ্ট operator) আর কখন, দুটোই রাখা হয়। Operator-এর পরিচয় আসে credential থেকে, request body থেকে নয়।</li><li>এরপর statement চিরতরে বদলায় না।</li></ul> | Period → Finalized; finalizer, finalized_at | **হ্যাঁ।** সব statement একসাথে চূড়ান্ত হয়। |
| **Adjustment রেকর্ড** | <ul><li>শুধু একটা **চূড়ান্ত** statement-এর বিরুদ্ধে।</li><li>চূড়ান্ত হওয়ার ৩০ দিনের মধ্যে (dispute window)।</li><li>অঙ্ক শূন্য নয়; ধনাত্মক বা ঋণাত্মক।</li><li>কারণ লাগবে।</li><li>রেকর্ড হওয়ার পর বদলায় না।</li><li>পরের মাসের statement-এ ঠিক একবার line হয়ে আসে।</li></ul> | একটা নতুন adjustment | এক row। Period-এর কিছু বদলায় না। |
| **Provisional figures / report** | <ul><li>চূড়ান্ত না হওয়া মাস: Metering × rate card দিয়ে তখনই হিসাব হয়, বদলাতে পারে।</li><li>চূড়ান্ত মাস: **শুধু** রাখা statement থেকে পড়া হয়, কখনো নতুন করে হিসাব হয় না।</li></ul> | কিছুই না (শুধু পড়া) | প্রযোজ্য নয় |

---

## Business Invariants

1. **এক মাসে একটা period** (Dhaka সময়ের ক্যালেন্ডার মাস)।
2. **অবস্থা শুধু সামনে যায়:** Open → Draft → Finalized। Finalized থেকে কোথাও যাওয়া যায় না।
3. **এক period-এ এক FI-র একটাই statement।**
4. **Finalized period-এর কোনো statement বা line বদলায় না।** ভুল শোধরানো শুধু adjustment দিয়ে।
5. **Finalize-এর পূর্বশর্ত:** `expectedTotal` = সব statement-এর মোট।
6. **Draft-এর পূর্বশর্ত:** grace পেরিয়েছে, আর usage complete।
7. **Line-এর দাম draft-এর সময়ের কপি।** পরে কোনো rate card যোগ হলে আগের line বদলায় না।
8. **Rounding line-এ একবার।** Statement-এর মোট = rounded line-গুলোর যোগফল; আবার rounding হয় না।
9. **Rate card বদলায় না, আর শুধু ভবিষ্যৎ মাস থেকে কার্যকর।** তাই কোনো মাস শুরু হওয়ার পর তার দাম আর বদলাতে পারে না।
10. **Adjustment বদলায় না, আর কোনো চূড়ান্ত statement-এ ঠিক একবারই আসে।**
11. **Adjustment শুধু চূড়ান্ত statement-এর বিরুদ্ধে, আর dispute window-এর মধ্যে।**
12. **Statement-এর মোট ঋণাত্মক হতে পারে (Credit)।** এটা ভুল নয়, বৈধ অবস্থা।

---

## Aggregates

| Aggregate | Root | Protects | Key Invariants |
|---|---|---|---|
| **BillingPeriod** | `BillingPeriod` (`AggregateRoot`) | মাস Draft করা, মাস Finalize করা | ২, ৩, ৪, ৫, ৬, ৭, ৮ |

কেন শুধু এটা:

- **BillingPeriod:** Finalize-এর নিয়ম (`expectedTotal`) সব statement জুড়ে একসাথে দেখে। তাই statement-গুলো এর ভেতরে থাকতে হবে। একটা মাসে FI-র সংখ্যা কম (দেশের ব্যাংক/MFS), তাই পুরো মাস একসাথে load করা সমস্যা নয়।
- **Rate card-এ aggregate নেই:** সব নিয়ম তৈরির মুহূর্তের (৯)। অনন্যতা DB দেখে।
- **Adjustment-এ aggregate নেই:** চূড়ান্ত period-কে সে শুধু **পড়ে** (চূড়ান্ত কিনা, আর কবে)। Finalized আর বদলায় না, তাই race-ও নেই। Adjustment period-এর ভেতরে রাখলে একটা চূড়ান্ত aggregate-এ অকারণে লেখা হতো।
- **Invariant ১** (এক মাসে এক period) DB-র unique constraint রক্ষা করে। দুজন একসাথে Draft করলে দ্বিতীয়জন ব্যর্থ হবে।
- **Invariant ১০** ("ঠিক একবার") DB রক্ষা করে: চূড়ান্ত statement-এর line-এ একই adjustment id দুবার থাকতে পারে না।

---

## Domain Objects

| Object | Type | Why Needed |
|---|---|---|
| `BillingPeriod` | Aggregate Root | Draft/Finalize-এর state machine আর সব statement-এর চূড়ান্ত হওয়া এক জায়গায় রক্ষা করে। `Tenant`-এর মতো: private constructor, `Draft(...)` / `Finalize(...)` method; নিয়ম ভাঙলে `InvalidOperationException`, handler সেটাকে 409 বানায়। |
| `Statement` | Entity (aggregate-এর ভেতরে) | এক FI-র এক মাসের হিসাব। নিজের পরিচয় আছে, কারণ report আর adjustment একে নির্দিষ্ট করে দেখায়। |
| `StatementLine` | Value Object (statement-এর ভেতরে) | Meter-এর line (সংখ্যা, দামের কপি, অঙ্ক) বা adjustment-এর line (adjustment id, অঙ্ক)। পরিচয় লাগে না; তৈরির সময় একবার rounding হয় (৮)। |
| `BillingMonth` | Value Object | বছর + মাস, Dhaka সময়ে। মাসের শুরু-শেষ, grace কবে শেষ, rate card কোন মাস থেকে কার্যকর: সব সময়ের সীমা এক জায়গায় হিসাব হয়। মাস-সীমার ভুল এখানে টাকার ভুল। |
| `PeriodStatus` | Value Object (enum) | `Draft`, `Finalized` (Open নিচে দেখুন)। |
| `RateCard` | Entity (append-only; `AggregateRoot` নয়) | এক factory দিয়ে তৈরি, যেটা "ভবিষ্যৎ মাসের ১ তারিখ" আর "দাম ≥ 0" দেখে। কোনো update method নেই। |
| `Adjustment` | Entity (append-only; `AggregateRoot` নয়) | Factory চূড়ান্ত period-এর তথ্য নিয়ে window আর "চূড়ান্ত কিনা" দেখে। কোনো update method নেই। |

**ইচ্ছাকৃতভাবে বানানো হয়নি:**

- **`Money` VO:** মুদ্রা একটাই (BDT), আর `decimal` যথেষ্ট। Rounding-এর নিয়ম শুধু `StatementLine`-এ, তাই আলাদা type-এ কিছু যোগ হয় না।
- **Rating service:** সংখ্যা × দাম আর rounding `StatementLine`-এর factory-তেই হয়। আলাদা domain service লাগে না।
- **"Open" অবস্থার row:** Period-এর row প্রথম Draft-এর সময় তৈরি হয়। Row না থাকা মানেই Open। এতে আগে থেকে মাস বানিয়ে রাখার কোনো job লাগে না।

আকার (শুধু বোঝানোর জন্য, চূড়ান্ত code নয়):

```csharp
public sealed class BillingPeriod : AggregateRoot<BillingPeriodId>
{
    public BillingMonth Month { get; }
    public PeriodStatus Status { get; private set; }
    public IReadOnlyList<Statement> Statements { get; }
    public string? FinalizedBy { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }

    // usageComplete handler Metering থেকে এনে দেয়, যেমন Tenant.Activate(readiness)
    public static BillingPeriod Draft(BillingMonth month, IEnumerable<Statement> statements,
                                      bool usageComplete, DateTimeOffset now) { ... }

    public void Finalize(decimal expectedTotal, string actor, DateTimeOffset now) { ... }
}
```

---

## Domain Events

**কোনো Domain Event নেই।**

- Billing-এর ভেতরের কেউ অন্য কোনো Billing ঘটনায় সাড়া দেয় না।
  - Draft নিজেই সব statement বানায়।
  - Adjustment পরের Draft-এ query করে তোলা হয়; event লাগে না।
- `RateCardAdded`, `AdjustmentRecorded`, `BillingPeriodDrafted`, `BillingPeriodFinalized` শুধু audit-এ যায়। Code-এর নিয়মে handler নিজে `IAuditLogger` call করে (যেমন `ActivateTenantCommandHandler` লেখে `tenant.activated`)। তাই এগুলো **audit action** (`billing.*`), Domain Event হিসেবে বানানোর দরকার নেই।
- `BillingPeriodFinalized` integration event হিসেবে শুধু ভবিষ্যৎ F8-এ। এখন বানানো হবে না।

---

## Persistence

- **`BillingPeriod`:** repository লাগে (`IBillingPeriodRepository`: মাস দিয়ে খোঁজা, যোগ করা) আর `IUnitOfWork`, `Tenant`-এর মতো। Statement আর line সবসময় period-এর সাথে একসাথে load ও save হয়।
- **`RateCard`, `Adjustment`:** repository নেই। Handler module-এর `DbContext` দিয়ে সরাসরি insert করে।
- **Report আর provisional figures:** সরাসরি query। Aggregate load করতে হয় না।
- **DB-র দায়িত্ব** (টেবিলের নকশা নয়, শুধু কোন নিয়ম DB রক্ষা করবে):
  - Period: মাস unique।
  - Statement: `(period, FI)` unique।
  - Rate card: `(FI, কার্যকর মাস)` unique।
  - চূড়ান্ত statement-এর line-এ adjustment id unique।
  - Finalized statement, rate card আর adjustment যেন বদলানো বা মোছা না যায়, `audit_logs`-এর মতো।

---

## Production Risks / Decisions

1. **একসাথে দুটো কাজ একই period-এ** (দুজন operator Finalize করছেন, বা Finalize আর নতুন Draft একসাথে)।
   - `BillingPeriod`-এ optimistic concurrency token লাগবে, যেমন Postgres `xmin`।
   - Code-এ এখনো কোনো aggregate-এ এটা নেই (`Tenant`-এও নেই)। Billing-এ এটা বাধ্যতামূলক, কারণ ভুলটা টাকার।
2. **প্রথম Draft একসাথে দুজন।** মাস unique হওয়ায় একজন `23505` পাবে। Handler এটাকে 409 বানাবে; retry করবে না।
3. **Usage complete দেখা আর সংখ্যা নেওয়া একই Draft কাজে।** Complete দেখার পরে কিন্তু সংখ্যা নেওয়ার আগে নতুন event এলে ক্ষতি নেই। সেটা মাসের শেষের **পরের** সময়ের, তাই সংখ্যা বদলায় না। তবে সংখ্যা নিতে হবে শুধু `occurred_at` দিয়ে মাসের সীমায়।
4. **চূড়ান্ত মাসের পরে দেরিতে আসা ব্যবহার।** Statement বদলানো হয় না। দরকার হলে operator adjustment দেন। এই ব্যবহার খুঁজে পাওয়ার query এখনো `IMeteringQueries`-এ নেই (discovery Open Question ৬)।
5. **Adjustment দুবার আসা।** Adjustment-এর নিজের কোনো "applied" field নেই, তাই বদলাতেও হয় না।
   - কোনো চূড়ান্ত statement-এর line-এ তার id থাকলে সে applied।
   - Draft তোলে শুধু সেগুলো, যাদের id কোনো চূড়ান্ত line-এ নেই।
   - DB-র unique দ্বিতীয়বার চূড়ান্ত হওয়া আটকায় (invariant ১০)।
6. **Dispute window** গোনা হয় `finalized_at` থেকে, server-এর সময়ে। Client-এর দেওয়া সময় কখনো নয়।
7. **টাকার হিসাব `decimal`-এ**, `double` নয়। Rounding-এর নিয়ম design §৩ অনুযায়ী, শুধু `StatementLine`-এ।
8. **Audit commit-এর পরে লেখা হয়,** code-এর বর্তমান নিয়মে (`ActivateTenantCommandHandler`)। Audit ব্যর্থ হলে কাজটা থেকে যায়, কিন্তু audit row হারায়। Billing-এও একই নিয়ম থাকবে; এটা জানা সীমাবদ্ধতা।

---

## Open Questions

শুধু যেগুলো tactical model আটকে রাখে।

| # | প্রশ্ন | কেন আটকায় | প্রস্তাব |
|---|---|---|---|
| 1 | Draft অবস্থায় কি মাস আবার Draft করা যাবে (Redraft), যেমন দেরিতে আসা ব্যবহার বা নতুন adjustment ধরতে? | Draft-এ statement বদলাতে পারে কিনা, আর `expectedTotal` আসলে কী রক্ষা করে, দুটোই এর ওপর নির্ভর করে। | হ্যাঁ। একটা `Redraft` action, শুধু Draft অবস্থায়, সব statement নতুন করে বানায়। `expectedTotal` নিশ্চিত করে যে operator ঠিক যেটা দেখেছেন, সেটাই চূড়ান্ত হচ্ছে। |
| 2 | মাস কি ক্রমানুসারে চলবে? অর্থাৎ M+1 Draft হবে শুধু M Finalized হওয়ার পরে? | Adjustment "পরের মাসের" কোন statement-এ যাবে, তা এর ওপর নির্ভর করে। ক্রম না থাকলে দুটো খোলা Draft একই adjustment তুলতে পারে। | হ্যাঁ, ক্রমানুসারে। Adjustment তখন সবসময় পরের Draft-এ যায়। |
| 3 | পরের মাসে FI-র rate card না থাকলে (billable নয়) তার adjustment কোথায় যাবে? | "Billable FI = rate card আছে" নিয়মে তার statement-ই হবে না, ফলে adjustment কোথাও যেতে পারবে না। | বাকি adjustment থাকলে সেই FI-র জন্যও statement হবে, তাতে শুধু adjustment line থাকবে। |
