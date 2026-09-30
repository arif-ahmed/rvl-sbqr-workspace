# Bounded Context Discovery — QrGeneration, QrVerification, Metering, Billing

- **তারিখ:** 2026-09-30
- **কী:** আগে থেকে ঠিক করা চারটে Bounded Context-এর সংক্ষিপ্ত Strategic DDD চিত্র।
  - কোনো context নতুন করে বানানো, ভাঙা, জোড়া বা নাম বদলানো হয়নি।
  - Tactical DDD (aggregate, entity ইত্যাদি) এর বাইরে।
- **ভিত্তি:**
  - Product requirement: [prd.md](prd.md)। Business নিয়মে বিরোধ হলে PRD ঠিক।
  - Design: [metering-billing-v1-bn.md](metering-billing-v1-bn.md) (v1.1)। এখানে "design ৪.৫"-এর মতো রেফারেন্স মানে এই doc-এর অংশ ৪.৫।
  - Glossary: [`rvl-secure-bqr-manager/CONTEXT.md`](../../../rvl-secure-bqr-manager/CONTEXT.md)
  - ADR: [0002](adr/0002-bill-conclusive-verdicts-to-verifying-fi.md)
  - Code: `rvl-secure-bqr-manager/src/Modules/{QrGeneration,Verification}`
- **প্রমাণের অবস্থা:**

| Context | কোথায় আছে |
|---|---|
| QrGeneration | **Code-এ আছে** (`SBQR.Modules.QrGeneration`) |
| QrVerification | **Code-এ আছে**, module-এর নাম `SBQR.Modules.Verification` |
| Metering | **Design (v1.1) + DB migration `011`**; application code নেই |
| Billing | **Design (v1.1) + DB migration `012`**; application code নেই |

> **গুরুত্বপূর্ণ:** এখানকার কোনো integration event এখনো code-এ নেই। দুটো producer-এর `.Contracts` project খালি (শুধু `.csproj`)। Outbox টেবিল আছে (migration `010`), কিন্তু কেউ লেখে বা পড়ে না। নিচের event-গুলো **v1.1 design-এ ঠিক করা, এখনো implement হয়নি**।

---

### `QrGeneration`

**Purpose:**
FI-র অনুরোধে BanglaQR P2P QR (static বা dynamic) তৈরি করা, FI-র নিজের key দিয়ে sign করে একবার ফেরত দেওয়া, আর প্রতিটি সফল তৈরির রেকর্ড রাখা।

**Responsibilities**
- Static ও Dynamic QR তৈরি করা (spec-এর payload নিয়ম অনুযায়ী)।
- শুধু তখনই তৈরি করা, যখন tenant Active আর তার signing key ACTIVE (`QrIssuancePipeline.cs`)।
- একই tenant-এর একই Idempotency key দিয়ে দ্বিতীয়বার তৈরি আটকানো (409)।
- প্রতিটি সফল তৈরির রেকর্ড রাখা: কে, কী ধরনের QR, কোন key version। QR string নিজে সংরক্ষিত হয় না।
- সফল, বাতিল ও ব্যর্থ তৈরির audit রাখা।
- *(Design, এখনো code-এ নেই)* "QR তৈরি হয়েছে" ঘটনাটা বাইরে জানানো (design ৪.৫)।

**Owns**
- **QR generation-এর রেকর্ড:** কোন tenant, কোন QR type (STATIC / DYNAMIC), key version, idempotency key, কখন।
- **"তৈরি সফল হয়েছে"** এই ঘটনা। Design অনুযায়ী এটাই billable মুহূর্ত: রেকর্ডটা commit হওয়া (design ৪.৩)।
- প্রতি tenant-এ Idempotency key-এর অনন্যতা।

**Does Not Own**
- Private key আর sign করার কাজ → KeyCustody।
- Tenant-এর পরিচয়, institution code আর Active কিনা → Tenancy।
- এই তৈরির জন্য টাকা লাগবে কিনা, বা কোন meter-এ পড়বে → Metering।
- দাম, statement → Billing।
- QR যাচাই → QrVerification।

**Ubiquitous Language**
- **QR generation:** একটা QR সফলভাবে তৈরির ঘটনা ও তার রেকর্ড।
- **Static QR / Dynamic QR:** বারবার ব্যবহারযোগ্য QR / টাকার অঙ্কসহ QR। Metering-এ এরা আলাদা meter-এ পড়ে।
- **Idempotency key:** একটা তৈরির অনুরোধের জন্য FI-র দেওয়া অনন্য চাবি (`Idempotency-Key` header)।
- **Signing key version:** কোন key version দিয়ে sign হয়েছে।
- **Admission:** Tenant-এর QR তৈরি করার অনুমতি আছে কিনা (Active)।

