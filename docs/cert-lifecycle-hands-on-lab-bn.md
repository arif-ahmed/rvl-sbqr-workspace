# Certificate ল্যাব — একদম সহজ সংস্করণ (বাংলা)

> **কার জন্য:** নতুন .NET developer — TLS/PKI-তে প্রথমবার। কোনো পূর্ব-জ্ঞান লাগবে না।
> **কী হবে:** Git Bash-এ হাতে-কলমে certificate-এর পুরো জীবন দেখবেন — **কে বানায়, কে রাখে, কে কখন ব্যবহার করে, আর খারাপ cert কীভাবে ধরা পড়ে।**
> **সময়:** ৩০–৪৫ মিনিট। **কোথায়:** ফাঁকা ল্যাব ফোল্ডার — কোনো repo touch হবে না। সব কমান্ড এই মেশিনে verify করা।
> **সিরিস:** [Theory notes](mtls-theory-notes-bn.md) (concept) · [.NET-এ integration](mtls-aspnetcore-integration-bn.md) (দুই প্রজেক্টের কোড-ম্যাপ) · [মূল গাইড](mtls-local-dev-guide-bn.md) (প্রজেক্ট + production)। এই ল্যাব হাতে-কলমের ভিত্তি।

## এক নজরে — ল্যাবের পুরো গল্প

```
তিনজন মিলে একটা নিরাপদ সংযোগ বানায়:

  🏛️ CA (issuer)  ──সই করে──►  🏦 sbqr.api-র cert (server)
       │
       └──সই করে──►  📱 Gateway-এর cert (client)

তারপর চারটা "যাত্রা" (journey) দেখবেন:
  যাত্রা ১ — CA-র private key সারাজীবন কাজে লাগে মাত্র ২ বার (সই করার সময়)
  যাত্রা ২ — public key দিয়ে certificate যাচাই হয় কীভাবে
  যাত্রা ৩ — Gateway যখন SBQR-এর সাথে কথা বলে: কোন ফাইল কখন চলে
  যাত্রা ৪ — 👿 Evil cert-এর মৃত্যু: কোথায় আটকে মরে
```

---

# ধাপ ০ — শুরুর আগে (২টা কমান্ড)

```bash
mkdir -p ~/sbqr-mtls-lab && cd ~/sbqr-mtls-lab
```
**ব্যাখ্যা:** হোম ফোল্ডারে একটা নতুরা ফোল্ডার বানালাম আর ভেতরে ঢুকলাম। এখানেই সব ফাইল জন্মাবে — প্রজেক্টের কোনো ফোল্ডারে না।

```bash
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
```
**ব্যাখ্যা:** Git Bash-এর একটা বাতিক — দেখলেই `/CN=...`-এর মতো মানকে Windows path ভেবে `C:/Program Files/Git/CN=...` বানিয়ে ফেলে, ফলে certificate-এর নাম নষ্ট। এই এক লাইন বলে দেয়: "কিছুই path ভেবে ঢেলো না।" (মনে রাখার দরকার নেই — কপি-পেস্ট করবেন।)

---

# ধাপ ১ — 🏛️ CA তৈরি করা (মোট ২টা কমান্ড)

CA মানে একটা **সই-করার দোকান**: তার একটা গোপন কলম (private key) আর একটা সবার-দেখার নামফলক (certificate)। এই দুটো বানাই এখন।

### কমান্ড ১.১ — CA-র private key (কলম)

```bash
openssl genrsa -out ca.key 4096
```

**এটা কী করে:** `genrsa` = "একটা RSA keypair বানাও", `4096` = শক্তি (বিট), `-out ca.key` = private অংশটা এই ফাইলে রাখো।
**ফলাফল:** `ca.key` ফাইল।
**মনে রাখুন:** এই একটা ফাইলই পুরো ল্যাবের সবচেয়ে দামি জিনিস — *যার এটা, সে-ই যে-কোনো certificate-এ "আমার CA সই করেছে" লিখে দিতে পারে।* এটা কোথাও copy/paste হবে না।

### কমান্ড ১.২ — CA-র নিজের certificate (নামফলক)

```bash
openssl req -x509 -new -nodes -key ca.key -sha256 -days 3650 \
  -subj "/CN=SBQR Dev Root CA" \
  -addext "basicConstraints=critical,CA:TRUE" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -out ca.crt
```

**এটা কী করে:** CA-র certificate বানায় যেখানে নিজেই নিজের সইকারী (`-key ca.key` দিয়ে নিজের নামে সই)। ভেতরের গুরুত্বপূর্ণ অংশগুলো:

