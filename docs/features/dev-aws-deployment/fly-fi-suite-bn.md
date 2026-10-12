# FI Suite: একটি Fly অ্যাপে Gateway + IdP (পোর্ট-ভিত্তিক) — মূল্যায়ন ও প্রস্তাব

> **অবস্থা:** খসড়া · **তারিখ:** ২০২৬-১০-১১ · সঙ্গী ডকুমেন্ট: [`deployment-plan-bn.md`](deployment-plan-bn.md) (AWS), [`deployment-plan-azure-bn.md`](deployment-plan-azure-bn.md)
> **প্রস্তাব:** প্রতি FI-র জন্য আলাদা দুটি Fly অ্যাপের (Gateway, IdP) বদলে **একটি অ্যাপ = একটি FI "Suite"**; Gateway `:443`-এ, IdP `:8443`-এ।

## ১. বর্তমান অবস্থা

আজ প্রতি FI-তে **দুটি আলাদা Fly অ্যাপ**: `rvl-sbqr-fi-gateway-dhakabank-dev` (.NET 8, `fly.dhakabank-dev.toml`) ও `idp-dhakabank-mock` (Node 22, `fly.dhakabank.toml`)। অর্থাৎ ২টি toml, ২ সেট সিক্রেট, ২টি ডিপ্লয়, ২টি URL, এবং Gateway-র `Auth__Authority` ম্যানুয়ালি IdP-র URL-এর সাথে মেলাতে হয়।

## ২. যাচাইকৃত তথ্য (Fly ডক ও কোড থেকে)

| বিষয় | ফল | সূত্র |
|---|---|---|
| একটি অ্যাপে একাধিক পোর্টে সেবা (যেমন ৪৪৩ ও ৮৪৪৩) | ✅ সম্ভব। Shared IPv4-এও `tls` হ্যান্ডলারসহ ৪৪৩ ছাড়া অন্য TCP পোর্ট চলে — **অতিরিক্ত $2 Dedicated IP লাগে না** | Fly Networking/Services ডক |
| Process group (`[processes]`) ও প্রতি গ্রুপের আলাদা `[[services]]` | ✅ Fly-র মূল ফিচার; প্রতি গ্রুপ আলাদা Machine, আলাদা autostop | Fly কনফিগ রেফারেন্স |
| এক Machine-এ একাধিক Container (localhost শেয়ার) | ⚠️ আছে, তবে `[experimental]`; সিক্রেট সব Container-এর জন্য এক; Pilot init দরকার; autostop আচরণ অস্পষ্ট | Fly Multi-container ডক |
| Fly-র "free tier" | ❌ **নেই।** নতুন org পায় শুধু ট্রায়াল (২ ঘণ্টা/৭ দিন)। গেটওয়ে রিপোর `deploy-fly-io.md`-র "Free Hobby tier" বর্ণনা পুরনো | Fly Pricing |
| Gateway-র HTTPS-মেটাডেটা বাধ্যবাধকতা | `ConfigureJwtBearerOptions.cs:38` → `RequireHttpsMetadata = !IsDevelopment()`। toml-এ দেওয়া `Auth__RequireHttpsMetadata` **কোডে পড়া হয় না** | কোড |
| IdP-র Issuer | `ISSUER` env, না থাকলে `https://$FLY_APP_NAME.fly.dev` (পোর্ট ছাড়া) | `fi-idp-mock/src/server.js:53` |

**কোড-সংক্রান্ত ফল:** Gateway Production মোডে চলে, তাই IdP-কে `http://localhost:…`-এ ধরতে পারবে না — **পাবলিক HTTPS URL-ই ব্যবহার করতে হবে** (`https://<app>.fly.dev:8443`)। একই অ্যাপের ভেতরেও এটি Fly-র এজ ঘুরে আসবে; কাজ করবে, শুধু একটু ল্যাটেন্সি। ("localhost শর্টকাট" চাইলে Gateway কোডে পরিবর্তন লাগবে — সুপারিশ করছি না।)

