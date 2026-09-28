# FI Gateway (BFF) — End-to-End ব্যাংক-সিমুলেশন প্লেবুক (বাংলা)

> **এই ডকের বিষয়বস্তু কী নয়, আগে সেটা বলে নিই:** এটা `rvl-secure-bqr-manager` (sbqr.api) বা কোনো IdP-এর অনবোর্ডিং গাইড না। **এই প্লেবুকের একমাত্র নায়ক হলো এই রিপো — `rvl-sbqr-fi-bff`।** বাকি সবকিছু — fake IdP, sbqr.api (real বা fake), মোবাইল/ওয়েব অ্যাপ — শুধু BFF-কে বাস্তব পরিস্থিতিতে চালানোর জন্য দরকারি props। প্রতিটা ধাপে প্রশ্ন একটাই: **"BFF এইমাত্র কী করল, আর কোন header/log সেটার প্রমাণ?"**
>
> **এই পুরো ডকটা একটা একটাই গল্পের ওপর দাঁড়িয়ে আছে** — **Alice** (receiver) **Bob**-কে (sender/payer) **ঢাকা ব্যাংক**-এর অ্যাপ দিয়ে টাকা চান, আর **SBQR P2P** প্ল্যাটফর্ম (আসল `rvl-secure-bqr-manager`) সেই QR-টা generate/validate করে।
>
> **v0.1 স্কোপ নোট:** BFF ইনবাউন্ড অথ এই রিলিজে শুধু **মোবাইল OIDC JWT** (ট্র্যাক A — §৫)। ওয়েব cookie/password-grant ট্র্যাক (ট্র্যাক B — পুরোনো §৫.২) AR-2026-09-23-01 §৪.৩ অনুযায়ী পরবর্তী রিলিজে স্থগিত — BFF কোডবেস থেকে `/v1/auth/login`, কুকি স্কিম, IdP password-grant ক্লায়েন্ট মুছে ফেলা হয়েছে। এই ডকের ট্র্যাক B সেইজন্য সংরক্ষিত (legacy narrative)। কার্যকর ডেমো-র জন্য শুধু ট্র্যাক A অনুসরণ করুন; Cookie-track-এর curl-recipe-গুলো আর চলবে না।
>
> **আগে পড়ুন (কনটেক্সটের জন্য, না পড়েও এগোনো যায়):** [`dev-onboarding-guide.md`](./dev-onboarding-guide.md) — কেন এই BFF আছে, two-token মডেল। **গভীরে যেতে চাইলে পরে পড়ুন:** [`dev-manual-testing.md`](./dev-manual-testing.md) — প্রতিটা slice-এর সম্পূর্ণ curl-কুকবুক ও ২৪+ troubleshooting entry (এই ডক সেটার সংক্ষিপ্ত, linear সংস্করণ — সবকিছু এখানে নেই, ইচ্ছা করেই)।
>
> **সময়:** সেটআপ ~১৫-২০ মিনিট (প্রথমবার `tmp/provision-real-sbqr.sh` Docker ইমেজ বিল্ড করতে ৫-৮ মিনিট নিতে পারে), পুরো walkthrough আরও ~২০ মিনিট।
>
> **সহোদর ডকুমেন্ট:** [`e2e-playbook-bn.md`](./e2e-playbook-bn.md) — একই যাত্রার command-verified সংস্করণ (Bob=generator/Alice=validator দৃষ্টিকোণে, প্রতিটা কমান্ড আসল মেশিনে চালিয়ে যাচাই করা)। এই ডকের অনেক তথ্য (real পোর্ট, real body-shape, real error) ওই ডক থেকেই প্রমাণিত — সরাসরি হাতে-কলমে আরও ড্রিল (tamper/forge/replay) চাইলে ওখানে যান।

---

## সূচিপত্র