| অংশ | মানে সহজ ভাষায় |
|---|---|
| `-subj "/CN=SBQR Dev Root CA"` | নাম রাখলাম "SBQR Dev Root CA" |
| `-days 3650` | ১০ বছর মেয়াদ (CA দীর্ঘজীবী হয়) |
| `-addext "...CA:TRUE"` | ★ সনদ: **"এ একটা CA — অন্যের certificate-এ সই করার অধিকার আছে।"** এটা ছাড়া পরে কেউ এই CA-র সই মানবে না |
| `-addext "...keyCertSign"` | এই key-এর একমাত্র কাজ: সই করা |
| `-sha256` | সই-এর গণিত SHA-256 দিয়ে |

**ফলাফল:** `ca.crt` — এটা **public**, সবার সাথে শেয়ার করা যায়। এর ভেতরে CA-র নাম + CA-র **public key**।

### দুই লাইনে যাচাই করে দেখুন

```bash
openssl x509 -in ca.crt -noout -subject -issuer
```
**এটা কী করে:** certificate-এর শুধু নাম-সুচনা দেখায়।
**প্রত্যাশিত:** `subject=CN = SBQR Dev Root CA` আর `issuer=CN = SBQR Dev Root CA` — **দুটোই এক** (নিজে নিজের সই, তাই "self-signed root")।

✅ **এই মুহূর্তে আপনি একটা CA-র মালিক** — `ca.key` + `ca.crt` হাতে। এবার দুই "গ্রাহক" আসবে সই করাতে।

---

# ধাপ ২ — 🏦 sbqr.api-এর certificate বানানো (server-পক্ষ)

sbqr.api-র জন্য ৪টা কাজ: **নিজের key বানানো → সই-চাওয়ার আবেদন (CSR) → CA-র সই → .NET-এর জন্য PFX মোড়া।**

### কমান্ড ২.১ — sbqr.api-র নিজের private key

```bash
openssl genrsa -out sbqr-api.key 2048
```
**এটা কী করে:** কমান্ড ১.১-এর মতোই, তবে এবার sbqr.api-র নিজস্ব key (২০৪৮-বিট — সাধারণ leaf cert-এর জন্য যথেষ্ট)।
**মনে রাখুন:** এটা CA-র key না — এটা শুধু sbqr.api-র। এটা দিয়ে সে পরে নিজের পরিচয় "সই" করবে।

### কমান্ড ২.২ — CSR: সই চাওয়ার আবেদন

```bash
openssl req -new -key sbqr-api.key -subj "/CN=localhost" -out sbqr-api.csr
```
**এটা কী করে:** "আমার নাম **localhost**, আর এই যে আমার public key — কেউ কি সই করে দিবেন?" — এই আবেদন ফাইল (`.csr`) বানায়।
**নাম কেন localhost:** server certificate-এর নাম = **যে ঠিকানায় client ঢুকবে**। Gateway যেহেতু `https://localhost:7443`-এ ঢুকবে, তাই নাম localhost। (প্রোডাকশনে হতো আসল DNS নাম।)
**গুরুত্বপূর্ণ:** CSR-এর ভেতরে private key **নেই** — শুধু public অংশ + নাম। বাস্তব দুনিয়ায় এই ফাইলটাই ইমেইলে টিম-টু-টিম যায়, key কারো কাছে যায় না।

### কমান্ড ২.৩ — certificate-এর "সনদপত্র" ফাইল লেখা

```bash
cat > sbqr-api.ext <<'EOF'
authorityKeyIdentifier=keyid,issuer
basicConstraints=CA:FALSE
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = @alt_names

[alt_names]
DNS.1 = localhost
IP.1  = 127.0.0.1
EOF
```
**এটা কী করে:** সই-এর সময় certificate-এ বসবে এমন কিছু অতিরিক্ত ঘোষণার খসড়া লিখল — প্রতিটা লাইনের মানে:

| লাইন | মানে সহজ ভাষায় |
|---|---|
| `basicConstraints=CA:FALSE` | "আমি পাত্র-মাত্র — **আমি দিয়ে আর কারো certificate সই করা যাবে না**" |
| `keyUsage = digitalSignature, keyEncipherment` | "আমার key TLS-এ সই আর key-বিনিময়ের কাজে লাগবে" |
| `extendedKeyUsage = serverAuth` | ★ "আমি **server** হিসেবে কাজ করব" — এই সনদ ছাড়া অনেক client মানবে না |
| `subjectAltName = @alt_names` + নিচের দুই লাইন | ★ "আমার বৈধ ঠিকানা: **localhost** আর **127.0.0.1**" — আজকের সব client (browser, .NET, curl) নাম মেলায় এই তালিকায় |
| `authorityKeyIdentifier` | সইকারী CA-কে চেনার ছোট্ট হাতল |