**Integration Events Published**
| Event | Business Meaning | Consumer |
|---|---|---|
| `QrGenerated` *(design; code-এ নেই)* | একটা QR সফলভাবে তৈরি ও save হয়েছে (201)। 409, 4xx বা 5xx হলে কখনো প্রকাশ হয় না। থাকে: event id, generation id, tenant id, QR type, idempotency key, কখন হয়েছে (design ৪.৫)। | Metering |

**Integration Events Consumed**
| Event | Producer | Why Consumed |
|---|---|---|
| — | — | কিছুই না। |

---

### `QrVerification`

> Code-এ module-এর নাম `Verification`। API আর রেকর্ডে "validation" শব্দ (`QrValidationController`, `QrValidation`, scope `qr:validate`)। নিচে Observations দেখুন।

**Purpose:**
FI-র পাঠানো QR payload spec-এর Annex B অনুযায়ী যাচাই করে একটা **verdict** দেওয়া, আর প্রতিটি যাচাইয়ের ফল যে FI যাচাই করেছে তার নামে রেকর্ড রাখা।

**Responsibilities**
- Replay আটকানো: request-এর সময় ±৫ মিনিটের মধ্যে থাকতে হবে, আর একই tenant-এর একই request id একবারই চলে।
- গঠন যাচাই: TLV আর CRC।
- শ্রেণিবিভাগ: P2P নাকি NON_P2P।
- QR-দাতা প্রতিষ্ঠানের public key trust store থেকে খুঁজে বের করা। না মিললে সর্বশেষ retired key দিয়েও চেষ্টা করা।
- Ed25519 signature যাচাই করে verdict ঠিক করা।
- প্রতিটি যাচাইয়ের এক row রাখা, যাচাইকারী tenant-এর নামে। শুধু replay হলে row হয় না, audit হয়।
- Audit রাখা।
- *(Design, এখনো code-এ নেই)* "QR যাচাই হয়েছে" ঘটনাটা বাইরে জানানো (design ৪.৫)।

**Owns**
- **Verdict** আর তার মানে: `VALID`, `INVALID_SIGNATURE`, `STRUCTURAL_INVALID`, `KEY_NOT_FOUND`, `KEY_SUSPENDED`, `KEY_REVOKED`, `KEY_NOT_ACTIVE`, `NON_P2P`, `REQUEST_STALE`, `REQUEST_REPLAYED`।
- **QR validation-এর রেকর্ড:** যাচাইকারী tenant, request id, verdict, trust source, QR-দাতার institution code।
- যাচাইকারী tenant কে: এটা credential থেকে আসে, request body থেকে কখনো নয়।
- Request id-এর একবার-ব্যবহার নিয়ম।

**Does Not Own**
- কোন verdict-এ টাকা লাগবে → **Metering**। design ২.২ স্পষ্ট বলে: "Verification শুধু business fact প্রকাশ করে; billable কিনা জানে না।"
- Trust store-এর key → InstitutionTrust (আর KeyCustody)।
- Tenant-এর পরিচয় → Tenancy।
- দাম, statement → Billing।
- QR তৈরি → QrGeneration।

**Ubiquitous Language**
- **Validation / Verification:** QR যাচাই। দুটো শব্দই চলছে (Observations দেখুন)।
- **Verdict:** যাচাইয়ের নির্দিষ্ট ফল (ওপরের তালিকা)।
- **Verifying tenant:** যে FI যাচাইয়ের API call করেছে।
- **Issuer:** যে প্রতিষ্ঠান QR তৈরি করেছিল (payload-এর institution code)।
- **Request id:** একটা যাচাই-অনুরোধের জন্য FI-র দেওয়া অনন্য id; replay আটকায়।
- **Replay window:** ±৫ মিনিট।
- **Trust source:** key কোথা থেকে এসেছে (`TRUST_DIRECTORY` বা `NONE`)।
- **Historical key:** পুরনো (retired) key, যেটা দিয়ে আগে তৈরি QR যাচাই করা যায়।

