# SBQR Development (Pre-UAT) পরিবেশ — Microsoft Azure রিসোর্স ডিপ্লয়মেন্ট প্ল্যান

> **অবস্থা:** খসড়া (রিভিউয়ের জন্য) · **তারিখ:** ২০২৬-১০-১১ · **অঞ্চল (Region):** `southeastasia` (সিঙ্গাপুর)
> **সঙ্গী ডকুমেন্ট:** [`deployment-plan-bn.md`](deployment-plan-bn.md) (AWS সংস্করণ) · **রেফারেন্স:** `rvl-secure-bqr-manager/docs/deployments.md`
> **পাঠক:** যাঁদের ক্লাউড/DevOps সম্পর্কে আগে থেকে ধারণা নেই। AWS প্ল্যানের সাথে মিল রেখে লেখা, যাতে দুটো পাশাপাশি পড়ে তুলনা করা যায়।

> ⚠️ **খরচের সংখ্যা আনুমানিক** (Pay-as-you-go, Southeast Asia, জানা দামের ভিত্তিতে)। চূড়ান্ত করার আগে [Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/) দিয়ে মিলিয়ে নিতে হবে।

---

## ০. 🚨 সবচেয়ে গুরুত্বপূর্ণ আবিষ্কার: Vault-এর কোড S3-নির্ভর

আপনার চাহিদায় "S3 bucket for the certificate vault" ছিল। কোড দেখে নিশ্চিত হয়েছি: `SBQR.SharedKernel.Storage/S3/S3ObjectStorage.cs` সরাসরি **AWS-এর `AmazonS3Client`** ব্যবহার করে। Azure-এ S3 নেই; Azure Blob Storage-এর API আলাদা।

| উপায় | কীভাবে | সুবিধা | অসুবিধা | সুপারিশ |
|---|---|---|---|---|
| **১. Azure Blob অ্যাডাপ্টার লেখা** | `IObjectStorage` ইন্টারফেসের নতুন বাস্তবায়ন `AzureBlobObjectStorage` (Managed Identity দিয়ে, কোনো চাবি ছাড়া) | Azure-এর ভেতরেই সব; স্থায়ী চাবি নেই; ক্রস-ক্লাউড খরচ/ল্যাটেন্সি নেই | কোড পরিবর্তন (আনুমানিক ১–২ দিন + টেস্ট + VAPT পুনর্যাচাই) | ✅ **দীর্ঘমেয়াদী সঠিক পথ** |
| **২. AWS S3 ই রেখে দেওয়া (ক্রস-ক্লাউড)** | Azure API সার্ভার থেকে AWS-এর `rvl-sbqr-key-vault-dev` Bucket-এ যাওয়া | কোড বদল নেই, আজই চলে | দুই ক্লাউডের বিল ও অ্যাকাউন্ট; **AWS Access Key** Azure Key Vault-এ রাখতে হবে (Instance Role-এর মতো চাবিহীন নিরাপত্তা নেই — চাইলে OIDC ফেডারেশন সম্ভব কিন্তু জটিল); Vault ট্রাফিক ইন্টারনেট পেরোয় | ⚠️ **শুধু অন্তর্বর্তী**, Dev-এ দ্রুত শুরুর জন্য |
| ৩. S3-সামঞ্জস্যপূর্ণ প্রক্সি (MinIO ইত্যাদি) Blob-এর সামনে | অতিরিক্ত সার্ভিস চালানো | কোড বদল নেই | নতুন নিরাপত্তা-সীমানা ও রক্ষণাবেক্ষণ, স্পর্শকাতর Vault-এর সামনে অপ্রয়োজনীয় স্তর | ❌ সুপারিশ নয় |

**সিদ্ধান্ত লাগবে:** ১ নম্বর (সুপারিশকৃত) নাকি শুরুতে ২ নম্বর দিয়ে পরে ১-এ যাওয়া। এই প্ল্যানের বাকি অংশ ধরে নিচ্ছে **Vault = Azure Blob Storage** (১ নম্বর); ২ নম্বর বেছে নিলে §৪.৬-এর শেষ অংশ দেখুন।

---

## ১. এক নজরে সারসংক্ষেপ

| # | রিসোর্স | Dev-এ কী চালাব (Azure) | আনুমানিক মাসিক খরচ | Production-এ? |
|---|---|---|---|---|
| 1 | PostgreSQL | **Azure Database for PostgreSQL – Flexible Server**, Burstable `B2s`, ৩২ GiB, Private | ~$36 | ✅ (Zone-redundant HA) |
| 2 | SBQR.API | **Linux VM** `Standard_B2s` (২ vCPU/৪ GB) + Caddy | ~$45 | ✅ (বহু ইনস্ট্যান্স + Gateway/Front Door) |
| 3 | FI Gateway (বহু FI) | **Azure Container Apps (Consumption)** — প্রতি FI-র জন্য একটি অ্যাপ, **অলস থাকলে শূন্যে নেমে যায়** | ~$0–10 (সব FI মিলিয়ে) | ❌ |
| 4 | FI IdP (Mock) | একই Container Apps পরিবেশে, প্রতি FI-র জন্য একটি অ্যাপ | ৩ নম্বরের খরচের মধ্যেই | ❌ |
| 5 | SBQR Portal | স্ট্যাটিক ফাইল — Dev-এ API VM-এর Caddy থেকে (একই origin); বিকল্প: Static Web Apps | ~$0 | ✅ (Front Door + Storage) |
| 6 | Certificate Vault | **Blob Storage** (প্রাইভেট, Versioning, এনক্রিপ্টেড) | <$1 | ✅ (ZRS/GZRS + immutability) |
| | আনুষঙ্গিক (Key Vault, DNS, Monitor) | | ~$3–5 | ✅ |
| | **মোট (২৪×৭ চালু)** | | **≈ $90–100 / মাস** | |
| | **মোট (অফ-আওয়ারে VM+DB বন্ধ)** | | **≈ $60–70 / মাস** | |