## ৩. প্রস্তাবিত নকশা: Process Group (এক ইমেজ, এক অ্যাপ, দুই Machine)

```
fly app: rvl-sbqr-fi-dhakabank-dev          (একটিই toml, একটিই ডিপ্লয়)
 ├─ process "gateway" → dotnet /app/gateway/SBQR.FiGateway.Api.dll   ← https://<app>.fly.dev        (:443 → 8080)
 └─ process "idp"     → node   /app/idp/server.js                    ← https://<app>.fly.dev:8443   (:8443 → 5105)
```

```toml
# fly.suite.dhakabank.toml  (খসড়া)
app = "rvl-sbqr-fi-dhakabank-dev"
primary_region = "sin"

[build]
  dockerfile = "Dockerfile.suite"

[env]
  ASPNETCORE_ENVIRONMENT = "Production"
  ASPNETCORE_URLS = "http://+:8080"
  PORT = "5105"                       # IdP
  FI_ID = "dhakabank"
  FI_NAME = "Dhaka Bank"
  AUDIENCE = "sbqr-fi-gateway"
  # পোর্টসহ স্পষ্ট issuer — FLY_APP_NAME-ফলব্যাকে পোর্ট থাকে না, তাই বাধ্যতামূলক
  ISSUER = "https://rvl-sbqr-fi-dhakabank-dev.fly.dev:8443"
  Auth__Authority = "https://rvl-sbqr-fi-dhakabank-dev.fly.dev:8443"
  Auth__Issuer    = "https://rvl-sbqr-fi-dhakabank-dev.fly.dev:8443"
  Auth__Audience  = "sbqr-fi-gateway"

[processes]
  gateway = "dotnet /app/gateway/SBQR.FiGateway.Api.dll"
  idp     = "node /app/idp/server.js"

[[services]]                          # Gateway
  processes = ["gateway"]
  internal_port = 8080
  protocol = "tcp"
  auto_stop_machines = "stop"
  auto_start_machines = true
  min_machines_running = 0
  [[services.ports]]
    port = 80
    handlers = ["http"]
    force_https = true
  [[services.ports]]
    port = 443
    handlers = ["tls", "http"]
  [[services.http_checks]]
    path = "/health/live"
    interval = "10s"
    timeout = "2s"
    grace_period = "15s"

[[services]]                          # IdP
  processes = ["idp"]
  internal_port = 5105
  protocol = "tcp"
  auto_stop_machines = "stop"
  auto_start_machines = true
  min_machines_running = 0
  [[services.ports]]
    port = 8443
    handlers = ["tls", "http"]
  [[services.http_checks]]
    path = "/health"
    interval = "30s"
    timeout = "2s"
    grace_period = "10s"

[[vm]]
  processes = ["gateway"]
  size = "shared-cpu-1x"
  memory = "512mb"

[[vm]]
  processes = ["idp"]
  size = "shared-cpu-1x"
  memory = "256mb"
```

**কেন Process Group (আর Multi-container বা এক-মেশিন-সুপারভাইজার নয়):**
- প্রতিটি প্রসেস আলাদা Machine → একটির ক্র্যাশ/মেমরি-সমস্যা অন্যটিকে ফেলে না; `DOTNET_gcServer=1` (বর্তমান Dockerfile) আর Node একই RAM-এ ঠাসাঠাসি হয় না।
- প্রতিটির **নিজস্ব scale-to-zero**; Fly-র সাধারণ, স্থিতিশীল ফিচার (Multi-container এখনো `[experimental]`)।
- সুপারভাইজার স্ক্রিপ্ট (`wait -n`) লিখতে/রক্ষণাবেক্ষণ করতে হয় না।