**Integration Events Published**
| Event | Business Meaning | Consumer |
|---|---|---|
| `QrValidated` *(design; code-এ নেই)* | একটা যাচাই verdict সহ রেকর্ড হয়েছে। প্রতিটি রেকর্ড হওয়া row-এর জন্য একটা; replay বা HTTP error-এ নয়। থাকে: event id, validation id, যাচাইকারী tenant id, verdict, request id, কখন হয়েছে (design ৪.৫)। | Metering |

**Integration Events Consumed**
| Event | Producer | Why Consumed |
|---|---|---|
| — | — | কিছুই না। |

---

### `Metering`

> Design (৪.৬, ১৪.২) আর DB migration `011`; application code নেই।

**Purpose:**
Platform যত QR তৈরি ও যাচাই করে, প্রতিটি **ঠিক একবার, স্থায়ীভাবে** গুনে রাখা। প্রতিটি billable কিনা চিহ্ন দেওয়া, আর কোন FI-র নামে পড়বে তা রাখা। "কী ব্যবহার হয়েছে?" প্রশ্নের একমাত্র উৎস হওয়া।

**Responsibilities**
- দুই producer-এর ঘটনা থেকে ব্যবহার রেকর্ড করা। একই ব্যবসায়িক ঘটনা দুবার এলেও একবারই গোনা হয় (`(source_type, source_id)` অনন্য)।
- **Billable কিনা ঠিক করা:** Conclusive verdict হলে billable, Protocol rejection হলে নয় (design ২.২, ADR 0002)। চিহ্নটা রেকর্ডের সময়ই বসে, আর পরে বদলায় না।
- **কে টাকা দেবে ঠিক করা:** ঘটনায় যে tenant আছে, সে (design ১৪.৫)।
- কাজটা আসলে কখন হয়েছে (`occurred_at`) রাখা, যাতে দিন আর মাস ঠিক থাকে।
- ব্যবহারের হিসাব দেওয়া: billable সংখ্যা, দিন/meter/verdict/tenant অনুযায়ী ভাগ, প্রতিটি কাজের তালিকা (raw extract)।
- "একটা নির্দিষ্ট সময় পর্যন্ত সব ব্যবহার রেকর্ড হয়েছে কি?" প্রশ্নের উত্তর দেওয়া (usage completeness)।
- ব্যবহারের তথ্য অন্তত ১৩ মাস রাখা (design ৯)।

**Owns**
- **Usage event:** প্রতিটি কাজের স্থায়ী রেকর্ড।
- **Meter:** `GENERATION_STATIC`, `GENERATION_DYNAMIC`, `VALIDATION`।
- **Billability policy:** billable কিনা, সেই সিদ্ধান্ত।
- **Attribution:** কোন FI-র নামে পড়বে।
- Usage completeness-এর উত্তর।
- Raw usage extract-এর বিষয়বস্তু।

**Does Not Own**
- Verdict কীভাবে বের হয় → QrVerification।
- QR তৈরি, আর HTTP স্তরে duplicate আটকানো → QrGeneration / QrVerification।
- দাম, rate card, টাকা, billing period, statement, adjustment → Billing।
- কোন FI billable → Billing।
- Tenant-এর পরিচয় → Tenancy।
- ঘটনা পৌঁছানোর ব্যবস্থা (outbox) → shared infrastructure।

**Ubiquitous Language**
- **Metered operation:** যে কাজের জন্য টাকা নেওয়া হয়: QR তৈরি বা QR যাচাই।
- **Meter:** তিনটে গণনা-খাতা (ওপরে)।
- **Usage event:** "একটা কাজ হয়েছে", এই কথার অপরিবর্তনীয় রেকর্ড।
- **Billable usage:** যে ব্যবহার চার্জে গোনা হয়।
- **Conclusive verdict:** যাচাই সম্পূর্ণ হয়ে নিশ্চিত উত্তর এসেছে (valid হোক বা বাতিল)। Billable।
- **Protocol rejection:** request পুরনো বা replay, তাই যাচাই হয়নি। Billable নয়।
- **Paying FI:** যে FI কাজটা চেয়েছে; যাচাইয়ের বেলায় যাচাইকারী FI।
- **Usage completeness:** একটা সময় পর্যন্ত সব ব্যবহার রেকর্ড হওয়ার নিশ্চয়তা।
- **Raw usage extract:** statement-এর পেছনের প্রতিটি কাজের তালিকা; dispute-এর প্রমাণ।
- **occurred_at / recorded_at:** কাজটা কখন হয়েছে / Metering কখন লিখেছে।

