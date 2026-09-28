# mTLS Full-Flow Testing গাইড — Postman + Visual Studio (বাংলায়, fresh শুরু থেকে)

> **পাঠক:** .NET ডেভেলপার, **Windows 11** লোকাল মেশিন। টুল: **PowerShell**, **Git (Bash)**, **Postman**, **Visual Studio**, **Docker**।
> **উদ্দেশ্য:** একদম শূন্য থেকে — certificate বানানো, দুই সার্ভিস তোলা, তারপর Postman ও Visual Studio দিয়ে mTLS flow-এর **পুরো টেস্ট + ডিবাগ + edge case ও evil scenario** পর্যন্ত।
> **লক্ষ্য একটাই:** ফিচারটা কাজ করছে মাত্র না — **ভাঙার যা যা উপায় আছে সবগুলোই ভাঙে, শুধু সঠিক পথটাই খোলে** — এই আত্মবিশ্বাস নিয়ে উঠবেন।
> **Theory দরকার হলে:** [`mtls-theory-notes-bn.md`](mtls-theory-notes-bn.md), [`mtls-local-dev-guide-bn.md`](mtls-local-dev-guide-bn.md) — এই গাইড তার hands-on drill।

---

## ০. বড় ছবি — কী বানাচ্ছি, কোনটা কোথায়

```
[Postman]  ← আপনি দুই ভূমিকায়: (ক) gateway সেজে সরাসরি mTLS, (খ) mobile-user সেজে পুরো flow
   │
   ▼
[FI IdP mock :5105] ──login──► [FI Gateway :8080] ══mTLS (client cert)══► [SBQR api :7443]
 (docker, rvl-sbqr-mocks)       (rvl-sbqr-fi-gateway)                     (rvl-secure-bqr-manager)
                                                                        + HTTP :5001 (health/docs)

[dev-pki/] ← নিরপেক্ষ PKI ফোল্ডার — CA এখানে থাকে, কোনো repo-র ভেতরে না
[Postgres :5432] [BB trust-store mock :5002]  ← পেছনের সাপোর্ট
```

**নাম-জোড়া (ভুল হবে না):**

| গাইডে যে নাম | আসল জায়গা | ভূমিকা |
|---|---|---|
| **sbqr-api** (platform) | `rvl-secure-bqr-manager/src/Host/SBQR.Api` | mTLS-এর **server** — `:7443`-এ client cert ছাড়া ঢুকতেই দেয় না |
| **sbqr-gateway** (BFF) | `rvl-sbqr-fi-gateway/src/SBQR.FiGateway.Api` | mTLS-এর **client** — নিজের cert নিয়ে `:7443`-এ ঢোকে |
| **dev-pki** | workspace-root-এ `dev-pki/` | **নিরপেক্ষ issuer** — CA, sbqr-api-র server cert, প্রতি FI-এর client cert, evil জোড়া। সব repo-র **বাইরে** |
| IdP mock | `rvl-sbqr-mocks` (docker) | Fatema-র login/JWT দেয় |
| Trust store mock | `rvl-bb-trust-store` | QR validate-এ প্রতিষ্ঠান-তালিকা |

**dev-pki কেন repo-র বাইরে — এক লাইনে তিনটা কারণ:** (১) বাস্তবেও CA স্বতন্ত্র তৃতীয় পক্ষ (BB PKI-র মতো), কোনো অ্যাপ repo-র জিনিস না; (২) কোনো `.git`-এর ভেতরে না থাকায় `ca.key` বা private key **ভুলেও commit হওয়ার পথ নেই**; (৩) repo বদলালে/ফেলে দিলে PKI অক্ষত থাকে (`rvl-sbqr-api` copy এভাবেই ফেলে দেওয়া হয়েছে)।

**নামকরণ-চুক্তি (per-FI client cert):** `fi-gateway-<institution-code>-<fi-id>` — যেমন `fi-gateway-000085-dhakabank`। ফাইলের নাম = certificate-এর CN; আর **আসল পরিচয় thumbprint** (নাম না) — authorization সবসময় thumbprint দিয়ে।

---

## ১. Preparation — fresh শুরু, কোনো certificate নেই ধরে

### 1.1 টুল চেক (PowerShell + Git Bash, ২ মিনিট)

**Git Bash** খুলে (openssl এখানেই পাওয়া যায়):

```bash
openssl version     # Git-এর সাথে আসে
dotnet --version    # .NET 10 SDK দরকার (SBQR.Api)
```

**PowerShell**-এ:

