# Metering ও Billing — Feature docs

FI-দের QR generation ও validation-এর জন্য usage metering ও মাসিক billing।

- **Target repo:** `rvl-secure-bqr-manager`
- **অবস্থা:** PRD draft (sign-off বাকি); design চূড়ান্ত; DB migration `010`–`012` লেখা (`develop`); application code বাকি

## কোন doc কোনটার ওপর দাঁড়িয়ে

```text
BRD (rvl-sbqr-api/docs)            কেন: business
 └─ prd.md                         কী: product requirement R1-R32
     ├─ user-stories.md            কাজের ভাগ: ১৫টা story
     └─ metering-billing-v1-bn.md  কীভাবে: technical design (v1.1)
         ├─ bounded-context-discovery-bn.md
         ├─ metering-tactical-ddd-bn.md, billing-tactical-ddd-bn.md
         └─ database-design.md  →  migration 010-012
traceability.md                    সব স্তর জোড়া: requirement → story → design → DB → test
```

- **Business নিয়মে বিরোধ হলে PRD ঠিক।** Technical বিষয়ে design, আর schema-র বেলায় migration file।
- PRD, user stories আর traceability English-এ (BRD-র মতো); বাকি doc বাংলায়।
- PRD product-এর ভাষায় লেখা ("Approve", "Price"); technical doc-এ একই জিনিস "Finalize", "Rate card"। পুরো তালিকা: [traceability.md](traceability.md) অংশ ১।

## এই folder-এ

| Doc | অবস্থা | কী আছে |
|---|---|---|
| [prd.md](prd.md) | 📝 Draft, sign-off বাকি | **Product requirement** (BRD-র child): কী চার্জ হবে, কে দেবে, মাস close, সংশোধন, report; business সিদ্ধান্তের অবস্থা, খোলা প্রশ্ন Q1–Q17। Technical শব্দ ছাড়া। |
| [user-stories.md](user-stories.md) | 📝 Draft, PRD-র সাথে | PRD থেকে ১৫টা user story, acceptance criteria, priority, size |
| [traceability.md](traceability.md) | ✅ 30 Sep অবস্থা | শব্দের mapping; BRD → PRD; প্রতিটি requirement → story → design → DB constraint/trigger → acceptance test, build status; PRD প্রশ্ন ↔ design প্রশ্ন; জানা gap |
| [metering-billing-v1-bn.md](metering-billing-v1-bn.md) | ✅ **বর্তমান (v1.1)** | Technical design, domain model, report template, open concern, HoE-এর প্রশ্ন |
| [bounded-context-discovery-bn.md](bounded-context-discovery-bn.md) | ✅ code + v1.1 থেকে | চারটে নির্দিষ্ট context (QrGeneration, QrVerification, Metering, Billing): দায়িত্ব, মালিকানা, integration event, সম্পর্ক, context map |
| [metering-tactical-ddd-bn.md](metering-tactical-ddd-bn.md) | ✅ v1.1 + discovery + code থেকে | Metering-এর tactical model: business action, invariant, domain object, persistence, production risk (aggregate লাগে না কেন) |
| [billing-tactical-ddd-bn.md](billing-tactical-ddd-bn.md) | ✅ v1.1 + discovery + code থেকে | Billing-এর tactical model: একমাত্র aggregate `BillingPeriod`; rate card ও adjustment aggregate নয়; invariant, concurrency, open question |
| [dotnet-dev-onboarding-guide.md](dotnet-dev-onboarding-guide.md) | ✅ code থেকে (English) | নতুন .NET developer-এর জন্য: business story (Alpha/Beta Bank, billing team, Finance) ও প্রতিটি অংশের scenario, তারপর step-by-step local setup (Visual Studio, PostgreSQL, PowerShell), live metering, rate card, adjustment, draft → finalize → export, sample payload/response, test, known gap |
| [database-design.md](database-design.md) | ✅ migration `010`–`012` লেখা | Outbox, Metering, Billing-এর টেবিল, সম্পর্ক, নিয়ম, trigger, grant, জোড়া-লাগানো sample data; SQL file-এর link |

পুরনো design ([`metering-billing-design.md`](../../misc/metering-billing-design.md), [বাংলা](../../misc/metering-billing-design-bn.md)) এখন `docs/misc/`-এ, শুধু রেফারেন্সের জন্য। এর architecture অংশ v1.1-এ নেওয়া হয়েছে; commercial অংশ ভবিষ্যৎ scope (F1, F5)-এর জন্য।

## Glossary ও ADR

| Doc | কী আছে |
|---|---|
| [`rvl-secure-bqr-manager/CONTEXT.md`](../../../rvl-secure-bqr-manager/CONTEXT.md) | Ubiquitous language / glossary। **Billing অংশ এখনো যোগ হয়নি।** |
| [ADR 0001](adr/0001-outbox-between-modules.md) | Module-এর মধ্যে Outbox, module-এর ভেতরে plain transaction |
| [ADR 0002](adr/0002-bill-conclusive-verdicts-to-verifying-fi.md) | প্রতিটি conclusive verdict billable, বিল verifying FI-র নামে (PRD R2, R8) |

## পরের ধাপ

1. PRD-র খোলা প্রশ্ন (Q1–Q17) Product, HoE, Finance, Compliance-এর কাছে; আগে Q1 (BRD addendum) আর Q2 (device-এ check বনাম platform-এ check)
2. উত্তর অনুযায়ী PRD sign-off, তারপর design, doc ও glossary আপডেট
3. Implementation plan (story ধরে) → `rvl-secure-bqr-manager`-এ কাজ → traceability-র status আপডেট → `/rvl-commit`
