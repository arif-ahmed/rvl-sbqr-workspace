# Metering ও Billing — Feature docs

FI-দের QR generation ও validation-এর জন্য usage metering ও মাসিক billing।

- **Target repo:** `rvl-secure-bqr-manager`
- **অবস্থা:** Design চূড়ান্ত, HoE ও Product team-এর উত্তরের অপেক্ষা, implementation বাকি

## এই folder-এ

| Doc | অবস্থা | কী আছে |
|---|---|---|
| [metering-billing-v1-bn.md](metering-billing-v1-bn.md) | ✅ **বর্তমান (v1.1)** | চূড়ান্ত design, domain model, report template, open concern, HoE-এর প্রশ্ন |
| [bounded-context-discovery-bn.md](bounded-context-discovery-bn.md) | ✅ code + v1.1 থেকে | চারটে নির্দিষ্ট context (QrGeneration, QrVerification, Metering, Billing): দায়িত্ব, মালিকানা, integration event, সম্পর্ক, context map |
| [metering-tactical-ddd-bn.md](metering-tactical-ddd-bn.md) | ✅ v1.1 + discovery + code থেকে | Metering-এর tactical model: business action, invariant, domain object, persistence, production risk (aggregate লাগে না কেন) |
| [billing-tactical-ddd-bn.md](billing-tactical-ddd-bn.md) | ✅ v1.1 + discovery + code থেকে | Billing-এর tactical model: একমাত্র aggregate `BillingPeriod`; rate card ও adjustment aggregate নয়; invariant, concurrency, open question |
| [database-design.md](database-design.md) | ✅ migration `010`–`012` লেখা | Outbox, Metering, Billing-এর টেবিল, সম্পর্ক, নিয়ম, trigger, grant, জোড়া-লাগানো sample data; SQL file-এর link |

পুরনো design ([`metering-billing-design.md`](../../misc/metering-billing-design.md), [বাংলা](../../misc/metering-billing-design-bn.md)) এখন `docs/misc/`-এ, শুধু রেফারেন্সের জন্য। এর architecture অংশ v1.1-এ নেওয়া হয়েছে; commercial অংশ ভবিষ্যৎ scope (F1, F5)-এর জন্য।

## Glossary ও ADR

| Doc | কী আছে |
|---|---|
| [`rvl-secure-bqr-manager/CONTEXT.md`](../../../rvl-secure-bqr-manager/CONTEXT.md) | Ubiquitous language / glossary। **Billing অংশ এখনো যোগ হয়নি।** |
| [ADR 0001](adr/0001-outbox-between-modules.md) | Module-এর মধ্যে Outbox, module-এর ভেতরে plain transaction |
| [ADR 0002](adr/0002-bill-conclusive-verdicts-to-verifying-fi.md) | প্রতিটি conclusive verdict billable, বিল verifying FI-র নামে |

## পরের ধাপ

1. HoE-এর কাছে প্রশ্ন পাঠানো; প্রশ্ন ১৬–১৭ HoE + Product team-এর সাথে (v1.1 doc-এর অংশ ১৫.৩)
2. উত্তর অনুযায়ী doc ও glossary আপডেট
3. Implementation plan → `rvl-secure-bqr-manager`-এ কাজ → `/rvl-commit`
