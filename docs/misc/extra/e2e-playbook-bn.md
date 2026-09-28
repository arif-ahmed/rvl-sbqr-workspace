# এন্ড-টু-এন্ড প্লেবুক — আসল ব্যবহারকারীর চোখে SBQR FI Gateway

> **এই ডকুমেন্টটি কার জন্য:** rvl-sbqr-fi-gateway-এ নতুন যোগ দেওয়া ডেভেলপার, যিনি পুরো
> QR-পেমেন্ট জার্নি নিজের হাতে চালিয়ে, লগ দেখে, ইচ্ছাকৃতভাবে ভেঙেেে ভেঙে সিস্টেমটা বুঝতে
> চান। এখানকার **প্রতিটি কমান্ড আর expected output** একটি আসল Windows + Git Bash মেশিনে
> **আসল sbqr.api-র বিরুদ্ধে** চালিয়ে verify করা।
>
> **সহোদর ডকুমেন্ট:** [`dev-e2e-simulation-playbook-bn.md`](dev-e2e-simulation-playbook-bn.md) —
> একই যাত্রার আরেক রূপায়ণ। এই সংস্করণটি command-verified এবং সম্পূর্ণরূপে
> **আসল `rvl-secure-bqr-manager` sbqr.api-কেন্দ্রিক** — কোনো fake sbqr.api নয়।
>
> **পড়ার আগে:** ব্যাকগ্রাউন্ডের "কেন" জানতে চাইলে
> [dev-onboarding-guide.md](dev-onboarding-guide.md) আর
> [0001-fi-direct-oauth-to-bff-model.md](0001-fi-direct-oauth-to-bff-model.md) (বাংলা ADR)
> পড়ুন। এখানে শুধু "কীভাবে" আছে।
>
> **v0.1 স্কোপ নোট:** ইনবাউন্ড অথ এই রিলিজে শুধু **মোবাইল OIDC JWT** (ধাপ ৩, §৫.১)।
> ওয়েব cookie/password-grant ধাপটি (§৫-এর cookie মোড) AR-2026-09-23-01 §৪.৩ অনুযায়ী পরবর্তী
> রিলিজে স্থগিত — BFF কোডবেস থেকে `/v1/auth/login`, কুকি স্কিম, IdP password-grant ক্লায়েন্ট মুছে ফেলা হয়েছে।
> এই ডকের cookie মোডের curl-recipe-গুলো আর চলবে না।

---

## ১. এক নজরে (Executive Summary)

প্রোডাকশনে এই সিস্টেমটা বসে থাকে একটা **ব্যাংকের (FI = Financial Institution) ইনফ্রায়**:

```
┌─────────────┐   HTTPS    ┌──────────────────────┐   platform token (+mTLS প্রোডাকশনে) ┌──────────────┐
│ ব্যাংকের     │ ────────▶ │  FI Gateway (BFF)     │ ────────────────────────────────────▶ │  sbqr.api     │
│ mobile/web  │            │  = এই repo            │                                     │  (RVL প্ল্যাটফর্ম)│
│ app         │ ◀────────  │  rvl-sbqr-fi-gateway  │ ◀──────────────────────────────────── │  QR ইস্যু+ভেরিফাই│
└─────────────┘            └──────────────────────┘                                     └──────────────┘
        ▲                           ▲
        │ user JWT (mobile OIDC)    │ client_credentials টোকেন নিজে নিজে নেয়,
        │ (ব্যাংকের IdP থেকে)          │ মেমোরিতে ক্যাশ করে, ৮০% আয়ুতে refresh করে
```

**দুই রকম টোকেন, দুই দিকে** — এটাই পুরো ডিজাইনের মূল কথা:

| | Inbound (app → BFF) | Outbound (BFF → sbqr.api) |
|---|---|---|
| টোকেন কে বানায় | ব্যাংকের IdP | sbqr.api নিজে (`/v1/oauth/token`) |
| ধরন | user JWT (mobile OIDC) | platform token (client_credentials) |
| কে দেখে | app ও BFF | **শুধু BFF — app কোনোদিন পায় না** (এটাই VAPT-এর P1 ফাইন্ডিং থেকে বাঁচার উপায়) |
| আয়ু | JWT ৫ মিনিট | ১০ মিনিট (৮০% = ৮ মিনিটে refresh) |

**আমরা যে দৃশ্যপট stage করব** — হুবহু প্রোডাকশনের মতো একটা দিন:

> বব (Bob) তার দোকানে একটা **QR কোড বানালেন** (রিসিভার) আর কাউন্টারে রাখলেন।
> অ্যালিস (Alice) ফোনের ক্যামেরায় QR-টা **স্ক্যান করলেন** (পেয়ার), অ্যাপ ভেরিফাই করল,
> ৳৫০০ টাকা পেমেন্ট করলেন।

**কোনটা আসল, কোনটা simulation:**