```powershell
docker --version    # Postgres + IdP mock
```

আর লাগবে: **Visual Studio** (দুই solution), **Postman** (desktop app)।

### 1.2 নতুন PKI বানানো — দুই কমান্ড (Git Bash)

```bash
cd /d/Workspace/Sources/RVL/rvl-sbqr/rvl-sbqr-workspace

# (ক) একবারের জন্য: root CA + sbqr-api server cert + evil জোড়া
bash dev-pki/scripts/dev-pki-init.sh

# (খ) প্রতিটা FI-এর জন্য: client certificate
bash dev-pki/scripts/dev-client-issue.sh 000085 dhakabank "Dhaka Bank"
```

ফলাফল (`dev-pki/`):

```
ca.crt / ca.key                       ← একমাত্র issuer ("SBQR Dev Root CA")
sbqr-api.pfx (pass: sbqr-dev)         ← server-এর পরিচয় (SAN: localhost, 127.0.0.1)
evil-ca.* / evil-client.pfx (evil-dev) ← খারাপ পক্ষ — negative test-এর জন্য
clients/
  fi-gateway-000085-dhakabank.pfx/.crt/.key   ← pass: fi-gateway-dhakabank-dev
```

> issue-script শেষে **SHA-256 thumbprint প্রিন্ট করে** — ওটা কোথাও সংরক্ষণ করুন, অংশ ৫-এ pinning টেস্টে লাগবে। প্রতিবার fresh generate করলে thumbprint **বদলে যায়** — গাইডে লেখা মান না, script-এর প্রিন্ট করাই সত্য।
>
> পুরনো ব্যবস্থা (`rvl-secure-bqr-manager/scripts/dev-certs-generate.sh` + সব `dev-certs/` folder) এখন legacy — আর ব্যবহার করবেন না; খুশি হলে মুছে ফেলুন।

### 1.3 বানানো certificate চিনে নিন (ঐচ্ছিক ২ মিনিট)

```bash
openssl x509 -in dev-pki/ca.crt -text -noout | head -12
#   Subject: CN=SBQR Dev Root CA ...  Basic Constraints: CA:TRUE

openssl x509 -in dev-pki/clients/fi-gateway-000085-dhakabank.crt -subject -noout
#   subject=CN=fi-gateway-000085-dhakabank, O=Dhaka Bank

openssl x509 -in dev-pki/sbqr-api.crt -text -noout | grep -A2 "Alternative"
#   DNS:localhost, IP:127.0.0.1  ← তাই BaseUrl-এ localhost-ই লিখতে হবে
```

### 1.4 sbqr-api-তে mTLS চালু (`.env`)

`rvl-secure-bqr-manager` repo-root-এর **`.env`**-এর শেষে mTLS অংশ এমন থাকতে হবে (path-গুলো repo-root থেকে relative — `dev-pki/` sibling বলে `../dev-pki/...`):

```ini
Mtls__Enabled=true
Mtls__ServerCertificatePath=../dev-pki/sbqr-api.pfx
Mtls__CaCertificatePath=../dev-pki/ca.crt
# ঐচ্ছিক pinning (অংশ ৫.৪):
#Mtls__AllowedClientThumbprints__0=<স্ক্রিপ্টের প্রিন্ট করা thumbprint, কোলন ছাড়া>
```

> `.env` শুধু **Development** environment-এ লোড হয় (এজন্যই VS-এর ডিফল্ট profile-ই চলবে), আর gitignored। এই মেশিনে Postgres/S3-এর connection `.env`-এ আগেই সেট আছে; নতুন মেশিনে `.env.example` + `docs/dev-s3-guide.md` দেখে ভরতে হবে।

### 1.5 সাপোর্ট সার্ভিস (Docker + একটা dotnet)

**PowerShell:**

```powershell
# (ক) Postgres — এক কমান্ড (Docker Desktop নিজেই চালু করে নেয়)
cd D:\Workspace\Sources\RVL\rvl-sbqr\rvl-sbqr-workspace\rvl-secure-bqr-manager
pwsh docker/start-db.ps1          # শেষে sbqr_app + sbqr_key_vault healthy দেখাবে

# (গ) BB trust store mock — আলাদা উইন্ডোতে চালু রাখুন
cd D:\Workspace\Sources\RVL\rvl-sbqr\rvl-sbqr-workspace\rvl-bb-trust-store\src\BB.TrustStoreMock
dotnet run                         # :5002
```

**Git Bash** (বা PowerShell-এ `docker compose up --build`):