**Integration Events Published**
| Event | Business Meaning | Consumer |
|---|---|---|
| — | কিছুই না। Billing নিজে query করে সংখ্যা নেয় (design ৪.৭)। | — |

**Integration Events Consumed**
| Event | Producer | Why Consumed |
|---|---|---|
| `QrGenerated` | QrGeneration | প্রতিটি সফল QR তৈরি গুনতে: static বা dynamic meter-এ, তৈরিকারী FI-র নামে। |
| `QrValidated` | QrVerification | প্রতিটি যাচাই গুনতে: `VALIDATION` meter-এ, যাচাইকারী FI-র নামে। Verdict দেখে billable কিনা ঠিক হয়। |

---

### `Billing`

> Design (৩, ৪.৭, ৫, ১৪.৩) আর DB migration `012`; application code নেই।

**Purpose:**
মাসের billable ব্যবহারকে প্রতিটি Billable FI-র জন্য একটা **review করা, চূড়ান্ত Statement**-এ পরিণত করা। চূড়ান্ত হওয়ার পর ভুল শুধু Adjustment দিয়ে শোধরানো। FI আর platform team-কে ব্যবহার ও টাকার report দেওয়া। "এর দাম কত?" আর "FI-কে কত চার্জ করা হবে?" প্রশ্নের উত্তর দেওয়া।

**Responsibilities**
- প্রতি FI-র rate card রাখা: প্রতি unit-এ দাম, কোনো ভবিষ্যৎ মাসের ১ তারিখ থেকে কার্যকর।
- **কোন FI billable ঠিক করা:** যার ওই মাসে rate card আছে (design ২.৫)।
- **দাম হিসাব (Rating):** সংখ্যা × দাম, প্রতি line-এ একবার rounding (design ৩)।
- মাসের জীবনচক্র চালানো: Open → Draft → Finalized। Grace আর usage completeness না মিললে Draft হয় না; `expectedTotal` না মিললে Finalize হয় না (design ৫)।
- প্রতি FI, প্রতি মাসে Statement তৈরি; তাতে সেই সময়ের দামের কপি থাকে।
- Adjustment দিয়ে সংশোধন; dispute window ৩০ দিন।
- FI report আর Platform report বানানো (design ৭)। Report আলাদা context নয়, Billing-এর "পড়ার দিক" (design ৪.২)।
- টাকা-সংক্রান্ত প্রতিটি কাজের audit রাখা (design ১০)।

**Owns**
- Rate card
- Billable FI-র সিদ্ধান্ত
- Rating
- Billing period আর তার অবস্থা
- Statement (আর statement line)
- Adjustment
- Provisional figures
- Finalization
- FI report ও Platform report

**Does Not Own**
- কাজটা হয়েছে কিনা, বা billable কিনা → Metering (Billing শুধু `billable = true` গোনে; নিজে নতুন করে বিচার করে না)।
- Verdict, QR → QrVerification / QrGeneration।
- Tenant-এর পরিচয় → Tenancy।
- Invoice, VAT, আদায় → Finance (সিস্টেমের বাইরে)।
- FI-কে বিল পাঠানো → v1-এ হাতে; পরে F8।

**Ubiquitous Language**
- **Rate card:** একটা FI-র সাথে ঠিক করা প্রতি unit-এর দাম (তৈরি ও যাচাইয়ের জন্য একটা করে), মাসের ১ তারিখ থেকে কার্যকর।
- **Billable FI:** যে FI-র ওই মাসে rate card আছে।
- **Billing period:** Dhaka সময়ে একটা ক্যালেন্ডার মাস; Open → Draft → Finalized।
- **Provisional figures:** চূড়ান্ত না হওয়া মাসের সংখ্যা; বদলাতে পারে।
- **Finalization:** নির্দিষ্ট operator মাসের statement চিরতরে চূড়ান্ত করেন।
- **Statement:** এক FI-র এক মাসের billable ব্যবহার ও টাকা। Invoice নয়।
- **Adjustment:** চূড়ান্ত statement-এর ভুল শোধরানোর অঙ্ক; পরের মাসের statement-এ line হয়ে আসে।
- **Credit:** Statement-এর মোট ঋণাত্মক হলে।
- **Dispute window:** চূড়ান্ত হওয়ার পর ৩০ দিন।

