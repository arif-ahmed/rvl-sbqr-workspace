# Tactical DDD — Metering BC

- **তারিখ:** 2026-09-30
- **কী:** Metering context-এর সবচেয়ে ছোট tactical model। শুধু সেটুকু, যা ছাড়া implementation সঠিক থাকে না।
- **ভিত্তি:**
  - Product requirement: [prd.md](prd.md)। Business নিয়মে বিরোধ হলে PRD ঠিক। PRD-র শব্দ ↔ এখানকার শব্দ: [traceability.md](traceability.md) অংশ ১।
  - Design: [metering-billing-v1-bn.md](metering-billing-v1-bn.md) (v1.1)। এখানে "design ৪.৫"-এর মতো রেফারেন্স মানে এই doc-এর অংশ ৪.৫। বিরোধ হলে design-ই ঠিক; শুধু domain model-এর বেলায় (design ১৪) এই doc নতুন।
  - Strategic baseline: [bounded-context-discovery-bn.md](bounded-context-discovery-bn.md), Metering অংশ। Context-এর সীমানা বা নাম এখানে বদলানো হয়নি।
  - Code: `rvl-secure-bqr-manager/src`। বিশেষ করে producer-দের রেকর্ড (`QrGeneration.cs`, `QrValidation.cs`), `AuditLog.cs` আর `SBQR.SharedKernel.Domain`।

---

## মূল কথা

Metering-এর কাজ একটাই: **প্রতিটি কাজের জন্য ঠিক একটা অপরিবর্তনীয় রেকর্ড লেখা।**

- রেকর্ডের কোনো জীবনচক্র নেই। লেখা হয়, তারপর শুধু পড়া হয়।
- একটা রেকর্ডের নিয়ম অন্য রেকর্ডের ওপর নির্ভর করে না। ব্যতিক্রম একটাই: একই ঘটনা দুবার গোনা যাবে না। এটা database-এর unique constraint দিয়েই সবচেয়ে নিরাপদে আটকানো যায়।

তাই এখানে কোনো Aggregate, Repository বা Domain Event লাগে না। Code-এ এই ধরনের "একবার লেখা" রেকর্ড (`QrGeneration`, `QrValidation`, `AuditLog`) এভাবেই বানানো: সাধারণ `sealed class`, `AggregateRoot` নয়। Handler `DbContext` দিয়ে সরাসরি insert করে, আর duplicate ধরা পড়ে Postgres `23505` দিয়ে। Metering-ও একই pattern নেবে।

---

## Business Actions

| Action | Business Rules | State Changed | Atomic? |
|---|---|---|---|
| **Generation usage রেকর্ড** (`QrGenerated` থেকে) | <ul><li>একটা generation id → ঠিক একটা usage event।</li><li>Meter আসে QR type থেকে: `STATIC` → `GENERATION_STATIC`, `DYNAMIC` → `GENERATION_DYNAMIC`।</li><li>সবসময় billable। শুধু সফল তৈরিই event হয়।</li><li>টাকা দেবে event-এর tenant।</li><li>`occurred_at` = event-এর সময়।</li></ul> | একটা নতুন usage event | **হ্যাঁ।** "আগে আছে কিনা দেখা" আর "লেখা" একটাই DB কাজ হতে হবে (`ON CONFLICT DO NOTHING`)। |
| **Validation usage রেকর্ড** (`QrValidated` থেকে) | <ul><li>একটা validation id → ঠিক একটা usage event।</li><li>Meter সবসময় `VALIDATION`।</li><li>Billable কিনা ঠিক করে verdict (নিচে Billability টেবিল)।</li><li>টাকা দেবে **যাচাইকারী** tenant (ADR 0002)।</li><li>`occurred_at` = event-এর সময়।</li></ul> | একটা নতুন usage event | **হ্যাঁ**, ওপরের মতো। |
| **ব্যবহারের হিসাব দেওয়া** (`IMeteringQueries`, design ৪.৬) | <ul><li>Billing শুধু `billable = true` গোনে।</li><li>দিন/মাস ঠিক হয় `occurred_at` দিয়ে, Dhaka সময়ে। `recorded_at` দিয়ে নয়।</li></ul> | কিছুই না (শুধু পড়া) | প্রযোজ্য নয় |
| **Usage completeness-এর উত্তর** (`IsUsageComplete`) | একটা সময় T পর্যন্ত "সম্পূর্ণ" তখনই, যখন `occurred_at ≤ T` এমন সব ঘটনা পৌঁছে গেছে। অপেক্ষায় থাকা (PENDING) বা ব্যর্থ (DEAD) ঘটনা থাকলে সম্পূর্ণ নয়। | কিছুই না (শুধু পড়া) | প্রযোজ্য নয় |