| কম্পোনেন্ট | কী ব্যবহার করবেন | কেন |
|---|---|---|
| Mobile/web app | `tmp/mock-mobile-app.js` + curl | আসল ব্যাংক-app এই repo-র scope-এর বাইরে; এটাই বাইরের client-এর ভূমিকা |
| ব্যাংকের IdP | `tmp/fake-idp.js` (:5105) — alice/s3cret, bob/bobs-pass | ব্যাংকের আসল IdP ব্যাংক-সাইডের বিষয়; এখানে সেই ভূমিকার স্ট্যান্ড-ইন |
| **FI Gateway (BFF)** | **আসল কোড, আসল বিল্ড** (:8080) | এটাই আমাদের পণ্য |
| **sbqr.api** | **আসল `rvl-secure-bqr-manager`** (:5001, Docker) — আসল Ed25519 সাইনিং, আসল Postgres, আসল contract | যা প্রোডাকশনে হবে, ঠিক তার বিরুদ্ধে টেস্ট |

**Prerequisites:** .NET 8 SDK (BFF), .NET 10 SDK + Docker (sbqr.api stack), Node 18+,
Git Bash/WSL, curl। পোর্ট `8080` (BFF), `5105` (IdP), `5001` (sbqr.api) খালি থাকতে হবে।

---

## ২. সেটআপ — দুই ধাপে চালু

দুটো জিনিস চালু করলেই আমরা তৈরি: **ধাপ ১ — sbqr.api**, তারপর **ধাপ ২ — sbqr-fi-bff**।

| কী চালু হবে | কী | ঠিকানা |
|---|---|---|
| **sbqr.api** | আসল QR প্ল্যাটফর্ম (`rvl-secure-bqr-manager`) — Docker-এ | `http://localhost:5001` |
| **sbqr-fi-bff** | এই repo-র আসল gateway | `http://localhost:8080` |

### ধাপ ১ — sbqr.api চালু করুন

```bash
cd rvl-sbqr-fi-gateway
bash tmp/provision-real-sbqr.sh
```

- প্রথমবার ~৫–৮ মিনিট (Docker image build); পরেরবার এক মিনিটের কম।
- Script-টা নিজে করে দেয়: Postgres + migration → tenant + সাইনিং key তৈরি →
  **`tmp/.env-real`** লেখে (BFF-এর env)। বারবার চালানো যায় (idempotent)।

সফল হলে শেষের output:

```
✔ sbqr.api live at http://localhost:5001
✔ tmp/.env-real written
✔ Mode R stack is ready:
   fake IdP           → http://localhost:5105  (node tmp/fake-idp.js)
   tenant clientId    → 031267-77214637
```

যাচাই করুন:

```bash
curl -s http://localhost:5001/health/live    # {"status":"Healthy",...}
docker logs -f sbqr.api                      # লগ ফলো — এই terminal খোলা রাখুন
```

### ধাপ ২ — sbqr-fi-bff চালু করুন

আগে ছোট্ট helper একটা: **fake IdP** (ব্যাংকের IdP-এর স্ট্যান্ড-ইন — BFF-এর লগইন যাত্রার
জন্য), নিজের একটা terminal-এ:

```bash
node tmp/fake-idp.js        # :5105 — ইউজার: alice/s3cret, bob/bobs-pass
```

এবার BFF, আরেকটা terminal-এ:

```bash
set -a; . tmp/.env-real; set +a
dotnet run --project src/SBQR.FiGateway.Api --no-launch-profile
```

যাচাই করুন:

```bash
curl -s http://localhost:8080/health/live     # 200
curl -s http://localhost:8080/health/ready    # 200 — platform token এলে
curl -s http://localhost:8080/debug/platform-token
```

শেষ command-টায় আসল api থেকে আসা টোকেন দেখবেন:

```json
{"hasToken":true,"expiresAtUtc":"2026-09-23T21:53:57.1...","lifetimeSeconds":464.03,...}
```

> `lifetimeSeconds: 464` কোথা থেকে? আসল api ৬০০ সেকেন্ডের টোকেন দেয়, gateway ৮০% পার
> হলেই refresh করে → 600 × 0.8 = 480, ব্যালান্স 464 মানে এইমাত্র নেওয়া হয়েছে।

### এখন আপনার ডেস্কে যা চলছে

| Terminal | কী চলছে |
|---|---|
| ১ | fake IdP (`:5105`) — ব্যাংক-সাইড লগইন সিমুলেশন |
| ২ | আসল sbqr.api-র লগ (`docker logs -f sbqr.api`, service `:5001`) |
| ৩ | BFF (`:8080`) — এই repo-র কোড, লাইভ |
| (ঐচ্ছিক) | Adminer: `http://localhost:8081` — Postgres-এ যা ঘটছে চোখে দেখতে |

> **Windows + Git Bash ব্যবহারকারীদের জন্য:** `.env-real`-এ
> `MSYS2_ENV_CONV_EXCL=Platform__TokenEndpoint` লাইনটা ইতিমধ্যে আছে — এটা ছাড়া Git Bash
> `/v1/oauth/token`-কে path ভেবে `C:/Program Files/Git/v1/...` বানিয়ে ফেলে, তখন প্রতিটা
> QR call 500 দেয় (`NotSupportedException: 'file' scheme`)। বিস্তারিত §৯-এ।