### কমান্ড ২.৪ — CA সই করে দিল (এখানে আপনি CA-র টুপি পরলেন)

```bash
openssl x509 -req -in sbqr-api.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out sbqr-api.crt -days 825 -sha256 -extfile sbqr-api.ext
```
**এটা কী করে:** CSR পড়ে, তার সাথে ২.৩-এর সনদগুলো জুড়ে, তারপর **`ca.key` দিয়ে সই করে** চূড়ান্ত certificate (`sbqr-api.crt`) বানায়।

| অংশ | মানে |
|---|---|
| `-CA ca.crt -CAkey ca.key` | ★ **কার নামে সই হচ্ছে + কার কলম দিয়ে।** `-CAkey`-ই সেই জায়গা যেখানে CA-র private key আসলে কাজে লাগে — মনে রাখুন এই মুহূর্তটা (যাত্রা ১ দেখুন) |
| `-CAcreateserial` | CA প্রতিটা সই-করা certificate-কে ক্রমিক নম্বর দেয়, খাতা রাখে `ca.srl` ফাইলে |
| `-days 825` | ~২.২৫ বছর মেয়াদ — সাধারণ certificate CA-র চেয়ে ছোট-জীবী |

### কমান্ড ২.৫ — .NET-এর জন্য PFX বান্ডিং

```bash
openssl pkcs12 -export -out sbqr-api.pfx \
  -inkey sbqr-api.key -in sbqr-api.crt -certfile ca.crt \
  -passout pass:sbqr-dev
```
**এটা কী করে:** certificate (`-in`) + private key (`-inkey`) + CA-র certificate (`-certfile`, চেইন-সহ দেওয়ার জন্য) — তিনটা জিনিস একটাই password-locked বান্ডলে (`sbqr-api.pfx`) মুড়ে দেয়।
**কেন:** .NET এক লাইনে PFX লোড করতে পারে — `new X509Certificate2("sbqr-api.pfx", "sbqr-dev")`। Kestrel ঠিক এভাবেই লোড করে।

✅ **sbqr.api-র সম্পত্তি তৈরি:** `sbqr-api.key` + `sbqr-api.crt` + `sbqr-api.pfx`।

---

# ধাপ ৩ — 📱 FI Gateway-এর certificate বানানো (client-পক্ষ)

একই ৪টা কাজ — তবল কয়েকটা জায়গায় **ইচ্ছাকৃত পার্থক্য**। কমান্ডগুলো আগে, পার্থক্যের ব্যাখ্যা পরে:

```bash
# ৩.১ — Gateway-র নিজের private key
openssl genrsa -out fi-gateway.key 2048

# ৩.২ — CSR: নাম এবার প্রতিষ্ঠান, ঠিকানা না
openssl req -new -key fi-gateway.key -subj "/CN=fi-gateway-dev/O=FI Gateway" -out fi-gateway.csr

# ৩.৩ — সনদপত্র: clientAuth, SAN নেই
cat > fi-gateway.ext <<'EOF'
basicConstraints=CA:FALSE
keyUsage = digitalSignature
extendedKeyUsage = clientAuth
EOF

# ৩.৪ — একই CA সই করে দিল
openssl x509 -req -in fi-gateway.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out fi-gateway.crt -days 825 -sha256 -extfile fi-gateway.ext

# ৩.৫ — PFX (এখানে CA-র cert সাথে দরকার নেই)
openssl pkcs12 -export -out fi-gateway.pfx \
  -inkey fi-gateway.key -in fi-gateway.crt -passout pass:fi-gateway-dev
```

**Server cert-এর (ধাপ ২) থেকে ঠিক যেখায় যেখায় আলাদা — এই ৪টা পার্থক্যই client cert-এর পরিচয়:**

| # | server cert (sbqr-api) | client cert (fi-gateway) | কেন |
|---|---|---|---|
| ১ | নাম `CN=localhost` — যে **ঠিকানায়** ঢুকবে | নাম `CN=fi-gateway-dev/O=FI Gateway` — **কোন প্রতিষ্ঠান** | client-এর কোনো "ঠিকানা" নেই; তার পরিচয়ই তার নাম |
| ২ | SAN তালিকা **লাগেই** | SAN **নেই** | কেউ client-এর নাম কোনো ঠিকানার সাথে মেলায় না |
| ৩ | সনদ `serverAuth` | সনদ **`clientAuth`** | "TLS-এ নিজেকে **client** হিসেবে প্রমাণ করার" অনুমতিপত্র — এটা ছাড়া কড়া server ফিরিয়ে দেবে |
| ৪ | PFX-এ CA-র cert-ও সাথে (`-certfile`) | PFX-এ শুধু নিজেরটা | server পুরো চেইন দেখায়; client শুধু নিজের cert পাঠায় |