```bash
# (খ) FI IdP mock — gateway-এর JWT এখান থেকেই আসবে
cd /d/Workspace/Sources/RVL/rvl-sbqr/rvl-sbqr-workspace/rvl-sbqr-mocks
docker compose up --build          # fi-idp-dhakabank :5105
```

দ্রুত যাচাই:

```powershell
curl.exe -s http://localhost:5105/health    # {"status":"ok",...,"users":5}
curl.exe -s http://localhost:5002/health    # {"status":"ok"}
```

> PowerShell-এ `curl` লিখলে সেটা `Invoke-WebRequest`-এর alias — তাই `curl.exe` লিখুন, নয় Git Bash ব্যবহার করুন।

---

## ২. দুই সার্ভিস তোলা (Visual Studio-কেন্দ্রিক)

### 2.1 sbqr-api — VS-এ F5

1. VS-এ `rvl-secure-bqr-manager\SBQR.slnx` খুলুন।
2. `src/Host/SBQR.Api` → right-click → **Set as Startup Project**।
3. **F5** (profile: `Development`, `http://localhost:5001`)।

mTLS চালু থাকলে Kestrel-এর নিজের `Listen()` URL replace করে, তাই console-এ **দুটো** লাইন:

```
Now listening on: http://127.0.0.1:5001     ← আগের মতো (health, docs)
Now listening on: https://127.0.0.1:7443    ← ★ mTLS দরজা
```

> টার্মিনালে চালালে: `$env:ASPNETCORE_ENVIRONMENT="Development"` **সেট করা জরুরি** — `--no-launch-profile` দিলে ডিফল্ট Production ধরে, ফলে `.env`-ই লোড হবে না (হাতে ভুল হয়ে দেখা গেছে!)।

### 2.2 sbqr-gateway — `MtlsTesting` environment-এ

সাধারণ Development-এ gateway `http://localhost:5001`-এ plain HTTP-এ কথা বলে। আজ দরকার `https://localhost:7443` + certificate — এজন্য আলাদা environment: **`MtlsTesting`** (`appsettings.MtlsTesting.json`-এ সব সেট-করা: BaseUrl, cert path `dev-pki/clients/fi-gateway-000085-dhakabank.pfx`, CA path)।

**পদ্ধতি A — Visual Studio (ডিবাগ চাইলে):** gateway-এর `Properties/launchSettings.json`-এ profile যোগ করুন:

```json
"mtls-testing": {
  "commandName": "Project",
  "dotnetRunMessages": true,
  "launchBrowser": false,
  "applicationUrl": "http://localhost:8080",
  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "MtlsTesting" }
}
```

তারপর দ্বিতীয় VS উইন্ডোয় `SBQR.FiGateway.slnx` খুলে (দুই solution = দুই জানালা), এই profile সিলেক্ট করে **F5**।

**পদ্ধতি B — PowerShell (দ্রুত):**

```powershell
cd D:\Workspace\Sources\RVL\rvl-sbqr\rvl-sbqr-workspace\rvl-sbqr-fi-gateway\src\SBQR.FiGateway.Api
$env:ASPNETCORE_ENVIRONMENT = "MtlsTesting"
$env:ASPNETCORE_URLS        = "http://localhost:8080"
dotnet run --no-launch-profile
```

> অবশ্যই **প্রজেক্ট ফোল্ডার থেকে** চালান — cert path-গুলো relative (`../../../dev-pki/...`)। অন্য কোনো FI সেজে পরীক্ষা করতে চাইলে শুধু env var বদলান: `$env:Mtls__CertPath="...\dev-pki\clients\fi-gateway-000086-ebl.pfx"` + `$env:Mtls__CertPassword="fi-gateway-ebl-dev"`।

### 2.3 উঠল কি না — প্রথম প্রমাণসহ

```powershell
curl.exe -s http://localhost:8080/health/live    # self: Healthy
curl.exe -s http://localhost:8080/health/ready   # platform-token: Healthy হতে হবে
```

**প্রথম mTLS-প্রমাণ এখানেই:** boot-এর সাথে `TokenPrewarmService` token আনতে যায় `https://localhost:7443`-এ, **client cert নিয়ে**। `platform-token` **সবুজ** = mTLS handshake সফল। sbqr-api-র console-এ সেই মুহূর্তে লগ পড়ে — `CN=fi-gateway-000085-dhakabank` accepted (thumbprint-সহ)।

---

## ৩. Postman — সরাসরি mTLS দরজা (আপনি নিজে gateway সেজে)