---

## ৩. রিয়েল-ওয়ার্ল্ড যাত্রা: বব QR বানালেন → অ্যালিস পেমেন্ট করলেন

এই section-টা এক নাগাড়ে করুন — curl-এর প্রতিটা উত্তরের সাথে "এখন অন্য pane-গুলোতে কী
হচ্ছে" মিলিয়ে দেখুন।

### ধাপ ১ — বব লগইন (mobile app তার JWT পায়)

```bash
# v0.1: শুধু মোবাইল OIDC JWT bearer — native app নিজের IdP-র সাথে OAuth/PKCE করে JWT আনে
BOB=$(curl -fsS -X POST http://localhost:5105/mint -H "Content-Type: application/json" \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"user-bob"}' \
  | node -e "console.log(JSON.parse(require('fs').readFileSync(0)).token)")
```

ভেতরে ঘটলো: বব-এর mobile app এই `Bearer $BOB` টোকেন দিয়েই BFF-এর কাছে পরিচয়
প্রমাণ করে। BFF প্রতিটা request-এ JWT signature/issuer/audience যাচাই করে
(`Auth/ConfigureJwtBearerOptions.cs`)।

### ধাপ ২ — বব QR বানালেন (রিসিভার সাইড)

```bash
curl -s -H "Authorization: Bearer $BOB" -X POST http://localhost:8080/v1/qr/generate/dynamic \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: bob-qr-001" \
  -d '{"recipientName":"Bob The Merchant","recipientCity":"Dhaka",
       "recipientPan":"01711111111","transactionAmount":"500.00"}' | tee tmp/bob-qr.json
```

```json
{"qrPayload":"00020101021226470014bd.org.bb.npsb010203020412670311017111111115204482953030505406500.005802BD5916Bob The Merchant6005Dhaka80660014bd.org.bb.npsb0144SW51/9eRN...81660014bd.org.bb.npsb0144Ku6ngPMX...630477A9","payloadHash":"fa9f1af4...","qrType":"DYNAMIC","signatureKeyVersion":1}
```

HTTP **201**। এই `qrPayload`-টাই আসল জিনিস — tenant-এর **আসল Ed25519 প্রাইভেট key দিয়ে
সাইন করা** EMVCo/BanglaQR স্ট্রিং (Tag 80/81-এ base64 signature, শেষে Tag 63 CRC)।
এটাই প্রোডাকশনে দোকানের কাউন্টারে ছাপা QR ছবির ভেতরের কনটেন্ট।

এখন Pane 2 (`docker logs -f sbqr.api`) দেখুন — request প্রক্রিয়ার লগ চলছে; ব্যর্থতার
লগে (§৯-এর মতো) BFF-এর পাঠানো `idempotency_key=…` পর্যন্ত দেখা যায়।

### ধাপ ৩ — "স্ক্যান": QR হাতবদল

পেয়ারের ফোন ক্যামেরা QR পড়ে `qrPayload` স্ট্রিংটা পায়। Simulation-এ "স্ক্যান" মানে
স্ট্রিংটা বব-এর app থেকে অ্যালিসের app-এ কপি হওয়া:

```bash
QR=$(node -e "console.log(require('./tmp/bob-qr.json').qrPayload)")
echo "${QR:0:40}…  (${#QR} chars captured)"
```

### ধাপ ৪ — অ্যালিস লগইন করে QR ভেরিফাই করলেন (পেয়ার সাইড, JWT auth)

```bash
# JWT-bearer ধাঁচের লগইন — native app এভাবেই করে
ALICE=$(curl -fsS -X POST http://localhost:5105/mint -H "Content-Type: application/json" \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"user-alice"}' \
  | node -e "console.log(JSON.parse(require('fs').readFileSync(0)).token)")

curl -s -X POST http://localhost:8080/v1/qr/validate \
  -H "Authorization: Bearer $ALICE" -H "Content-Type: application/json" \
  -d "{\"qrPayload\":\"$QR\"}"
```

```json
{"verdict":"VALID","trustSource":"TRUST_DIRECTORY","reasonCode":null,"institutionCode":"031267","payloadHash":"fa9f1af4...","recipientName":"Bob The Merchant","recipientPan":"01711111111","qrClassification":"P2P"}
```

**verdict `VALID`** — অ্যালিসের অ্যাপ এবার নিশ্চিন্তে "Bob The Merchant-কে ৳৫০০ পাঠানো"
স্ক্রিনটা দেখাতে পারে। খেয়াল করুন: signature verify হয়েছে trust directory থেকে পাওয়া
tenant-এর **পাবলিক key** দিয়ে, recipient-এর নাম-PAN QR-এর ভেতর থেকেই এসেছে।

### ৩.১ আসল failure drill-গুলো

**Drill A — tamper: শেষ অক্ষর বদলান → CRC ধরে ফেলল:**