### বোনাস কমান্ড — আঙুলের ছাপ (thumbprint)

```bash
openssl x509 -in fi-gateway.crt -noout -fingerprint -sha256
# → sha256 Fingerprint=97:9F:34:F4:24:BD:...
```
**এটা কী করে:** পুরো certificate-এর একটা ছোট্ট hash-ঠিকানা বের করে — "এই certificate-টা আর কারো সাথে গুলিয়ে যাবে না" এমন একটা আঙুলের ছাপ। প্রজেক্টে এটাই `Mtls__AllowedClientThumbprints__0`-তে বসে: "শুধু ঠিক এই certificate-টাই মানব" জাতীয় কড়া নিয়মের জন্য।

---

# ধাপ ৪ — 👿 Evil CA + evil client (খারাপ পক্ষের রিহার্সাল)

নিরাপত্তা প্রমাণ করতে হলে খারাপ পক্ষ লাগে। এখন **আরেকটা সম্পূর্ণ আলাদা CA** বানাব আর তার একটা client — সব নিয়ম মেনেই বানানো, কিন্তু **আমাদের CA-র সাথে এর কোনো সম্পর্ক নেই:**

```bash
# ৪.১ — খারাপ CA-র key ও certificate (নাম দিলাম "Evil Dev CA")
openssl genrsa -out evil-ca.key 2048
openssl req -x509 -new -nodes -key evil-ca.key -sha256 -days 3650 \
  -subj "/CN=Evil Dev CA" \
  -addext "basicConstraints=critical,CA:TRUE" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -out evil-ca.crt

# ৪.২ — তার client (নাম: attacker) — evil-ca সই করে দিল
openssl genrsa -out evil-client.key 2048
openssl req -new -key evil-client.key -subj "/CN=attacker" -out evil-client.csr
cat > evil-client.ext <<'EOF'
basicConstraints=CA:FALSE
keyUsage = digitalSignature
extendedKeyUsage = clientAuth
EOF
openssl x509 -req -in evil-client.csr -CA evil-ca.crt -CAkey evil-ca.key -CAcreateserial \
  -out evil-client.crt -days 825 -sha256 -extfile evil-client.ext
```
**ব্যাখ্যা এক লাইনে:** এগুলো আমাদের ল্যাবের কমান্ডগুলোরই কপি — শুধু সইকারী আমাদের CA (`ca.key`) না, **অন্য এক CA** (`evil-ca.key`)। যাত্রা ৪-এ দেখবেন এই "অন্য সই"-ই তাদের মৃত্যুর কারণ।

---

# ধাপ ৫ — 🔍 ফাইল পরীক্ষা ও যাচাই

> এই যাচাই-র ভেতরে ঠিক কী কী ঘটে, সেই গল্পটা একটু পরেই আলাদা করে — **যাত্রা ২**-তে।

### যাচাই-র কমান্ড

```bash
openssl verify -CAfile ca.crt sbqr-api.crt     # → sbqr-api.crt: OK
openssl verify -CAfile ca.crt fi-gateway.crt   # → fi-gateway.crt: OK
openssl verify -CAfile evil-ca.crt evil-client.crt   # → evil-client.crt: OK  (!)
```
**এটা কী করে:** "বলো তো কার কথা বিশ্বাস করব (`-CAfile`), তাহলে এই certificate তার সই-এর ভেতরে পড়ে কি না দেখো।"

### খুলে দেখার কমান্ডগুলো (certificate আসলে পড়ার মতো text)

```bash
openssl x509 -in ca.crt -text -noout | head -15
```
**কী করে:** CA certificate-এর ভেতরটা দেখায় — খুঁজুন: `Issuer` = `Subject` (নিজের সই), `CA:TRUE`।

```bash
openssl x509 -in sbqr-api.crt -text -noout | grep -A2 -E "Issuer:|Alternative|Basic Constraints"
```
**কী করে:** leaf certificate-এর মূল ঘরগুলো — দেখুন `Issuer: CN = SBQR Dev Root CA` (Subject-এর সাথে আলাদা!), `DNS:localhost, IP:127.0.0.1` (SAN), `CA:FALSE`।

---

# যাত্রা ১ — 🗝️ CA-র private key সারাজীবন কাজে লাগে মাত্র ২ বার

এই ল্যাবে `ca.key` আসলে ব্যবহৃত হয়েছে কোথায় কোথায়? পুরো ফাইলটা খুঁজে দেখুন:

```
ব্যবহার ১ → কমান্ড ২.৪: sbqr-api.crt সই করার সময়        (-CAkey ca.key)
ব্যবহার ২ → কমান্ড ৩.৪: fi-gateway.crt সই করার সময়       (-CAkey ca.key)
ব্যবহার ৩ → নেই। আর কোথাও না — কোনো handshake-এ না, কোনো যাচাইয়ে না, কোনো প্রজেক্টে না।
```

এটাই CA-র গল্প: **তার private key শুধু সই-খাতায় কাজে লাগে, কখনো wire-এ নামে না।** Gateway-এর কাছে আছে `fi-gateway.pfx` + `ca.crt`; SBQR-এর কাছে `sbqr-api.pfx` + `ca.crt` — **কারো কাছেই `ca.key` নেই**, লাগেও না। এজন্যই:
- `ca.key` লিক হওয়া = সবচেয়ে বড় দুর্ঘটনা (যে-কেউ নকল cert বানাতে পারবে) — তাই প্রোডাকশনে এটা থাকে offline PKI টিমের তালাবন্ধ মেশিনে
- আর বাকি সবাই শুধু public (`ca.crt`) দিয়ে কাজ চালায় — verify-তে private key লাগেই না (পরের যাত্রাই সেই প্রমাণ)

---

# যাত্রা ২ — 🔍 Public key দিয়ে certificate যাচাই হয় কীভাবে

ধাপ ৫-এর `openssl verify` কমান্ডটা চালালে ভেতরে আসলে ৩টা কাজ হয় — এবং এই ৩টাই handshake-এ (যাত্রা ৩-এর ধাপ ৩ ও ৫) দুই দিকে আবার হয়, শুধু TLS library নিজে করে:

```
১. certificate-এর Issuer ঘর পড়ে: "সই করেছে অমুক" (যেমন SBQR Dev Root CA)
২. বিশ্বাস-তালিকার CA (ca.crt) খুঁজে তার ভেতরের PUBLIC key নিয়ে
   certificate-এর গায়ের SIGNATURE মেলায়
   ── signature বানানো হয়েছিল ca.key (PRIVATE) দিয়ে      (যাত্রা ১-এর সেই ২ বার)
   ── শুধু তার জোড়া PUBLIC key-ই সেই signature ঠিকভাবে খুলতে পারে
   ── মিলে গেলে = "সত্যিই সেই CA সই করেছে, কেউ নকল করেনি" ✓
৩. মেয়াদ (Validity) বাঁচা আছে কি না দেখে → "OK"
```

**মূল সূত্রটা এক লাইনে:** *সই হয় private key দিয়ে, যাচাই হয় তার জোড়া public key দিয়ে — verify করার সময় private key কারো দরকার হয় না, তাই যাচাইকারীর হাতে শুধু `ca.crt` (public) থাকলেই চলে।*

আর ধাপ ৫-এর তৃতীয় কমান্ডটা মনে করুন: evil-client **তার নিজের পৃথিবীতে valid** (`-CAfile evil-ca.crt` দিলে OK)। বিশ্বাস আপেক্ষিক — কার সই মানবি সেটা যাচাইকারীর `-CAfile`/trust anchor ঠিক করে দেয়। এই এক লাইনই যাত্রা ৪-এর পুরো রহস্যের চাবি।

---

# যাত্রা ৩ — 🛤️ Gateway যখন SBQR-এর সাথে কথা বলে: কোন ফাইল কখন চলে

দুইজন কথা শুরু করার আগেই ৬টা ধাপ ঘটে যায় (TLS handshake)। প্রতিটা ধাপে হাতে কোন ফাইল, সেটাই নিচে — **ল্যাবের ফাইলগুলোর সাথে মিলিয়ে পড়ুন:**

| ধাপ | কী ঘটে | কার হাতে কোন ফাইল | private key চলে? |
|---|---|---|---|
| ১ | Gateway: "connection চাই" | — | না |
| ২ | SBQR: নিজের **server certificate** পাঠায় + বলে "তোমারটাও দাও" | SBQR পাঠায়: `sbqr-api.pfx`-এর ভেতরের cert | না — cert তো public |
| ৩ | Gateway: আসা server cert **যাচাই** করে (যাত্রা ২-এর ৩ ধাপ: Issuer খোঁজা → `ca.crt`-এর public key দিয়ে signature মেলানো → মেয়াদ) | Gateway-এর চশমা: `ca.crt` | **না** — যাচাই সবটাই public key দিয়ে |
| ৪ | Gateway: নিজের **client certificate** পাঠায় **+ private key দিয়ে একটা challenge-এর উত্তরে সই করে** | Gateway পাঠায়: `fi-gateway.pfx`-এর ভেতরের cert; সই করে: `fi-gateway.key` | **হ্যাঁ — এখানে**, শুধু নিজেরটা দিয়ে, নিজের মেশিনে |
| ৫ | SBQR: আসা client cert যাচাই — `ca.crt`-র public key দিয়ে চেইন + `clientAuth` সনদ + thumbprint | SBQR-এর চশমা: `ca.crt` (+ thumbprint তালিকা) | না |
| ৬ | সব পাস → গোপন session key → এরপর সব কথা encrypted — ভেতরে token (T) আর QR request/response (R) | — | না |

