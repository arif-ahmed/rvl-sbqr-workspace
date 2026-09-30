# Metering ও Billing — Feature docs

FI-দের QR generation ও validation-এর জন্য usage metering ও মাসিক billing।

- **Target repo:** `rvl-secure-bqr-manager`
- **অবস্থা:** Design চূড়ান্ত, HoE ও Product team-এর উত্তরের অপেক্ষা, implementation বাকি

## এই folder-এ

| Doc | অবস্থা | কী আছে |
|---|---|---|
| [metering-billing-v1-bn.md](metering-billing-v1-bn.md) | ✅ **বর্তমান (v1.1)** | চূড়ান্ত design, domain model, report template, open concern, HoE-এর প্রশ্ন |

পুরনো design ([`metering-billing-design.md`](../../misc/metering-billing-design.md), [বাংলা](../../misc/metering-billing-design-bn.md)) এখন `docs/misc/`-এ, শুধু রেফারেন্সের জন্য। এর architecture অংশ v1.1-এ নেওয়া হয়েছে; commercial অংশ ভবিষ্যৎ scope (F1, F5)-এর জন্য।

## Submodule-এ (code-এর পাশে থাকে, এখানে সরানো হয়নি)

| Doc | কী আছে |
|---|---|
| [`rvl-secure-bqr-manager/CONTEXT.md`](../../../rvl-secure-bqr-manager/CONTEXT.md) → **Billing** অংশ | Ubiquitous language / glossary |
| [ADR 0001](../../../rvl-secure-bqr-manager/docs/adr/0001-outbox-between-modules.md) | Module-এর মধ্যে Outbox, module-এর ভেতরে plain transaction |
| [ADR 0002](../../../rvl-secure-bqr-manager/docs/adr/0002-bill-conclusive-verdicts-to-verifying-fi.md) | প্রতিটি conclusive verdict billable, বিল verifying FI-র নামে |

## পরের ধাপ

1. HoE-এর কাছে প্রশ্ন পাঠানো; প্রশ্ন ১৬–১৭ HoE + Product team-এর সাথে (v1.1 doc §১৫.৩)
2. উত্তর অনুযায়ী doc ও glossary আপডেট
3. Implementation plan → `rvl-secure-bqr-manager`-এ কাজ → `/rvl-commit`