**Integration Events Published**
| Event | Business Meaning | Consumer |
|---|---|---|
| — | v1-এ বাইরে কিছুই প্রকাশ হয় না (design ৪.৫)। Billing-এর ঘটনাগুলো (`RateCardAdded`, `AdjustmentRecorded`, `BillingPeriodDrafted`, `BillingPeriodFinalized`) শুধু audit-এ যায় (`billing.*` action হিসেবে), তাই সেগুলো ভেতরের ঘটনা, এই তালিকায় পড়ে না (design ১৪.৬)। | — |
| `BillingPeriodFinalized` *(শুধু ভবিষ্যৎ scope F8)* | একটা মাস চূড়ান্ত হয়েছে। | একটা ভবিষ্যৎ Notification module; `Unclear / Needs Decision` |

**Integration Events Consumed**
| Event | Producer | Why Consumed |
|---|---|---|
| — | — | কিছুই না। Metering থেকে query করে সংখ্যা নেয়; event-এ subscribe করে না, কারণ statement একটা batch কাজ (design ৪.৭)। |

---

## Context Relationships

| Upstream | Downstream | Information Exchanged | Mechanism | DDD Relationship |
|---|---|---|---|---|
| QrGeneration | Metering | "একটা QR তৈরি হয়েছে": generation id, tenant id, QR type (static/dynamic), idempotency key, কখন হয়েছে | **Integration Event**: transactional outbox → in-process publish; অন্তত একবার পৌঁছায় (design ৪.৪) *(design; code-এ নেই)* | **Published Language**: versioned event; producer billable কিনা জানে না (design ১৪.১) |
| QrVerification | Metering | "একটা যাচাই হয়েছে": validation id, যাচাইকারী tenant id, verdict, request id, কখন হয়েছে | **Integration Event**: ওপরের মতো *(design; code-এ নেই)* | **Published Language** (design ১৪.১) |
| Metering | Billing | Tenant আর meter অনুযায়ী billable সংখ্যা; দিন/meter/verdict/tenant অনুযায়ী ভাগ; প্রতিটি কাজের তালিকা; usage complete কিনা | **Query/API**: in-process `IMeteringQueries`, synchronous pull (design ৪.৬) | **Customer–Supplier**: Billing গ্রাহক, Metering সরবরাহকারী (design ১৪.১) |
| QrGeneration ↔ QrVerification | (দুই দিকেই) | QR payload-এর গঠন আর signature payload বানানোর নিয়ম। একটা context আরেকটাকে সরাসরি call করে না। | **Other**: একই shared code `SBQR.SharedKernel.QrCodec`। QrGeneration এটা দিয়ে sign করার payload বানায়, QrVerification একই function দিয়ে সেটা আবার তৈরি করে (`ValidateQrCommandHandler.cs`-এর মন্তব্য) | **Shared Kernel** (code-এ প্রমাণিত) |

যা **নেই** (ইচ্ছাকৃত):

- **QrGeneration/QrVerification → Billing:** কোনো সরাসরি সম্পর্ক নেই। Billing শুধু Metering থেকে নেয় (design ৪.২)।
- **Metering → QrGeneration/QrVerification:** কোনো call বা query নেই। Metering তাদের table পড়ে না (design ৪.১, v1.1-এর মূল পরিবর্তন)।
- **Billing → Metering-এর দিকে কোনো ঘটনা:** নেই। দিক সবসময় Metering থেকে Billing।

---

## Context Map

```text
QrGeneration   ──event (PL): QrGenerated──────────────────►  Metering
QrVerification ──event (PL): QrValidated──────────────────►  Metering
Metering       ──query (Customer–Supplier): IMeteringQueries──► Billing

QrGeneration   ◄──── Shared Kernel: SBQR.SharedKernel.QrCodec ────► QrVerification

(event দুটো design-এ ঠিক করা; code-এ এখনো নেই)
```

---

## Observations (context নিয়ে অসংগতি, শুধু জানানো হলো)

1. **QrVerification-এর নাম তিন রকম।**
   - দেওয়া নাম: `QrVerification`।
   - Code-এর module: `Verification`।
   - API, রেকর্ড আর scope-এ "validation": `QrValidationController`, `QrValidation`, `qr:validate`।
   - Design-এর event: `QrValidated` (namespace `SBQR.Modules.Verification.Contracts`)।
   - একই context-এর জন্য "verification" আর "validation" দুটোই চলছে।