**পুরো হ্যান্ডশেকের নিয়মটা এক লাইনে:** *প্রতিটি পক্ষ নিজের private key দিয়ে **নিজের কথা** সই করে — আর প্রতিপক্ষের কথা যাচাই করে সবসময় **public** জিনিস দিয়ে (CERT + ca.crt)। কেউ কারো private key ছোঁয় না, দেখেও না।*

(প্রজেক্টে ধাপ ৪টা কোন code করে: Gateway-এ `MtlsConfigurator.cs` লোড করে `fi-gateway.pfx` → `HttpClientHandler.ClientCertificates`; ধাপ ২-এ Kestrel লোড করে `sbqr-api.pfx` → `UseHttps(...)`; ধাপ ৩/৫-এর চশমা দুই পাশেই `ca.crt` — gateway-এ `ServerCaCertPath`, sbqr-এ `CaCertificatePath`।)

### লাইভ ডেমো — কোন টার্মিনালের কোন লাইন কোন ধাপের প্রমাণ

উপরের টেবিলের ধাপ ২–৫ কাগজে-কলমের গল্প না — নিচের **"নিজে প্রমাণ করুন"** সেকশনের drill চালালে দুই টার্মিনালে হুবহু এই লাইনগুলো আসবে (নিচের আউটপুট এই মেশিনে চালিয়ে নেওয়া):

**Terminal ২ (`s_client` — Gateway-এর চোখে):**

```text
Certificate chain                         ← ধাপ ২: server তার certificate পাঠাল
 0 s:CN=localhost                         ←   s: কার cert (server-এর, নাম localhost)
   i:CN=SBQR Dev Root CA                  ←   i: কে সই করেছে — আমাদের CA!
 1 s:CN=SBQR Dev Root CA                  ←   সাথে CA-র নিজের cert-ও এলো (চেইন-সহ)
   i:CN=SBQR Dev Root CA
Acceptable client certificate CA names    ← ধাপ ২-এর দ্বিতীয় অর্ধ: "তোমার cert-ও দাও —
 …                                          এই CA-র সই-ওয়ালা হলে মানব" (নামটা দেখুন)
Verify return code: 0 (ok)                ← ধাপ ৩: আমি (client) server-cert যাচাই করলাম — পাস
```

**Terminal ১ (`s_server` — SBQR-এর চোখে):**

```text
depth=1 CN=SBQR Dev Root CA               ← ধাপ ৫: আসা চেইন root পর্যন্ত হাঁটল…
verify return:1                           ←   …এই গভীরতায় যাচাই ঠিক
depth=0 CN=fi-gateway-dev, O=FI Gateway   ← ধাপ ৪: client-এর cert আমার হাতে পৌঁছেছে — কার, দেখুন!
verify return:1                           ← ধাপ ৫: leaf-ও পাস — পুরো যাচাই সবুজ
ACCEPT                                    ← সব ধাপ পাস, connection খুলে দিলাম
```

> ছোট্ট কৌতূহল দুটো: (১) server-এর লাইনগুলো উল্টো ক্রমে আসে — আগে `depth=1` (root), পরে `depth=0` (leaf) — কারণ openssl চেইন-যাচাই root থেকে leaf-এর দিকে নামে। (২) client-পাশে `Verify return code: 0 (ok)` একাধিকবার ছাপতে পারে — প্রথমটাই মূল রায়।

**আর ব্যর্থতার সাক্ষী-লাইনগুলো (সব একই জায়গায় — Terminal ১-এ):**

| লাইন | মানে |
|---|---|
| `peer did not return a certificate` | ধাপ ৪ ঘটলই না — client certificate পাঠায়নি |
| `verify error:num=20:unable to get local issuer certificate` | ধাপ ৫-এ মৃত্যু — চেইন আমার anchor-এ পৌঁছাল না (evil) |

এখন drill-এর তিন test চালালে প্রতিটা লাইন আর গোপন রহস্য না — আপনি জানেন কোন লাইন কোন ধাপের সাক্ষী।

---