**মূল্য/মান:** (Fly-র প্রকাশিত দাম: shared-cpu-1x ২৫৬ MB $2.19, ৫১২ MB $3.69 /মাস; Stopped Machine-এর rootfs $0.15/GB/মাস)

| অবস্থা | প্রতি FI মাসিক |
|---|---|
| সব সময় চালু (২৪×৭) | ≈ $5.9 |
| scale-to-zero, মাসে ~৪০ ঘণ্টা সক্রিয় | ≈ $0.5–1 |
| আগের দুই-অ্যাপ মডেল | **একই** খরচ |

**সৎ মূল্যায়ন:** *খরচে* সাশ্রয় নেই; সাশ্রয় হলো **হ্যান্ডলিং-এ**: ১টি অ্যাপ/toml/ডিপ্লয়/সিক্রেট-সেট/রিলিজ-ইউনিট প্রতি FI, Issuer ও Authority এক জায়গায়, "নতুন FI" = toml কপি + `FI_ID` বদল।

## ৪. ঝুঁকি ও সীমাবদ্ধতা

| # | বিষয় | প্রভাব | প্রশমন |
|---|---|---|---|
| 1 | **একটি Fly অ্যাপ = একটি ইমেজ।** এখন Gateway ও IdP দুই রিপোতে (`rvl-secure-bqr-api-gateway`, `rvl-sbqr-mocks`) আলাদা Dockerfile | কম্বাইন্ড `Dockerfile.suite` লাগবে: `aspnet:8.0` বেস + `node` বাইনারি (`COPY --from=node:22-slim`) + দুই অ্যাপের আউটপুট | `rvl-sbqr-mocks/services/fi-suite/`-এ রাখা; Gateway-কে CI-তে প্রকাশিত ইমেজ (যেমন ghcr.io) থেকে `COPY --from=` করা, যাতে রিপো মেশামেশি না হয় |
| 2 | **সিক্রেট অ্যাপ-ব্যাপী** — IdP প্রসেস Gateway-র `Platform__ClientSecret` পরিবেশে দেখতে পাবে | Mock IdP কম-বিশ্বস্ত কোড | শুধু Dev/টেস্ট ক্রেডেনশিয়াল; কখনো Prod/আসল FI সিক্রেট নয়; IdP কোড ছোট ও আমাদের নিজের |
| 3 | **মক কোড Gateway ইমেজের ভেতরে** | ভুলে Prod-এ গেলে ঝুঁকি | Suite ইমেজ আলাদা নাম/ট্যাগ (`-devsuite`), CI-তে Prod পাইপলাইন থেকে নিষিদ্ধ; আসল Gateway ইমেজ অপরিবর্তিত থাকে |
| 4 | **`:8443` পোর্ট** | কিছু কর্পোরেট ফায়ারওয়াল/মোবাইল নেটওয়ার্ক ব্লক করতে পারে; Issuer URL-এ পোর্ট থাকে (বাহ্যিক টুলে বিভ্রান্তিকর) | Dev-এর জন্য গ্রহণযোগ্য; আগে Emulator ও টেস্টার নেটওয়ার্কে যাচাই। Fly-র প্রক্সি হোস্টনেম দেখে আলাদা পোর্টে রাউট করে না — তাই হোস্ট-ভিত্তিক বিকল্প চাইলে Caddy লাগবে (AWS শেয়ার্ড-VM নকশা) |
| 5 | **Cold-start চেইন:** Gateway জাগে → IdP-র JWKS আনে → IdP জাগে | প্রথম অনুরোধ কয়েক সেকেন্ড ধীর (Gateway-তে `BootJitterSecondsMax=30` পর্যন্ত বিলম্ব যোগ হতে পারে) | UAT/ডেমোর আগে `min_machines_running=1` (Gateway), Dev-এ `BootJitter` ছোট (২–৫) |
| 6 | **Fly বিলিং:** free tier নেই | ডকে "free" লেখা বিভ্রান্তিকর | `deploy-fly-io.md` ঠিক করা; Fly org-এ কার্ড/বাজেট সেট করা |
| 7 | **নেটওয়ার্ক:** Fly AWS/Azure-এর বাইরে | Gateway→API সবসময় ইন্টারনেট-পথে (আগেও তাই; FI-র বাস্তব পথও তাই) | সমস্যা নয়; উল্টো Prod-এর পথের সাথে মিলে |
| 8 | **Image ডিপ্লয়-সাইজ:** একত্রিত ইমেজ ~২৫০–৩০০ MB | Stopped Machine-এর rootfs ≈ $0.04–0.05/মাস | নগণ্য |