1. [নির্বাহী সারসংক্ষেপ](#১-নির্বাহী-সারসংক্ষেপ)
2. [সেটআপ](#২-সেটআপ)
3. [বাস্তব-জগতের সিমুলেশন](#৩-বাস্তব-জগতের-সিমুলেশন)
4. [প্ল্যাটফর্ম-টোকেন ডিবাগিং](#৪-প্ল্যাটফর্ম-টোকেন-ডিবাগিং)
5. [ইউজার সেশন: মোবাইল বনাম ওয়েব অ্যাপ](#৫-ইউজার-সেশন-মোবাইল-বনাম-ওয়েব-অ্যাপ)
6. [QR জেনারেশন](#৬-qr-জেনারেশন)
7. [QR ভ্যালিডেশন](#৭-qr-ভ্যালিডেশন)
8. [BFF ↔ SBQR P2P: সুরক্ষিত যোগাযোগ](#৮-bff--sbqr-p2p-সুরক্ষিত-যোগাযোগ)
9. [সমস্যা সমাধান](#৯-সমস্যা-সমাধান)
10. [পরিশিষ্ট: শব্দকোষ ও চেকলিস্ট](#১০-পরিশিষ্ট-শব্দকোষ-ও-চেকলিস্ট)

---

## ১. নির্বাহী সারসংক্ষেপ

এই রিপো (`rvl-sbqr-fi-bff`, কোডে namespace `SBQR.FiGateway.Api`) হলো **BFF (Backend-for-Frontend)** — একটা bank-এর নিজস্ব ডেটা-সেন্টারে বসা একটা পাতলা gateway। bank-এর মোবাইল/ওয়েব অ্যাপ কখনো RVL-এর `sbqr.api`-কে সরাসরি কল করে না — সবসময় এই BFF-কে কল করে, আর BFF-ই `client_secret` ও প্ল্যাটফর্ম-টোকেন নিজের মেমোরিতে রেখে আসল কলটা proxy করে। এই সিদ্ধান্তের পূর্ণ যুক্তি বাংলায় আছে [`0001-fi-direct-oauth-to-bff-model.md`](./0001-fi-direct-oauth-to-bff-model.md)-এ (VAPT finding, RFC 8252, ইত্যাদি) — এখানে repeat করছি না।

### চরিত্র-পরিচিতি (পুরো ডকে এই একই গল্প চলবে)

| ভূমিকা | ইউজার | কে | কী করেন |
|---|---|---|---|
| 🧑 গ্রহীতা (Receiver) | **Alice** (`alice` / `s3cret`) | ঢাকা ব্যাংক-এর গ্রাহক, নিজের ফোনে bank-অ্যাপ ব্যবহার করেন | টাকা **চান**, QR **জেনারেট** করেন |
| 🧑‍💼 প্রেরক/পেয়ার (Sender) | **Bob** (`bob` / `bobs-pass`) | ঢাকা ব্যাংক-এর গ্রাহক, আলাদা ফোনে bank-অ্যাপ ব্যবহার করেন | QR **স্ক্যান** করে টাকা **পাঠান** |
| 🏦 FI Gateway (BFF) | — | **ঢাকা ব্যাংক**-এর নিজস্ব ডেটা-সেন্টার | *এই রিপো — যা আমরা টেস্ট করছি* |
| 🌐 QR প্ল্যাটফর্ম | — | **SBQR P2P** (RVL-হোস্টেড, কোডে `sbqr.api`) — ঢাকা ব্যাংকের মতো একাধিক FI-কে ইন্টারঅপারেবল P2P QR সুইচ হিসেবে সার্ভিস দেয় | আসল `rvl-secure-bqr-manager` (§২) |

> "ঢাকা ব্যাংক" এখানে একটা উদাহরণমাত্র — কোনো নির্দিষ্ট ডিপ্লয়মেন্টের রেফারেন্স না। `alice`/`bob` dev IdP-তে আগে থেকেই হার্ডকোড করা ইউজারনেম, তাই curl কমান্ড ও গল্প — দুটোতেই একই নাম ব্যবহার করছি, আলাদা কোনো ছদ্মনাম চাপানো হচ্ছে না।

### প্রোডাকশনে কে কোথায় বসে

**সংশোধনী:** Alice বা Bob-এর ফোন **ঢাকা ব্যাংক-এর নেটওয়ার্কের ভেতরে বসে না।** এটা তাদের নিজের ফোন — নিজের মোবাইল-ডেটা বা wifi-তে, পাবলিক ইন্টারনেটের ওপর দিয়ে HTTPS-এ ব্যাংকের পাবলিক-ফেসিং এন্ডপয়েন্টে কানেক্ট করে। ব্যাংকের নেটওয়ার্কের ভেতরে থাকে শুধু ব্যাংকের নিজের infra — BFF, ও ব্যাংকের IdP।

```
[Alice/Bob-এর ফোন — bank mobile/web app]
        │   নিজের ডিভাইস, নিজের নেটওয়ার্ক (wifi/মোবাইল-ডেটা) — ব্যাংকের অংশ না
        │   HTTPS, পাবলিক ইন্টারনেটের ওপর দিয়ে
        ▼
┌──────────── ঢাকা ব্যাংক-এর নিজস্ব ইনফ্রাস্ট্রাকচার (data centre) ─────────┐
│  [ঢাকা ব্যাংক-এর IdP]  ← গ্রাহকের লগইন/সেশন যাচাই করে                   │
│  [FI Gateway / BFF]   ← এই রিপো; client_secret + প্ল্যাটফর্ম-টোকেন এখানে │
└────────────────────────────┬─────────────────────────────────────────┘
                              │  mTLS / VPN — প্রাইভেট লিংক, পাবলিক ইন্টারনেটে না
                              ▼
┌──────── SBQR P2P প্ল্যাটফর্ম (RVL) ────────┐
│  sbqr.api  /v1/oauth/token                 │
│            /v1/qr/generate/*               │
│            /v1/qr/validate                 │
└───────────────────────────────────────────┘
```

### এই প্লেবুকে কে "real", কে "simulated"

| উপাদান | প্রোডাকশনে কোথায় থাকে | এই প্লেবুকে | কেন |
|---|---|---|---|
| Alice/Bob-এর mobile app | **ঢাকা ব্যাংক-এর বাইরে** — গ্রাহকের নিজের ফোন, HTTPS দিয়ে ব্যাংকের BFF-এ initiate করে | `tmp/mock-mobile-app.js` (JWT bearer) — এই স্ক্রিপ্টটাই সেই বাইরের, HTTPS-initiating client-এর ভূমিকা পালন করে (curl দিয়েও একই কাজ করা যায়, §৫-৭ জুড়ে দেখানো হয়েছে) | পুরো অ্যাপ বানানো এই টেস্টের স্কোপে না, কিন্তু initiator হিসেবে একটা fake client লাগবেই — এটাই সেটা |
| ঢাকা ব্যাংক-এর IdP | ব্যাংকের নিজস্ব ইনফ্রাস্ট্রাকচারে, গ্রাহকের অ্যাপ থেকে reachable | `tmp/fake-idp.js` (:5105) | ব্যাংকের IdP এই রিপো বা SBQR P2P টিমের নিয়ন্ত্রণে না — সবসময় fake |
| SBQR P2P (sbqr.api) | RVL-এর হোস্টেড প্ল্যাটফর্ম, শুধু BFF-থেকে VPN/mTLS-এ reachable | **আসল `rvl-secure-bqr-manager`**, `tmp/provision-real-sbqr.sh` দিয়ে এক কমান্ডে লোকালি চালানো (§২) — শুধু on-demand ব্যর্থতা (৪০১/৫xx/contract-violation) দেখাতে সাময়িকভাবে `tmp/fake-sbqr-api-401.js` / `-503.js` / `-contract.js`-এ সুইচ করা হবে (§৪, §৮) | আসল contract, আসল Ed25519 সাইনিং, আসল DB persistence — "real world" দাবিটা সত্যি করতে |
| **FI Gateway (BFF)** | **ঢাকা ব্যাংক-এর নিজস্ব ইনফ্রাস্ট্রাকচারে** | **এই কোড — কখনো fake না** | **এটাই আমরা টেস্ট করছি** |

**পরের ধাপ:** §২-এ তিনটা প্যানেল বুট করুন।

---

## ২. সেটআপ

### প্রয়োজনীয় টুল

.NET 8 SDK (BFF), Docker (real sbqr.api + Postgres), Node.js 18+, `curl`, (ঐচ্ছিক) `jq`।

### তিন-প্যানেল লেআউট

real sbqr.api নিজেই Docker-এ ব্যাকগ্রাউন্ডে (`-d`) চলে — এর জন্য আলাদা foreground প্যানেল লাগে না, `docker logs sbqr.api -f` দিয়ে যেকোনো সময় দেখা যায়।

| প্যানেল | কী চলবে | পোর্ট |
|---|---|---|
| ১ — ঢাকা ব্যাংক IdP (fake) | `node tmp/fake-idp.js` | `:5105` |
| ২ — **FI Gateway (BFF)** | `dotnet run --project src/SBQR.FiGateway.Api` | `:8080` |
| ৩ — Driver | `curl` / `tmp/mock-mobile-app.js` | — |
| *(ব্যাকগ্রাউন্ড, Docker)* | real sbqr.api + Postgres + trust-store mock | `:5001` |

> real sbqr.api ইচ্ছাকৃতভাবে `:5001`-এ চলে, `:8080` না — যাতে আমাদের BFF-এর পোর্টের সাথে কোনো সংঘর্ষ না হয় (`tmp/provision-real-sbqr.sh`-এর নিজস্ব কমেন্ট অনুযায়ী)।

### ধাপ ২.১ — ঢাকা ব্যাংক IdP (Panel 1)

```bash
node tmp/fake-idp.js
# → fake IdP on http://localhost:5105
```

### ধাপ ২.২ — real sbqr.api, এক কমান্ডে (`tmp/provision-real-sbqr.sh`)

এই রিপোতে এখন একটা idempotent স্ক্রিপ্ট আছে যেটা `rvl-secure-bqr-manager` sibling রিপো (`../rvl-secure-bqr-manager`) থেকে পুরো real sbqr.api স্ট্যান্ড-আপ করে দেয় — হাতে কিছু করতে হয় না:

```bash
bash tmp/provision-real-sbqr.sh        # প্রথমবার ~৫-৮ মিনিট (Docker ইমেজ বিল্ড)
```

script নিজে ৬টা ধাপে যা করে: PostgreSQL + একটা trust-store mock তোলে → `db/migrations/*.sql` apply করে → `platform-bootstrap` সিক্রেট মিন্ট করে (Argon2id হ্যাশ) → real sbqr.api `:5001`-এ চালু করে → admin API দিয়ে একটা tenant বানিয়ে/activate করে ও Ed25519 সাইনিং-কী মিন্ট করে → শেষে **`tmp/.env-real`** লিখে দেয় (real tenant-এর `client_id`/`client_secret`-সহ, contract pinning **চালু করা অবস্থায়**)। বারবার চালানো নিরাপদ — আগেরবারের tenant credential কাজ করলে সেটাই reuse করে।

সফল শেষে দেখবেন:

```
✔ Mode R stack is ready:
   real sbqr.api      → http://localhost:5001/health/live
   fake IdP           → http://localhost:5105  (node tmp/fake-idp.js)
   BFF env            → tmp/.env-real
   tenant clientId    → 031267-77214637
```

(`tenant clientId` প্রতি লোকাল ইনস্টলে randomly মিন্ট হয় — উপরেরটা শুধু উদাহরণ।)

> **S3 লাগে না:** sbqr.api-এর compose-ডিফল্ট key-custody provider (`PlainFile`) আসলে একটা dev-stub যেটা প্রথম সাইনিংয়েই `NotImplementedException` ছোঁড়ে। `tmp/sbqr-real-compose.override.yml` তাই সেটাকে `PemVault`-এ override করে — এনক্রিপ্টেড ফাইল-ভিত্তিক Ed25519 vault, একটা named Docker volume-এ (`sbqr.key-vault`)। কোনো ধাপেই AWS S3 বা কোনো S3-emulator লাগে না।

> **ফ্রেশ শুরু করতে চাইলে:** `bash tmp/provision-real-sbqr.sh --reset` (ভলিউম মুছে আবার provision করে)।

### ধাপ ২.৩ — FI Gateway / BFF (Panel 2)

script-এর লেখা `tmp/.env-real` সরাসরি সোর্স করুন — কিছু হাতে বদলাতে হবে না:

```bash
set -a; . tmp/.env-real; set +a
dotnet run --project src/SBQR.FiGateway.Api --no-launch-profile
```

**PowerShell-এ:**

```powershell
Get-Content tmp/.env-real | ForEach-Object {
  if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
  $name, $val = $_ -split '=', 2
  Set-Item -Path "Env:$name" -Value $val
}
dotnet run --project src/SBQR.FiGateway.Api -c Release --no-build --no-launch-profile
```

`--no-launch-profile` বাদ দেবেন না — নইলে `launchSettings.json` আপনার env var-গুলো silently overwrite করে দেবে।

**বুট সফল হলে দেখবেন:**

```
info: SBQR.FiGateway.Api.Platform.TokenPrewarmService[0]
      Token pre-warm service starting.
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:8080
```

### ধাপ ২.৪ — স্বাস্থ্য যাচাই (Panel 3)

```bash
curl -s http://localhost:8080/health/ready | jq .
curl -s http://localhost:8080/debug/platform-token | jq .
```

`hasToken: true` মানে BFF সত্যিই **real sbqr.api**-এর `/v1/oauth/token`-থেকে একটা প্ল্যাটফর্ম-টোকেন এনে ক্যাশ করেছে। (real api-র টোকেনের আয়ু ~৬০০ সেকেন্ড, BFF ৮০%-এ — অর্থাৎ প্রায় ৮ মিনিটে — refresh করে, তাই `lifetimeSeconds` সবসময় ৪৮০-এর কাছাকাছি বা কম দেখাবে।) এখান থেকেই আসল টেস্টিং শুরু।

**পরের ধাপ:** §৩-এ পুরো গল্পটা (Alice → Bob, টাকা চাওয়া থেকে পাঠানো পর্যন্ত) চালিয়ে দেখুন।

---

## ৩. বাস্তব-জগতের সিমুলেশন

**দৃশ্য:** Alice-এর কাছে Bob-এর ৫০০ টাকা পাওনা। Alice নিজের ফোনে (নিজের মোবাইল-ডেটা, ঢাকা ব্যাংক-এর নেটওয়ার্কের বাইরে) ঢাকা ব্যাংক অ্যাপ খুলে **"টাকা চাই" (Request Money)** অপশনে ৫০০ টাকার একটা QR জেনারেট করেন — এটাই `/v1/qr/generate/dynamic`। Alice সেই QR-এর স্ক্রিনশট WhatsApp-এ **Bob**-কে পাঠান (এই পাঠানোটা আমাদের সিস্টেমের বাইরে, শুধু মানুষে-মানুষে)। Bob তার নিজের ফোনে ঢাকা ব্যাংক অ্যাপ খুলে QR স্ক্যান করেন — এটাই `/v1/qr/validate`, যেটা Alice-এর নাম ও পরিচয় sbqr.api-এর সিগনেচার-যাচাই দিয়ে নিশ্চিত করে (৫০০ টাকা অ্যামাউন্টটা `validate`-এর JSON রেসপন্সে ফেরত আসে না — সেটা QR-এর `qrPayload` স্ট্রিং-এর ভেতরেই এনকোড থাকে, §৬-৭-এ প্রমাণসহ), Bob "Pay" চাপার আগে কনফার্ম করার জন্য।

| # | কী ঘটছে | কে/কোথায় | কল |
|---|---|---|---|
| ১ | **Alice** ঢাকা ব্যাংক অ্যাপে লগইন করেন (§৫, মোবাইল বা ওয়েব ট্র্যাক) | Alice-এর ফোন ↔ ঢাকা ব্যাংক IdP | — |
| ২ | Alice ৫০০ টাকার একটা "টাকা চাই" QR জেনারেট করেন (§৬) | Alice-এর ফোন → **BFF** → SBQR P2P | `POST /v1/qr/generate/dynamic` |
| ৩ | Alice QR-এর স্ক্রিনশট Bob-কে পাঠান | *(সিস্টেমের বাইরে — WhatsApp/সামনাসামনি)* | — |
| ৪ | **Bob** আলাদাভাবে লগইন করেন (§৫) | Bob-এর ফোন ↔ ঢাকা ব্যাংক IdP | — |
| ৫ | Bob QR স্ক্যান করেন → app Alice-এর নাম+অ্যামাউন্ট দেখায় (§৭) | Bob-এর ফোন → **BFF** → SBQR P2P | `POST /v1/qr/validate` |
| ৬ | Bob "Pay" চাপেন | *core banking-এর কাজ — এই সিস্টেমের বাইরে, নিচে নোট দেখুন* | — |

**লক্ষ্য করুন:** প্রতিটা ধাপে "কী ঘটল" প্রশ্নের উত্তর **BFF-এর ভেতরে** — SBQR P2P-এর রেসপন্স এখানে শুধু *প্রমাণ* যে BFF সঠিকভাবে একটা real upstream-এর সাথে কথা বলতে পেরেছে। SBQR P2P কীভাবে ভেতরে QR সাইন/ভ্যালিডেট করে, সেটা এই প্লেবুকের বিষয় না।

> ⚠️ **সীমানা স্পষ্ট করা জরুরি:** `/v1/qr/generate/*` ও `/v1/qr/validate` — এই দুটো কলই BFF/SBQR P2P-এর পুরো দায়িত্ব। ধাপ ৬-এ আসল টাকা Alice-এর অ্যাকাউন্টে জমা হওয়া (debit Bob, credit Alice) ঢাকা ব্যাংকের **core banking** সিস্টেমের কাজ — এই রিপো বা SBQR P2P কেউই সেটা করে না। এই প্লেবুক শুধু QR generate+validate পর্যন্তই যাচাই করে।

**পরের ধাপ:** এই পুরো প্রবাহ চালানোর আগে §৪-এ দেখে নিন BFF কীভাবে প্ল্যাটফর্ম-টোকেনটা পায় ও রাখে — কারণ ধাপ ২ ও ৫, দুটোতেই এটা লাগে।

---

## ৪. প্ল্যাটফর্ম-টোকেন ডিবাগিং

BFF দুই ধরনের টোকেন সামলায় — এই দুটো গুলিয়ে ফেলাই newcomer-দের #১ ভুল:

| | **User JWT (ইনবাউন্ড)** | **প্ল্যাটফর্ম-টোকেন (আউটবাউন্ড)** |
|---|---|---|
| দিক | app → BFF | BFF → sbqr.api |
| কে প্রমাণ করে | নির্দিষ্ট একজন end-user (Alice বা Bob) | FI tenant নিজে (ঢাকা ব্যাংক) |
| কোথায় থাকে | কোথাও সংরক্ষিত হয় না, প্রতি রিকোয়েস্টে যাচাই হয় | শুধু BFF-এর মেমোরিতে (singleton ক্যাশ), কখনো disk/client-এ না |

### কোড কোথায়

`Platform/PlatformTokenManager.cs` (ক্যাশ + single-flight), `Platform/PlatformAuthHandler.cs` (401 হলে force-refresh + একবার retry), `Platform/TokenPrewarmService.cs` (বুট-টাইম fetch)।

### ক্যাশ পরীক্ষা করুন

```bash
curl -s http://localhost:8080/debug/platform-token | jq .
```

`lastRefreshedUtc` মিলিয়ে দেখুন sbqr.api-র real লগে (`docker logs sbqr.api -f`) ঠিক এই সময়ে একটা `POST /v1/oauth/token` এন্ট্রি আছে কিনা। দ্বিতীয়বার এই curl চালালে নতুন কোনো `/v1/oauth/token` কল **হবে না** — কারণ টোকেন এখনো ক্যাশে আছে (single-flight/reuse প্রমাণ)। লক্ষ্য করুন: Alice ও Bob দুইজনের কলই **একই** ক্যাশড টোকেন ব্যবহার করে — কারণ এটা tenant-লেভেল (ঢাকা ব্যাংক), user-লেভেল না।

### ৪০১ force-refresh (এখানে ইচ্ছাকৃতভাবে fake ব্যবহার — Mode F)

Real sbqr.api-কে অন-ডিমান্ড ৪০১ দিতে বলা যায় না, তাই এই ড্রিলের জন্য সাময়িকভাবে **পুরো Mode F**-এ (fake sbqr.api, `tmp/.env-dev`) সুইচ করুন — Docker-এ চলা real sbqr.api বন্ধ করতে হবে না, শুধু BFF আপাতত অন্য env-এ restart হবে:

```bash
# নতুন একটা টার্মিনালে, fake (401-mode):
node tmp/fake-sbqr-api-401.js
# → fake sbqr.api (401-mode) on http://localhost:5200

# Panel 2 (BFF) বন্ধ করে .env-dev দিয়ে আবার চালু করুন:
set -a; . tmp/.env-dev; set +a
dotnet run --project src/SBQR.FiGateway.Api --no-launch-profile
```

এই fake `/v1/qr/generate/static`-এ প্রথমবার ৪০১, তারপর সবসময় ২০০ দেয়। একটা static-generate কল করুন — caller `200 OK` পাবে, কিন্তু fake-এর কনসোলে দেখবেন **দুটো** কল: প্রথমটা ৪০১, তারপর একটা `/v1/oauth/token` force-refresh, তারপর দ্বিতীয় কল ২০০। দুটো কলেই `Idempotency-Key` **একই** — এটাই প্রমাণ যে BFF রিট্রাই করেছে, নতুন ইউজার-অ্যাকশন হিসেবে না।

শেষে real sbqr.api-তে ফিরে যান — BFF আবার `.env-real` দিয়ে রিস্টার্ট করুন (Docker-এ real sbqr.api আগে থেকেই চলছিল, তাই সেটা ছুঁতে হবে না)।

**পরের ধাপ:** §৫-এ দেখুন Alice ও Bob কীভাবে নিজেদের JWT মিন্ট করেন।

---

## ৫. ইউজার সেশন: মোবাইল JWT bearer

v0.1-এ শুধু মোবাইল OIDC JWT bearer — ওয়েব cookie/password-grant ধাপটি পরবর্তী রিলিজে স্থগিত (AR §৪.৩)। Alice ও Bob **আলাদা মানুষ, আলাদা ফোন** — তাই দুইজনের JWT আলাদা রাখা হবে।

### ট্র্যাক A — মোবাইল অ্যাপ (JWT bearer)

```bash
ALICE_TOKEN=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"user-alice"}' | jq -r .token)

BOB_TOKEN=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"user-bob"}' | jq -r .token)
```

`$ALICE_TOKEN` ও `$BOB_TOKEN` — এই দুটোই §৬/§৭-এ যার যার `Authorization: Bearer` হেডারে বসবে। (dev-এ `/mint` দিয়ে সরাসরি টোকেন বানানো হচ্ছে; বাস্তব মোবাইল অ্যাপ IdP-র সাথে একটা প্রকৃত OIDC লগইন-ফ্লো করে এটা পেত।)

**এক কমান্ডে (fake mobile app client):**

```bash
node tmp/mock-mobile-app.js alice s3cret
```

> ⚠️ এই স্ক্রিপ্টটা **একজনের** নিজের generate/static → validate রাউন্ড-ট্রিপ একাই চালায় (নিজের বানানো, অ্যামাউন্ট-হীন static QR নিজেই validate করে দেখায় সব ঠিক আছে — কোড দেখুন `tmp/mock-mobile-app.js`-এর `generateStaticQr()`) — এটা ভালো **স্মোক-টেস্ট**, কিন্তু আমাদের Alice→Bob দুই-পক্ষের, অ্যামাউন্ট-সহ (dynamic QR) গল্পের জন্য না। দুই-পক্ষের গল্পের জন্য §৬/§৭-এ `$ALICE_TOKEN` ও `$BOB_TOKEN` আলাদাভাবে ব্যবহার করুন।

**পরের ধাপ:** §৬-এ Alice `$ALICE_TOKEN` দিয়ে একটা "টাকা চাই" QR generate করবেন।

---

## ৬. QR জেনারেশন

> 🧑 **আপনি এখন Alice** — ৫০০ টাকা চেয়ে একটা QR বানাচ্ছেন (mobile JWT bearer)।

**আসল contract (`rvl-secure-bqr-manager/contracts/v1.public.json`) অনুযায়ী `GenerateDynamicQrRequest`-এর প্রয়োজনীয় ফিল্ড:** `recipientName`, `recipientCity`, `recipientPan`, আর শুধু dynamic-এর জন্য **`transactionAmount`** (স্ট্রিং, যেমন `"500.00"`)। *সংশোধনী: এই ডকের আগের সংস্করণে `amountMinor`/`currency` ফিল্ড ব্যবহার করা হয়েছিল, যেটা অনুমান ছিল — আসল contract-এ এই নামে কোনো ফিল্ড নেই, নিচেরটাই প্রমাণিত শেপ।* static QR-এ `transactionAmount` লাগে না — সেটা পুনর্ব্যবহারযোগ্য, অ্যামাউন্ট-হীন QR (এইজন্যই `tmp/mock-mobile-app.js`-এর ডিফল্ট ডেমো static ব্যবহার করে, §৫ দেখুন)।

**মোবাইল (JWT bearer):**

```bash
curl -i -H "Authorization: Bearer $ALICE_TOKEN" -X POST http://localhost:8080/v1/qr/generate/dynamic \
  -H 'content-type: application/json' \
  -H 'X-Correlation-Id: trace-alice-request' \
  -d '{"recipientName":"Alice Smith","recipientCity":"Dhaka","recipientPan":"01799999999","transactionAmount":"500.00"}'
```

(`recipientName`/`recipientPan` এখানে Alice-এর **নিজের** নাম/অ্যাকাউন্ট — এটাই তো তার "টাকা চাই" রিকোয়েস্ট, টাকাটা তার কাছেই আসবে।) `201` পেলে রেসপন্সে `qrPayload`, `payloadHash`, `qrType:"DYNAMIC"`, `signatureKeyVersion` থাকবে (schema: `GenerateQrResponse`) — `qrPayload`-টা টুকে রাখুন, এটাই সেই QR যেটা Alice Bob-কে "পাঠাবেন"।

> **প্রমাণ — অ্যামাউন্ট কোথায় থাকে:** `e2e-playbook-bn.md` §৩-এ ঠিক এই ধরনের একটা কল real sbqr.api-এর বিপরীতে চালিয়ে যা পাওয়া গেছে, তার `qrPayload` স্ট্রিং-এর ভেতরেই EMVCo ট্যাগ `5406` হিসেবে অ্যামাউন্টটা বসানো আছে (`...5406500.005802BD...`) — অর্থাৎ `transactionAmount` sbqr.api নিজেই QR স্ট্রিং-এর ভেতরে এনকোড করে দেয়, আলাদা কোনো ফিল্ড হিসেবে ফেরত আসে না। এটা §৭-এ কাজে লাগবে।

**BFF কী করল (sbqr.api-র real লগে — `docker logs sbqr.api -f` — যাচাই করুন):**

| ইনবাউন্ড (Alice-এর অ্যাপ পাঠাল) | BFF যোগ করল | লগে দেখবেন |
|---|---|---|
| JWT → `user-alice` | ফরওয়ার্ড | `x-user-sub: user-alice` |
| `X-Correlation-Id: trace-alice-request` | echo | `x-correlation-id: trace-alice-request` |
| *(কিছু না)* | ক্যাশ থেকে প্ল্যাটফর্ম-টোকেন | `authorization: Bearer <token>` |
| *(কিছু না)* | derived `Idempotency-Key` | `idempotency-key: <~43-char>` |

**Idempotency প্রমাণ — real api-র প্রকৃত আচরণসহ:** ঠিক এই একই কমান্ড ৬০ সেকেন্ডের মধ্যে আবার চালান। BFF দুইবারই **একই** derived `idempotency-key` পাঠায় (deterministic derivation: user + body-hash + ৬০-সেকেন্ড bucket), আর real sbqr.api দ্বিতীয় কলটা **প্রত্যাখ্যান** করে:

```
HTTP/1.1 409 Conflict
{"error":"DUPLICATE_IDEMPOTENCY_KEY","message":"This idempotency key was already used by this tenant."}
```

BFF এই বডিটা হুবহু forward করে — এটাই Alice ভুল করে দুইবার চাপলে ডাবল-QR/ডাবল-চার্জ ঠেকানোর আসল বীমা। (আগের সংস্করণে এই আচরণটা fake দিয়ে অনুমান করা হয়েছিল; এখানে real api-র প্রমাণিত রেসপন্স।)

**পরের ধাপ:** এই `qrPayload`-টা "Bob-কে পাঠিয়ে" §৭-এ Bob হয়ে validate করুন — round-trip।

---

## ৭. QR ভ্যালিডেশন

> 🧑‍💼 **আপনি এখন Bob** — Alice-এর পাঠানো QR স্ক্যান করছেন, নিজের সেশন দিয়ে (Alice-এর সেশন না — `$BOB_TOKEN`, `$ALICE_TOKEN` না)।

§৬-এর রেসপন্স থেকে পাওয়া `qrPayload`-টা বসিয়ে:

**মোবাইল (JWT bearer):**

```bash
curl -i -H "Authorization: Bearer $BOB_TOKEN" -X POST http://localhost:8080/v1/qr/validate \
  -H 'content-type: application/json' \
  -d '{"qrPayload":"<Alice-এর generate থেকে পাওয়া qrPayload>"}'
```

**রেসপন্সে যা আসবে (schema: `ValidateQrResponse`):** `verdict`, `trustSource`, `reasonCode` (VALID হলে `null`), `institutionCode`, `payloadHash`, `recipientName`, `recipientPan`, `qrClassification`। বাস্তবে (`e2e-playbook-bn.md` §৩-এর প্রমাণিত রান, ঠিক এই ধরনের একটা dynamic P2P কলের জন্য):

```json
{"verdict":"VALID","trustSource":"TRUST_DIRECTORY","reasonCode":null,"institutionCode":"031267","payloadHash":"fa9f1af4...","recipientName":"Alice Smith","recipientPan":"01799999999","qrClassification":"P2P"}
```

(`institutionCode`/`payloadHash`-এর প্রকৃত মান আপনার নিজের tenant/রান-অনুযায়ী আলাদা হবে — উপরেরটা শেপ বোঝানোর জন্য।) `verdict: "VALID"` আর `recipientName` দেখেই Bob-এর অ্যাপ "**Alice-কে পাঠাবেন?**" কনফার্মেশন স্ক্রিন বানাতে পারে।

> ⚠️ **সংশোধনী:** এই ডকের আগের সংস্করণে বলা হয়েছিল validate রেসপন্সে টাকার অ্যামাউন্টও থাকে — **সেটা ভুল ছিল, অনুমান-নির্ভর।** `ValidateQrResponse` schema-তে অ্যামাউন্টের কোনো ফিল্ড নেই (উপরের তালিকা দেখুন)। অ্যামাউন্টটা `qrPayload`-এর ভেতরেই এনকোড থাকে (§৬-এর প্রমাণ) — Bob-এর অ্যাপ QR স্ক্যান করার মুহূর্তেই client-side সেটা পড়ে ফেলে (আলাদা কোনো API কল লাগে না); `validate` কলটা শুধু **recipient কে, সিগনেচার আসল কিনা, verdict কী** — এইটুকু sbqr.api-এর কাছ থেকে নিশ্চিত করে।

**একই headers-লজিক (§৬-এর টেবিল, তবে এবার `x-user-sub: user-bob`), শুধু একটা পার্থক্য:** sbqr.api-র real লগে (`docker logs sbqr.api -f`) **কোনো `idempotency-key` হেডার থাকবে না** — কারণ `validate` read-only, BFF কখনো এর জন্য একটা derive করে না (`QrProxyController.cs`-এ `deriveIdempotencyKey: false`)। এটাই generation বনাম validation-এর মূল পার্থক্য।

Bob এখান থেকে "Pay" চাপলে আসল টাকা-স্থানান্তর ঘটে — কিন্তু সেটা §৩-এর নোটে বলা core-banking ধাপ, এই দুটো এন্ডপয়েন্টের (generate/validate) বাইরে।

**পরের ধাপ:** §৮-এ দেখুন এই পুরো যোগাযোগটা কীভাবে সুরক্ষিত থাকে।

---

## ৮. BFF ↔ SBQR P2P: সুরক্ষিত যোগাযোগ

### আউটবাউন্ড হেডার (প্রতিটা `/v1/qr/*` কলে — Alice-এর generate হোক বা Bob-এর validate)

| Header | উৎস | উদ্দেশ্য |
|---|---|---|
| `Authorization: Bearer <platformToken>` | `PlatformTokenManager` ক্যাশ | SBQR P2P-কে প্রমাণ করা এটা কোন FI (ঢাকা ব্যাংক) |
| `Idempotency-Key` | derived অথবা client-supplied | নিরাপদ retry (§৪, §৬) |
| `X-Correlation-Id` | প্রতি-রিকোয়েস্ট GUID | cross-system trace |
| `X-User-Sub` | ইনবাউন্ড JWT-এর `sub` (Alice বা Bob) | প্ল্যাটফর্ম-সাইডে user-attribution (hashed, raw PII না) |

### mTLS/VPN — dev বনাম prod

লোকালি BFF ও real SBQR P2P plain HTTP-তে কথা বলছে (`http://localhost:5001`) — real sbqr.api-র বর্তমান লোকাল বিল্ডে mTLS সাপোর্ট নেই। প্রোডাকশনে ঢাকা ব্যাংক ↔ SBQR P2P এই লিংকটাই VPN টানেলের ভেতর দিয়ে, mTLS ক্লায়েন্ট-সার্টিফিকেট সহ যায় (`Mtls:Enabled=true`) — `MtlsConfigurator`-এ কনফিগার করা। হাতে-কলমে লোকালি mTLS দেখতে চাইলে `tmp/make-mtls-certs.sh` + `tmp/fake-sbqr-api-mtls.js` (HTTPS fake, `:5201`) দিয়ে একটা আলাদা ড্রিল আছে — ধাপে ধাপে `e2e-playbook-bn.md` §৮.৩-এ (এখানে ডুপ্লিকেট করা হলো না)।

### Contract pinning

Mode R-এ (`tmp/.env-real`, script-generated) contract pinning **চালু থাকে** — `Platform__ContractPath` সরাসরি sibling রিপো-র `rvl-secure-bqr-manager/contracts/v1.public.json`-এ পয়েন্ট করে। Mode F-এ (`tmp/.env-dev`) এই ভ্যারিয়েবল **খালি** রাখা (pinning বন্ধ)। real SBQR P2P-এর বিপরীতে টেস্ট করাটাই এখন সবচেয়ে অর্থবহ — pin-টা সত্যিকারের upstream-এর সাথেই মিলছে কিনা যাচাই হচ্ছে, একটা hand-written fake-এর সাথে না।

> **এই pinning-ই একটা real drift ধরেছিল:** real sbqr.api প্রতিটা `VALID` verdict-এ `reasonCode: null` পাঠায়, কিন্তু contract-এ সেটা OpenAPI 3.0-এর `nullable: true` হিসেবে লেখা — যেটা raw JSON Schema validator বোঝে না। Gateway এখন বুট-টাইমে সেটাকে `["string","null"]`-এ অনুবাদ করে নেয় (`PlatformContractRegistry.NormalizeOpenApiNullable`, regression test আছে) — নইলে প্রতিটা সফল validate-ই ভুলভাবে ৫০২ দিত। schema-ভাঙা upstream response পেলে BFF কী করে (৫০২ sanitized ProblemDetails) — `tmp/fake-sbqr-api-contract.js` দিয়ে ড্রিল, বিস্তারিত `e2e-playbook-bn.md` §৮.২-এ।

### সংক্ষেপে বাকি দুটো

- **Polly 5xx retry:** exponential backoff দিয়ে ৩ বার পর্যন্ত রিট্রাই; ৪xx কখনো retry হয় না (সেটা `PlatformAuthHandler`-এর কাজ)।
- **Serilog masking:** raw টোকেন বা PAN কখনো লগ হয় না — শুধু মাস্কড ভার্সন (`Bearer ***last4`, `1234****3456`)।

**পরের ধাপ:** কিছু ভুল হলে §৯ দেখুন।

---

## ৯. সমস্যা সমাধান

| লক্ষণ | সবচেয়ে সম্ভাব্য কারণ | সমাধান |
|---|---|---|
| `/health/ready` সবসময় `503`/`Unhealthy` | real sbqr.api (Docker) চালু নেই, বা ভুল `.env` ফাইল সোর্স হয়েছে | `docker logs sbqr.api --tail 30` ও `curl localhost:5001/health/live` দেখুন |
| সব রিকোয়েস্ট `401` | Panel 1 (fake IdP) রিস্টার্ট হয়েছিল — কীপেয়ার পাল্টে গেছে, পুরনো JWKS ক্যাশড | BFF (Panel 2) রিস্টার্ট করুন |
| curl-এ `'file' scheme is not supported` | পুরনো `dotnet` প্রসেস `:8080` ধরে বসে আছে, খালি `BaseUrl`-সহ | `Get-Process dotnet \| Stop-Process -Force`, তারপর `--no-launch-profile` দিয়ে আবার চালান |
| `tmp/provision-real-sbqr.sh` চালাতে `invalid_client` (বুটস্ট্র্যাপ টোকেনে) | sibling রিপোর `docker/.env`-এ পুরনো/ভাঙা bootstrap-secret hash | `bash tmp/provision-real-sbqr.sh --reset` |
| Mode R-এ `Custody handle … not present in the key store` | key-vault ভলিউম মুছে গেছে কিন্তু Postgres-এ signing key এখনো `ACTIVE` (re-mint করলে 409) | `bash tmp/provision-real-sbqr.sh --reset` |
| Bob-এর validate কলে `x-user-sub: user-alice` দেখাচ্ছে | ভুল টোকেন ব্যবহার হয়েছে (`$ALICE_TOKEN` দিয়ে Bob-এর কল করা হয়েছে) | §৭-এ `$BOB_TOKEN` ব্যবহার করছেন কিনা যাচাই করুন |

আরও proven trouble (real-incident ভিত্তিক, ১০+ entry) — [`e2e-playbook-bn.md` §৯](./e2e-playbook-bn.md#৯-ট্রাবলশুটিং-সবগুলোই-বাস্তবে-ঘটেছে)।

আরও বিস্তারিত (২৪+ কেস) — [`dev-manual-testing.md` §8](./dev-manual-testing.md#8-troubleshooting)।

---

## ১০. পরিশিষ্ট: শব্দকোষ ও চেকলিস্ট

| টার্ম | মানে |
|---|---|
| **BFF** | Backend-for-Frontend — client-এর সামনে বসা পাতলা gateway যেটা সিক্রেট আড়াল করে |
| **প্ল্যাটফর্ম-টোকেন** | BFF → sbqr.api কলে ব্যবহৃত OAuth2 টোকেন; শুধু BFF-এর মেমোরিতে |
| **User JWT** | app → BFF কলে end-user-এর (Alice/Bob) পরিচয়; bank-এর IdP ইস্যু করে |
| **single-flight** | একাধিক সমসাময়িক কল একই টোকেন-fetch শেয়ার করে |
| **Idempotency-Key** | deterministic হেডার, ডুপ্লিকেট generate-কল ঠেকায় |
| **force-refresh** | ৪০১ পেলে BFF ক্যাশড টোকেন বাতিল করে নতুন এনে একবার রিট্রাই করে |
| **contract pinning** | upstream রেসপন্স একটা ফ্রোজেন schema-র বিপরীতে যাচাই করা |

### সাইন-অফ চেকলিস্ট

- [ ] `tmp/provision-real-sbqr.sh` সফল হয়েছে, তিনটা প্যানেল (IdP, BFF, Driver) বুট হয়েছে, `/health/ready` → `200`
- [ ] `/debug/platform-token` → `hasToken: true`, sbqr.api-র real লগে (`docker logs sbqr.api`) `/v1/oauth/token` দেখা গেছে
- [ ] Alice ও Bob — দুইজনের JWT আলাদাভাবে mint করা হয়েছে (`$ALICE_TOKEN`, `$BOB_TOKEN`)
- [ ] Alice-এর QR generate/dynamic (body-তে `transactionAmount`, `amountMinor` না) → `201`, sbqr.api-র লগে সব ৪টা হেডার (auth/idempotency/correlation/`x-user-sub: user-alice`) দেখা গেছে
- [ ] একই body দুইবার generate (৬০ সেকেন্ডের মধ্যে) → same `idempotency-key`, দ্বিতীয়বার real api থেকে `409 DUPLICATE_IDEMPOTENCY_KEY`
- [ ] Bob-এর QR validate → `200`, `x-user-sub: user-bob`, `idempotency-key` **অনুপস্থিত** (প্রত্যাশিতভাবে), রেসপন্সে `recipientName: "Alice Smith"` (কিন্তু কোনো অ্যামাউন্ট ফিল্ড না — সেটা `qrPayload`-এর ভেতরে)
- [ ] Mode F fake দিয়ে ৪০১ force-refresh ড্রিল চালিয়ে দেখা হয়েছে, তারপর `.env-real`-এ (Mode R) ফেরত যাওয়া হয়েছে

সব টিক পড়লে, আপনি BFF-এর মূল আচরণগুলো — মোবাইল ও ওয়েব দুই ধরনের ক্লায়েন্টের জন্যই — নিজের চোখে দেখে নিশ্চিত হয়েছেন। এখন আরও গভীরে যেতে চাইলে [`dev-manual-testing.md`](./dev-manual-testing.md)।