# যাত্রা ৪ — 👿 Evil client-এর মৃত্যু: কোথায় আটকে মরে

ধরুন attacker তার `evil-client.crt` নিয়ে আমাদের SBQR-এ ঢুকতে চাইল। ধাপে ধাপে কী হবে:

```
১. SBQR: "client certificate দাও" (RequireCertificate)
২. Evil client: evil-client.crt পাঠায় — দেখতে একদম ঠিকঠাক certificate,
   সনদও আছে (clientAuth), সইও আছে… শুধু সইটা Evil Dev CA-র
৩. SBQR যাচাই শুরু করে (যাত্রা ২-এর ধাপ ১): cert-এর Issuer ঘর পড়ল — "Evil Dev CA"
৪. SBQR তার বিশ্বাস-তালিকায় (trust anchor = আমাদের ca.crt) খোঁজে
   "Evil Dev CA" কে — ★ নেই। আমাদের তালিকায় একটাই নাম: SBQR Dev Root CA
৫. চেইন কোনো বিশ্বাস-করা root-এ পৌঁছাল না → signature মেলানোর
   public key-ই পাওয়া গেল না
   → TLS alert, connection মরে যায়
   → error বার্তা: "unable to get local issuer certificate"
```

**কেন এটা এত নিরাপদ নিয়ম:** attacker-এর certificate বানাতে কোনো অসুবিধা হয়নি, নিজের CA বানিয়ে নিজে সই-ও করেছে — কিন্তু আমাদের দরজার নিয়ম তার সই-এর কথা না, **আমার তালিকার CA-র সই-এর কথা**। Evil CA-র private key (`evil-ca.key`) যত নিখুঁতই হোক, আমার `ca.crt`-র public key-এর সাথে তার কোনো সম্পর্ক নেই — তাই মেলানোর প্রশ্নই ওঠে না।

আর কমনো ব্যর্থতা-রূপ দুটোও জেনে রাখুন: **certificate না দিলে** → "peer did not return a certificate" (দরজাতেই ফেরত), আর **মেয়াদ শেষ হলে** → "certificate has expired"।

---

# নিজে প্রমাণ করুন — শূন্য .NET কোডে, আপনার বানানো ফাইল দিয়েই

দুইটা Git Bash terminal, দুটোই `~/sbqr-mtls-lab`-এ।

**Terminal ১ — mini sbqr.api (আপনার server cert + আপনার CA-র নিয়ে দাঁড়াল):**

```bash
openssl s_server -accept 9443 -cert sbqr-api.crt -key sbqr-api.key -CAfile ca.crt -Verify 1 -www
```
**ব্যাখ্যা:** TLS server হয়ে দাঁড়াও পোর্ট 9443-এ — পরিচয় `sbqr-api.crt/key` (ধাপ ২), নিয়ম: "client certificate **দিতেই হবে** (`-Verify`, বড় হাতের V) এবং অবশ্যই **এই CA-র সই-খাওয়া** (`-CAfile ca.crt`) হতে হবে"। চালু হলে ছাপবে: `verify depth is 1, must return a certificate` + `ACCEPT`।

**Terminal ২ — তিনটা পরীক্ষা, তিনটা যাত্রার প্রমাণ:**

```bash
# (ক) ভালো client — যাত্রা ৩-এর সফল সংস্করণ:
echo | openssl s_client -connect localhost:9443 -CAfile ca.crt \
  -cert fi-gateway.crt -key fi-gateway.key | grep "Verify return code"
# → Verify return code: 0 (ok)        ★ mTLS সফল

# (খ) certificate ছাড়া ঢোকার চেষ্টা:
echo | openssl s_client -connect localhost:9443 -CAfile ca.crt
# → Terminal ১-এ: "peer did not return a certificate" — handshake মরল

# (গ) evil client — যাত্রা ৪ চোখের সামনে:
echo | openssl s_client -connect localhost:9443 -CAfile ca.crt \
  -cert evil-client.crt -key evil-client.key
# → Terminal ১-এ: "verify error:num=20:unable to get local issuer certificate"
```

তিনটা ফল একসাথে: **আমার CA-র cert-সহ ঢুকা যায় ✓, cert ছাড়া যায় না ✗, অন্যের CA-র cert-ও যায় না ✗** — প্রজেক্টের Kestrel + `HttpClientHandler` ঠিক এই তিনটাই করে, code-সহ।

---

# ফাইল হিসাব — কে কী রাখে (এক টেবিলে সব)