**Billability (verdict → billable), design ২.২:**

| Verdict | ধরন | Billable |
|---|---|---|
| `VALID`, `INVALID_SIGNATURE`, `STRUCTURAL_INVALID`, `KEY_NOT_FOUND`, `KEY_SUSPENDED`, `KEY_REVOKED`, `KEY_NOT_ACTIVE`, `NON_P2P` | Conclusive verdict | ✅ |
| `REQUEST_STALE`, `REQUEST_REPLAYED` | Protocol rejection | ❌ (তবু `billable = false` দিয়ে রেকর্ড হয়, report-এর জন্য) |
| অন্য কিছু (তালিকায় নেই) | অজানা | রেকর্ড হয় না; event ব্যর্থ হয় (design ১১ নিয়ম ১০, fail closed) |

> `REQUEST_REPLAYED`-এর কোনো row হয় না, তাই event-ও আসার কথা নয়। তবু policy-তে রাখা হয়েছে, যাতে তালিকা সম্পূর্ণ থাকে। `REQUEST_STALE` আসতে পারে (C8)।

---

## Business Invariants

1. **প্রতি ঘটনায় একটা রেকর্ড:** `(source_type, source_id)` অনন্য। Source id হলো generation id বা validation id; event id নয়। কারণ একই ব্যবসায়িক ঘটনাকে "একবার" ধরতে হবে, message-কে নয়।
2. **রেকর্ড অপরিবর্তনীয়:** লেখার পর কোনো field বদলায় না, আর রেকর্ড মোছা হয় না। ভুল শোধরানো Metering-এর কাজ নয়, সেটা Billing-এর Adjustment।
3. **Billable চিহ্ন লেখার মুহূর্তেই চূড়ান্ত:** পরে policy বদলালে পুরনো রেকর্ডে কোনো প্রভাব পড়ে না।
4. **Meter সবসময় তিনটের একটা**, আর source থেকেই আসে। কেউ হাতে দিতে পারে না।
5. **প্রতিটি রেকর্ডের একজন paying FI থাকে:** event-এর tenant। খালি হতে পারে না, আর Metering এটা নিজে বদলায় না।
6. **`occurred_at` producer-এর সময়**, Metering-এর লেখার সময় নয়। দেরিতে পৌঁছানো ঘটনাও তার আসল দিনে পড়ে।
7. **অজানা কিছু আন্দাজ করা হয় না।** অজানা verdict বা QR type এলে রেকর্ড হয় না। নিজে থেকে billable বা non-billable ধরে নেওয়া হয় না।

---

## Aggregates

**No dedicated Aggregate required.**

কারণ:

- প্রতিটি invariant হয় (ক) এক রেকর্ডের ভেতরে, তৈরির মুহূর্তে একবার (২–৭), নয়তো (খ) অনেক রেকর্ড জুড়ে অনন্যতা (১)।
- (ক)-র জন্য একটা factory method যথেষ্ট। রেকর্ড পরে বদলায় না, তাই কোনো অবস্থা-পরিবর্তন রক্ষা করার দরকার নেই।
- (খ) কোনো aggregate নিরাপদে রক্ষা করতে পারে না, কারণ দুটো consumer একসাথে চললে দুজনেই "নেই" দেখবে। শুধু DB unique constraint এটা ঠিকভাবে আটকায়।
- একসাথে একাধিক রেকর্ড বদলানোর কোনো কাজ নেই, তাই transactional consistency-র boundary-ও লাগে না।

---

## Domain Objects