2. **Verdict-এর তালিকা ভুল জায়গায় আছে।** `QrVerdict` enum এখন `Verification.Application`-এ (`ValidateQrCommand.cs`), `Verification.Contracts`-এ নয়। অথচ Metering-কে verdict দেখেই billable কিনা ঠিক করতে হবে। মানে verdict-এর তালিকা আসলে integration contract-এর অংশ, কিন্তু সেটা এখনো প্রকাশ করা হয়নি।
3. **`REQUEST_STALE`-এর দুটো পথ code-এ আছে।**
   - FluentValidation পুরনো timestamp-কে 400 দিয়ে আটকায়; তখন কোনো row হয় না।
   - Handler-এও একটা নিরাপত্তা-স্তর আছে, যেটা `REQUEST_STALE` row রেকর্ড করে (`ValidateQrCommandHandler.cs:87–105`)।
   - তাই এই verdict event-এ আসবে কিনা, অস্পষ্ট। Design এটা আগেই C8 হিসেবে তুলেছে।
4. **Key এখনো ঐচ্ছিক, অথচ design বলে বাধ্যতামূলক।**
   - QR তৈরিতে `Idempotency-Key` ঐচ্ছিক (`QrGenerationController.cs:58`)।
   - যাচাইয়ে `requestId` না দিলে server নিজে বানায় (`QrValidationController.cs:59`)।
   - Design দুটোকেই বাধ্যতামূলক করতে বলে (design ২.৪)। তার আগে event-এ এই field খালি আসতে পারে।
5. **দুটো ছোট অবশিষ্ট বিষয়।**
   - `src/Modules/Issuance/`-এ শুধু `bin`/`obj` folder আছে, কোনো source নেই। অথচ QrGeneration-এর pipeline-এর নাম `QrIssuancePipeline`। এটা সম্ভবত পুরনো নামের অবশিষ্টাংশ; QrGeneration-এর মালিকানা নিয়ে বিভ্রান্তি তৈরি করতে পারে।
   - Canonical repo কোনটা (`rvl-secure-bqr-manager` নাকি `rvl-sbqr-api`), design-এ আগেই C6 হিসেবে খোলা আছে।

---

## Open Questions

শুধু যে প্রশ্নগুলো context-এর মালিকানা, event/চুক্তি, দিক বা সম্পর্ককে প্রভাবিত করে।

| # | প্রশ্ন | কী প্রভাবিত করে |
|---|---|---|
| 1 | Context-এর canonical নাম কী হবে: `QrVerification`, নাকি code-এর `Verification`? Contracts namespace (`SBQR.Modules.Verification.Contracts`) কি সেটাই অনুসরণ করবে? | Event contract, নামকরণ |
| 2 | Verdict-এর তালিকা (`QrVerdict`) কি `Verification.Contracts`-এ প্রকাশ করা হবে, যাতে Metering সেটা published language হিসেবে পায়? নাকি event-এ verdict শুধু string থাকবে, আর তালিকার মালিক কে হবে? | Integration contract |
| 3 | `REQUEST_STALE` কি কখনো `QrValidated`-এ আসবে, নাকি সবসময় 400 হয়ে আটকে যাবে? (C8) | Event contract; Metering-এর billable-নয় পথ |
| 4 | `Idempotency-Key` আর `requestId` বাধ্যতামূলক হওয়ার আগে event-এ এই field কি খালি আসতে পারবে? নাকি বাধ্যতামূলক করাটা event চালুর পূর্বশর্ত? (design ২.৪) | Event contract |
| 5 | `QrValidated`-এর `TenantId` মানে যাচাইকারী tenant। Contract-এ field-এর নাম কি সেটা স্পষ্ট বলবে (যেমন verifying tenant)? Metering-এর Attribution এই মানের ওপরই নির্ভর করে (ADR 0002)। | Event contract, Attribution-এর মালিকানা |
| 6 | Platform report-এ "late usage" (চূড়ান্ত হওয়ার পরে রেকর্ড হওয়া ব্যবহার) দেখাতে Metering-এর query-তে "কখন রেকর্ড হয়েছে" দিয়ে filter লাগে। এটা `IMeteringQueries`-এর তালিকায় নেই। এটা কি Metering → Billing চুক্তিতে যোগ হবে? (design ৫ item 5, design ৭.২) | Metering → Billing contract |
| 7 | "Billable FI" কে ঠিক করবে: Billing (rate card দিয়ে, এখনকার design), নাকি ভবিষ্যতে Tenancy-র service flag দিয়ে (F15, প্রশ্ন ১৭)? | Billing-এর মালিকানা |
| 8 | `BillingPeriodFinalized` কি কখনো outbox-এ প্রকাশ হবে (F8)? হলে consumer কোন context? | Billing-এর published event |