```bash
curl -s -X POST http://localhost:8080/v1/qr/validate -H "Authorization: Bearer $ALICE" \
  -H "Content-Type: application/json" \
  -d "{\"qrPayload\":\"${QR:0:-1}7\"}"     # শেষ char ৭ করে দিলাম
```

```json
{"verdict":"STRUCTURAL_INVALID","reasonCode":"CRC_MISMATCH",...,"qrClassification":"UNKNOWN"}
```

**Drill B — আসল forger: signature নষ্ট করে CRC নিজে ঠিক করা** (দেখানোর জন্য যে
signature-check CRC-র উপরে আলাদা সুরক্ষা):

```bash
node tmp/forge-invalid-qr.js tmp/bob-qr.json > tmp/qr-forged.txt
FORGED=$(cat tmp/qr-forged.txt)
curl -s -X POST http://localhost:8080/v1/qr/validate -H "Authorization: Bearer $ALICE" \
  -H "Content-Type: application/json" -d "{\"qrPayload\":\"$FORGED\"}"
```

```json
{"verdict":"INVALID_SIGNATURE","trustSource":"TRUST_DIRECTORY","reasonCode":"SIGNATURE_MISMATCH",...}
```

গঠনগতভাবে নিখুঁত QR, CRC-ও ঠিক — কিন্তু Ed25519 signature মিলল না। ব্যাস, ফেল।

**Drill C — replay: একই `requestId` দুবার** (আসল api-র single-use replay guard):

```bash
for i in 1 2; do
  curl -s -X POST http://localhost:8080/v1/qr/validate -H "Authorization: Bearer $ALICE" \
    -H "Content-Type: application/json" \
    -d "{\"qrPayload\":\"$QR\",\"requestId\":\"replay-drill-001\"}" | head -c 80; echo; done
```

```
{"verdict":"VALID",...}            ← প্রথমবার
{"verdict":"REQUEST_REPLAYED","reasonCode":"REPLAY_DETECTED",...}   ← একই requestId আবার
```

**Drill D — idempotency: একই body দুবার** — BFF নিজেই HMAC দিয়ে একই `Idempotency-Key`
বানায় (৬০ সেকেন্ডের bucket-এ), আসল api সেটা মনে রাখে:

```bash
BODY='{"recipientName":"Idem Test","recipientCity":"Dhaka","recipientPan":"01711111111","transactionAmount":"100.00"}'
curl -s -H "Authorization: Bearer $BOB" -X POST http://localhost:8080/v1/qr/generate/dynamic \
  -H "Content-Type: application/json" -d "$BODY" -o /dev/null -w 'first: %{http_code}\n'
curl -s -H "Authorization: Bearer $BOB" -X POST http://localhost:8080/v1/qr/generate/dynamic \
  -H "Content-Type: application/json" -d "$BODY"
```

```
first: 201
{"error":"DUPLICATE_IDEMPOTENCY_KEY","message":"This idempotency key was already used by this tenant."}   (409)
```

এটাই ডাবল-চার্জ ঠেকানোর বীমা: নেটওয়ার্ক retry-তে একই অনুরোধ দুবার গেলেও দ্বিতীয়টা
আসল api নাকচ করে দেয়।

**Drill E — আসল api-র কড়া যাচাই:** static QR-এ `transactionAmount` পাঠালে (ওটা শুধু
dynamic-এর ফিল্ড) আসল api 400 দেয়, BFF সেটা **হুবহু** পাঠিয়ে দেয়:

```json
{"type":".../rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,
 "errors":{"$.transactionAmount":["The JSON property 'transactionAmount' could not be mapped..."]}}
```

### ৩.২ এক-কমান্ড shortcut

```bash
node tmp/mock-mobile-app.js alice s3cret    # JWT-bearer ধাঁচে generate/static → validate
```

আসল api-র বিপরীতে চলে (body-গুলো pinned contract-মতো)। প্রোডাকশনে FI app
বানানোর আগে এটাই আপনার reference client।

---

## ৪. ডিবাগিং: প্ল্যাটফর্ম টোকেন — আসা, মেমোরিতে থাকা, refresh হওয়া

### ৪.১ জীবনচক্র এক নজরে

| ঘটনা | কোড | কী দেখবেন |
|---|---|---|
| Boot-এ prewarm | `Platform/TokenPrewarmService.cs` | ০–১ সেকেন্ড random jitter-এর পর প্রথম `/v1/oauth/token` call |
| মেমোরি-ক্যাশ | `Platform/PlatformTokenManager.cs` (singleton, single-flight lock) | `/debug/platform-token` → `hasToken:true` |
| ৮০% আয়ুতে refresh | `Platform/TokenLifetimePolicy.cs` (`RefreshRatio=0.8`) | আসল api-তে টোকেন ৬০০s → প্রতি ~৮ মিনিটে `lastRefreshedUtc` বদলায় |
| 401 পেলে force-refresh | `Platform/PlatformAuthHandler.cs` | §৪.৩ |
| টোকেন না থাকলে | `Observability/PlatformTokenHealthCheck.cs` | `/health/ready` → 503 (কারণ `platform-token` check fail) |