| Object | Type | Why Needed |
|---|---|---|
| `UsageEvent` | Entity (append-only রেকর্ড; `AggregateRoot` নয়) | এটাই Metering-এর মূল তথ্য। শুধু দুটো factory দিয়ে তৈরি হয়, `FromGeneration(...)` আর `FromValidation(...)`, যাতে meter, billable আর paying FI **এক জায়গায়** ঠিক হয় (invariant ৩–৫, ৭)। কোনো public setter বা update method নেই। |
| `Meter` | Value Object (enum) | `GENERATION_STATIC`, `GENERATION_DYNAMIC`, `VALIDATION`। এটা ubiquitous language-এর শব্দ আর report-এর মূল ভাগ। Enum থাকলে ভুল string ঢুকতে পারে না। (design ১৪.৪ এটাকে `MeteredOperation` বলে; discovery-র ভাষায় এটা **Meter**।) |
| Billability policy | `UsageEvent`-এর ভেতরের একটা `static` function (আলাদা service নয়) | Verdict → billable। প্রতিটি verdict স্পষ্টভাবে লেখা একটা `switch`; অজানা verdict পেলে exception। টাকার নিয়ম এক জায়গায় থাকে, আর unit test দিয়ে পুরোটা ঢাকা যায়। |

**ইচ্ছাকৃতভাবে বানানো হয়নি:**

- `SourceRef` / `UsageEventId` wrapper: দুটো column (`source_type`, `source_id`) দিয়েই চলে। Wrapper-এ কোনো নিয়ম যোগ হয় না। Code-এর fact রেকর্ডগুলোও (`QrGeneration`, `QrValidation`) সাধারণ `Guid` ব্যবহার করে।
- `Verdict` type: এর মালিক QrVerification, Metering নয় (Open Question ১ দেখুন)।
- `TenantId` wrapper: Tenancy-র `TenantId` Metering-এ আনলে module-এর মধ্যে অপ্রয়োজনীয় নির্ভরতা তৈরি হয়। Event যেমন দেয়, তেমনই `Guid` থাকবে।

আকার (শুধু বোঝানোর জন্য, চূড়ান্ত code নয়; column-এর নাম migration `011_metering.sql`-এ, ব্যাখ্যা [database-design.md](database-design.md)-এ):

```csharp
public sealed class UsageEvent
{
    public Guid UsageEventId { get; private set; }
    public string SourceType { get; private set; }       // "qr_generation" | "qr_validation"
    public Guid SourceId { get; private set; }            // generation id / validation id
    public Guid SourceEventId { get; private set; }       // integration event id (tracing)
    public Meter Meter { get; private set; }
    public Guid TenantId { get; private set; }            // paying FI
    public string? Detail { get; private set; }           // validation-এর verdict
    public bool Billable { get; private set; }
    public string ClientReference { get; private set; }   // FI-র Idempotency key / requestId
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    public static UsageEvent FromGeneration(QrGenerated e, DateTimeOffset now) { ... }
    public static UsageEvent FromValidation(QrValidated e, DateTimeOffset now) { ... }

    internal static bool IsBillable(string verdict) => verdict switch { ... , _ => throw ... };
}
```

---

## Domain Events

**কোনো Domain Event নেই।**

- Metering-এর ভেতরের কেউ "usage রেকর্ড হয়েছে" ঘটনায় সাড়া দেয় না।
- Billing ঘটনা শোনে না; নিজে query করে (design ৪.৭)।
- `QrGenerated` আর `QrValidated` হলো **consume করা Integration Event**। Consumer handler এগুলো নিয়ে `UsageEvent` factory call করে, তারপর insert করে। এগুলোকে Domain Event হিসেবে আবার প্রকাশ করা হবে না।

> **নামের সতর্কতা:** `UsageEvent` একটা রেকর্ড, MediatR-এর event বা `IDomainEvent` নয়। নামটা glossary-র ("Usage event"), তাই রাখা হয়েছে। Code review-এ এই বিভ্রান্তি যেন না হয়।

---

## Persistence