এখানে Postman নিজেই client cert পরে `:7443`-এ ঢোকে — ভালো/নেই/খারাপ তিন ফলই চোখের সামনে।

### 3.1 একবারের সেটআপ

Postman → ⚙️ **Settings** → **Certificates**:

1. **CA Certificates** → import `dev-pki\ca.crt` (নইলে আমাদের self-made CA বিশ্বাস করবে না)।
2. **Client Certificates** → **Add Certificate**: Host `localhost`, Port `7443`, CRT = `dev-pki\clients\fi-gateway-000085-dhakabank.crt`, KEY = একই নামের `.key` (PFX দিলেও হয়, pass `fi-gateway-dhakabank-dev`)।

### 3.2 তিন প্রোব (প্রত্যেকটা আলাদা request)

| # | Request | প্রত্যাশিত | কী প্রমাণ হলো |
|---|---|---|---|
| ১ | `GET https://localhost:7443/health/live` (cert চালু) | `200 {"status":"live"}` | handshake পুরো হয়েছে ✓ |
| ২ | same request, client cert **disable** করে | connection মরবে (SSL error / socket hang up) — **status code নেই** | `RequireCertificate`: ঢোকার আগেই ফেরত |
| ৩ | cert বদলে `evil-client.crt/.key` | আবার connection মরবে | অন্য CA-র সই — chain মেলেনি |

> লাইভ যাচাইকৃত ফল (এই সেটআপে openssl দিয়ে পরীক্ষা করা): প্রোব ১-এ `Verify return code: 0 (ok)`; প্রোব ২-এ TLS **alert 46** (certificate unknown); প্রোব ৩-এ **alert 48** (unknown CA)। Postman-এ একই ঘটনা দেখাবে SSL-error আকারে। **HTTP status code নেই** — কারণ request অ্যাপ পর্যন্ত পৌঁছায়ইনি। এটাই mTLS আর 401-এর পার্থক্য।

---

## ৪. Postman — পুরো flow (mobile user সেজে; gateway-এর পেছন দিয়ে)

এবার Postman = মোবাইল অ্যাপ। mTLS চোখে দেখা যাবে না (gateway→api পেছনের হপ), কিন্তু **প্রতিটা সফল ধাপ প্রমাণ করে সুড়ঙ্গ বেঁচে**, আর দুই console-এ লগ দেখা যায়।

### 4.1 Environment `SBQR Local`

| Variable | Value |
|---|---|
| `idp_base` | `http://localhost:5105` |
| `gw_base` | `http://localhost:8080` |
| `fatema_token` | (খালি — Step ২-এর script ভরবে) |

> শর্টকাট: `rvl-sbqr-fi-gateway/docs/postman-dhakabank-fatema-e2e-dev.postman_collection.json` **Import** করুন — ১২ ধাপের তৈরি collection; শুধু base URL লোকাল করুন।

### 4.2 ধাপে ধাপে

**Step ১ — IdP healthy:** `GET {{idp_base}}/health` → `200 {"status":"ok","fi_id":"dhakabank",...}`

**Step ২ — Fatema লগইন:** `POST {{idp_base}}/connect/token`, body (x-www-form-urlencoded): `grant_type=password`, `username=fatema`, `password=fatema@1234` → `200` + `access_token`। Tests tab:

```javascript
pm.test("200 OK", () => pm.response.to.have.status(200));
pm.environment.set("fatema_token", pm.response.json().access_token);
```

**Step ৩ — gateway + mTLS-পথে token:** `GET {{gw_base}}/health/live` → 200; `GET {{gw_base}}/health/ready` → `platform-token: Healthy`।

**Step ৪ — STATIC QR:** `POST {{gw_base}}/v1/qr/generate/static`, headers: `Authorization: Bearer {{fatema_token}}`, `Content-Type: application/json`, `X-Correlation-Id: local-e2e-001`; body:

```json
{ "recipientName": "Fatema Akter", "recipientCity": "Dhaka", "recipientPan": "1234567890123456" }
```

→ `201 Created`, `qrPayload` (000201... দিয়ে শুরু), `payloadHash`, `qrType: "STATIC"`।

**Step ৫ — DYNAMIC QR (৳500):** same headers, `{{gw_base}}/v1/qr/generate/dynamic`, body-তে আরেক ঘর `"transactionAmount": "500.00"` → `201`। Tests tab:

```javascript
pm.test("201 Created", () => pm.response.to.have.status(201));
pm.environment.set("qr_dynamic_payload", pm.response.json().qrPayload);
pm.environment.set("qr_dynamic_hash", pm.response.json().payloadHash);
```