**Azure-এ কৌশলগত পার্থক্য (AWS-এর তুলনায়):**
- **Container Apps-এর "শূন্যে নামা" (scale-to-zero)** — Gateway/IdP-র মতো বেশির ভাগ সময় অলস সার্ভিসের জন্য অসাধারণ মানানসই। মাসে ফ্রি অনুদান: ১.৮ লক্ষ vCPU-সেকেন্ড, ৩.৬ লক্ষ GiB-সেকেন্ড ও ২০ লাখ অনুরোধ। ফলে ৫–১০টি FI-ও প্রায় বিনামূল্যে।
- **Azure Front Door ও Application Gateway (WAF) দামি** (Front Door Standard-এর ভিত্তি ফি ~$35/মাস) — তাই Dev-এ এড়াচ্ছি; Prod-এ বিবেচনা।
- **PostgreSQL Flexible Server-এ PgBouncer বিল্ট-ইন** — Prod-এ AWS-এর RDS Proxy-র আলাদা খরচ লাগে না।
- **Azure Bastion দামি** — তাই SSH-এর বিকল্প ভাবতে হয় (§৪.২)।

---

## ২. শব্দের তালিকা (AWS ↔ Azure)

| AWS-এ যা | Azure-এ তার সমতুল্য | সহজ ব্যাখ্যা |
|---|---|---|
| Account | **Subscription** (+ Resource Group) | বিলিং ও অনুমতির সীমানা; Resource Group = একই প্রকল্পের রিসোর্সের "ফোল্ডার" |
| EC2 | **Virtual Machine (VM)** | ভাড়া করা ভার্চুয়াল সার্ভার |
| RDS PostgreSQL | **PostgreSQL Flexible Server** | ম্যানেজড ডাটাবেস |
| S3 | **Blob Storage** (Storage Account-এর ভেতরে) | ফাইল/অবজেক্ট স্টোরেজ |
| VPC | **Virtual Network (VNet)** | নিজস্ব ব্যক্তিগত নেটওয়ার্ক |
| Security Group | **Network Security Group (NSG)** | ফায়ারওয়াল নিয়ম |
| IAM Role (Instance Role) | **Managed Identity** | পাসওয়ার্ড ছাড়াই "এই সার্ভিস শুধু অমুক রিসোর্স ছুঁতে পারবে" |
| SSM Parameter Store | **Azure Key Vault** | সিক্রেট রাখার নিরাপদ সিন্দুক |
| Fargate / ECS | **Azure Container Apps** | সার্ভার না সামলে Container চালানো |
| CloudFront | **Front Door / Static Web Apps** | CDN ও স্ট্যাটিক হোস্টিং |
| Route 53 | **Azure DNS** | ডোমেইন রেকর্ড |
| CloudWatch | **Azure Monitor + Log Analytics** | মনিটরিং, লগ, অ্যালার্ম |
| Budgets | **Cost Management → Budgets** | খরচের সীমা ও সতর্কতা |
| Terraform | Terraform (`azurerm`) বা Bicep | কোড দিয়ে অবকাঠামো |

---

## ৩. সিস্টেমটা কীভাবে কাজ করে (Dev-এ)

```
 ব্রাউজার (Portal)      FI মোবাইল অ্যাপ/Emulator          টেস্টার
      │                         │ (১) লগইন
      │                         ▼
      │            ┌─ Azure Container Apps পরিবেশ (শেয়ার্ড, পাবলিক ইনগ্রেস) ─┐
      │            │  idp-<fi>   (FI IdP Mock)  ──JWT──►  gw-<fi> (Gateway)   │
      │            │  (প্রতিটি অ্যাপ অলস হলে 0 রেপ্লিকা)                      │
      │            └──────────────────────────────┬────────────────────────────┘
      │                                           │ (২) টোকেন + QR অনুরোধ
      ▼                                           ▼
   portal.dev.sbqr… ───────────────►  api.dev.sbqr…  (SBQR.API VM + Caddy)
   (স্ট্যাটিক ফাইল + /v1 প্রক্সি,                │            │
    একই VM-এর Caddy)                    VNet (Private subnet)   Managed Identity
                                                 ▼            ▼
                                  PostgreSQL Flexible Server   Blob Storage (Vault)
                                  (পাবলিক অ্যাক্সেস বন্ধ)       (প্রাইভেট)
```

---

## ৪. রিসোর্স-ভিত্তিক বিস্তারিত পরিকল্পনা

### ৪.০ ভিত্তি: Subscription, নেটওয়ার্ক, নাম