```bash
watch -n 2 curl -s http://localhost:8080/debug/platform-token   # বা নিজে বারবার চালান
```

### ৪.২ লাইভ দেখুন

BFF চালু রেখে ৮–৯ মিনিট অপেক্ষা করুন — `lastRefreshedUtc` এগিয়ে যাবে, মাঝে কোনো QR call
fail করবে না। Refresh-এর মুহূর্তে আসল api-ও সেই নতুন token request পায় (Pane 2-এর
`docker logs -f sbqr.api`-তে দেখুন), আর টোকেনের আসল প্রমাণ পাবেন Postgres-এ — Adminer
(`http://localhost:8081`) খুলে `sbqr_app` DB-র `tenant_configurations` টেবিলে
credential-এর অবস্থা দেখুন।

> **সতর্কতা:** আসল api-র token endpoint-এ rate limit **10 request/60s**। BFF-এর স্বাভাবিক
> চক্র এতে সমস্যা করে না, কিন্তু পরপর অনেকবার BFF restart করলে 429 দেখতে পারেন —
> এটাই আসল প্রোডাকশন আচরণ।

### ৪.৩ fault-আচরণ (401-refresh, ৫xx-retry) কোথায় যাচাই হয়

আসল api সুস্থ থাকায় ইচ্ছাকৃত 401/503 ঘটানো যায় না — এবং গাইডে সেটা করার দরকারও নেই,
কারণ gateway-এর এই আচরণগুলো **automated test suite-এ প্রমাণিত** (খুলে পড়ুন, ছোট
ফাইল):

| আচরণ | প্রমাণ | মূল বক্তব্য |
|---|---|---|
| upstream 401 → force-refresh → **একবার** retry | `tests/.../Platform/PlatformAuthHandlerTests.cs` | retry-তে নতুন টোকেন কিন্তু **একই Idempotency-Key** — তাই ডাবল-ইস্যু হয় না |
| ৫xx/timeout → Polly retry (backoff+জিটার) | `tests/.../Resilience/ResiliencePipelineTests.cs`, `Integration/ResilienceIntegrationTests.cs` | 503 তিনবার চেষে যা পায় তা-ই caller-কে; 401 কখনো Polly-retry হয় না |
| contract না মিললে sanitized 502 | `tests/.../Integration/ContractPinningIntegrationTests.cs` | আসল upstream body (PAN/PII) caller-এর কাছে যায় না |
| mTLS client-cert load/fail-fast | `tests/.../Mtls/MtlsConfiguratorTests.cs` | cert না থাকলে boot-ই ব্যর্থ |

```bash
dotnet test tests/SBQR.FiGateway.Tests --nologo    # 118/118 পাস দেখুন
```

---

## ৫. অ্যাপের লগইন ও সেশন — JWT bearer

v0.1-এ একমাত্র প্রবেশপথ: মোবাইল OIDC JWT bearer — native app নিজের IdP-র সাথে OAuth/PKCE
flow করে JWT আনে (`Authorization: Bearer …`)। BFF প্রতিটা request-এ JWT-র
signature/issuer/audience যাচাই করে (`Auth/ConfigureJwtBearerOptions.cs`)। ওয়েব
cookie/password-grant ধাপটি পরবর্তী রিলিজে স্থগিত (AR §৪.৩)।

Negative test করে দেখুন:

```bash
curl -s -X POST localhost:8080/v1/qr/validate -d '{"qrPayload":"x"}' \
  -o /dev/null -w 'anonymous: %{http_code}\n'                  # 401

BAD=$(curl -fsS -X POST localhost:5105/mint -H "Content-Type: application/json" \
  -d '{"iss":"http://localhost:5105","aud":"someone-else","sub":"user-alice"}' \
  | node -e "console.log(JSON.parse(require('fs').readFileSync(0)).token)")
curl -s -X POST localhost:8080/v1/qr/validate -H "Authorization: Bearer $BAD" \
  -d '{"qrPayload":"x"}' -o /dev/null -w 'wrong audience: %{http_code}\n'      # 401
```

লগইনের পর `curl -s -X POST localhost:8080/debug/auth -H "Authorization: Bearer $BOB"`
দিয়ে BFF দৃষ্টিতে নিজের identity দেখুন: `{"isAuthenticated":true,"userSub":"user-bob"}`
*(দ্রষ্টব্য: `/debug/auth` Slice 6-এর debug endpoint — production-এ নেই।)*

---

## ৬. QR জেনারেশন ফ্লো — hop by hop