**Step ৬ — validate (স্ক্যান):** `POST {{gw_base}}/v1/qr/validate`, `Authorization: Bearer {{fatema_token}}`, body `{ "qrPayload": "{{qr_dynamic_payload}}" }` → `200`, `verdict: "VALID"`, আর **hash হুবহু মিলবে** — এটাই pass/fail গেট:

```javascript
pm.test("verdict VALID + hash round-trip", () => {
  const b = pm.response.json();
  pm.expect(b.verdict).to.eql("VALID");
  pm.expect(b.payloadHash).to.eql(pm.environment.get("qr_dynamic_hash"));
});
```

**Step ৭ — app-layer negative (এখনকার অংশের বাইরের খারাপ ছেলে):** `Authorization` ছাড়া Step ৪ → `401`; `Bearer not-a-real-jwt` → `401`; IdP-তে ভুল পাসওয়ার্ড → `400 invalid_grant`; locked অ্যাকাউন্ট `nasrin` → `400 account_locked`।

**পেছনের গল্প:** Postman → JWT নিয়ে gateway-এ; gateway **mTLS সুড়ঙ্গে** নিজের platform token নেয়, সেই token দিয়ে QR করে — Postman কখনো `client_secret`/platform token ছোঁয়নি (BFF ডিজাইন)।

---

## ৫. Edge cases + Evil scenarios — আত্মবিশ্বাসের আসল অংশ

> নিয়ম: **প্রতিটা টেস্টের পর স্টেট ফেরত আনবেন** (cert ফিরিয়ে দিন, env var মুছুন, `.env` আগের মতো করুন) — নইলে পরের অংশ ভাঙা অবস্থায় শুরু হবে।

### 5.1 Evil client (transport-layer আক্রমণ) — অংশ ৩.২ প্রোব ৩
`evil-client` cert দিয়ে ঢোকা → handshake-ই মরে (alert 48), sbqr-api console-এ: untrusted chain, `CN=attacker` — rejected।

### 5.2 Gateway-এর চোখ ধোঁকা — server CA pin ভাঙা

Gateway কি সত্যিই server-এর cert যাচাই করে, নাকি যে-কোনো server মানে? পরীক্ষা:

```powershell
# Gateway বন্ধ করে আবার চালান — এবার ভুল CA দেখিয়ে:
$env:ASPNETCORE_ENVIRONMENT = "MtlsTesting"
$env:ASPNETCORE_URLS        = "http://localhost:8081"
$env:Mtls__ServerCaCertPath = "D:\Workspace\Sources\RVL\rvl-sbqr\rvl-sbqr-workspace\dev-pki\evil-ca.crt"
dotnet run --no-launch-profile
```

প্রত্যাশিত: `/health/ready` (এবার `:8081`-এ) **লাল** — gateway-এর `MtlsConfigurator` server cert-টা `evil-ca` দিয়ে যাচাই করতে গিয়ে ব্যর্থ (`AuthenticationException: The remote certificate is invalid...`)। অর্থাৎ **client-পাশের validation-ও সত্যিকারের চালু** — ভুয়া server-কে gateway চিনবে না। শেষ হলে `$env:Mtls__ServerCaCertPath` মুছে ফেলুন।

### 5.3 Gateway-এর mTLS বন্ধ করে পাঠানো

```powershell
$env:MTLS__ENABLED = "false"   # MtlsTesting config-কে override করে
dotnet run --no-launch-profile
```

প্রত্যাশিত: `/health/ready` লাল — cert ছাড়া `:7443` ঢুকতে দেয় না। **দরজাটা সত্যিই বন্ধ, gateway-এর ভালো আচরণের ভরসায় না।**

### 5.4 Thumbprint pinning — দ্বিতীয় FI-এর গল্প (সবচেয়ে শিক্ষণীয়)

এতক্ষণ sbqr-api যেকোনো **CA-signed + clientAuth** cert মেনেছে (allow-list খালি = dev fallback)। এবার pinning চালু করি আর দেখি fallback কীভাবে উধাও হয়:

1. **দ্বিতীয় FI issue করুন** (এখনো করেননি তোলে):
   ```bash
   bash dev-pki/scripts/dev-client-issue.sh 000086 ebl "Eastern Bank"
   ```