- **Subscription:** Dev ও Prod **আলাদা Subscription** (Management Group-এর নিচে)। Dev-এ ভুল হলে Prod প্রভাবিত হয় না; বাজেটও আলাদা। Visual Studio/Dev-Test সাবস্ক্রিপশন থাকলে Dev/Test রেট ও মাসিক ক্রেডিট কাজে লাগবে।
- **Resource Group:** `rg-rvl-sbqr-dev` (মূল), `rg-rvl-sbqr-testsupport-dev` (Gateway/IdP — **আলাদা**, যাতে এক ক্লিকে মুছে ফেলা যায় ও খরচ আলাদা দেখা যায়)।
- **নাম:** `rvl-sbqr-<রিসোর্স>-dev`; স্টোরেজ নামে হাইফেন চলে না → `rvlsbqrvaultdev`।
- **VNet** `rvl-sbqr-vnet-dev` (`10.10.0.0/16`):
  - `snet-app` (`10.10.1.0/24`) — API VM
  - `snet-db` (`10.10.2.0/24`) — PostgreSQL এর জন্য **delegated** subnet (`Microsoft.DBforPostgreSQL/flexibleServers`), ইন্টারনেট থেকে অদৃশ্য
  - ❌ **NAT Gateway বানাব না** (~$32+/মাস); VM-এ Public IP আছে বলে বের হওয়ার পথ ঠিক আছে।
  - ✅ Storage ও Key Vault-এর জন্য **Service Endpoint** (বিনামূল্যে) — ট্রাফিক Microsoft-এর ব্যাকবোনেই থাকে। (Private Endpoint প্রতিটি ~$7–8/মাস — Prod-এ।)
- **Tag:** `Project=SBQR`, `Env=dev`, `Tier=core|test-support`, `Owner`, `CostCenter`। Azure Policy দিয়ে ট্যাগ বাধ্যতামূলক করা যায়।

### ৪.১ PostgreSQL ডাটাবেস

| বিষয় | সিদ্ধান্ত | কারণ |
|---|---|---|
| সেবা | PostgreSQL Flexible Server, ভার্সন ১৬ | `deployments.md`-এর RDS PG16-এর সমতুল্য |
| সাইজ | Burstable `Standard_B2s` (২ vCPU/৪ GB) | `db.t4g.small` (২ vCPU/২ GB)-এর কাছাকাছি, Dev-এ যথেষ্ট; খরচ কমাতে `B1ms` পরীক্ষা করা যাবে |
| HA | বন্ধ (Dev) | খরচ অর্ধেক; Dev-এ ডাউনটাইম সহনীয় |
| স্টোরেজ | ৩২ GiB (Azure-এর ন্যূনতম), Storage auto-grow **বন্ধ** | অপ্রত্যাশিত খরচ ঠেকাতে |
| নেটওয়ার্ক | VNet Integration (Private access), Public access বন্ধ | ইন্টারনেট থেকে ঢোকা অসম্ভব |
| ফায়ারওয়াল | NSG: `snet-db` শুধু `snet-app` থেকে ৫৪৩২ | টেস্ট কম্পোনেন্ট ডাটাবেস ছুঁতে পারে না |
| এনক্রিপশন | At-rest (Microsoft-managed key, বিনামূল্যে); `require_secure_transport=ON` (ডিফল্ট), TLS ১.২+ | |
| অথেন্টিকেশন | Microsoft Entra authentication বা শক্তিশালী পাসওয়ার্ড Key Vault-এ | কোডে/Git-এ পাসওয়ার্ড নয় |
| ব্যাকআপ | Automated, ৭ দিন (স্টোরেজ সাইজ পর্যন্ত বিনামূল্যে), Point-in-time restore | |
| মাইগ্রেশনের আগে | ম্যানুয়াল ব্যাকআপ/PITR মার্ক নোট করে রাখা | |
| মনিটরিং | Azure Monitor অ্যালার্ম: CPU, storage %, active connections, **burst credit শেষ হওয়া** | Burstable সিরিজে ক্রেডিট ফুরোলে ধীর হয়ে যায় — এটি আলাদা নজরদারির বিষয় |

> **খরচ-টিপ:** Flexible Server **Stop** করা যায় (সর্বোচ্চ ৭ দিন, তারপর Azure নিজে চালু করে দেয়)। Stop থাকলে কম্পিউট বিল বন্ধ, স্টোরেজ বিল চলে। Azure Automation/Logic App বা CLI স্ক্রিপ্ট দিয়ে শিডিউল।

### ৪.২ SBQR.API