## ৫. AWS/Azure প্ল্যানের সাথে তুলনা (Gateway + IdP অংশ, প্রতি ৫ FI)

| বিকল্প | মাসিক | হ্যান্ডলিং | মন্তব্য |
|---|---|---|---|
| **Fly Suite (এই প্রস্তাব), scale-to-zero** | **≈ $3–6** | সহজতম (১ toml/FI) | ইতিমধ্যে ব্যবহৃত টুলিং; AWS/Azure-এর বাইরে |
| Azure Container Apps | ≈ $0–10 | ভালো (Terraform) | Azure-এ বাকিটা থাকলে স্বাভাবিক |
| AWS শেয়ার্ড EC2 + Compose + Caddy | ≈ $22 ফ্ল্যাট | মাঝারি (VM প্যাচ) | AWS-এর ভেতরেই সব থাকে |

**পরামর্শ:** মূল ব্যবসায়িক কম্পোনেন্ট (API, DB, Vault, Portal) AWS-এ গেলেও Gateway+IdP **Fly Suite-এ রাখা বেশ যুক্তিযুক্ত** — এগুলো Prod-এ যাবে না, কম গুরুত্বপূর্ণ, এবং Fly-তেই সবচেয়ে সস্তা ও সহজ। AWS প্ল্যানের §৪.৩ ও §৪.৪ তখন "বিকল্প/ফলব্যাক" হবে।

## ৬. বাস্তবায়ন ধাপ

1. `Dockerfile.suite` লেখা (বেস `aspnet:8.0`, node কপি, দুই অ্যাপ `/app/gateway` ও `/app/idp`-এ) — non-root, দুই হেলথ-চেক বাদ দিয়ে (Fly `http_checks` ব্যবহার)।
2. `fly.suite.dhakabank.toml` (উপরের খসড়া) + `fly apps create` + `fly secrets set` (Gateway ও IdP সিক্রেট একই কমান্ডে; `SIGNING_KEY_PEM` রাখা যাতে রিস্টার্টে কী না বদলায়)।
3. `fly deploy -c fly.suite.dhakabank.toml`; যাচাই: `/.well-known/openid-configuration`-এ `issuer` = `…:8443`; Emulator দিয়ে লগইন → Gateway `/health/ready` 200 → QR প্রবাহ।
4. দ্বিতীয় FI (`ebl`) toml কপি করে প্রমাণ: ≤১০ মিনিট।
5. পুরনো দুই-অ্যাপ মুছে ফেলা (নতুন প্রবাহ প্রমাণের পর); `deploy-fly-io.md` ও `Platform__BaseUrl` মান হালনাগাদ।

## ৭. সিদ্ধান্ত যা দরকার
1. Process Group মডেল (প্রস্তাবিত) কি গ্রহণযোগ্য, নাকি এক-Machine চান (তাহলে সুপারভাইজার লাগবে)?
2. `:8443` টেস্টার/Emulator নেটওয়ার্কে খোলা আছে কি?
3. Suite Dockerfile কোন রিপোতে থাকবে (প্রস্তাব: `rvl-sbqr-mocks/services/fi-suite/`)?
4. Gateway ইমেজ আগে ghcr.io-তে প্রকাশ করার CI যোগ করা যাবে কি?