2. **Baseline:** Postman-এ ebl cert দিয়ে `GET https://localhost:7443/health/live` → এখনো `200` (fallback এখনও চালু — একই CA-র সই, তাই ঢুকছে)।
3. **Pinning চালু:** manager `.env`-এ শুধু dhakabank-এর thumbprint ভরুন (script-এর প্রিন্ট করা মান, কোলন-ছাড়া):
   ```ini
   Mtls__AllowedClientThumbprints__0=AE2C9513...B85D3A08
   ```
   **API restart** (VS-এ Stop → F5) — allow-list boot-এ পড়ে।
4. **পরীক্ষা:** dhakabank cert → এখনো `200` ✓; **ebl cert → connection মরবে ✗** — একই CA-র সই সত্ত্বেও thumbprint রেজিস্টার নেই! sbqr-api console: thumbprint not allowed, `CN=fi-gateway-000086-ebl`।
5. **ঠিক করা:** ebl-এর thumbprint-ও যোগ করুন (`Mtls__AllowedClientThumbprints__1=...`) → restart → এবার দুটোই `200` ✓।
6. **ফেরত:** দুই লাইনই comment করে restart করলে fallback ফেরত।

**শিক্ষা:** প্রথম thumbprint ভরলেই "any CA-signed" fallback সবার জন্য বন্ধ (কোডে ইচ্ছাকৃত — এক FI onboard করলে অন্য unregistered FI ঢুকতে না পারা চাই-ই)। মানে **নতুন FI onboard = cert ইস্যু + thumbprint রেজিস্টার, একই দিনে।**

### 5.5 ভুল PFX password — fail-fast প্রমাণ

```powershell
$env:Mtls__CertPassword = "wrong-password"
dotnet run --no-launch-profile
```

প্রত্যাশিত: gateway **শুরুই হয় না** — cert লোডেই `CryptographicException` (PFX ভাঙা পেলে `FileNotFoundException`, মিসিং config পেলে `InvalidOperationException` — তিনটাই actionable message-সহ fail-fast; মাঝপথে নিঃশব্দে চলতে চলতে মরে না)।

### 5.6 মেয়াদোত্তীর্ণ certificate (ভাঙা না দেখেই বুঝে নেওয়া)

`chain.Build()` মেয়াদও যাচাই করে — `NotAfter` পেরোলেই rejection, আচরণ 5.4-এর মতোই। হাতে করে দেখতে চাইলে issue-script-এ `-days 825` বদলে `-days 1` করে একটা ফাঁকি cert বানিয়ে আগামীকাল পরীক্ষা করুন; নইলে মনে মনেই থাক — **মেয়াদ ফুরলে সব connection একসাথে মরবে**, তাই production-এ expiry-warning বসানোর কথা ভাবুন।

### 5.7 সব evil scenario এক টেবিলে

| # | খারাপ কাজ | কোথায় আটকায় | কী দেখবেন |
|---|---|---|---|
| ১ | evil CA-র client cert (5.1) | `BuildsWithTrustedCa()` — chain মিলেনি | handshake মৃত (alert 48), api log: untrusted chain |
| ২ | cert ছাড়া ঢোকা (৩.২) | `RequireCertificate` | handshake মৃত (alert 46), কোনো status code নেই |
| ৩ | সঠিক CA, কিন্তু unregistered FI (5.4) | `ThumbprintAllowed()` | handshake মৃত, api log: thumbprint not allowed |
| ৪ | server-এর পরিচয় নিয়ে gateway ধোঁকা (5.2) | gateway-এর `ServerCertificateCustomValidationCallback` | gateway নিজে সংযোগ কাটে, ready লাল |
| ৫ | gateway mTLS বন্ধ (5.3) | server-এর দরজা | ready লাল — token পাওয়াই যায় না |
| ৬ | ভুল PFX password/path (5.5) | gateway fail-fast | শুরুতেই ব্যতিক্রম, অস্পষ্ট লগ নয় |
| ৭ | ভাঙা JWT / নেই / locked user (৪-এর ধাপ ৭) | app layer (401/400) | mTLS-এর **নিচের** স্তর — এরা ঢুকে ভেতরে ধরা পড়ে |

লক্ষ করুন ১–৬ হলো **transport layer** (দরজাতেই ফেরত, কোনো HTTP নেই), ৭ হলো **application layer** (401/400 আছে)। দুই স্তর পাশাপাশি, একে অপরকে replace করে না।

---

## ৬. Visual Studio ডিবাগিং ট্যুর — ভেতরে ঢুকে দেখা

দুই VS জানালা খোলা, দুই প্রজেক্ট F5-এ। Breakpoint বসিয়ে অংশ ৩/৪/৫-এর request পাঠান:

| কোথায় (file) | কখন | কী দেখবেন |
|---|---|---|
| **gateway** `Mtls/MtlsConfigurator.cs` — `Apply()` | শুধু boot-এ (দুই HttpClient) | `certPath` = `dev-pki/clients/fi-gateway-000085-dhakabank.pfx`; `X509Certificate2` লোড |
| **gateway** একই ফাইল — `ServerCertificateCustomValidationCallback` | প্রতি TLS connection-এ | `serverCertificate.Subject` = `CN=localhost` — gateway যাচাই করছে **server-কে** |
| **gateway** `Platform/PlatformAuthHandler.cs` | প্রতি QR call-এ | cached platform token → `Authorization: Bearer …` |
| **sbqr-api** `Mtls/ClientCertificateValidator.cs` — `Validate()` | প্রতি `:7443` handshake-এ | `certificate.Subject` = `CN=fi-gateway-000085-dhakabank, O=Dhaka Bank` — gateway-এর পরিচয় হাতে-হাতে |
| একই ফাইল — `BuildsWithTrustedCa()` | পরের ধাপ | `chain.Build()` true/false — CA চেইন |
| একই ফাইল — `ThumbprintAllowed()` | তারপর | খালি তালিকা = fallback চলছে; 5.4-এর পরে ভরা তালিকা |

**সবচেয়ে শিক্ষণীয় দুই experiment:**
- **Live rejection:** `Validate()`-এ breakpoint → Postman-এ evil cert (5.1) → ডিবাগারে দেখুন `CN=attacker`, `BuildsWithTrustedCa()` false, `Validate()` false — handshake মরছে ঠিক এই লাইনে।
- **Pinning সিদ্ধান্ত:** `ThumbprintAllowed()`-এ breakpoint → ebl cert (5.4-এর ধাপ ৪) → ভালো CA, ভালো EKU, তবু false — কারণ তালিকায় নেই।

ছোট কৌশল: breakpoint-এ Immediate window-এ `certificate.Thumbprint`, `certificate.NotAfter` টাইপ করে মিলিয়ে দেখুন। Boot-চলাকালীন sbqr-api-র `Mtls/MtlsEndpointsExtensions.cs`-এ `Listen(...)` ব্লক step-through করলে দেখবেন `RequireCertificate` + callback ঠিক কোথায় বসছে।

---

## ৭. এক পাতায়: কোন config কোন কোডে

| দিক | Config key | কোডে কোথায় | কাজ |
|---|---|---|---|
| api | `Mtls__Enabled` | `MtlsEndpointsExtensions.AddServerMtls()` (Program.cs §1b) | true হলেই `:5001`+`:7443` Listen, cert লোড |
| api | `Mtls__ServerCertificatePath=../dev-pki/sbqr-api.pfx` | একই ফাইল — `LoadPkcs12FromFile` | নিজের পরিচয় (pass `sbqr-dev`) |
| api | `Mtls__CaCertificatePath=../dev-pki/ca.crt` | `ClientCertificateValidator` — `CustomRootTrust` | এই CA-র সই ছাড়া client মানে না |
| api | `Mtls__AllowedClientThumbprints__0..N` | একই validator | ভরলে শুধু ওই thumbprint-রা; fallback বন্ধ |
| gateway | `Platform__BaseUrl=https://localhost:7443` | Program.cs-এর দুই HttpClient | mTLS দরজার ঠিকানা |
| gateway | `Mtls__CertPath / CertPassword` | `MtlsConfigurator.Apply()` | client cert পরানো (`fi-gateway-000085-dhakabank.pfx`) |
| gateway | `Mtls__ServerCaCertPath=../../../dev-pki/ca.crt` | একই ফাইলের callback | server-কে শুধু এই CA-তে বাঁধা |

দুই পাশেই নিয়ম এক: **"শুধু আমার দেওয়া একটা CA-কে বিশ্বাস করব" (`CustomRootTrust`)** — Windows-এর বিশ্বাসের লম্বা তালিকা অপ্রাসঙ্গিক।

---

## ৮. Troubleshooting