```
বব-অ্যাপ          FI Gateway (BFF)                        sbqr.api (আসল)
   │  POST /v1/qr/generate/dynamic     │                                      │
   │  Authorization: Bearer <jwt>      │                                      │
   │───────────────────────────────────▶│                                      │
   │                                   │ 1. JWT যাচাই → userSub=user-bob     │
   │                                   │ 2. X-Correlation-Id echo/mint       │
   │                                   │ 3. Idempotency-Key derive (HMAC)    │
   │                                   │ 4. ক্যাশ করা platform token লাগানো │
   │                                   │   POST /v1/qr/generate/dynamic      │
   │                                   │   Authorization: Bearer <platform>  │
   │                                   │   Idempotency-Key / X-Correlation-Id│
   │                                   │   X-User-Sub: user-bob              │
   │                                   │─────────────────────────────────────▶│
   │                                   │         Ed25519 সাইন + Postgres persist│
   │                                   │◀───────────── 201 GenerateQrResponse│
   │                                   │ 5. contract pinning যাচাই (schema)  │
   │◀───────────────────────────────────│                                      │
   │  201 + qrPayload (verbatim)       │
```

**BFF যা পাঠায়, আসল api যেখানে রেখে রাখে** — প্রমাণ দুই জায়গায়:

1. `docker logs sbqr.api` — স্বাভাবিক লগের পাশাপাশি ব্যর্থতার লগে সরাসরি
   `idempotency_key=…` দেখা যায় (§৯-এর উদাহরণ)।
2. Adminer (`http://localhost:8081`, server `sbqr.postgres`, db `sbqr_app`): `qr_generations`
   টেবিলে প্রতিটা ইস্যুর সারি — tenant-scoped idempotency unique index-ই Drill D-এর 409-এর
   জন্মদাতা। `audit_logs` টেবিলে correlation সূত্র।

| Header | কে বসায় | মানে |
|---|---|---|
| `authorization: Bearer <platform-token>` | `PlatformAuthHandler` | app-এর টোকেন **নয়** — BFF-নিজের server-to-server টোকেন |
| `idempotency-key: 1cGd5RnF…` (Base64Url) | `IdempotencyKeyDeriver` | HMAC-SHA256(salt, `userSub \| SHA256(body) \| সময়-bucket`) — একই ব্যবহারকারী+একই body+৬০s = একই key |
| `x-correlation-id` | `CorrelationIdMiddleware` | app-এর পাঠানোটা echo, না থাকলে নতুন GUID — তিনটা সিস্টেমে এক লাইনে খোঁজা যায় |
| `x-user-sub: user-bob` | `HttpPlatformQrClient` | কোন ব্যবহারকারীর হয়ে কাজ হচ্ছে, downstream audit-এর জন্য |

Idempotency নিজে যাচাই করুন: ধাপ ২-এর body হুবহু আবার পাঠালে (৬০ সেকেন্ডের ভেতর)
**একই key** তৈরি হয়ে 409 খাবে (Drill D); ৬০ সেকেন্ড পেরোলে bucket বদলে **ভিন্ন** key —
নতুন QR ইস্যু হবে। Caller নিজে `Idempotency-Key` header দিলে BFF সেটাই verbatim পাঠায়।

---

## ৭. QR ভ্যালিডেশন ফ্লো — পেয়ার সাইড

জেনারেশনের মতোই একই পাইপলাইন, মাত্র দুটো পার্থক্য:

1. **Idempotency-Key derive হয় না** — validate read-only; caller দিলে তবেই যায়
   (`QrProxyController.Validate` → `deriveIdempotencyKey: false`)।
2. Pin হয় `ValidateQrResponse` schema-র বিরুদ্ধে, আর upstream-এর 2xx **হুবহু** ফিরে আসে
   (status code সহ) — তাই §৩-এর সব verdict আনডিম কোডে অ্যাপ পর্যন্ত পৌঁছায়।

Response field গাইড:

| Field | মানে |
|---|---|
| `verdict` | `VALID` / `INVALID_SIGNATURE` / `STRUCTURAL_INVALID` / `KEY_NOT_FOUND` / `REQUEST_REPLAYED` |
| `reasonCode` | কেন — `SIGNATURE_MISMATCH`, `CRC_MISMATCH`, `REPLAY_DETECTED`; VALID হলে `null` |
| `trustSource` | `TRUST_DIRECTORY` = trust directory-র পাবলিক key দিয়ে verify হয়েছে |
| `institutionCode` / `recipientName` / `recipientPan` | QR ইস্যুকারী প্রতিষ্ঠান + টাকা যার অ্যাকাউন্টে যাবে |
| `payloadHash` | যে payload যাচাই হলো তার hash — অ্যাপে দেখানো তথ্যের সাথে মেলানোর জন্য |

---

## ৮. BFF ↔ sbqr.api সিকিউর কমিউনিকেশন — চারটি স্তর

### ৮.১ স্তর ১: প্ল্যাটফর্ম টোকেন

`client_id/client_secret` শুধু BFF-এর env-এ (`tmp/.env-real`), কখনো app-এর কাছে যায় না
— §৪-এ লাইভ দেখেছেন। Serilog-এর `SensitiveDataMaskingEnricher` লগে এসব মান
`***` দিয়ে ঢেকে দেয় (unit-test করা: `tests/.../SensitiveDataMaskingEnricherTests.cs`)।

### ৮.২ স্তর ২: Contract pinning — schema না মিললে থামবেই

