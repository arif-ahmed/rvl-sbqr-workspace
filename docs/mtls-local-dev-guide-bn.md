# mTLS হাতে-কলমে গাইড — FI Gateway ↔ SBQR api (লোকাল ডেভ → প্রোডাকশন, বাংলায়)

> **পাঠক:** মিড-লেভেল .NET ডেভেলপার, Windows মেশিন + Visual Studio।
> **উদ্দেশ্য:** (১) mTLS, CA আর chain of trust — আত্মবিশ্বাসী বোঝা; (২) লোকাল ডেভে drill করা; (৩) implementation কোথায় কী হয়েছে তার ম্যাপ; (৪) লোকাল হয়ে গেলে **প্রোডাকশনে কী বদলায়** — একই শেখানোর ধাঁচে।
> **সম্পর্কিত ডকুমেন্ট:** ইংরেজি implementation plan — [`rvl-sbqr-fi-gateway/docs/mtls-implementation-plan.md`](rvl-sbqr-fi-gateway/docs/mtls-implementation-plan.md)।
> **Theory notes (সিরিজের প্রথম ধাপ — ভিত্তি):** [`mtls-theory-notes-bn.md`](mtls-theory-notes-bn.md) — mTLS-এর সব concept ও terminology-র শর্ট নোট, cascading লেভেলে (L1 Encryption → … → L14 প্রজেক্টের নাম-ধাম), annotated hands-on স্ক্রিপ্ট ও master glossary-সহ।
> **হাতে-কলমে certificate ল্যাব:** [`cert-lifecycle-hands-on-lab-bn.md`](cert-lifecycle-hands-on-lab-bn.md) — root CA বানানো থেকে শুরু করে sbqr.api আর gateway-এর certificate নিজে হাতে বানানো, প্রতিটা ফাইল code-এর কোথায় যায়, আর `openssl s_server` দিয়ে শূন্য-কোডে mTLS handshake চালিয়ে দেখা।
> **ASP.NET Core-এ integration:** [`mtls-aspnetcore-integration-bn.md`](mtls-aspnetcore-integration-bn.md) — ল্যাবের প্রতিটা জিনিস দুই Web API প্রজেক্টের কোন API-তে বসে (Gateway = HttpClient পক্ষ, sbqr.api = Kestrel পক্ষ) — annotated code, config keys, test ও error-অভিধান-সহ।

> ## 🔄 অবস্থার পরিবর্তন (2026-09-27, সন্ধ্যা)
>
> এই গাইডের প্রথম সংস্করণের পরেই বিষয়টা দ্রুত এগিয়েছে — বর্তমান সত্য এটা:
>
> - **Gateway-পক্ষ সম্পূর্ণ হয়ে গেছে** (commit `ae111de`): `MtlsOptions.ServerCaCertPath` + custom chain validation, আর `MtlsConfigurator` এখন **দুই** HttpClient-ই (token + QR) apply হয়।
> - **Server-side mTLS-ও হয়ে গেছে** — `rvl-secure-bqr-manager`-এ (commit `a9facee` + `e027dcf`): Kestrel HTTPS `:7443`, client-cert validation, এমনকি per-tenant certificate registry।
> - **পুরনো `tmp/` dev tooling মুছে দেওয়া হয়েছে** (commit `b5f9fa8`) — এর বদলে `rvl-secure-bqr-manager`-এর `scripts/dev-certs-generate.sh` + `dev-certs/` harness। অর্থাৎ CA generation এখন **gateway repo-র বাইরে**, platform-পক্ষের repo-তে — অংশ ২-এর issuer-separation আলোচনার সঠিক দিকেই।
> - **ক্যাননিকাল (সত্যের উৎস) গাইড এখন repo-গুলোর ভেতরে:** `rvl-secure-bqr-manager/docs/mtls-guide.md` (Bangla, concept→code→test→production) এবং `docs/mtls-dev-test-guide.md` (অপারেশনাল চেকলিস্ট)।
>
> এই গাইড workspace-লেভেল শিক্ষা-ডকুমেন্ট হিসেবে রইল: concept (অংশ ১–২) ও production (অংশ ৬) পুরো প্রযোজ্য; drill (অংশ ৪) নতুন harness অনুযায়ী; অংশ ৫-এর নকশা এখন "কী হয়েছে তা বোঝার ব্যাখ্যা"।
>
> **সর্বশেষ হালনাগাদ:** 2026-09-27, রাত।

---

## সূচি