| ফাইল | কী | কার কাছে থাকবে | কেন |
|---|---|---|---|
| `ca.key` | CA-র private key (কলম) | **কারো কাছেই না** — শুধু আপনার ল্যাব ফোল্ডারে (issuer) | শুধু সই করার কাজে লাগে (যাত্রা ১); লিক = সর্বনাশ |
| `ca.crt` | CA-র public cert | **দুই পক্ষেই** — প্রতিপক্ষকে চেনার চশমা | সব যাচাই এটা দিয়েই (যাত্রা ২) |
| `sbqr-api.key/.crt/.pfx` | server-এর পরিচয় | শুধু **sbqr.api** | পাঠায় handshake-এর ধাপ ২-তে |
| `fi-gateway.key/.crt/.pfx` | client-এর পরিচয় | শুধু **Gateway** | পাঠায় ধাপ ৪-তে (+ তখনই key দিয়ে সই) |
| fi-gateway-এর thumbprint | cert-এর আঙুলের ছাপ | sbqr-এর কাছে (optional কড়া নিয়ম) | "শুধু এই cert-টাই" pinning |
| `evil-*` | খারাপ পক্ষ | শুধু test-এর হাতে | যাত্রা ৪ প্রমাণের জন্য |

---

# আসল প্রজেক্টে এই ল্যাবের ম্যাপ

| ল্যাবে | আসল প্রজেক্টে |
|---|---|
| ধাপ ১–৪-এর সব কমান্ড | এক কমান্ড: `rvl-secure-bqr-manager` repo-তে `bash scripts/dev-certs-generate.sh` — কমান্ড/নাম/password হুবহু এই ল্যাবের, আউটপুট `dev-certs/` |
| `s_server` | আসল sbqr.api (Kestrel `:7443`, `Mtls__Enabled=true`) |
| `s_client` | আসল gateway (`ASPNETCORE_ENVIRONMENT=MtlsTesting`) |
| আপনার মাথায় দুই টুপি (গ্রাহক + CA) | প্রোডাকশনে এক টুপি অন্যের মাথায় — CA-র টুপি ব্যাংক/platform-এর **PKI টিমের**; আপনি শুধু CSR পাঠাবেন, সই-করা cert ফেরত পাবেন |

পরের ধাপ: [মূল গাইডের অংশ ৪](mtls-local-dev-guide-bn.md) — এই certs দিয়েই দুই আসল service চালিয়ে end-to-end। ল্যাব শেষে ফোল্ডার মুছে দিতে পারেন: `rm -rf ~/sbqr-mtls-lab` — সব ছিঁড়ে-ফেলার মাল।

---

## চিটশিট — শুধু কমান্ড, ক্রমে

```bash
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
mkdir -p ~/sbqr-mtls-lab && cd ~/sbqr-mtls-lab

# CA
openssl genrsa -out ca.key 4096
openssl req -x509 -new -nodes -key ca.key -sha256 -days 3650 -subj "/CN=SBQR Dev Root CA" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out ca.crt

# sbqr.api (server)
openssl genrsa -out sbqr-api.key 2048
openssl req -new -key sbqr-api.key -subj "/CN=localhost" -out sbqr-api.csr
# (ext ফাইল: ধাপ ২.৩ থেকে)
openssl x509 -req -in sbqr-api.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out sbqr-api.crt -days 825 -sha256 -extfile sbqr-api.ext
openssl pkcs12 -export -out sbqr-api.pfx -inkey sbqr-api.key -in sbqr-api.crt -certfile ca.crt -passout pass:sbqr-dev

# Gateway (client)
openssl genrsa -out fi-gateway.key 2048
openssl req -new -key fi-gateway.key -subj "/CN=fi-gateway-dev/O=FI Gateway" -out fi-gateway.csr
# (ext ফাইল: ধাপ ৩.৩ থেকে)
openssl x509 -req -in fi-gateway.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out fi-gateway.crt -days 825 -sha256 -extfile fi-gateway.ext
openssl pkcs12 -export -out fi-gateway.pfx -inkey fi-gateway.key -in fi-gateway.crt -passout pass:fi-gateway-dev

# যাচাই + প্রমাণ
openssl verify -CAfile ca.crt sbqr-api.crt fi-gateway.crt
openssl x509 -in fi-gateway.crt -noout -fingerprint -sha256
openssl s_server -accept 9443 -cert sbqr-api.crt -key sbqr-api.key -CAfile ca.crt -Verify 1 -www
openssl s_client -connect localhost:9443 -CAfile ca.crt -cert fi-gateway.crt -key fi-gateway.key
```

*doc-only deliverable — কোনো repo/code touch হয়নি। সব কমান্ড Git Bash + OpenSSL 3.x-এ এই মেশিনে চালিয়ে যাচাই (সফল + ব্যর্থ দুই ধরনের ফলাফলসহ)।*