`.env-real`-এ `Platform__ContractPath` = sibling repo-র `v1.public.json` — অর্থাৎ এই গাইডের
প্রতিটা QR call-এ pinning **চালু আছে**: upstream-এর প্রতিটা 2xx response আগে
`components/schemas` দিয়ে যাচাই হয়, তারপরই অ্যাপে যায়। schema-ভাঙা response পেলে
caller পায় sanitized `502 ProblemDetails` (upstream body/PAN কখনো লিক হয় না)।

এই নিরাপত্তাটা শুধু কাগজে-কলমে নয় — **এই গাইড তৈরির সময় এটাই একটা আসল drift ধরেছিল:**
আসল api VALID verdict-এ `reasonCode: null` দেয়, কিন্তু contract OpenAPI-৩.০-এর
`nullable:true` লিখেছিল — যেটা JSON Schema validator-এর কাছে অর্থহীন, ফলে সব ভালো
response-ই 502 হচ্ছিল। Gateway এখন `nullable:true`-কে `["string","null"]`-এ অনুবাদ করে
(`PlatformContractRegistry.NormalizeOpenApiNullable`; regression test:
`ContractValidatorTests.Validate_ValidateQrResponse_null_reasonCode_passes`)।
ভবিষ্যতে sbqr.api আসল contract থেকে সরে গেলে এই স্তরটাই প্রথম ধরবে।

### ৮.৩ স্তর ৩: mTLS — client certificate

লোকাল আসল api plain HTTP (TLS ingress প্রোডাকশন ডিপ্লয়মেন্টের কাজ), তাই এই গাইডে mTLS
হাতে-কলমে দেখার রাস্তা নেই। যা জানা দরকার:

- কনফিগ: `Mtls__Enabled=true` + `Mtls__CertPath` (PFX) + `Mtls__CertPassword` —
  প্রোডাকশনে RVL-এর দেওয়া client certificate বসবে; BootGuard cert-না-থাকলে boot-ই
  ব্যর্থ করে দেয়।
- Cert-টা `MtlsConfigurator` শুধু **PlatformQr** client-এ attach করে — token client-এ
  নয়। sbqr.api যদি সব endpoint-এ cert চায়, token client-কেও লাগবে — platform টিমের
  সাথে agree করার open প্রশ্ন।
- PFX load/fail-fast আচরণ automated test-এ প্রমাণিত: `tests/.../Mtls/MtlsConfiguratorTests.cs`।

### ৮.৪ স্তর ৪: Resilience + স্বাস্থ্য

- ৫xx/timeout → Polly retry, 401 → force-refresh — দুটো আলাদা কৌশল, ইচ্ছাকৃত
  (§৪.৩-এর টেবিল)।
- `/health/live` = প্রসেস বেঁচে আছে কিনা (k8s liveness); `/health/ready` = platform
  টোকেন আছে কিনা (readiness — না থাকলে traffic আসা বন্ধ রাখা উচিত)।
- BootGuard (প্রোডাকশনে): `set-me…` sentinel, `.invalid` URL, বা mTLS চালু-কিন্তু-cert-নেই
  হলে boot-ই ব্যর্থ — ভুল কনফিগ নিয়ে ট্রাফিক সামলানোর আগেই ধরা পড়ে।

---

## ৯. ট্রাবলশুটিং (সবগুলোই বাস্তবে ঘটেছে)

| উপসর্গ | কারণ | সমাধান |
|---|---|---|
| QR call-এ 500, লগে `NotSupportedException: The 'file' scheme is not supported` | Git Bash (MSYS) `/v1/...`-দেরমতো env value-কে Windows path বানিয়ে ফেলে: dotnet দেখে `C:/Program Files/Git/v1/oauth/token` | `.env-real`-এ `MSYS2_ENV_CONV_EXCL=Platform__TokenEndpoint` (already আছে); অথবা PowerShell থেকে চালান। যাচাই: `cmd //c "echo %Platform__TokenEndpoint%"` |
| `ready 503` সারাক্ষণ, `/debug/platform-token`-এ `hasToken:false` | prewarm ব্যর্থ — আসল api নেই/ঠিকানা ভুল | `curl http://localhost:5001/health/live`, `docker logs sbqr.api`; `Platform__BaseUrl` ঠিক কিনা দেখুন |
| `EADDRINUSE :::5105/8080` বা পোর্ট ব্লক | পুরনো session-এর node/dotnet পোর্ট ধরে আছে | `netstat -ano | grep :8080` → PID খুঁজে `Stop-Process -Id <pid> -Force` |
| `dotnet build`: "file is locked by SBQR.FiGateway.Api" | চালু BFF exe lock করেছে | আগে BFF বন্ধ করে build করুন |
| 400 `Institution Type (Tag 26 sub 01) must be two digits 00–05` | tenant-এর institution code-এর প্রথম ২ অঙ্ক (type) অবৈধ | নতুন করে provision করুন — script এখন `03…` prefix দেয়। (লক্ষণীয়: tenant API এটা ধরেনি, QR codec ধরেছে — cross-repo validation gap, sbqr.api টিমকে জানানো যায়) |
| `{"error":"GENERATION_FAILED",…reference=…}` | আসল api-র ভেতরে কিছু ব্যর্থ | `docker logs sbqr.api --since 5m`-এ reference id দিয়ে grep করুন — আসল stack পাবেন |
| `Custody handle … not present in the key store` | key-vault volume মুছে গেছে কিন্তু Postgres-এ key এখনো ACTIVE (re-mint 409, rotate stub) | stack-এ এখন named volume (`sbqr.key-vault`) — recreate নিরাপদ। এমন অবস্থায় পড়লে `bash tmp/provision-real-sbqr.sh --reset` |
| `KEY_NOT_ACTIVE` (422) | tenant-এর সাইনিং key নেই | script এখন key বানিয়েই activate করে; নিজে করতে চাইলে §২.১-এর admin ধাপগুলো |
| `invalid_client` (বুটস্ট্র্যাপ টোকেনে) | docker/.env-এর PHC hash পুরনো/escape ভাঙা | `bash tmp/provision-real-sbqr.sh --reset` |
| আসল api-তে 429 (token endpoint) | rate limit 10/60s | BFF বারবার restart করবেন না; এক মিনিট অপেক্ষা |
| login-এ 400 invalid_grant আশা করে 401 পাচ্ছেন | BFF ইচ্ছাকৃতভাবে upstream error detail লিক করে না — IdP-র 4xx সব 401 ProblemDetails | এটাই সঠিক আচরণ (`AuthController` দেখুন) |