1. [Concept — একদম শূন্য থেকে](#অংশ-১--concept--একদম-শূন্য-থেকে)
2. [CA আর Chain of Trust — সবচেয়ে বড় ধাঁধা, গভীরে](#অংশ-২--ca-আর-chain-of-trust--সবচেয়ে-বড়-ধাঁধা-গভীরে)
3. [আপনার প্রজেক্টে mTLS কোথায় দাঁড়িয়ে — বর্তমান অবস্থা](#অংশ-৩--আপনার-প্রজেক্টে-mtls-কোথায়-দাঁড়িয়ে--বর্তমান-অবস্থা)
4. [লোকাল drill — নতুন harness দিয়ে](#অংশ-৪--লোকাল-drill--নতুন-harness-দিয়ে)
5. [Implementation ব্যাখ্যা — কোন ফাইলে কী হয়েছে](#অংশ-৫--implementation-ব্যাখ্যা--কোন-ফাইলে-কী-হয়েছে)
6. [লোকাল হলে এবার প্রোডাকশন — একই পদ্ধতিতে](#অংশ-৬--লোকাল-হলে-এবার-প্রোডাকশন--একই-পদ্ধতিতে)
7. [Troubleshooting](#অংশ-৭--troubleshooting)
8. [Glossary + আরও পড়ার লিস্ট](#অংশ-৮--glossary--আরও-পড়ার-লিস্ট)

---

# অংশ ১ — Concept, একদম শূন্য থেকে

## 1.1 সাধারণ TLS (HTTPS) কী করে

আপনি প্রতিদিন যে HTTPS ব্যবহার করছেন, তাতে **একমুখী** পরিচয় যাচাই হয়:

```
[Client]                          [Server]
   │ ─── "connection চাই" ───────► │
   │ ◄── "আমার certificate নাও" ── │   ← server নিজের পরিচয় প্রমাণ করে
   │  (certificate verify করলাম ✓) │
   │ ═══ encrypted কথাবার্তা ══════ │
```

Browser-এ ব্যাংকের সাইট খুললে ব্রাউজার জিজ্ঞেস করে "তুমি সত্যিই এই ব্যাংক?" — server তার certificate দেখিয়ে উত্তর দেয়, ব্রাউজার verify করে। কিন্তু **server কখনো জানে না client কে** — যে-কেউ ঢুকতে পারে, তারপর username/password দিয়ে নিজেকে প্রমাণ করে।

**উপমা:** অফিসের গেটে দারোয়ান তার ID কার্ড দেখায় (server নিজে প্রমাণিত), কিন্তু ঢুকতে আসা মানুষটা কে — সেটা ভেতরে ঢুকে আরেকটা পরীক্ষায় ধরা পড়ে।

## 1.2 mTLS (Mutual TLS) — যেটা যোগ করে

mTLS-এ **client-কেও certificate দেখাতে হয়**। TLS handshake-এর মাঝখানেই server জিজ্ঞেস করে "তোমার certificate-টা কই?" — client তার certificate পাঠায়, server সেটা verify করে। যাচাই ব্যর্থ হলে **connection-ই হয় না** — request অ্যাপ্লিকেশন লেয়ার পর্যন্ত পৌঁছায়ই না।

```
[FI Gateway (client)]                [SBQR api (server)]
   │ ─── "connection চাই" ────────► │
   │ ◄── server certificate ──────── │
   │ ◄── "তোমার certificate দাও" ──  │   ← এটাই mTLS-এর অতিরিক্ত ধাপ
   │ ─── client certificate ──────► │
   │  (দুই পক্ষই দুজনকে verify ✓)     │
   │ ═══ encrypted কথাবার্তা ══════ │
```

এটা মূলত **machine-to-machine** কথোপকথনের জিনিস — যেখানে কোনো মানুষ/user বসে নেই, দুটো server নিজেদের মধ্যে কথা বলছে। ঠিক আপনার case-টাই: FI Gateway (আপনার BFF) আর SBQR platform।

## 1.3 Certificate, Private Key, CA — ডিজিটাল পরিচয়পত্রের ব্যাকরণ

| জিনিস | সহজ ভাষায় | মনে রাখার লাইন |
|---|---|---|
| **Certificate** | ডিজিটাল ID কার্ড — ভেতরে নাম (CN), public key, মেয়াদ, আর একজন CA-এর signature | সবাই দেখতে পায়, লুকানো জিনিস না |
| **Private Key** | ওই ID-র মালিকানার সত্যিকারের প্রমাণ | কখনো, কারো কাছে, কোনো request-এ পাঠানো হয় না |
| **Public Key** | Private key-এর জোড়া — certificate-এর ভেতরে বসে থাকে | যা private key দিয়ে "সই" করা, এটা দিয়ে যাচাই করা যায় |
| **CA (Certificate Authority)** | বিশ্বস্ত তৃতীয় পক্ষ, যে certificate-এ sign করে দেয় | নিজে বানানো ID-তে নিজের নাম লেখা কেউ মানে না |
| **Chain of Trust** | দুই পক্ষ আগে থেকে একই CA-কে বিশ্বাস করে — তাই একে অপরের CA-signed certificate মানে | mTLS-এর মেরুদণ্ড (পুরো অংশ ২ এটার) |

**Private key কীভাবে পরিচয় প্রমাণ করে, না পাঠিয়েই?** Handshake-এর সময় এক পক্ষ একটা র‍্যান্ডম challenge দেয়; অন্য পক্ষ সেটার উত্তর **private key দিয়ে sign করে** পাঠায়; প্রথম পক্ষ certificate-এর ভেতরের **public key** দিয়ে signature যাচাই করে। শুধু সঠিক private key-এর মালিকই সঠিক signature বানাতে পারে — আর private key নিজে কখনো wire-এ যায় না। এজন্যই network শুনে বা log ঘেঁটে কারো পরিচয় চুরি করা যায় না।

## 1.4 ফাইল ফরম্যাট: PEM বনাম PFX — অর্ধেক confusion এখানেই মেটে

একই certificate-এর দুই রকম প্যাকেজিং, বিষয়বস্তু এক:

| ফরম্যাট | চেহারা | ভেতরে কী | কোথায় দেখবেন |
|---|---|---|---|
| **PEM** (`.pem`/`.crt`/`.key`) | `-----BEGIN CERTIFICATE-----` দিয়ে শুরু হওয়া Base64 text | সাধারণত এক জিনিস প্রতি ফাইল (cert আলাদা, key আলাদা) | `dev-certs/ca.crt`, `dev-certs/fi-gateway.crt` — Linux/Node/openssl দুনিয়ার প্রিয় |
| **PFX / PKCS#12** (`.pfx`) | Binary বান্ডল | cert + private key একসাথে, **password-protected** | `dev-certs/fi-gateway.pfx` — .NET-এ `new X509Certificate2(path, password)` দিয়ে সরাসরি লোড হয় |

```csharp
// PFX লোড — gateway-এর MtlsConfigurator.cs ঠিক এটাই করে:
var cert = new X509Certificate2(certPath, certPassword, keyStorageFlags);

// PEM জোড়া লোড:
var serverCert = X509Certificate2.CreateFromPemFile("server-cert.pem", "server-key.pem");
```

নতুন `dev-certs/` harness-এ দুই ফরম্যাটই পাশাপাশি বানায় (`fi-gateway.crt/.key` + `fi-gateway.pfx`) — openssl-জগতের জন্য PEM, .NET-এর জন্য PFX।

## 1.5 Handshake-এ ঠিক কী ঘটে — Gateway → SBQR scenario ধরে ধাপে ধাপে

1. Gateway: "TLS connection চাই" (**ClientHello** — নিজের সাপোর্টেড cipher লিস্ট পাঠায়)
2. SBQR api: নিজের **server certificate** পাঠায় **এবং** জানিয়ে দেয় "client certificate-ও লাগবে" (**CertificateRequest**)
3. Gateway: server certificate-টা dev CA (`ca.crt`) দিয়ে verify করে — নাম, মেয়াদ, signature সব ঠিক? ✓
4. Gateway: নিজের **client certificate** (`fi-gateway.pfx`) পাঠায়, সাথে private key দিয়ে signed প্রমাণ
5. SBQR api: client certificate-টা **একই CA** দিয়ে verify করে ✓
6. দুজনে গোপন **symmetric session key** বানিয়ে নেয় — এরপর সব কথাবার্তা encrypted

যেকোনো ধাপ ব্যর্থ হলে `SSLHandshakeException`/connection refused — request অ্যাপ্লিকেশনে পৌঁছায় না। **এটাই mTLS-এর শক্তি: নেটওয়ার্কের দরজাতেই অচেনা পক্ষ ফিরে যায়।**

## 1.6 কেন fintech-এ এত জোর — শুধু `client_secret` কেন যথেষ্ট নয়

আপনার Gateway এখন SBQR-কে এভাবে নিজেকে পরিচয় দেয়:

```
POST /v1/oauth/token
  client_id=…&client_secret=…
```

`client_secret` আসলে **একটা পাসওয়ার্ড**। পাসওয়ার্ডের সমস্যা:

- চুরি হলে (log লিক, config exposure, backup থেকে) **যে-কেউ** সেটা নিয়ে platform-এ ঢুকে ব্যাংকের নামে QR ইস্যু করতে পারবে
- পাসওয়ার্ড request-এর সাথে **প্রতিবার wire ক্রস করে** — একবার লিক হলেই শেষ
- কে ব্যবহার করছে তার কোনো cryptographic প্রমাণ নেই

mTLS-এ পরিচয় = **certificate + private key**, যেখানে private key কখনো wire-এ যায় না। তাই আপনার architecture diagram-এ লেখা **"mTLS over secure VPN"** মানে defense in depth — দুটো আলাদা তালা:

- **VPN** দিয়ে নেটওয়ার্ক শুধুই ব্যাংক-অনুমোদিত পক্ষের — বাইরের কেউ ঢুকতেই পারে না
- **mTLS** নিশ্চিত করে VPN-এর ভেতরেও প্রতিটা connection-এর দুই পক্ষ প্রমাণিত পরিচয়সহ — VPN-এর ভেতরের কোনো compromised মেশিনও certificate ছাড়া SBQR-এর দরজা খুলতে পারে না

বাংলাদেশের ব্যাংক/Bangladesh Bank-স্তরের নিরাপত্তা expectation এবং international card-scheme নিয়মেও inter-institution hop-এ mTLS-ই ডিফল্ট শব্দ।

## 1.7 Layer গুলো গুলিয়ে ফেলবেন না — সবচেয়ে কমন confusion

| স্তর | প্রশ্ন যেটার উত্তর দেয় | টুল | কার জন্য |
|---|---|---|---|
| **Transport (mTLS)** | "কোন **মেশিন** কথা বলছে?" | X.509 certificate | Gateway ↔ SBQR — কোনো user নেই |
| **Application (OAuth2 + JWT)** | "কোন **client/user**-কে কী **অনুমতি**?" | client_credentials, token, scope | Gateway-এর platform-এ প্রবেশাধিকার |

এরা একে অপরকে replace করে **না** — উপরে-নিচে বসে থাকে:

```
[mTLS: encrypted, দুই-পক্ষ-প্রমাণিত tunnel]
   └─ [OAuth2 client_credentials → platform token]      ← ভেতরে
        └─ [Bearer JWT নিয়ে /v1/qr/generate/... call]  ← আরও ভেতরে
```

Drill-এ আপনি দেখবেন Gateway প্রথমে mTLS tunnel-এ ঢুকে তারপর token নিচ্ছে, তারপর সেই token নিয়ে QR call করছে — দুটোই একসাথে চালু।

### নিজেকে যাচাই (উত্তর ভেবে তারপর খুলুন)

1. শুধু HTTPS (mTLS ছাড়া) থাকলে SBQR api কি জানত কে call করছে?
2. Private key কি কখনো network-এর ওপারে যায়?
3. `client_secret` চুরি হলে আর client certificate-এর private key চুরি হলে — কোনটা বেশি সহজে attacker-কে SBQR-এ ঢুকতে দেবে?

<details><summary><b>উত্তর</b></summary>

1. না — শুধু জানত server-এর পরিচয় ঠিক আছে; client বেনামী থাকত।
2. কখনো না — wire-এ যায় শুধু certificate আর private key দিয়ে বানানো signature।
3. `client_secret` — সেটা সরাসরি ব্যবহারযোগ্য একটা পাসওয়ার্ড; certificate হাতে পেলেও private key ছাড়া সেটা কোনো কাজে লাগে না।

</details>

---

# অংশ ২ — CA আর Chain of Trust: সবচেয়ে বড় ধাঁধা, গভীরে

> আপনার প্রশ্নটা একদম জায়গামতো: *"CA আর chain of trust তো gateway-ও না, আবার sbqr.api-ও না — তাহলে এটা কার?"*
> এই অংশটা পুরো মন দিয়ে পড়ুন — mTLS-এর বাকি সব এখানেই দাঁড়িয়ে। এটা পরিষ্কার হলে লোকাল আর প্রোডাকশন দুটোই আপনার কাছে একই জিনিস লাগবে।

## 2.1 উত্তর এক লাইনে: CA কোনো পক্ষের "ভেতরের" জিনিস না — CA হলো তৃতীয় একটা ভূমিকা, "issuer" (প্রমাণপত্র ইস্যুকারী)

একটা mTLS setup-এ **তিনটা ভূমিকা** আছে, আর একটাই বিশেষ কথা: একটা পক্ষ একাধিক ভূমিকা পালন করতে পারে।

| ভূমিকা | কাজ | কী হাতে থাকে |
|---|---|---|
| **Issuer (CA)** | Certificate-এ **সই করে** — "আমি এই পরিচয়টা চিনি, আমার বিশ্বাসের বলয়ে এটা" | CA-র নিজের certificate (public) + CA-র **private key** (গোপন!) |
| **Prover** | নিজের পরিচয় **প্রমাণ করে** (certificate দেখায় + private key দিয়ে challenge-এর উত্তর দেয়) | নিজের certificate + নিজের private key |
| **Verifier** | প্রতিপক্ষের certificate **যাচাই করে** | যে CA সেই certificate সই করেছে, তার **public certificate** মাত্র |

এবার আপনার সিস্টেমে ভূমিকাগুলো বসান:

```
                    ┌─────────────────────────────┐
                    │  DEV ROOT CA (issuer)       │
                    │  dev-certs/ca.crt (public)  │
                    │  dev-certs/ca.key (গোপন)     │
                    └──────────┬────────┬─────────┘
                        সই করে ↓        ↓ সই করে
              ┌──────────────────┐   ┌──────────────────┐
              │ sbqr-api.pfx     │   │ fi-gateway.pfx   │
              │ (sbqr.api-র পরিচয়)│   │ (gateway-র পরিচয়) │
              └──────────────────┘   └──────────────────┘

  FI Gateway = client cert-এর prover + server cert-এর verifier
  SBQR api   = server cert-এর prover + client cert-এর verifier
```

লক্ষ্য করুন: **Gateway আর SBQR দুজনেই একইসাথে prover আর verifier** — নিজের পরিচয় দেয়, আর প্রতিপক্ষেরটা যাচাই করে। আর CA? সে runtime-এ কোথাও "চলে" না — সে কেবল আগে একবার সই করে দিয়েছিল, তার কাজ শেষ। তাই CA gateway-ও না, sbqr.api-ও না — **CA হলো সেই কলম, যার কালি দিয়ে দুই পক্ষের পরিচয়পত্র লেখা।**

## 2.2 দুই দিকের বিশ্বাস — একই `ca.crt`, দুই পক্ষের হাতে, দুই রকম কারণে

এটাই আপনার প্রশ্নের মূল অংশ: "লোকালে implement করার সময় এটা কীভাবে কাজ করে?" — উত্তর: **একই CA certificate দুই পক্ষ দুই কারণে রাখে:**

| কার কাছে | কী থাকে | কেন |
|---|---|---|
| **Gateway-এর কাছে** | `fi-gateway.pfx` (নিজের পরিচয়) + `ca.crt` | `ca.crt` দিয়ে **server-এর certificate verify** করবে — config key: `Mtls:ServerCaCertPath` |
| **SBQR api-র কাছে** | `sbqr-api.pfx` (নিজের পরিচয়) + `ca.crt` | `ca.crt` দিয়ে **client-এর certificate verify** করবে (server-side validation-এর trust anchor) |
| **কারো কাছেই না** | `ca.key` (CA-র private key) | যে পক্ষ শুধু verify করে, তার CA-র private key লাগেই না। এটা শুধু issuer-এর কাছে। লিক হলে যে-কেউ নকল certificate বানাতে পারবে — পুরো বিশ্বাসের বলয় ভেঙে যাবে |

**.NET dev-এর ভাষায় এক লাইনে:**

- Gateway-এর দিকে `ca.crt` = `MtlsConfigurator`-এর server-validation callback (config: `ServerCaCertPath`)
- SBQR-এর দিকে `ca.crt` = Kestrel-এর client-cert validation-এর trust anchor
- দুই দিকেই "verify" মানে সই-চেইন মিলিয়ে দেখা — private key কোথাও লাগে না।

আপনাদের বর্তমান সেটআপ এই নীতিটাই মানে: `scripts/dev-certs-generate.sh` আর `dev-certs/` (CA-সহ) থাকে **`rvl-secure-bqr-manager`** repo-তে — platform/verifier পক্ষের কাছে — আর gateway repo-তে CA-র private key নেই। Gateway শুধু ইস্যু-করা `fi-gateway.pfx` আর `ca.crt` (public) পায়। প্রোডাকশনে এই বিভাজন আরও কড়া হবে (অংশ ৬.২)।

## 2.3 Chain of Trust ভেতরে কীভাবে যাচাই হয় — leaf থেকে Root পর্যন্ত হাঁটা

যখন SBQR, Gateway-এর client certificate যাচাই করে, তখন আসলে ঘটে এই হাঁটা:

```
fi-gateway.pfx (leaf cert)
   │  প্রশ্ন: এর উপরে কার সই?
   ▼
ca.crt (root cert) — এর সই-ই root-এর নিজের (self-signed)
   │  প্রশ্ন: root-টা কি আমার বিশ্বাসের তালিকায় (trust store / CustomTrustStore)?
   ▼
হ্যাঁ → chain verified ✓   (এখানেই হাঁটা শেষ — এটাই "trust anchor")
না → chain failed ✗
```

নিয়মগুলো:

1. **প্রতিটা certificate-এ তার issuer-এর নাম লেখা থাকে** (Issuer ঘর), আর প্রতিটার নিজের নাম (Subject ঘর)। Leaf-এর Issuer = পরের certificate-এর Subject — এই মিলে মিলে চেইন সাজানো হয়।
2. **Root certificate self-signed** — নিজের দিয়ে নিজের সই। তাই তার উপরে আর যাওয়ার জায়গা নেই; বিশ্বাস এখানেই "anchor" করে দিতে হয়। আপনি আগে থেকে সিদ্ধান্ত করে রাখেন কোন root-কে বিশ্বাস করবেন — OS-এর trust store-এ বসিয়ে, বা code-এ `CustomTrustStore`-এ হাতে দিয়ে।
3. বড় সংস্থার PKI-তে মাঝে **intermediate CA** থাকে: `leaf → intermediate → root`। যাচাই একই — সই-চেইন ধরে উপরে উঠতে থাকা, যতক্ষণ না বিশ্বাস-করা root-এ পৌঁছায়। Dev-এ সাধারণত শুধু root + leaf, intermediate নেই — এটাই স্বাভাবিক।

.NET-এ এই হাঁটাটা করে `X509Chain.Build()` — আর "কোন root-কে বিশ্বাস করব" সেটা বলে দেয় `ChainPolicy`:

```csharp
using var chain = new X509Chain();
chain.ChainPolicy.TrustMode = X509TrustMode.CustomRootTrust; // OS-এর দীর্ঘ তালিকা নয় —
chain.ChainPolicy.CustomTrustStore.Add(rootCaCert);          // শুধু আমার দেওয়া এই একটা root
bool ok = chain.Build(clientCert);                            // leaf → root হাঁটা + মেয়াদ চেক
```

**কেন `CustomRootTrust`?** Windows-এর trust store-এ পৃথিবীর শত শত public root CA আছে (DigiCert...)। Private mTLS-এ আপনি চান ঠিক **একটা** CA-র সই-করা certificate-ই মানবে — তাই নিজের ছোট্ট তালিকা বানিয়ে দেন। Gateway-র `MtlsConfigurator` আর manager-এর server-side validator — দুই জায়গাতেই এখন ঠিক এই কোড বসে আছে।

## 2.4 নিজের চোখে দেখুন: certificate-এর ভেতরে আসলে কী লেখা

Git Bash-এ একটা কমান্ড — এরপর থেকে certificate আপনার কাছে রহস্য না, একটা text ফাইল:

```bash
cd /d/Workspace/Sources/RVL/rvl-sbqr/rvl-sbqr-workspace/rvl-secure-bqr-manager

openssl x509 -in dev-certs/ca.crt -text -noout | head -20
```

যে লাইনগুলো খেয়াল করবেন:

```
Issuer:  CN=SBQR Dev Root CA …          ← কে সই করেছে (root-এ নিজের নাম-ই)
Subject: CN=SBQR Dev Root CA …          ← certificate-টা কার
Validity: Not Before … Not After …      ← মেয়াদ
X509v3 Basic Constraints: critical
    CA:TRUE                              ← ★ এটা CA — অন্যকে সই করার অনুমতি আছে
```

Leaf certificate-গুলোতে (`sbqr-api.crt`, `fi-gateway.crt`, `evil-client.crt`) একই কমান্ড চালিয়ে দেখুন:

```
Issuer:  CN=SBQR Dev Root CA …          ← root CA সই করেছে (Subject আর Issuer আলাদা!)
Subject: CN=localhost …                 ← server cert-এর নাম
X509v3 Subject Alternative Name:
    DNS:localhost, IP:127.0.0.1         ← ★ server cert-র বৈধ নামের তালিকা (SAN)
X509v3 Basic Constraints:
    CA:FALSE                             ← এটা CA নয় — এটা দিয়ে আর certificate সই করা যাবে না
```

**`CA:TRUE/FALSE` এত গুরুত্বপূর্ণ কেন?** এটাই ঠেকায় যে কেউ নিজের leaf certificate দিয়ে আরেকটা নকল certificate সই করে চেইন বাড়িয়ে দিচ্ছে। আর **`CA:TRUE` ছাড়া কোনো certificate-কে trust anchor বানালে .NET-এর `chain.Build()` fail করবে** — troubleshooting-এ এটা আছে।

আর একটা মজার জিনিস — `dev-certs/`-এ একটা **`evil-ca` + `evil-client`** জোড়া আছে। এটা ইচ্ছাকৃতভাবে বানানো "খারাপ পক্ষ": আলাদা CA-র সই-করা certificate, যেটা দিয়ে ঢুকতে গেলে বসতেই হবে connection মরবে। Negative test-এর জন্য প্রস্তুত রাখা প্রতিপক্ষ-অভিনেতা।

## 2.5 Dev-এ তিন ভূমিকাই এখন সঠিক জায়গায়; Production-এ ভূমিকা আরও ছড়িয়ে যায়

| ভূমিকা | লোকাল ডেভে (এখন) | প্রোডাকশনে (অংশ ৬) |
|---|---|---|
| **Issuer (CA)** | `rvl-secure-bqr-manager`-এর `scripts/dev-certs-generate.sh` — platform পক্ষের repo-তে, gateway-এর নাগালের বাইরে | ব্যাংক/platform-এর PKI টিম — আলাদা, প্রায়ই offline মেশিনে root CA; দৈনন্দিন ইস্যু করে intermediate CA |
| **Gateway-এর cert** | script-এর ইস্যু করা `fi-gateway.pfx` (pass: `fi-gateway-dev`) | PKI টিমের কাছ থেকে **CSR flow**-এ ইস্যু করা cert (অংশ ৬.২) |
| **SBQR-এর cert** | script-এর ইস্যু করা `sbqr-api.pfx` | আসল hostname-সহ cert — internal PKI বা (server পাশটা প্রয়োজনে) public CA |
| **বিশ্বাস করানো** | config-এ pinned CA (gateway: `ServerCaCertPath`) + দরকার হলে `certutil` | শুধু config-এ pinned CA — prod মেশিনের OS store-এ dev-style ঢুকতে দেবেন না |

মনে রাখার সারমর্ম, তিন বাক্যে:

1. **CA একটা ভূমিকা, একটা সত্তা** — যে সই করে; দুই পক্ষ তার প্রতিলিপি (public cert) রেখে একে অপরকে যাচাই করে।
2. **একই CA দুই ধরনের certificate সই করতে পারে** (server + client) — বা প্রোডাকশনে দুই পাশে আলাদা CA-ও থাকতে পারে; বাধ্যতামাত্র একটাই: *verifier-এর কাছে প্রতিপক্ষের certificate-এর CA-র public cert থাকতে হবে।*
3. **`ca.key` (CA-র private key) হলো পুরো সিস্টেমের প্রাণ** — এখন সেটা platform পক্ষের repo-তে (সঠিক দিক), প্রোডাকশনে এর নিরাপত্তাই PKI-র নিরাপত্তা।

---

# অংশ ৩ — আপনার প্রজেক্টে mTLS কোথায় দাঁড়িয়ে: বর্তমান অবস্থা

## 3.1 বড় ছবি

Architecture অনুযায়ী mobile app কখনো সরাসরি `sbqr.api`-কে call করে না এবং platform-এর কোনো secret ধরে না — সবকিছু চেইন করা:

```
[Mobile App] ──HTTPS/JWT──► [FI IdP]      (login)
[Mobile App] ──HTTPS/JWT──► [FI Gateway]  (BFF — এটা আপনার সার্ভিস)
[FI Gateway] ──mTLS over VPN──► [sbqr.api platform]
```

mTLS-এর দৃশ্যপট শুধু **শেষ hop-টা**। সেখানে:

- **FI Gateway = TLS client** (নিজের certificate *পাঠায়*)
- **sbqr.api = TLS server** (নিজের certificate দেখায় **এবং** দেখা মাত্র client-এর certificate চেয়ে বসে)

## 3.2 কোন ফাইলে কী আছে — আজকের সত্যি হিসাব (সবকিছু হয়ে গেছে)

**Gateway পক্ষ (`rvl-sbqr-fi-gateway`) — সম্পূর্ণ ✅:**

| কাজ | ফাইল | অবস্থা |
|---|---|---|
| Client cert config bind | `src/SBQR.FiGateway.Api/Mtls/MtlsOptions.cs` — `"Mtls"`: `Enabled`, `CertPath`, `CertPassword`, **`ServerCaCertPath`** | ✅ |
| Cert লোড + উভয় client-এ apply | `Mtls/MtlsConfigurator.cs` — token **ও** QR HttpClient-ই | ✅ (commit `ae111de`) |
| Server cert verify (pinned CA) | একই `MtlsConfigurator.cs`-এ `CustomRootTrust` chain callback | ✅ (commit `ae111de`) |
| Production fail-fast | `Observability/BootGuard.cs` | ✅ আগে থেকেই |
| Unit tests | `tests/…/Mtls/MtlsConfiguratorTests.cs` | ✅ (+81 লাইন নতুন test) |
| লোকাল e2e env | `src/SBQR.FiGateway.Api/appsettings.MtlsTesting.json` — **untracked, local-only** (আসল dev bootstrap secret আছে বলে commit করা হয়নি) | ✅ (অংশ ৪, Step ৫) |

**SBQR পক্ষ (`rvl-secure-bqr-manager`) — সম্পূর্ণ ✅:**

| কাজ | ফাইল | অবস্থা |
|---|---|---|
| HTTPS endpoint (`:7443`) + client cert দাবি | `src/Host/SBQR.Api/Mtls/MtlsEndpointsExtensions.cs` — `ClientCertificateMode.RequireCertificate` | ✅ (commit `a9facee`) |
| Config | `src/Host/SBQR.Api/Mtls/MtlsOptions.cs` — `Mtls:Enabled` (default false), `HttpsPort` (7443), `AllowedClientThumbprints` (optional pinning) | ✅ |
| Per-tenant certificate registry | DB-backed binding (commit `e027dcf`) | ✅ |
| Dev PKI generator | `scripts/dev-certs-generate.sh` → `dev-certs/` (CA + server + client + **evil pair** + PFX) | ✅ |
| বাংলা গাইড | `docs/mtls-guide.md` (concept→code→test→prod), `docs/mtls-dev-test-guide.md` (চেকলিস্ট), `docs/mtls-fi-onboarding-guide.md`, evidence reports | ✅ |

**জেনে রাখুন (লিড হিসেবে সিদ্ধান্ত বাকি):**

- `rvl-sbqr-api` (আগের "current copy")-তে এই mTLS commit-গুলো **নেই** — দুই SBQR copy এখন diverged। কোনটা canonical হবে, সেই সিদ্ধান্ত + mirroring বাকি।
- Gateway-এর `docker-compose.yml` এখনো মুছে-ফেলা `./tmp` folder mount করে (fake-idp/fake-sbqr-api service) — `docker compose up` ওই দুই service-এ ব্যর্থ হবে।
- Manager-এর `docs/mtls-guide.md`-এর শুরুতে "implementation এখনো রিপোতে নেই" banner — implementation এসে যাওয়ায় সেটা এখন stale।

## 3.3 Gateway-এর client-side code পড়ে বুঝুন (শেখার উদাহরণ)

`Mtls/MtlsConfigurator.cs`-এর মূল অংশ:

```csharp
var cert = new X509Certificate2(certPath, certPassword,
    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ClientCertificateOptions = ClientCertificateOption.Manual,
    ClientCertificates = { cert },
});
```

লাইন ধরে কেন-টা:

- `X509Certificate2(path, password, flags)` — PFX থেকে cert + private key লোড। `MachineKeySet | PersistKeySet` মানে Windows key store ব্যবহার করে key রাখা।
- `ConfigurePrimaryHttpMessageHandler` — handler pipeline-এর **একদম নিচের স্তরে** (Polly retry-রও, auth handler-এরও নিচে) cert বসে, যাতে প্রতিটা আসল network call-ই cert-সহ যায়।
- `ClientCertificates = { cert }` — server certificate চাইলে এটা পাঠাবে; সময়মতো পাঠানোর সিদ্ধান্ত TLS নিজে নেয়।
- আর `ServerCaCertPath` সেট থাকলে সাথে server-validation callback-ও configure হয় (অংশ ৫.৩)।
- পুরোটা `Mtls:Enabled=false` হলে **no-op** — mTLS ছাড়া dev flow অক্ষত।

## 3.4 আজকের dev tooling

| টুল | কোথায় | কাজ |
|---|---|---|
| Dev PKI generator | `rvl-secure-bqr-manager/scripts/dev-certs-generate.sh` | root CA + `sbqr-api.*` + `fi-gateway.*` + `evil-*` (negative test) + PFX বানায় → `dev-certs/` |
| Server-side mTLS endpoint | manager SBQR.Api, `Mtls:Enabled=true` হলে | `http://127.0.0.1:5001` (আগের মতো) + `https://127.0.0.1:7443` (client cert ছাড়া connection নেই) |
| Gateway লোকাল mTLS env | `rvl-sbqr-fi-gateway/src/SBQR.FiGateway.Api/appsettings.MtlsTesting.json` | `ASPNETCORE_ENVIRONMENT=MtlsTesting` হলে লোড হয় — সব `Mtls__*` + `Platform__*` সেট-করা |
| ম্যানুয়াল test গাইড | `rvl-secure-bqr-manager/docs/mtls-dev-test-guide.md` | openssl s_client / curl দিয়ে পুরো test matrix — drill-এর সত্যের উৎস |
| কনসেপ্ট গাইড | `rvl-secure-bqr-manager/docs/mtls-guide.md` | লাইন-বাই-লাইন ব্যাখ্যা (নোট: শুরুর "implementation নেই" banner-টা এখন পুরনো) |

---

# অংশ ৪ — লোকাল drill, নতুন harness দিয়ে

> পুরনো drill-টা (gateway-এর মুছে-ফেলা `tmp/fakes` Node fake-এর ওপর) আর নেই — এখন আরও ভালো জিনিস আছে: **আসল SBQR api-ই mTLS ঢালাই অবস্থায়** `:7443`-এ চলে, আর gateway আসল config দিয়ে তার সাথে কথা বলে। কোনো code change লাগে না। পূর্ণাঙ্গ test matrix-এর জন্য `rvl-secure-bqr-manager/docs/mtls-dev-test-guide.md` দেখুন — নিচে মেরুদণ্ডটা। Certificate গুলো নিজে হাতে বানাতে বানাতে শিখতে চাইলে আগে [certificate lifecycle ল্যাবটা](cert-lifecycle-hands-on-lab-bn.md) করুন — এই drill-এর স্বাভাবিক পূর্বসূরি।

## Step 0: Prerequisites

```bash
dotnet --version    # 8.0+ (gateway), .NET 10 SDK (manager SBQR.Api-র জন্য)
node --version      # দরকার নেই আর — fake চলে গেছে
openssl version     # Git Bash-এর ভেতরে
```

দুটো repo sibling হিসেবে checkout থাকতে হবে (workspace layout ঠিক এটাই): `rvl-sbqr-workspace/rvl-secure-bqr-manager` + `rvl-sbqr-workspace/rvl-sbqr-fi-gateway`।

## Step 1: Dev certificates (একবারই)

```bash
cd /d/Workspace/Sources/RVL/rvl-sbqr/rvl-sbqr-workspace/rvl-secure-bqr-manager
bash scripts/dev-certs-generate.sh
```

ফলাফল `dev-certs/`-এ — মনে মনে অংশ ২-এর ভূমিকা-টেবিল মিলিয়ে নিন:

```
ca.crt / ca.key            ← issuer (root CA) — key শুধু এখানে
sbqr-api.crt/.key/.pfx     ← server-এর পরিচয়
fi-gateway.crt/.key/.pfx   ← gateway-এর পরিচয় (PFX pass: fi-gateway-dev)
evil-ca.* / evil-client.*  ← খারাপ পক্ষ (negative test-এর জন্য)
```

## Step 2: SBQR api-তে mTLS চালু করা

Manager repo-root `.env`-এ (template: `.env.example`-এর শেষ অংশ):

```ini
Mtls__Enabled=true
# ঐচ্ছিক hardening — শুধু নির্দিষ্ট certificate মানতে:
#Mtls__AllowedClientThumbprints__0=<THUMBPRINT>   # openssl x509 -in dev-certs/fi-gateway.crt -noout -fingerprint -sha256
```

চালান — Visual Studio-এ SBQR.Api প্রজেক্টে **F5**, বা:

```bash
dotnet run --project src/Host/SBQR.Api
```

Console-এর শুরুর দিকে দুটো লাইন দেখলেই mTLS endpoint বসেছে:

```
Now listening on: http://127.0.0.1:5001
Now listening on: https://127.0.0.1:7443      ← ★
```

## Step 3: প্রথম প্রমাণ — openssl দিয়ে gateway সেজে ঢোকা

```bash
# ভালো certificate সহ (fi-gateway):
openssl s_client -connect localhost:7443 -CAfile dev-certs/ca.crt \
  -cert dev-certs/fi-gateway.crt -key dev-certs/fi-gateway.key
# প্রত্যাশিত: "Verify return code: 0 (ok)" — handshake পুরো হয়েছে
# (এর পরে GET টাইপ করে দেখতে পারেন: GET /health/live HTTP/1.1 + Host + দুইবার Enter)

# certificate ছাড়া:
openssl s_client -connect localhost:7443 -CAfile dev-certs/ca.crt
# প্রত্যাশিত: handshake-ই fail / alert — RequireCertificate মানে ঢুকতে দেবে না

# খারাপ certificate সহ (evil-client — অন্য CA-র সই):
openssl s_client -connect localhost:7443 -CAfile dev-certs/ca.crt \
  -cert dev-certs/evil-client.crt -key dev-certs/evil-client.key
# প্রত্যাশিত: connection মরবে — আমাদের CA-র সই না, চেইন মিলল না
```

তিনটা ফলাফলই এক নজরে দেখলেন: **ভালো cert ✓, cert নেই ✗, খারাপ cert ✗** — mTLS-এর পুরো গল্প।

## Step 4: Gateway দিয়ে end-to-end (আসল ফ্লো)

Gateway-এর `src/SBQR.FiGateway.Api/appsettings.MtlsTesting.json` ফাইলটা (local-only, untracked) সবকিছু সেট-করা রাখে — `Platform:BaseUrl=https://localhost:7443`, `Mtls:Enabled=true`, manager-এর `dev-certs/`-এর path-গুলো। শুধু environment বদলে চালান:

```powershell
cd D:\Workspace\Sources\RVL\rvl-sbqr\rvl-sbqr-workspace\rvl-sbqr-fi-gateway\src\SBQR.FiGateway.Api
$env:ASPNETCORE_ENVIRONMENT = "MtlsTesting"
$env:ASPNETCORE_URLS        = "http://+:8080"
dotnet run --no-launch-profile
```

(`MtlsTesting` environment হলে ASP.NET Core নিজে থেকেই `appsettings.MtlsTesting.json` লোড করে — এটাই ওই ফাইলের কাজ। Relative cert path-গুলো (`../../../rvl-secure-bqr-manager/dev-certs/...`) ঠিকভাবে মেপে পড়ার জন্য project ফোল্ডার থেকে চালান।)

Gateway উঠলে যাচাই: `curl -s http://localhost:8080/health/ready` → platform token warm হলে সবুজ — মানে gateway ইতিমধ্যে `https://localhost:7443`-এ **mTLS পথে** token নিতে পেরেছে। এরপর fake IdP দিয়ে JWT mint করে QR endpoint call করলে পুরো চেইন সসম্পূর্ণ।

## Step 5: Negative test — gateway-এর mTLS বন্ধ করলে

```powershell
$env:MTLS__ENABLED = "false"     # appsettings.MtlsTesting.json-কে override করে
dotnet run --no-launch-profile
```

`/health/ready` এবার লাল হবে — token fetch TLS-এই মরবে (`RequireCertificate` client cert ছাড়া ঢুকতে দিচ্ছে না)। ব্যস — server-এর দরজা সত্যিই বন্ধ।

## Step 6: Automated tests

```bash
# Gateway:
cd /d/Workspace/Sources/RVL/rvl-sbqr/rvl-sbqr-workspace/rvl-sbqr-fi-gateway
dotnet test tests/SBQR.FiGateway.Tests --filter "FullyQualifiedName~Mtls"

# Manager (server-side validator + endpoint tests):
cd /d/Workspace/Sources/RVL/rvl-sbqr/rvl-sbqr-workspace/rvl-secure-bqr-manager
dotnet test SBQR.slnx --filter "FullyQualifiedName~Mtls"
```

Test-গুলো নিজেরাই runtime-এ certificate বানায় — কোনো key file commit হয় না।

---

# অংশ ৫ — Implementation ব্যাখ্যা: কোন ফাইলে কী হয়েছে

> ⚠️ এই অংশটা আর "করণীয় তালিকা" না — সবকিছু **implemented হয়ে গেছে**। এখন এর দাম হলো: কোড পড়তে বসলে কোন ফাইল কীন ওইরকম লেখা, তার ভেতরের যুক্তি। নিচের নকশা-ব্যাখ্যাগুলো আসল implementation-এর সাথে প্রায় হুবহু মিলবে; যেখানে নাম/পথ আলাদা, সেখানে আসলটা বলে দিয়েছি।

## 5.1 Server-পক্ষের নকশা (manager: `src/Host/SBQR.Api/Mtls/`)

দুটো ফাইলই আসলে সব বহন করে:

- **`MtlsOptions.cs`** — `Mtls:Enabled` (default false → আজকের HTTP-only আচরণ অক্ষত), `HttpsPort` (7443), `AllowedClientThumbprints` (optional — D2-ধাঁচের pinning, এখন থেকেই available)।
- **`MtlsEndpointsExtensions.cs`** — enabled হলে Kestrel-এ **আলাদা HTTPS endpoint** যোগ করে (`Listen(IPAddress.Loopback, 7443, …UseHttps(…))`), সেখানে `ClientCertificateMode.RequireCertificate` + CA-pinned validation। আগের HTTP `:5001` endpoint অক্ষত থাকে — health/docs/orchestrator সেখানে।

**ডিজাইন-পার্থক্যটা খেয়াল করুন** (এটাই architecture-review প্রশ্নের জবাব দেবে): একটা পথ হলো একই endpoint-এ `AllowCertificate` + middleware দিয়ে cert-নেই-তো-403 (ভাবটা: ভেতরে ঢুকে দরজা দেখো); আসল implementation নিয়েছে উল্টোটা — **দুটো আলাদা endpoint, HTTPS-টায় `RequireCertificate`** (ভাবটা: দরজাতেই পরিচয়, নাহলে ঢুকাই নেই)। দুটোই সঠিক; endpoint-split নেওয়ায় `/health` exemption-এর ঝামেলাও নেই — health ওতো আগের HTTP পোর্টেই। `RequireCertificate`-এর মানে অংশ ৪-এর Step ৩-এ দেখলেন: cert ছাড়া/খারাপ cert-এ **HTTP status-ই নেই**, handshake মরে।

Validation callback-এর ভেতরটা অংশ ২.৩-এর সেই হাঁটা: `X509Chain` + `TrustMode = CustomRootTrust` + `CustomTrustStore.Add(ca)` + `chain.Build(clientCert)`, সাথে EKU/thumbprint-যাচাই (per-tenant registry-র সাথে মিলিয়ে)।

## 5.2 Client-পক্ষের নকশা (gateway: `src/SBQR.FiGateway.Api/Mtls/`)

`MtlsConfigurator.Apply()` এখন **দুই** named HttpClient-এই ডাকা হয় (`Program.cs`):

1. cert লোড: `X509Certificate2(CertPath, CertPassword, MachineKeySet | PersistKeySet)`
2. `ConfigurePrimaryHttpMessageHandler` → `HttpClientHandler` যার `ClientCertificates`-এ সেই cert
3. `ServerCaCertPath` সেট থাকলে **server-validation callback** — নিচের যুক্তি (server-side validator-এর আয়না-রূপ, দিক উল্টো):

```csharp
handler.ServerCertificateCustomValidationCallback = (request, serverCert, _, errors) =>
{
    if (errors == SslPolicyErrors.None) return true;  // publicly-trusted cert (prod) — ঢুকতে দিন
    if (serverCert is null) return false;

    using var pinned = new X509Chain();
    pinned.ChainPolicy.TrustMode = X509TrustMode.CustomRootTrust;
    pinned.ChainPolicy.CustomTrustStore.Add(caCert);        // dev/ internal CA
    pinned.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    return pinned.Build(serverCert);
};
```

4. `Mtls:Enabled=false` → পুরো জিনিসটা no-op।
5. `BootGuard`: Production-এ enabled কিন্তু path/password missing → শুরুতেই fail-fast।

**Token client কেন include করা হলো** (আগের deferral ভেঙে): token endpoint-টাই সবচেয়ে sensitive call — platform token বানায়। এখন নিয়মটা সহজ: "CA না থাকলে API-তে ঢোকাই যাবে না, কোনো endpoint-এই।"

## 5.3 Config-এর আসল চাবিকাঠি (দুই পাশ)

**Gateway (`Mtls` section):** `Enabled`, `CertPath` (PFX), `CertPassword`, `ServerCaCertPath` (CA public cert) — `.env.example`-এ হুবহু উদাহরণ আছে।

**Manager SBQR.Api (`Mtls` section):** `Enabled`, `HttpsPort`, `AllowedClientThumbprints__0…` (optional)।

## 5.4 Test স্তরায়ন — কোন প্রমাণ কোথায় পাবেন

| প্রমাণ | কোথায় |
|---|---|
| Cert না দিলে handshake মরে | drill Step ৩ (`openssl s_client` cert ছাড়া) |
| খারাপ CA-র cert মরে | drill Step ৩ (`evil-client`) |
| ভালো cert-এ পুরো ফ্লো | drill Step ৪ (gateway end-to-end) |
| Config ভুল হলে app শুরুই হয় না | ইচ্ছা করে `Mtls__CertPath` ভুল দিয়ে চালান — fail-fast দেখুন |
| Validation logic স্বয়ংক্রিয়ভাবে | দুই repo-র `Mtls` test suite (drill Step ৬) — test-এর ভেতরেই `CertificateRequest` API দিয়ে PKI বানানো হয়; `WebApplicationFactory`/TestServer সত্যিকারের TLS করতে পারে না বলে আসল socket ব্যবহার করা হয় |

---

# অংশ ৬ — লোকাল হলে এবার প্রোডাকশন: একই পদ্ধতিতে

> লোকালে আপনি যা প্রমাণ করলেন, প্রোডাকশনে তার **একই কোড** চলবে — বদলাবে শুধু **certificate কোথা থেকে আসে, কোথায় থাকে, আর কে বিশ্বাস করে**। এই অংশটা সেই বদলগুলোর গাইড, একই ধাঁচে — ব্যাখ্যা + কমান্ড + চেকলিস্ট।

## 6.1 হাতে-কলমের ম্যাপ: লোকালে যা করেছেন → প্রোডাকশনে তার রূপ

| লোকালে (অংশ ৪) | প্রোডাকশনে | কেন বদলায় |
|---|---|---|
| `scripts/dev-certs-generate.sh` বানানো dev root CA | ব্যাংক/platform PKI টিমের **আসল root CA** (offline-এ থাকে, দৈনন্দিন ইস্যু করে intermediate) | Dev CA-র private key repo-র সাথে ঘোরে — সেটা দিয়ে সই-করা কিছুকে প্রোডাকশন কখনোই মানবে না |
| `fi-gateway.pfx` script বানিয়ে দিয়েছে (pass `fi-gateway-dev`) | **CSR flow**-এ ইস্যু করা certificate (৬.২) | প্রকৃত প্রতিষ্ঠান-পরিচয়সহ, মেয়াদ-নিয়ন্ত্রিত, revoke করা সম্ভব |
| `certutil -user -addstore Root` (দরকার হলে) | হবে **না** — বিশ্বাস config থেকে: gateway-এ `Mtls__ServerCaCertPath` | Prod মেশিনের OS store-এ dev-style CA ঢোকানো = নিরাপত্তা-দুর্ঘটনা; pinned CA-ই স্পষ্ট ও audit-যোগ্য |
| `sbqr-api.pfx` (SAN: localhost/127.0.0.1) | আসল hostname/FQDN-সহ cert (SAN-এ internal DNS নাম) | নাম না মিললে client-এর `NameMismatch` |
| `fi-gateway-dev` password | শক্ত password / Key Vault secret | Dev password জানা মানুষ অনেক — prod-এ অকল্পনীয় |
| ফাইল `dev-certs/`-এ | Secret হিসেবে mount (৬.৩) | Image-এ bake করা cert = যে-কেউ image পেলেই key পেলো |
| `Mtls:Enabled` toggle | **একই কোড** — শুধু rollout ক্রম মানবেন (৬.6) | — |

**মূল স্বস্তি-লাইন:** দুই পাশের implementation কোড প্রোডাকশনে **অপরিবর্তিত** যায় — বদলায় শুধু `Mtls__*`-এর **মান**। ঠিক যেমন dev থেকে prod-এ যায় connection string।

## 6.2 Certificate আসবে কোথা থেকে — CSR flow (সবচেয়ে গুরুত্বপূর্ণ অংশ)

Prod-এ কেউ আপনাকে তৈরি PFX ইমেইল করে দেবে না। হয় প্রায় সব প্রতিষ্ঠান-PKI-তে হয় **CSR (Certificate Signing Request) flow**:

```
[আপনার সার্ভার]                                  [PKI টিম / CA]
  ১. private key নিজে বানান ─────────────────────►  (কিছুই যায় না!)
  ২. CSR বানান (public অংশ + নাম) ────────────────►  ৩. পরিচয় যাচাই করে
                                                    ৪. সই করে cert ফেরত দেয় ◄────
  ৫. cert + নিজের private key জোড়া লাগান
```

**এর সৌন্দর্য:** private key কখনো আপনার মেশিন ছাড়েই না — CA-ও দেখে না। (একই কারণে অংশ ২-এ বলেছিলাম: gateway/sbqr-এর কাছে শুধু নিজের key, CA-র key শুধু CA-র কাছে।)

Gateway-এর client certificate-এর জন্য কমান্ড (আপনার মেশিনে, Git Bash):

```bash
# ১. Private key (এটা কোথাও যাবে না):
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out fi-gateway-prod.key

# ২. CSR — নামে প্রতিষ্ঠান-পরিচয় দিন, PKI টিম যা চায় তাই (FI code, OU ইত্যাদি):
openssl req -new -key fi-gateway-prod.key -out fi-gateway-prod.csr \
  -subj "/CN=fi-gateway/O=<bank-name>/OU=<fi-code>"

# ৩. CSR ফাইলটা PKI টিমকে পাঠান → সই-করা cert (PEM) ফেরত পাবেন

# ৪. ফেরত-পাওয়া cert + নিজের key জুড়ে PFX:
openssl pkcs12 -export -inkey fi-gateway-prod.key -in fi-gateway-prod.crt \
  -name fi-gateway -out fi-gateway-prod.pfx
```

SBQR-এর server certificate-এর জন্যও একই flow — শুধু CSR-এর নাম হবে আসল hostname (SAN), আর সেই CA-র public cert gateway-এ `Mtls__ServerCaCertPath` হয়ে বসাবেন।

**কার কাছ থেকে:** আপনাদের মডেলে client certificate ইস্যু করবে **platform/ব্যাংক-পক্ষের PKI** (কারণ SBQR-ই verifier — ঠিক যেমন dev-এ generator manager repo-তে বসেছে)। Server পাশটার cert internal PKI থেকেও আসতে পারে, public CA (Let's Encrypt-জাতীয়) থেকেও — দুটোই ঠিক, কারণ gateway-এর validation কোড দুই রকমই মানে (`SslPolicyErrors.None` → pass)।

## 6.3 Private key কোথায় থাকবে — file থেকে vault পর্যন্ত স্পেকট্রাম

| ধাপ | কোথায় | কেমন |
|---|---|---|
| Dev (এখন) | `dev-certs/`, পরিচিত password | ঠিক আছে — ওইসব certificate মূল্যহীন |
| প্রথম প্রোডাকশন ধাপ | Secret হিসেবে **mount**: Docker secret / K8s Secret / ACA secret / VM-এ root-only permission ফাইল | বাস্তবসম্মত শুরু |
| পরিণত ধাপ | Key Vault (cert হিসেবে), ব্যাংক-HSM | rotation/audit সহজ |

নিয়মগুলো: repo-তে কখনো না (`.gitignore`-এ `*.pfx`, `*.key` রাখুন — `appsettings.MtlsTesting.json`-ও এজন্যই untracked, ভেতরে আসল dev tenant-এর secret আছে); container image-এ bake না; PFX password env-var/secret থেকে, log-এ কখনো না; যে মেশিনে key, সেখানে প্রয়োজন-সর্বোচ্চ কম মানুষের অ্যাক্সেস।

## 6.4 Topology: TLS কোথায় terminate হবে — এই প্রজেক্টে উত্তর সরাসরি Kestrel-এ

```
(ক) [gateway] ══mTLS══► [Kestrel/SBQR :7443]                    ← এই প্রজেক্টের পথ (এখন লোকালেও ঠিক এটাই)
(খ) [gateway] ══mTLS══► [nginx/envoy] ──plain HTTP──► [SBQR]
```

(খ)-এ সমস্যা: mTLS-এর পরিচয়-প্রমাণ **proxy-তেই শেষ** হয়ে যায়; proxy→app অংশটা আবার অরক্ষিত plain HTTP — VPN-এর ভেতরে হলেও বাড়তি আক্রমণ-পথ। আর এখানে BFF-ই একমাত্র client, তাই proxy-র দরকারও নেই। **সিদ্ধান্ত: end-to-end TLS Kestrel পর্যন্ত** — লোকাল drill-এ যেটা চালালেন, সেটাই প্রোডাকশন topology। (ভবিষ্যতে proxy জরুরি হলে SSL-passthrough কনফিগার করতে হবে, termination না।)

একটা কোড-পরিবর্তন লাগবে: লোকালে `IPAddress.Loopback` — container/VM-এ বাইরের দিক শুনতে `ListenAnyIP`/config-driven address করতে হবে (`MtlsEndpointsExtensions.cs`-এ ছোট পরিবর্তন)।

## 6.5 প্রোডাকশন config — দুই পাশে পাশাপাশি

**SBQR api (server):**

```ini
Mtls__Enabled=true
Mtls__HttpsPort=7443
# ঐচ্ছিক কিন্তু প্রস্তাবিত — শুধু ইস্যু-করা certificate-গুলোই:
Mtls__AllowedClientThumbprints__0=<FI-GATEWAY-CERT-THUMBPRINT>
```
(server cert নিজে Kestrel config/secret থেকে পায় — দেখুন `MtlsEndpointsExtensions.cs` ও `.env.example`)

**FI Gateway (client):**

```ini
Platform__BaseUrl=https://sbqr-api.internal.example:7443
Mtls__Enabled=true
Mtls__CertPath=/var/secrets/fi-gateway-client.pfx
Mtls__CertPassword=<vault-secret-ref/env>
Mtls__ServerCaCertPath=/var/secrets/server-root-ca.pem     # SBQR-এর certificate-দাতা CA-র public cert
```

কোনো `certutil` নেই, কোনো OS store নেই — দুই পাশেই বিশ্বাস pinned। Gateway-এর `BootGuard` Production-এ ভুল config-এ শুরুই হতে দেয় না।

## 6.6 Rollout — zero-downtime ক্রম (উপেক্ষা করবেন না, এখানেই ভুল হয়)

নিয়ম: **আগে সবাইকে cert দাও, তারপর দরজা বন্ধ করো।** উল্টোটা করলে live traffic মরবে।

| ধাপ | পরিবর্তন | ঝুঁকি | কী দেখবেন |
|---|---|---|---|
| ১ | SBQR: HTTPS endpoint deploy কিন্তু `Mtls__Enabled=false` (শুধু HTTP, আগের মতো) — কোড পাশে থাকে, চালু না | কম | পুরনো flow অক্ষত |
| ২ | SBQR: `Mtls__Enabled=true` কিন্তু thumbprint pinning ছাড়া; Gateway: `Mtls__Enabled=true` — এখন cert পাঠাচ্ছে | কম | SBQR-এর log-এ client cert আসতে দেখুন |
| ৩ | SBQR: `AllowedClientThumbprints` pinning চালু — **সব BFF instance-এর cert confirm করার পরেই** | মাঝারি | drill Step ৩-এর negative test-গুলো prod smoke হিসেবে আবার |
| ৪ | BootGuard-কে দায়িত্ব দিন: Prod-এ mTLS ছাড়া শুরুই হবে না | কম | ভুল config দিয়ে restart করে fail-fast দেখুন (ইচ্ছা করা ভাঙা!) |

## 6.7 মেয়াদ, rotation, monitoring — চালু হওয়ার পরের জীবন

- **মেয়াদ ফুরিয়ে গেলে সব connection একসাথে মরবে** — এটাই mTLS-এর ক্লাসিক প্রোডাকশন-দুর্ঘটনা। শুরুতেই এক হাত রাখুন (BootGuard বা health check-এ):
  ```csharp
  var cert = new X509Certificate2(path, password);
  if (cert.NotAfter < DateTimeOffset.UtcNow.AddDays(30))
      // log করুন / health-degrade করুন — আগে জানা, আগে বদলানো
  ```
- **Rotation:** নতুন cert issue → config-এ path বদল → restart/roll। দুই পাশের মেয়াদের ক্যালেন্ডার রাখুন; আগে থেকে ৩০ দিনের warning।
- **Monitoring:** handshake-failure-এর spike দেখা মানে কারো cert মেয়াদ/ভুল হয়েছে — SBQR-এর log-এ TLS alert গুনে রাখুন।
- **Per-tenant registry** (commit `e027dcf`) ভবিষ্যতে এক FI-র cert revoke/rotate করলে বাকিদের ছোঁয়া লাগবে না — এই দিকেই বাড়ুন।

## 6.8 প্রোডাকশন চেকলিস্ট

| ✓ | জিনিস |
|---|---|
| ☐ | Dev CA (`SBQR Dev Root CA` / `sbqr-dev-root-ca`) কোনো prod মেশিনের store-এ **নেই** — বিশ্বাস সব pinned (`ServerCaCertPath` / server-side CA) |
| ☐ | দুই পাশের cert-ই CSR flow থেকে, মেয়াদ ক্যালেন্ডারে |
| ☐ | Private key secret-mount, repo/image-এ নেই; password vault/env থেকে; log-এ নেই; `appsettings.MtlsTesting.json`-এর মতো local-only ফাইল prod config-এ ঢুকেনি |
| ☐ | Server cert-এর SAN-এ আসল hostname; gateway `Platform__BaseUrl`-এ সেই নামই |
| ☐ | `/health/live` আগের HTTP পোর্টে reach-able — orchestrator probe সবুজ |
| ☐ | Drill Step ৩–৪-এর test গুলো prod smoke হিসেবে আবার চালানো |
| ☐ | Rollout ক্রম ৬.৬ মানা হয়েছে; ফিরতি পথ (rollback) লেখা আছে |
| ☐ | BootGuard prod-এ mTLS enforce করছে |
| ☐ | Expiry-warning (৩০ দিন) বসানো হয়েছে |
| ☐ | Charter/approval: TLS কাজ প্রোডাকশনে যাওয়ার আগে plan doc-ের D5 অনুযায়ী approval নথিভুক্ত |

---

# অংশ ৭ — Troubleshooting

| লক্ষণ | আসল কারণ | সমাধান |
|---|---|---|
| `AuthenticationException: The remote certificate is invalid according to the validation procedure.` | Gateway/curl dev CA-কে বিশ্বাস করছে না — `ServerCaCertPath` ভুল path, বা curl-এর ক্ষেত্রে `-CAfile` নেই | config-এর path যাচাই করুন; openssl/curl-এ `-CAfile dev-certs/ca.crt`; `.NET HttpClient` client হিসেবে test করলে একবার `certutil -user -addstore Root dev-certs\ca.crt` |
| `RemoteCertificateNameMismatch` | Server cert-এর SAN-এ যে নাম আছে তার বাইরের নাম দিয়ে ঢোকা | BaseUrl-এ ঠিক `https://localhost:7443` (বা prod-এ SAN-এর নাম) |
| `RemoteCertificateChainErrors` / `PartialChain` | Verify করার পক্ষের কাছে সঠিক CA নেই, **বা CA cert-এ `CA:TRUE` নেই** | `openssl x509 -in <ca>.crt -text -noout` দিয়ে `Basic Constraints: CA:TRUE` দেখুন (অংশ ২.৪); pinned path ঠিক করুন |
| শুরুতেই `FileNotFoundException` / cert load fail | cert path relative CWD থেকে মেপেছে — `appsettings.MtlsTesting.json`-এর relative path-গুলো **project ফোল্ডার থেকে চালালেই** ঠিক মেপে | `src/SBQR.FiGateway.Api` থেকে `dotnet run`; নয়তো absolute path |
| `The specified network password is not correct.` | PFX password ভুল | dev-এ `fi-gateway-dev`; script আবার চালালে তার printed password |
| Certificate expired (`NotAfter` পেরোনো) | মেয়াদ শেষ | `openssl x509 -noout -dates -in <cert>`; `dev-certs-generate.sh` আবার চালান |
| `openssl s_client`-এ ভালো cert দিয়েও handshake মরে | cert/key জোড়া মিলছে না, বা `evil-client` দিয়ে ঢুকছেন (এটাই তো মরার কথা!) | `-cert` আর `-key` একই জোড়ার (`fi-gateway.crt/.key`); evil pair শুধু negative test-এর |
| Gateway `MtlsTesting` env-এ `/health/ready` লাল | `https://localhost:7443`-এ পৌঁছাচ্ছে না — manager SBQR.Api তোলা নেই, বা তার `Mtls__Enabled=true` নেই, বা dev-certs generate হয়নি | manager-এর console-এ `Now listening on: https://…7443` লাইন খুঁজুন; `.env` আর `dev-certs/` যাচাই করুন |
| Gateway-এর token fetch-এ path-জাতীয় অদ্ভুত error (`C:/Program Files/Git/v1/...`) | **Git Bash-এর MSYS path-mangling** | PowerShell থেকে চালান, অথবা `export MSYS2_ENV_CONV_EXCL="Platform__TokenEndpoint"` |
| `curl` PowerShell-এ অদ্ভুত আচরণ করছে | PowerShell-এ `curl` আসলে `Invoke-WebRequest`-এর alias | `curl.exe` লিখুন, অথবা Git Bash |
| `docker compose up`-এ fake-idp/fake-sbqr-api মরছে | Gateway-এর `docker-compose.yml` এখনো মুছে-ফেলা `./tmp` mount করে — পুরনো reference, ঠিক করা দরকার | compose থেকে ওই দুই service বাদ দিন/ঠিক করুন (এই গাইড repo-র কোড ছোঁয় না) |
| SBQR api-তে `https://…7443` লাইনই আসছে না | `Mtls__Enabled=true` আসলে সেট হচ্ছে না | manager repo-root `.env`-এ key-টা আছে কি না; appsettings থেকে `.env` লোড হচ্ছে কি না |

---

# অংশ ৮ — Glossary + আরও পড়ার লিস্ট

## Glossary (এক নজরে)

| Term | এক লাইনে |
|---|---|
| **TLS** | Transport encryption + server-এর পরিচয় প্রমাণের প্রোটোকল (HTTPS-এর `S`) |
| **mTLS** | TLS-ই, কিন্তু client-কেও certificate দেখাতে হয় |
| **X.509 certificate** | পরিচয়পত্রের স্ট্যান্ডার্ড ফরম্যাট — নাম, public key, মেয়াদ, CA-এর সই |
| **Private key** | Certificate-এর মালিকানার প্রমাণ; কখনো wire-এ যায় না |
| **CA (Certificate Authority)** | Certificate-এ সই করে দেওয়া বিশ্বস্ত তৃতীয় পক্ষ — issuer ভূমিকা (অংশ ২.১) |
| **Root CA** | Chain-এর শীর্ষ; self-signed; বিশ্বাসের উৎস (trust anchor) |
| **Intermediate CA** | Root-এর সই-করা মাঝের CA — বড় PKI-তে দৈনন্দিন ইস্যু এরাই করে |
| **Chain of Trust** | leaf → intermediate → root সই-চেইন ধরে যাচাই (অংশ ২.৩) |
| **CSR** | Certificate Signing Request — private key নিজে রেখে CA-র কাছে সই চাওয়ার আবেদন (অংশ ৬.২) |
| **PEM** | Certificate/key-এর Base64 text ফরম্যাট (`.pem`/`.crt`/`.key`) |
| **PFX / PKCS#12** | cert + private key-এর password-protected binary বান্ডল (`.pfx`) — .NET-এর প্রিয় |
| **CN (Common Name)** | Certificate-এর "নাম" ঘর |
| **SAN (Subject Alternative Name)** | Certificate কোন কোন hostname/IP-র জন্য বৈধ — আজকের সঠিক জায়গা |
| **Basic Constraints (CA:TRUE/FALSE)** | এই certificate দিয়ে অন্য certificate সই করা যাবে কি না |
| **Fingerprint / Thumbprint** | Certificate-এর hash — "এই certificate-টাই" চেনার আঙুলের ছাপ; `AllowedClientThumbprints` pinning এটা দিয়েই |
| **Handshake** | Encryption শুরুর আগে দুই পক্ষের পরিচয়-আদান-প্রদান ও key মীমাংসা |
| **ChainTrust** | "যেকোনো বিশ্বস্ত (store-এ থাকা) CA-র সই-করা cert মানব" |
| **CustomRootTrust** | "শুধু আমি হাতে দেওয়া CA-টাকে root ধরব" — private CA-র সাথে সঠিক পদ্ধতি |
| **Revocation (CRL/OCSP)** | বাতিল-করা certificate ধরার ব্যবস্থা; dev/internal CA-র কাছে নেই বলে `NoCheck` |
| **TLS termination** | TLS কোথায় শেষ হয়ে plain হয়ে যায় — mTLS-এ এটা Kestrel পর্যন্তই রাখা উচিত (অংশ ৬.৪) |
| **BFF** | Backend-for-frontend — mobile-এর জন্য আপনার FI Gateway-এর ধরন |
| **Defense in depth** | একাধিক স্তরের নিরাপত্তা — এখানে VPN + mTLS + OAuth |

## আরও পড়ার লিস্ট (ক্রমানুসারে)

1. **আসল implementation-এর Bangla গাইড (সত্যের উৎস):** `rvl-secure-bqr-manager/docs/mtls-guide.md` — concept→certificates→code→test→production, লাইন-বাই-লাইন
2. **ম্যানুয়াল test চেকলিস্ট:** `rvl-secure-bqr-manager/docs/mtls-dev-test-guide.md`
3. **ইংরেজি implementation plan:** [`rvl-sbqr-fi-gateway/docs/mtls-implementation-plan.md`](rvl-sbqr-fi-gateway/docs/mtls-implementation-plan.md)
4. Kestrel endpoint ও TLS configuration: https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints
5. ASP.NET Core-এ certificate authentication: https://learn.microsoft.com/aspnet/core/security/authentication/certauth
6. `X509Chain` ও `ChainPolicy`: https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.x509chain
7. `ServerCertificateCustomValidationCallback`: https://learn.microsoft.com/dotnet/api/system.net.http.httpclienthandler.servercertificatecustomvalidationcallback
8. `CertificateRequest` class (test-এ in-memory PKI): https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.certificaterequest
9. mTLS-এর সহজ ব্যাখ্যা: https://www.cloudflare.com/learning/access-management/what-is-mutual-tls/
10. OAuth 2.0 + mTLS binding (RFC 8705): https://www.rfc-editor.org/rfc/rfc8705

---

*এই গাইড doc-only deliverable — কোনো কোড বা config পরিবর্তন করা হয়নি। Implementation সংক্রান্ত সত্যের উৎস এখন repo-গুলোর নিজেদের ডক: manager-এর `mtls-guide.md`/`mtls-dev-test-guide.md` আর gateway-এর `mtls-implementation-plan.md` — সেখানে ভিন্ন কিছু দেখলে ওরাই ঠিক।*