| বিষয় | সিদ্ধান্ত |
|---|---|
| কম্পিউট | Linux VM `rvl-sbqr-api-dev`, `Standard_B2s` (বা `B2as_v2`), Ubuntu 24.04 LTS — বিদ্যমান `deploy-sbqr` স্ক্রিপ্ট ও systemd মডেল অপরিবর্তিত থাকে |
| IP/ডোমেইন | Standard SKU static Public IP + `api.dev.sbqr.<company-domain>` (Azure DNS) |
| HTTPS | VM-এ **Caddy** (Let's Encrypt অটো) — Load Balancer ছাড়া $0 |
| ডিস্ক | ৩২–৬৪ GB Standard SSD / Premium SSD (এনক্রিপ্টেড by default) |
| সার্ভারে প্রবেশ | নিচে দেখুন |
| Vault অ্যাক্সেস | **System-assigned Managed Identity** + `Storage Blob Data Contributor` (শুধু Vault container-এ) |
| সিক্রেট | Azure Key Vault (Standard); VM-এর Managed Identity-তে `Key Vault Secrets User`; ডিপ্লয়ের সময় `/etc/sbqr/sbqr.env` (`chmod 600`) তৈরি |
| পরিবেশ-মান | `ASPNETCORE_ENVIRONMENT=Development` |
| ফায়ারওয়াল (NSG) | ৮০/৪৪৩ ইন্টারনেট থেকে; ২২ নিচের নিয়মে |

**সার্ভারে প্রবেশ — AWS-এর SSM-এর মতো "বিনামূল্যে ও পোর্টহীন" সমাধান Azure-এ সরাসরি নেই:**

| বিকল্প | খরচ | মন্তব্য |
|---|---|---|
| Azure Bastion **Developer** SKU | বিনামূল্যে (সীমিত রিজিয়ন/সীমাবদ্ধতা; Southeast Asia-তে আছে কি না যাচাই করতে হবে) | ✅ পছন্দ |
| Bastion Basic/Standard | ~$140+/মাস | ❌ Dev-এ অতিরিক্ত |
| NSG-তে ২২ শুধু অফিস/VPN IP থেকে + **Entra ID login** এক্সটেনশন (`AADSSHLoginForLinux`) + কী-ভিত্তিক | $0 | ✅ গ্রহণযোগ্য ফলব্যাক; পাসওয়ার্ড-লগইন বন্ধ |
| Just-In-Time VM access | Defender for Servers (পেইড) লাগে | Prod-এ বিবেচ্য |
| `az vm run-command` | $0 | জরুরি এককালীন কমান্ডের জন্য |

### ৪.৩ SBQR Gateway — বহু FI, সবচেয়ে সস্তা উপায় (Azure)

**মূল ধারণা:** Gateway প্রতি FI-র জন্য আলাদা কনফিগারে চলে (আলাদা `Auth__Authority`, `Platform__ClientId/Secret`)। কিন্তু প্রতিটিই খুব হালকা ও বেশির ভাগ সময় অলস।

| বিকল্প (৫ FI, Gateway+IdP সহ, আনুমানিক) | মাসিক খরচ | মন্তব্য |
|---|---|---|
| A. প্রতি FI-তে আলাদা VM | ~$100+ | অপচয় |
| B. AKS ক্লাস্টার | ~$60–100+ (নোড) | Dev-এর জন্য অতিরিক্ত জটিল |
| **C. ✅ Azure Container Apps (Consumption), scale-to-zero** | **~$0–10** | **সুপারিশকৃত।** সার্ভার প্যাচ/OS নেই; অলস থাকলে খরচ ≈ ০ |
| D. একটি শেয়ার্ড VM + Docker Compose + Caddy (AWS প্ল্যানের মতো) | ~$25–30 ফ্ল্যাট | নিখুঁত বিকল্প; এখন ফলব্যাক। AWS ও Azure উভয়ে একই Compose ফাইল চলবে |
| E. Fly.io (বর্তমানে ব্যবহৃত) | ~$0–10 | Azure-এর বাইরে; ফলব্যাক |

**সুপারিশ: বিকল্প C।** কারণ:
1. **খরচ ব্যবহার-ভিত্তিক:** FI-তে কেউ টেস্ট না করলে `minReplicas=0` → বিল প্রায় শূন্য। FI যোগ করলে ফ্ল্যাট খরচ বাড়ে না।
2. **ইন-বিল্ট HTTPS ও কাস্টম ডোমেইন** (Managed Certificate বিনামূল্যে) — Caddy/Let's Encrypt সামলাতে হয় না।
3. **Container Apps শুধু x86 (AMD64)** — AWS প্ল্যানের ARM-ইমেজ-সংক্রান্ত খোলা প্রশ্নটি এখানে নেই; বিদ্যমান `Dockerfile` সরাসরি চলবে।
4. সব FI-র অ্যাপ **একটি Container Apps Environment**-এ — পরিবেশ নিজে বিনামূল্যে (শুধু Consumption হলে)।

**নকশা:**
- Container Apps Environment `rvl-sbqr-cae-dev` (Resource Group `rg-rvl-sbqr-testsupport-dev`), VNet-সংযোগ **ছাড়া** (Gateway/IdP পাবলিক API-ই ডাকে, ডাটাবেসে যায় না — ইচ্ছাকৃত বিচ্ছিন্নতা)।
- প্রতি FI-তে ২টি অ্যাপ: `gw-<fi>` ও `idp-<fi>`; রিসোর্স `0.25 vCPU / 0.5 GiB` (Gateway), `0.25 / 0.5` (IdP), `minReplicas=0`, `maxReplicas=1`।
- **কোল্ড-স্টার্ট নোট:** শূন্য থেকে উঠতে ~কয়েক সেকেন্ড লাগে এবং Gateway-র BootGuard/টোকেন প্রি-ওয়ার্ম ধাপ চলে। প্রথম অনুরোধ ধীর হতে পারে — টেস্টিং সেশনের আগে `/health/ready` একবার ডেকে "ওয়ার্ম" করা যায়, অথবা সেশনে `minReplicas=1` (সাময়িকভাবে) করা যায়। ডেমো/UAT-র জন্য Gateway-তে `minReplicas=1` (প্রায় $8–10/অ্যাপ/মাস) রাখা যায়।
- **সিক্রেট:** Container Apps-এর secret-reference দিয়ে Key Vault-এ (Managed Identity) — প্রতি FI-র জন্য আলাদা secret নাম (`gw-<fi>-platform-client-secret`)।
- **DNS:** `gw-<fi>.dev.sbqr.<domain>` ও `idp-<fi>.dev.sbqr.<domain>` → CNAME অ্যাপের FQDN-এ, সঙ্গে Managed Certificate। (প্রতি অ্যাপে একটি করে ডোমেইন যুক্ত করতে হয়; ওয়াইল্ডকার্ড একত্রে নেই — তাই Terraform মডিউলে `for_each` দিয়ে স্বয়ংক্রিয় করব। ডোমেইন যুক্ত করা ঝামেলা মনে হলে ডিফল্ট `*.azurecontainerapps.io` নাম দিয়েও Dev চালানো যায়।)
- **"নতুন FI যোগ":** Terraform ভেরিয়েবলের তালিকায় একটি এন্ট্রি (`fi_id`, `fi_name`) + Key Vault-এ সিক্রেট → `terraform apply`। লক্ষ্য ≤৩০ মিনিট।
- **Production-এ নেই:** পুরো Resource Group আলাদা Terraform মডিউল; Prod-এ ডিপ্লয়ই হবে না।

### ৪.৪ FI IdP (Mock)

- ৪.৩-এর Container Apps Environment-এই প্রতি FI-র জন্য একটি অ্যাপ (`FI_ID`, `FI_NAME`, `AUDIENCE=sbqr-fi-gateway`, `ISSUER=https://idp-<fi>…`)। Node.js, খুব হালকা।
- **নিরাপত্তা:** ইউজার/পাসওয়ার্ড প্রকাশ্য নকল ডেটা; কোনো আসল তথ্য নয়। এর Managed Identity-তে **কোনো রোল নেই** (Vault/Key Vault/ডাটাবেস অগম্য)। আলাদা Resource Group ও আলাদা পরিবেশ। চাইলে Container Apps-এর IP restriction দিয়ে টেস্টার IP-তে সীমাবদ্ধ — তবে Gateway-কেও JWKS আনতে হয় (Gateway আউটবাউন্ড IP স্থির নয়), তাই সীমাবদ্ধ করলে সাবধান।
- `X-Robots-Tag: noindex` রাখা; Prod-এ এর অস্তিত্ব নেই।

### ৪.৫ SBQR Portal

Portal একটি স্ট্যাটিক React/Vite অ্যাপ (`dist/`)। **শর্ত:** লগইনের Refresh Token `HttpOnly` কুকি (`sbqr_rt`, `Path=/v1/auth`); ব্রাউজার **একই origin** থেকে Portal ও `/v1` পেলে সবচেয়ে নিরাপদ ও কোড-পরিবর্তনহীন (বর্তমান `vercel.json` ঠিক এভাবেই কাজ করে)।

| বিকল্প | খরচ | একই origin? | মন্তব্য |
|---|---|---|---|
| **P1. ✅ Dev-এ API VM-এর Caddy থেকেই পরিবেশন** (`portal.dev.sbqr…` → ফাইল; `/v1/*` → লোকাল API) | $0 | ✅ | কোনো কোড বদল নেই; সহজ। VM-এ স্ট্যাটিক ফাইলের লোড নগণ্য। Prod-এর নকশা এটা নয় — Prod-এ P3 |
| P2. Static Web Apps **Free** | $0 | ❌ (আলাদা সাবডোমেইন) | API-তে CORS + কুকি `SameSite`/`Domain` পরিবর্তন লাগতে পারে — **কোড/নিরাপত্তা রিভিউ ছাড়া নয়** |
| P2'. Static Web Apps **Standard** | ~$9 | ✅ কিন্তু শুধু যদি API **App Service/Functions/Container Apps/APIM** হয় (সাধারণ VM "linked backend" হতে পারে না) | API VM-এ থাকলে প্রযোজ্য নয় |
| P3. **Front Door Standard/Premium** + Blob static website (অরিজিন) + `/v1/*` → API | ~$35+ | ✅ | **Prod-এর জন্য** (WAF, গ্লোবাল CDN, DDoS); Dev-এ খরচ বেশি |

**সুপারিশ:** Dev = **P1**; Prod = **P3**। নিরাপত্তা হেডার (`vercel.json`-এর CSP, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`) Caddyfile-এ হুবহু বসাব (Prod-এ Front Door Rules Engine-এ)। `/assets/*` → `Cache-Control: immutable` ১ বছর; `index.html` → no-cache; SPA fallback → `try_files {path} /index.html` (শুধু ফাইল-পাথের জন্য, `/v1/*`-এ নয়)। ডিপ্লয়: CI-তে `npm ci && npm run build` → `rsync/scp` `dist/` → Caddy রিলোড লাগে না।

### ৪.৬ Certificate Vault (Blob Storage)

QR সই করার গোপন Ed25519 কী ও সার্টিফিকেট এখানে। (কোডে এগুলো আবার KEK দিয়ে অ্যাপ-স্তরে এনক্রিপ্ট হয়।)

| নিয়ন্ত্রণ | Dev | Prod |
|---|---|---|
| Storage Account | `rvlsbqrvaultdev`, StorageV2, **LRS**, Hot, container `keycustody` | `…prod`, **ZRS বা GZRS** |
| Public access | Account-এ "Allow Blob anonymous access" = **Disabled** | একই |
| Shared Key | **বন্ধ** (`allowSharedKeyAccess=false`) — শুধু Entra/Managed Identity | একই |
| নেটওয়ার্ক | Default deny + `snet-app` Service Endpoint | Private Endpoint |
| এনক্রিপশন | Microsoft-managed key (বিনামূল্যে); TLS ১.২ ন্যূনতম, HTTPS-only | Customer-managed key (Key Vault) |
| Versioning/Soft delete | Blob versioning + soft delete ৩০ দিন; পুরনো ভার্সন Lifecycle-এ ৩০ দিন পর মুছবে | ৯০+ দিন + **immutability (WORM)** নীতি + ক্রস-রিজিয়ন |
| অ্যাক্সেস | API VM-এর Managed Identity → `Storage Blob Data Contributor` শুধু container স্কোপে | একই |
| অডিট | Diagnostic settings → Log Analytics (শুধু Write/Delete) | সব অপারেশন |

**যদি অন্তর্বর্তী উপায় (§০-এর ২ নম্বর) বেছে নেওয়া হয়:** AWS S3 Bucket + IAM User-এর Access Key/Secret Azure Key Vault-এ রেখে `sbqr.env`-এ `Storage__AccessKeyId/SecretAccessKey` হিসেবে নামানো। এটি `deployments.md`-এর মূল নকশা (IAM User) হুবহু, কিন্তু (ক) চাবি ঘোরানোর (rotation) দায়িত্ব নিতে হবে, (খ) Vault ট্রাফিক ক্রস-ইন্টারনেট, (গ) দুটি ক্লাউডের বিল। Blob অ্যাডাপ্টার প্রস্তুত হলে এই চাবি বাতিল।

### ৪.৭ মনিটরিং, লগ, খরচ-নিয়ন্ত্রণ

- **Cost Management → Budget:** মাসিক বাজেট (যেমন $110), ৫০/৮০/১০০% ইমেইল সতর্কতা — বিনামূল্যে।
- **Log Analytics:** প্রতি মাসে ৫ GB ইনজেশন ফ্রি; Retention ৩০ দিন (প্রথম ৩১ দিন ফ্রি), Basic Logs/সংক্ষিপ্ত Table ব্যবহার করে খরচ কম রাখা। VM-এর জন্য Azure Monitor Agent, Container Apps লগ স্বয়ংক্রিয়।
- **অ্যালার্ম:** API `/health` ব্যর্থ (Availability test বা সহজ cron), VM CPU/মেমরি, PostgreSQL CPU>80%/Storage<২ GB/Burst credit, Container App রিস্টার্ট ক্রমাগত।
- **Defender for Cloud:** বিনামূল্যে CSPM স্তর চালু (সুপারিশ ও Secure Score)। পেইড Defender প্ল্যান Prod-এ।
- **অফ-আওয়ার শিডিউল (ঐচ্ছিক):** VM **Deallocate** (Stop নয়, শুধু OS-shutdown করলে বিল চলতেই থাকে!) + PostgreSQL Stop → রাত/ছুটিতে কম্পিউট খরচ ~৫০–৬০% কম (~$30–35 সাশ্রয়)। ডিস্ক ও Public IP-র বিল চলতে থাকবে।

---

## ৫. নিরাপত্তা ও স্থিতিশীলতা চেকলিস্ট (Dev-এও বাধ্যতামূলক)

| ক্ষেত্র | ব্যবস্থা | অতিরিক্ত খরচ |
|---|---|---|
| ডাটাবেস | Private access (VNet-delegated), NSG শুধু API subnet থেকে, TLS বাধ্যতামূলক | $0 |
| চাবিহীন অ্যাক্সেস | Managed Identity (Vault, Key Vault); Storage Shared Key বন্ধ | $0 |
| সিক্রেট | Key Vault (soft-delete + purge protection চালু); কোড/Git-এ কিছু নেই | ~$0 |
| সার্ভারে প্রবেশ | Bastion Developer বা IP-সীমিত SSH + Entra login; পাসওয়ার্ড-লগইন বন্ধ | $0 |
| ট্রান্সপোর্ট | সর্বত্র HTTPS (Let's Encrypt/Managed Certificate), HSTS | $0 |
| OS হার্ডেনিং | `unattended-upgrades`, ডিস্ক এনক্রিপ্টেড, Trusted Launch VM | $0 |
| আইসোলেশন | Gateway/IdP আলাদা Resource Group ও Container Apps Environment; কোনো রোল-অ্যাসাইনমেন্ট নেই; DB NSG-তে তারা নেই | $0 |
| অডিট | Activity Log (৯০ দিন বিনামূল্যে) + Diagnostic settings | ~$0 |
| ব্যাকআপ | PostgreSQL ৭ দিন PITR; Blob versioning/soft-delete; Terraform দিয়ে পুনর্নির্মাণ | $0 |
| রোলব্যাক | `deploy-sbqr`-এর অটো-রোলব্যাক; Container Apps-এ revision রোলব্যাক | $0 |
| Azure Policy | ট্যাগ বাধ্যতামূলক, পাবলিক Blob নিষেধ, অনুমোদিত রিজিয়ন-তালিকা | $0 |

**সজ্ঞানে মেনে নেওয়া SPOF (Dev):** একটি VM ও Single-zone PostgreSQL। Prod-এ গ্রহণযোগ্য নয় (পরের অংশ)।

---

## ৬. Production-এ উচ্চ ট্রানজ্যাকশনের জন্য যাত্রা (Azure)

| উপাদান | Dev (এখন) | Prod (লক্ষ্য) |
|---|---|---|
| API | ১টি VM | ≥২ ইনস্ট্যান্স, ৩ Availability Zone-এ। বিকল্প: **(ক) VM Scale Set + Application Gateway v2 (WAF)**, **(খ) Azure Container Apps (dedicated workload profile)**, **(গ) AKS** (রিপোজিটরিতে `deploy/k8s` Kustomize prod overlay আগে থেকেই আছে; AKS কন্ট্রোল প্লেন Free tier-এ বিনামূল্যে, শুধু নোডের খরচ)। সুপারিশ: স্কেলিং-কর্মক্ষমতা ও K8s-ম্যানিফেস্ট পুনঃব্যবহারের কারণে **AKS বা Container Apps** — দলের অপারেশনাল দক্ষতা অনুযায়ী চূড়ান্ত |
| ডাটাবেস | Burstable B2s | **General Purpose / Memory Optimized**, **Zone-redundant HA**, ৩৫ দিন ব্যাকআপ, **বিল্ট-ইন PgBouncer** (পোর্ট ৬৪৩২, অতিরিক্ত খরচ নেই), Read Replica, Query Performance Insight, Storage auto-grow চালু, Resource Lock |
| এজ/নিরাপত্তা | Caddy | **Front Door Premium + WAF** (রেট-লিমিট, Bot, ম্যানেজড রুল) বা Application Gateway WAF_v2; DDoS Network Protection কেবল প্রয়োজনে (দামি) |
| নেটওয়ার্ক | Public subnet VM | কম্পিউট Private subnet-এ; Private Endpoint (DB/Storage/Key Vault); আউটবাউন্ড ফায়ারওয়াল/NAT Gateway প্রয়োজন অনুযায়ী |
| Vault | LRS, Microsoft-managed key | ZRS/GZRS, CMK, immutability (WORM), Private Endpoint, ক্রস-রিজিয়ন কপি |
| সিক্রেট | Key Vault Standard | Key Vault Premium (HSM-সমর্থিত) বা Managed HSM (যেখানে নিয়ন্ত্রক-প্রয়োজন) |
| মনিটরিং | মৌলিক | Application Insights/OpenTelemetry (Gateway-র OTLP আগে থেকেই আছে), p95/p99 ড্যাশবোর্ড, On-call |
| খরচ | Pay-as-you-go | বেসলাইনের জন্য **১/৩ বছরের Reservation বা Savings Plan (~২৫–৫০%)**; Spot শুধু অ-গুরুত্বপূর্ণ কাজে |
| Gateway / IdP | Container Apps | **নেই** |

**কর্মক্ষমতা অথচ খরচ-সাশ্রয়ী করার উপায়:**
1. **আগে মাপো:** Pre-UAT-তে লোড-টেস্ট (k6) কয়েক ঘণ্টা **অস্থায়ীভাবে বড় SKU**-তে (যেমন PostgreSQL `D4ds_v5`) চালিয়ে সীমা বের করা, তারপর ফেরত — ঘণ্টা-হিসাবে বিল।
2. **Burstable সিরিজ Prod-এ নয়:** ক্রেডিট শেষ হলে থ্রটল হয় — স্থির উচ্চ লোডে General Purpose নিন।
3. **কানেকশন পুলিং (PgBouncer) ও ইনডেক্স** আগে; হার্ডওয়্যার পরে।
4. **ডেটা-রেসিডেন্সি/নিয়ন্ত্রক সীমা যাচাই** (প্রশ্ন #৬) — রিজিয়ন নির্বাচন খরচ ও ল্যাটেন্সি দুটোকেই প্রভাবিত করে।

---

## ৭. খরচ-হিসাবের ভাঙন (Dev, ২৪×৭, আনুমানিক)

| আইটেম | ~USD/মাস |
|---|---|
| PostgreSQL B2s (কম্পিউট) + ৩২ GiB স্টোরেজ | 36 |
| API VM `B2s` + ডিস্ক + Public IP | 45 |
| Container Apps (বহু FI, scale-to-zero; ফ্রি অনুদান সহ) | 0–10 |
| Blob Vault + Key Vault + DNS | 2 |
| Log Analytics/Monitor (ফ্রি সীমার ভেতরে বেশির ভাগ) | 1–3 |
| **মোট** | **≈ 90–100** |
| বিকল্প ৪.৩-D (শেয়ার্ড VM) ব্যবহার করলে | ≈ 115–120 |
| অফ-আওয়ার শিডিউল সহ | ≈ 60–70 |

(তুলনা: AWS প্ল্যান ≈ $105–110; পার্থক্যের মূল কারণ Gateway/IdP-র scale-to-zero। দুই ক্লাউডের মূল্য-পার্থক্য ছোট — সিদ্ধান্ত নেওয়ার আসল কারণ হওয়া উচিত Vault-এর কোড-সামঞ্জস্য, দলের দক্ষতা, কমপ্লায়েন্স ও বিদ্যমান চুক্তি/ক্রেডিট।)

---

## ৮. বাস্তবায়ন ধাপ

| ধাপ | কাজ | নির্ভরতা | সময় |
|---|---|---|---|
| ০ | সিদ্ধান্ত (§৯), Subscription, ডোমেইন, Budget, Terraform backend (State Storage) | — | ১ দিন |
| ১ | Terraform: Resource Group, VNet, NSG, Key Vault, Azure DNS | ০ | ১ দিন |
| ২ | Vault Storage Account + Managed Identity রোল; (**Blob অ্যাডাপ্টার কোড** — সমান্তরালে, §০) | ১ | ০.৫ দিন + কোড ১–২ দিন |
| ৩ | PostgreSQL Flexible Server + মাইগ্রেশন (`db/migrations/*.sql`) | ১ | ০.৫ দিন |
| ৪ | API VM + Caddy + `sbqr.env` + `deploy-sbqr` দিয়ে ডিপ্লয় + `--seed-admin` | ২, ৩ | ১ দিন |
| ৫ | Portal: Caddy-তে স্ট্যাটিক + `/v1` প্রক্সি + হেডার + CI | ৪ | ০.৫ দিন |
| ৬ | Container Apps Environment + প্রথম FI (Dhaka Bank) Gateway+IdP; ডোমেইন/সার্টিফিকেট | ৪ | ১ দিন |
| ৭ | দ্বিতীয় FI যোগ করে "FI যোগ" প্রক্রিয়া প্রমাণ | ৬ | ০.৫ দিন |
| ৮ | মনিটরিং/অ্যালার্ম, শিডিউল, ব্যাকআপ-রিস্টোর ড্রিল | ৪–৬ | ১ দিন |
| ৯ | End-to-end যাচাই: Portal লগইন → FI অ্যাপ লগইন → QR তৈরি/ভেরিফাই → Vault থেকে কী লোড | সব | ১ দিন |
| ১০ | লোড-টেস্ট (অস্থায়ী বড় SKU) → Prod সাইজিং রিপোর্ট | ৯ | ২ দিন |

**Definition of Done:**
- ডাটাবেস ইন্টারনেট থেকে অগম্য; শুধু API subnet থেকে সংযোগ হয়।
- Vault-এ বেনামি/Shared-Key/HTTP অনুরোধ প্রত্যাখ্যাত; শুধু API-র Managed Identity কাজ করে।
- `/health` (API) ও `/health/ready` (Gateway) ইন্টারনেট থেকে ২০০।
- Portal লগইন + রিলোডে সেশন ফেরে (কুকি ঠিক আছে)।
- নতুন FI যোগ ≤৩০ মিনিট; নিষ্ক্রিয় FI-র খরচ ≈ $0।
- PostgreSQL PITR থেকে একবার সফল পুনরুদ্ধার ড্রিল।
- Budget অ্যালার্ট ইমেইলে আসছে।

---

## ৯. খোলা প্রশ্ন ও ঝুঁকি

| # | প্রশ্ন | আমার প্রস্তাব |
|---|---|---|
| 1 | **Vault:** Blob অ্যাডাপ্টার লেখা (সুপারিশ) নাকি অন্তর্বর্তী AWS S3? | Blob অ্যাডাপ্টার; শুরুর জন্য সাময়িক S3 |
| 2 | কোম্পানির ডোমেইন ও DNS কোথায়? | Azure DNS ($0.50/মাস) বা বাইরের DNS-এ CNAME/A |
| 3 | Dev-এ ক'টি FI? | Container Apps-এ সংখ্যা প্রায় অপ্রাসঙ্গিক (খরচ ব্যবহার-ভিত্তিক) |
| 4 | Dev কি ২৪×৭? অফ-আওয়ার শিডিউল? | শিডিউল চাইলে ~$30–35 সাশ্রয় |
| 5 | Gateway-তে কোল্ড-স্টার্ট গ্রহণযোগ্য? নাকি `minReplicas=1`? | Dev-এ গ্রহণযোগ্য; UAT-র আগে `1` |
| 6 | **ডেটা রেসিডেন্সি / নিয়ন্ত্রক শর্ত** (Bangladesh Bank): ডেটা ও কী কোন দেশে থাকতে পারবে? (AWS প্ল্যানেও প্রযোজ্য) | কমপ্লায়েন্স থেকে নিশ্চিত করতে হবে; এটি রিজিয়ন বদলাতে পারে |
| 7 | Portal Dev-এ কি P1 (Caddy) গ্রহণযোগ্য? | হ্যাঁ — কোড বদল নেই |
| 8 | VM-এ প্রবেশ: Bastion Developer SKU কি Southeast Asia-তে পাওয়া যাচ্ছে? | যাচাই; না হলে IP-সীমিত SSH + Entra login |
| 9 | API stateless কি না (Prod-এ বহু ইনস্ট্যান্সের জন্য)? | কোডে `IMemoryCache`/ডিস্ট্রিবিউটেড ক্যাশ যাচাই |
| 10 | mTLS (`feature/mtls-server`) চালু হলে Gateway→API পথে ক্লায়েন্ট-সার্টিফিকেট | Dev: Gateway সরাসরি API VM ডাকে (এই নকশায় তাই); Prod: Application Gateway v2 mTLS বা AKS ingress-এ টার্মিনেশন বিবেচ্য |
| 11 | AWS ও Azure — কোনটি আসল লক্ষ্য? | দুটোই কি রাখবেন, নাকি একটি বাছবেন? বাছাই-মানদণ্ড: Vault কোড-সামঞ্জস্য, দলের দক্ষতা, বিদ্যমান ক্রেডিট/চুক্তি, কমপ্লায়েন্স |

---

## ১০. AWS বনাম Azure — দ্রুত তুলনা (এই প্রকল্পের জন্য)

| বিষয় | AWS | Azure |
|---|---|---|
| Vault কোড-সামঞ্জস্য | ✅ আজই কাজ করে (S3 + Instance Role) | ⚠️ নতুন Blob অ্যাডাপ্টার (১–২ দিন) |
| Gateway/IdP খরচ | ~$22 ফ্ল্যাট (শেয়ার্ড VM) | ~$0–10 (scale-to-zero) |
| সার্ভারে নিরাপদ প্রবেশ | SSM — বিনামূল্যে, পোর্টহীন | Bastion Developer / সীমিত SSH |
| Portal হোস্টিং (Dev) | CloudFront + S3 (প্রায় বিনামূল্যে, একই origin) | VM-এর Caddy ($0) বা Front Door ($35+) |
| Prod DB কানেকশন পুলিং | RDS Proxy (অতিরিক্ত খরচ) | বিল্ট-ইন PgBouncer (বিনামূল্যে) |
| Prod K8s | EKS ($73/মাস কন্ট্রোল প্লেন) | AKS Free tier (কন্ট্রোল প্লেন বিনামূল্যে) |
| মোট Dev খরচ | ≈ $105–110 | ≈ $90–100 |
| বিদ্যমান ডক/স্ক্রিপ্টের সাথে মিল | বেশি (`deployments.md` AWS-কেন্দ্রিক) | কম — ডক ও `deploy-sbqr` স্ক্রিপ্টে হালকা পরিবর্তন |

---

### পরবর্তী পদক্ষেপ
§৯-এর #১, #৬, #১১ নম্বর প্রশ্নের উত্তর পেলে Terraform (`azurerm`) কোড ও Container Apps মডিউল খসড়া করব; Blob অ্যাডাপ্টার বেছে নিলে সেটির জন্য আলাদা প্ল্যান (`docs/plans/<feature>/plan.md` টেমপ্লেটে) লিখব। এখনো Azure-এ কিছুই বানানো হয়নি — এটি শুধুই পরিকল্পনা।