**Stack বন্ধ/পরিষ্কার করা:**

```bash
docker compose -f ../rvl-secure-bqr-manager/docker/docker-compose.yml \
                -f tmp/sbqr-real-compose.override.yml down        # volume থাকবে
# ... down -v                                                          # সব মুছে ফেলে
```

---

## ১০. পরিশিষ্ট

### ক) curl cheat-sheet

```bash
BFF=localhost:8080
ALICE=$(curl -fsS -X POST localhost:5105/mint -H "Content-Type: application/json" \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"user-alice"}' \
  | node -e "console.log(JSON.parse(require('fs').readFileSync(0)).token)")
curl -s $BFF/health/live                                              # প্রাণ
curl -s $BFF/health/ready                                             # platform টোকেন আছে?
curl -s $BFF/debug/platform-token                                     # টোকেন ক্যাশ
curl -s -H "Authorization: Bearer $ALICE" $BFF/debug/auth             # আমি কে?
curl -s -H "Authorization: Bearer $ALICE" -X POST $BFF/v1/qr/generate/static \
  -H "Content-Type: application/json" \
  -d '{"recipientName":"Alice Smith","recipientCity":"Dhaka","recipientPan":"1234567890123456"}'
curl -s -H "Authorization: Bearer $ALICE" -X POST $BFF/v1/qr/validate \
  -H "Content-Type: application/json" -d '{"qrPayload":"…"}'
docker logs sbqr.api --tail 30                                        # আসল api-র দৃষ্টিতে সব
```

### খ) `tmp/.env-real`-এ কী আছে (script-generated)

- `Auth__*` → fake IdP :5105 (ব্যাংক-সাইড সিমুলেশন)
- `Platform__BaseUrl=http://localhost:5001` → **আসল sbqr.api**
- `Platform__ClientId/ClientSecret` → provisioning-এ মিন্ট হওয়া আসল tenant credential
- `Platform__ContractPath` → আসল `v1.public.json` (**contract pinning চালু**)
- `Idempotency__Salt/BucketSeconds` → derived key-এর উপাদান
- `MSYS2_ENV_CONV_EXCL` → Git Bash-এর path-conversion থেকে বাঁচানো

### গ) শব্দকোষ (সংক্ষেপে)

- **BFF** (Backend-for-Frontend): app কখনো backend-secret ছুঁতে পারে না — সব গোপন ব্যাংক-সাইডে।
- **Platform token**: BFF↔sbqr.api server-to-server টোকেন (client_credentials)।
- **Single-flight**: একসাথে অনেক request-এ টোকেন শেষ হলেও ঠিক একটা refresh হয়।
- **Idempotency-Key**: একই অনুরোধ দুবার গেলে দ্বিতীয়টা নাকচ — ডাবল-চার্জ বীমা।
- **Contract pinning**: upstream response আসল OpenAPI schema-র সাথে না মিললে 502।
- **X-Correlation-Id**: এক request-এর সূত্র তিন সিস্টেমে এক জায়গায় খোঁজার জন্য।
- **Boot jitter**: restart-storm-এ সব instance একসাথে token না চাইতে random delay।
- **Verdict**: validate-এর রায় — VALID/INVALID_SIGNATURE/STRUCTURAL_INVALID/KEY_NOT_FOUND/REQUEST_REPLAYED।

---

**আরও:** বিস্তারিত ইংরেজি reference (OTel export ইত্যাদি)
[dev-manual-testing.md](dev-manual-testing.md)-এ; কোডবেস ভ্রমণে
[dev-onboarding-guide.md](dev-onboarding-guide.md)।