- **Persist হয়:** শুধু `UsageEvent`। Insert আর read হয়; update বা delete হয় না।
- **Repository নেই।** Code-এ যেভাবে `QrIssuancePipeline` আর `ValidateQrCommandHandler` তাদের রেকর্ড লেখে, Metering-এর consumer handler-ও সেভাবে নিজের module-এর `DbContext` দিয়ে সরাসরি insert করবে। Aggregate নেই বলে repository কোনো সীমানা রক্ষা করে না।
- **পড়ার দিক:** `IMeteringQueries`-এর implementation সরাসরি query করে (EF বা Dapper, যেটা সুবিধা)। Domain object লোড করার দরকার নেই।
- **DB-র দায়িত্ব** (migration `011_metering.sql`-এ; এখানে শুধু কোন invariant DB রক্ষা করে):
  - `(source_type, source_id)` unique (invariant ১)।
  - Update/delete আটকানো: trigger, আর runtime role-এর শুধু `SELECT, INSERT` (invariant ২)।

---

## Production Risks / Decisions

1. **একই ঘটনা একাধিকবার পৌঁছানো (at-least-once)।**
   - Unique constraint-ই একমাত্র বিচারক।
   - Duplicate হলে **সফল** ধরে নিতে হবে: কিছু না করে handler শেষ হবে, error বা retry নয়। (Verification-এ duplicate মানে replay-reject; Metering-এ মানে "আগেই গোনা হয়েছে"। একই কৌশল, ফল উল্টো।)
   - "আগে SELECT, তারপর INSERT" করা যাবে না (design ১১ নিয়ম ৭)। দুটো consumer একসাথে চললে দুজনেই লিখে ফেলবে।
2. **Consumer আর outbox একই transaction-এ থাকার দরকার নেই।** Insert হয়ে outbox-এ "processed" লেখা ব্যর্থ হলে event আবার আসবে, আর risk ১ সেটা নিরাপদে সামলাবে। তাই কোনো distributed transaction লাগে না।
3. **Verdict-এর তালিকা বদলালে।**
   - QrVerification নতুন verdict যোগ করলে, Metering-এর policy আপডেট না হওয়া পর্যন্ত সেই event ব্যর্থ হবে (DEAD)।
   - এটা ইচ্ছাকৃত (fail closed): টাকার সিদ্ধান্ত নিঃশব্দে ভুল হওয়ার চেয়ে থেমে যাওয়া ভালো।
   - একটা unit test সব verdict ঘুরে দেখবে, প্রতিটির জন্য policy আছে কিনা। এর জন্য verdict-এর তালিকা Metering-এর কাছে পৌঁছাতে হবে (Open Question ১)।
4. **DEAD event মানে ব্যবহার অসম্পূর্ণ।** Completeness-এর উত্তর PENDING-এর সাথে DEAD-ও গোনে। না গুনলে একটা ব্যর্থ event-এর ব্যবহার বাদ রেখেই মাস Draft হয়ে যাবে।
5. **দেরিতে পৌঁছানো ঘটনা।** Metering কখনো দেরির কারণে event ফেরায় না; আসল `occurred_at` দিয়েই লেখে। মাস চূড়ান্ত হওয়ার পরে এলে কী হবে, সেটা Billing-এর বিষয় (design ৫ item 5)।
6. **`Idempotency-Key` / `requestId` বাধ্যতামূলক হওয়া event চালুর পূর্বশর্ত** (design ২.৪)। `client_reference` খালি হতে পারে না। তবে Metering-এর dedup এগুলোর ওপর নির্ভর করে না, generation id / validation id-এর ওপর করে।
7. **Policy বদলের প্রভাব।** Billability code বদলালে শুধু নতুন রেকর্ডে প্রভাব পড়ে (invariant ৩)। পুরনো রেকর্ড নতুন করে হিসাব করার কোনো পথ রাখা হবে না।

---

## Open Questions

শুধু যেগুলো tactical model আটকে রাখে।

| # | প্রশ্ন | কেন আটকায় | প্রস্তাব |
|---|---|---|---|
| 1 | Verdict-এর তালিকা (`QrVerdict`) কি `Verification.Contracts`-এ প্রকাশ হবে? (discovery Open Question ২) | Billability policy-র `switch` কোন type-এর ওপর চলবে, আর risk ৩-এর "সব verdict আছে কিনা" test লেখা যাবে কিনা, দুটোই এর ওপর নির্ভর করে। | Enum-টা `Verification.Contracts`-এ সরানো। Metering সেটা reference করবে; event-এ verdict string হিসেবেই থাকবে। |