| লক্ষণ | আসল কারণ | করণীয় |
|---|---|---|
| api console-এ `https://…7443` লাইন নেই | `Mtls__Enabled` পৌঁছাচ্ছে না | `.env`-এ key; environment `Development` কি না |
| api boot-এ `ServerCertificatePath was not found` | dev-pki নেই/পথ ভুল | অংশ ১.২; `.env`-এ `../dev-pki/...` ঠিক আছে কি না |
| api-তে DB connection error, কিন্তু `:7443` লাইন আছে | Postgres নেই — mTLS ঠিকই, support নেই | `pwsh docker/start-db.ps1` |
| api `--no-launch-profile`-এ বিদ্যুৎ-বেগে connection-string error | environment Production হয়ে গেছে, `.env` লোডই হয়নি | `$env:ASPNETCORE_ENVIRONMENT="Development"` সেট করুন |
| gateway `FileNotFoundException: …dhakabank.pfx` | relative path মেপে পড়েনি | gateway প্রজেক্ট ফোল্ডার থেকে চালান |
| gateway ready লাল + `AuthenticationException … validation procedure` | server-CA/নাম মিলছে না | `MtlsTesting` env চালু কি না; `ServerCaCertPath` ঠিক CA-তে নির্দেশ করছে কি না (5.2-এর env var রয়ে গেলে মুছুন!) |
| `The specified network password is not correct` | PFX password ভুল | api: `sbqr-dev`; gateway: `fi-gateway-<fi-id>-dev` |
| Postman-এ server cert error | CA import করেননি | Settings → Certificates → CA Certificates → `dev-pki/ca.crt` |
| Postman client cert দিয়েও মরছে | পুরনো cert বসানো আছে / ভুল জোড়া / pinning-এ unregistered | fresh জেনারেটের পর Postman-এ নতুন ফাইল বসান; 5.4 চেক |
| `curl` PowerShell-এ অদ্ভুত | alias | `curl.exe` লিখুন / Git Bash |
| trust-store sync error লগে (:5002) | mock চালু নেই | অংশ ১.৫(গ) |

---

## ৯. নিরাপত্তা-নোট

- `dev-pki/` সব repo-র বাইরে — তবু এগুলো **লোকাল-ডেভ, throwaway**; কোথাও share নয়, কোনো repo-তে কপি নয়।
- এই certificate দিয়ে production-এ কিছু মানার কথা না — ওখানে আসল PKI/CSR flow (বিস্তারিত `mtls-local-dev-guide-bn.md` অংশ ৬)।
- Thumbprint/password Postman environment ফাইলে রেখে শেয়ার্ড workspace-এ দিবেন না।

---

## ১০. চূড়ান্ত চেকলিস্ট — "full confidence" মানে এই টিকগুলো

| ✓ | প্রমাণ |
|---|---|
| ☐ | `dev-pki-init.sh` + `dev-client-issue.sh` চলে, thumbprint প্রিন্ট হয় |
| ☐ | api console-এ `http://127.0.0.1:5001` + `https://127.0.0.1:7443` দুই লাইনই |
| ☐ | gateway `/health/ready` সবুজ (mTLS পথে token) |
| ☐ | Postman + dhakabank cert → `:7443/health/live` = 200 |
| ☐ | cert বন্ধ → মৃত (alert 46); evil cert → মৃত (alert 48); **কোনো status code নেই** |
| ☐ | পুরো flow: fatema login → QR static/dynamic 201 → validate `VALID` + hash মিল |
| ☐ | 5.2: gateway ভুয়া CA-তে server reject করে (ready লাল) |
| ☐ | 5.3: gateway mTLS বন্ধ হলে ঢোকার পথ নেই |
| ☐ | 5.4: pinning-এ unregistered ebl বাদ, উভয় নিবন্ধিত হলে ঢোকে |
| ☐ | 5.5: ভুল password-এ gateway শুরুই হয় না (fail-fast) |
| ☐ | VS ডিবাগারে `CN=fi-gateway-000085-dhakabank` হাতে-হাতে দেখা |
| ☐ | evil-cert rejection ডিবাগারে ঘটতে দেখা |

সব টিক হলে আপনি শুধু "চলছে" দেখেননি — **প্রমাণ** করেছেন: দরজায় পরিচয়-বাইরে কিছু ঢোকে না, কোন লাইনের কোন সিদ্ধান্তে আটকায়, আর নতুন FI যোগ করার নিয়মটা (cert + thumbprint) হাতে-কলমে জানেন।

---

*সত্যের উৎস: repo-দের নিজেদের ডক — `rvl-secure-bqr-manager/docs/mtls-guide.md`, `docs/mtls-dev-test-guide.md`, `rvl-sbqr-fi-gateway/docs/mtls-implementation-plan.md`। এই গাইডের সব কমান্ড এই মেশিনেই চালিয়ে যাচাই করা (boot লাইন, alert 46/48 সহ); কোথাও অমিল দেখলে repo-দের ডক-ই ঠিক।*
