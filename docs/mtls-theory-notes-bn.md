# mTLS Theory Notes — নতুন Fintech .NET Developer-এর ভিত্তি (বাংলা, short notes)

> **এটা কী:** রিভিশন-নোট শিট — mTLS-এর প্রতিটা concept আর terminology-র ছোট্ট নোট, **cascading কাঠামোতে** (প্রতিটা লেভেল আগের লেভেলের ওপর দাঁড়িয়ে — একটা skip করলে পরেরটা ঝুলে যায়)।
> **কীভাবে পড়বেন:** উপর থেকে নিচে, একটানা। প্রতিটা লেভেল = ১-২ মিনিট। শেষে hands-on script + master glossary + self-test।
> **সিরিজের অন্য ডকুমেন্ট:** [মূল গাইড](mtls-local-dev-guide-bn.md) (বিস্তারিত ব্যাখ্যা + drill + production) · [Certificate ল্যাব](cert-lifecycle-hands-on-lab-bn.md) (হাতে-কলমে)। এই নোটশিট ওদের সংক্ষিপ্ত ভিত্তি।
> **প্রজেক্ট:** FI Gateway (.NET 8 BFF) → sbqr.api (.NET 10) hop-এ mTLS over VPN।

## ক্যাসকেড-ম্যাপ — কী কী লেভেল, কীভাবে জমে

```
L1 Encryption ──► L2 Signature ──► L3 Certificate ──► L4 Key-pair মালিকানা
                                                        │
L5 CA (issuer) ◄────────────────────────────────────────┘
   │
L6 Chain of Trust ──► L7 TLS Handshake ──► L8 mTLS (দুই দিকে পরিচয়)
   │
L9 Trust কোথায় বসবে ──► L10 ফাইল ফরম্যাট ও টুল ──► L11 .NET API
   │
L12 স্তর-বিন্যাস (mTLS + OAuth) ──► L13 Fintech বাস্তবতা ──► L14 আপনার প্রজেক্টের নাম-ধাম
```

---

## L1 — Encryption: সব শুরু "লুকানো" থেকে

- **Symmetric encryption**: একটাই গোপন key দুই পক্ষে — দ্রুত, কিন্তু সমস্যা: key-টা ওপারে পৌঁছাবে কীভাবে?
- **Asymmetric encryption (public/private keypair)**: যা একটা দিয়ে বন্ধ করা, অন্যটা দিয়ে খোলা — key শেয়ার করা লাগে না। ধীর।
- **TLS-এর কাজাকাজ**: handshake-এ asymmetric দিয়ে পরিচয় + গোপন **session key** মীমাংসা, তারপর সব কথাবার্তা দ্রুত symmetric-এ।
- মনে রাখার লাইন: *asymmetric মানেই পরিচয় না — এটা শুধু গণিত; পরিচয় আসবে L3-এ।*

**Terms:** encryption, symmetric/asymmetric key, session key, plaintext/ciphertext।

## L2 — Hash ও Digital Signature: "এটা সত্যিই তুমি লিখেছ, বদলায়নি কেউ"

- **Hash**: যেকোনো data-র fixed-দৈর্ঘ্যের আঙুলের ছাপ (SHA-256) — একমুখী, এক বিট বদলালেই ছাপ পাল্টায়।
- **Digital signature**: data-র hash-টা **private key দিয়ে সই** করা; যে-কেউ **public key** দিয়ে যাচাই করতে পারে।
- সই প্রমাণ করে দুইটা জিনিস: (ক) data বদলায়নি, (খ) সইকারী = private key-এর মালিক।
- মনে রাখার লাইন: *private key কখনো পাঠানো হয় না — পাঠায় শুধু তার সই।*

**Terms:** hash, SHA-256, signature, sign/verify।

## L3 — Certificate (X.509): পরিচয়পত্র = নাম + public key + কারো সই

- **Certificate** মানে: *"এই public key-টা এই নামের (Subject) — দাবিটা আমি দেখে নিয়েছি, বিশ্বাস করো" — CA-এর সইসহ।*
- ভেতরের ঘরগুলো:

| ঘর | কী | উদাহরণ |
|---|---|---|
| Subject | কার certificate | `CN=localhost` (server) / `CN=fi-gateway-dev` (client) |
| Issuer | কে সই করেছে | `CN=SBQR Dev Root CA` |
| Validity | মেয়াদ | NotBefore → NotAfter |
| Public Key | পরিচয়ের গণিত-অংশ | RSA 2048 |
| **SAN** | server-এর বৈধ নামের তালিকা — **আজকের client-রা hostname এখানেই মেলায়**, CN-এ না | `DNS:localhost, IP:127.0.0.1` |
| **Basic Constraints** | `CA:TRUE` (সই করতে পারে) / `CA:FALSE` (পাত্র মাত্র) | |
| **Key Usage** | key-টা কী কী কাজে লাগবে | digitalSignature, keyCertSign |
| **EKU** | কার ভূমিকার সনদ | `serverAuth` / `clientAuth` ★ |
| Thumbprint | পুরো cert-এর SHA-256 hash — "এইটাই সেই cert" চেনার ছাপ | `97:9F:34:…` |

- মনে রাখার লাইন: *certificate পড়ার মতো text — `openssl x509 -in <cert> -text -noout`।*

**Terms:** X.509, Subject/Issuer, SAN, EKU, Basic Constraints, thumbprint/fingerprint, leaf certificate।

## L4 — Key-pair মালিকানা: certificate সবাই দেখে, প্রমাণ আসে key থেকে

- Certificate **প্রমাণ না, দাবি** — "আমি fi-gateway"। প্রমাণ হয় যখন ধারী **private key** দিয়ে handshake-এর challenge-এ সই করে দেখায়।
- তাই চুরি-খোঁজার টার্গেট certificate না — **private key**; আর key পাঠানো হয় না কখনো (L2)।
- মনে রাখার লাইন: *certificate = ID card, private key = আঙুলের ছাপ — card হারালে সমস্যা না, ছাপ নকল হলে সর্বনাশ।*

**Terms:** key pair, proof-of-possession, challenge-response।

## L5 — CA (Certificate Authority): সইকারী তৃতীয় পক্ষ — কারো ভেতরের জিনিস না

- সমস্যা: নিজে বানানো certificate-এ নিজের লেখা নাম — কে মানবে? দরকার এমন একজন, যাকে **দুই পক্ষই আগে থেকে বিশ্বাস করে** — সে CA।
- CA-র সম্পত্তি: নিজের certificate (public — সবার কাছে যায়) + নিজের **private key** (শুধু CA-র কাছে — এটাই তার কলম)।
- **Root CA**: চেইনের শীর্ষ; **self-signed** (নিজে নিজের সই — Issuer = Subject); বিশ্বাস এখানেই anchor।
- **Intermediate CA**: বড় PKI-তে root-এর সই-খাওয়া মাঝের CA — দৈনন্দিন ইস্যু এরাই করে, root থাকে offline।
- আপনার প্রজেক্টে: dev CA = `SBQR Dev Root CA` (manager repo-র `dev-certs/`); prod-এ ব্যাংক/platform PKI টিম।
- মনে রাখার লাইন: *CA = ভূমিকা (issuer); gateway/sbqr দুজনেই অন্য দুই ভূমিকা (prover + verifier)।*

**Terms:** CA, root CA, intermediate CA, self-signed, trust anchor, PKI, CSR (cert চাওয়ার আবেদন — private key বাইরে যায় না)।

## L6 — Chain of Trust: সই-ধরে উপরে ওঠা

- Verify মানে হাঁটা: `leaf cert → যার সই সে → তার সইকারী → … → root` — root পর্যন্ত, ব্যস।
- মেলানোর নিয়ম: leaf-এর **Issuer = পরের cert-এর Subject**; প্রতিটা ধাপে signature ঠিক, মেয়াদ বাঁচা।
- **Trust anchor**: হাঁটা শেষ হয় এমন cert-এ যেটাকে আমি আগে থেকে বিশ্বাস করি বলে ঠিক করেছি — এটাই পুরো বিশ্বাসের ভিত্তি।
- মনে রাখার লাইন: *বিশ্বাস আপেক্ষিক — evil-CA-র cert তার পৃথিবীতে বৈধ, আমার anchor-এ পৌঁছায় না তাই আমার কাছে অচেনা।*

**Terms:** chain of trust, trust anchor, chain build/verify, `unable to get local issuer certificate` (চেইন ভাঙলে এই error)।

## L7 — TLS Handshake: encryption শুরুর আগে ৪টা দরজা

1. Client → Server: "connection চাই" (ClientHello)
2. Server → Client: **নিজের certificate** (আসল প্রজেক্টে `sbqr-api.pfx`)
3. Client: server cert **verify** (L6-এর হাঁটা, নিজের anchor দিয়ে) + দুজনে গোপন **session key** মীমাংসা
4. এরপর সব কথা encrypted (L1)

- লক্ষ করুন: এই গল্পে **client বেনামী** — server-ই শুধু পরিচয় দেখাল। এটাই সাধারণ HTTPS।
- ব্যর্থতা যেকোনো ধাপে হলে → handshake error, HTTP status পর্যন্ত পৌঁছায়ই না।

**Terms:** handshake, ClientHello/ServerHello, session key, TLS alert, `SSLHandshakeException`।

## L8 — mTLS: handshake-এর মাঝখানে আরেকটা দরজা — "তুমি কে?"

- mTLS = TLS + **server-ও client-এর certificate চায়** (handshake-এ `CertificateRequest` যায়, client নিজের cert + private-key-সই পাঠায়, server তা যাচাই করে)।
- দুই দিকে দুই verify: client যাচাই করে **server cert**, server যাচাই করে **client cert** — দুজনের anchor একই CA হতে পারে (আমাদের dev-এ একই), ভিন্নও হতে পারে।
- এটা **machine identity**: কোনো user/password নেই — দুটো সার্ভিস একে অপরকে চেনে। তাই bank-to-platform hop-এর প্রাণ।
- আপনার প্রজেক্টে: gateway = client (`fi-gateway.pfx` পাঠায়), sbqr.api = server (`sbqr-api.pfx` দেখায় + দেখা মাত্র client cert চায়)।

**Terms:** mutual TLS, client/server certificate, machine-to-machine identity, peer certificate।

## L9 — Trust কোথায় বসানো হবে: store বনাম pin

একই chain-verify, দুই রকম জায়গায় anchor রাখা যায়:

| উপায় | কী | কখন |
|---|---|---|
| **OS trust store** | Windows-এর user/machine store-এ CA ঢোকানো (`certutil -user -addstore Root`) — সেই মেশিনের সব app বিশ্বাস করে | dev সুবিধা, browser/curl |
| **Pinned (code-এ)** | app-এর config-এ CA-র path, verify-র সময় শুধু **এই** CA — .NET-এ `X509TrustMode.CustomRootTrust` | আসল পথ — স্পষ্ট, audit-যোগ্য, prod-safe |

- .NET-এ চেইন মানায় `X509Chain.Build(cert)`; নীতি বলে দেয় `ChainPolicy`।
- **Revocation**: বাতিল-cert ধরার ব্যবস্থা (CRL/OCSP); private CA-র কাছে এসব নেই বলে `NoCheck` — নইলে সব handshake মরবে।
- Kestrel-এ server-পক্ষের ভঙ্গি: `ClientCertificateMode` = `NoClientCert` / `AllowCertificate` (চাইবে, না দিলেও connection চলবে) / **`RequireCertificate`** (না দিলে handshake-ই মরবে — আমাদের sbqr.api এটাই)।

**Terms:** trust store, pinned CA, `CustomRootTrust` vs `ChainTrust`, revocation/CRL/OCSP, `ClientCertificateMode`।

## L10 — ফাইল ফরম্যাট ও টুল: একই জিনিসের বিভিন্ন মোড়ক

| Extension | ভেতরে | কার পছন্দ |
|---|---|---|
| `.crt`/`.pem` | শুধু certificate (public), Base64 text | openssl/Linux/Node |
| `.key`/`.pem` | শুধু private key | — |
| `.pfx` (PKCS#12) | cert + private key, **password-locked** এক বান্ডল | .NET (`X509Certificate2(path, pass)`) |
| `.csr` | সই-চাওয়ার আবেদন (public + নাম; key নেই) | CA-র কাছে পাঠানোর একমাত্র জিনিস |
| `.srl` | CA-র সিরিয়াল-নম্বর খাতা | CA নিজের |

টুল: `openssl` (সব কাজ), `certutil` (Windows store), `X509Explorer`/`certmgr.msc` (GUI দেখা)।

## L11 — .NET-এ mTLS-এর মানচিত্র: মাত্র ৫টা API-জিনিস

| কাজ | API | কোন পক্ষ |
|---|---|---|
| Cert লোড | `new X509Certificate2(pfx, password)` / `X509CertificateLoader.LoadPkcs12FromFile` | দুই পক্ষ |
| Client হিসেবে cert পাঠানো | `HttpClientHandler.ClientCertificates.Add(cert)` (+ `ClientCertificateOption.Manual`) | gateway |
| Server cert যাচাই (custom) | `HttpClientHandler.ServerCertificateCustomValidationCallback` | gateway |
| HTTPS + client-cert দাবি | Kestrel: `UseHttps(cert, o => { o.ClientCertificateMode = RequireCertificate; o.ClientCertificateValidation = … })` | sbqr.api |
| চেইন যাচাই | `X509Chain` + `ChainPolicy.CustomRootTrust` + `chain.Build(cert)` | দুই পক্ষ |

প্রজেক্টে যারা এগুলো জড়িয়ে রেখেছে: gateway-এ `MtlsConfigurator.cs`, sbqr-এ `MtlsEndpointsExtensions.cs` + `ClientCertificateValidator`।

## L12 — স্তর-বিন্যাস: mTLS ≠ OAuth, দুটোই লাগবে

| স্তর | প্রশ্ন | টুল |
|---|---|---|
| Transport = mTLS | কোন **মেশিন** কথা বলছে? | X.509 cert |
| Application = OAuth2/JWT | কোন **client**-কে কী অনুমতি? | client_credentials, token, scope |

মনে রাখার লাইন: *mTLS tunnel-এর ভেতরে ঢুকে gateway token নেয়, সেই token নিয়ে QR call করে — কেউ কাউকে replace করে না।*

## L13 — Fintech বাস্তবতা: কেন ব্যাংক-জগতে এত জোর

- **Defense in depth**: VPN (নেটওয়ার্ক বন্ধ) + mTLS (VPN-এর ভেতরেও প্রতিটা connection প্রমাণিত) + OAuth (অনুমতি)।
- `client_secret` পাসওয়ার্ড — চুরি হলেই ঢোকা যায়; cert+key-এ key কখনো যায় না, তাই চুরি-সহিষ্ণু।
- **Cert lifecycle**: key+CSR বানান (key বাইরে না) → PKI টিম সই করে → deploy (vault/secret-mount) → **মেয়াদ watch + rotation** → দরকারে revoke।
- বড় প্রতিষ্ঠানে key থাকে **HSM/Key Vault**-এ; compliance (central bank, card scheme) inter-institution hop-এ mTLS-ই চায়।
- ক্লাসিক দুর্ঘটনা: **cert-এর মেয়াদ ফুরালো সব connection একসাথে মরল** — expiry monitoring বাধ্যতামূলক।

**Terms:** defense in depth, VPN, CSR flow, rotation, revocation, HSM, Key Vault, certificate-bound token (RFC 8705 — পরে পড়ার)।

## L14 — আপনার প্রজেক্টের নাম-ধাম (এক টেবিলে সব)

| প্রজেক্টের জিনিস | এটা আসলে |
|---|---|
| `dev-certs/ca.crt`, `ca.key` | dev root CA (issuer-এর জিনিস; key শুধু manager repo-তে) |
| `dev-certs/sbqr-api.pfx` (pass `sbqr-dev`) | server cert — Kestrel `:7443`-এ |
| `dev-certs/fi-gateway.pfx` (pass `fi-gateway-dev`) | client cert — gateway পাঠায় |
| `dev-certs/evil-*` | negative test-এর অচেনা CA |
| Gateway `Mtls:Enabled/CertPath/CertPassword/ServerCaCertPath` | client-side config |
| SBQR `Mtls:Enabled/HttpsPort/ServerCertificatePath/CaCertificatePath/AllowedClientThumbprints` | server-side config |
| `MtlsConfigurator.cs` (gateway) | cert attach + server-verify wiring |
| `MtlsEndpointsExtensions.cs` (sbqr) | Kestrel HTTPS + RequireCertificate wiring |
| `appsettings.MtlsTesting.json` | লোকাল e2e env (untracked — ভেতরে আসল dev secret) |
| `:5001` / `:7443` | sbqr-এর HTTP / mTLS-HTTPS পোর্ট |
| thumbprint allowlist + per-tenant registry | "শুধু ইস্যু-করা cert-গুলোই" — DB-তে per-FI |

---

## Hands-on script — প্রতিটা কমান্ড এক লাইনের নোটসহ

> উদ্দেশ্য: উপরের L1–L8 নিজের হাতে ৫ মিনিটে অনুভব করা। বিস্তারিত ব্যাখ্যা [ল্যাব ডকুমেন্টে](cert-lifecycle-hands-on-lab-bn.md); এখানে সংক্ষিপ্ত, প্রতিটা লাইনের পাশে সে কী করছে। Git Bash-এ, ফাঁকা ফোল্ডারে।

```bash
mkdir -p ~/mtls-notes-lab && cd ~/mtls-notes-lab
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"   # Git Bash-কে বলা: "/CN=…" কে Windows path বানিয়ো না

# ── CA (issuer) ──
openssl genrsa -out ca.key 4096                                   # CA-র private key — সই-কলম (L5)
openssl req -x509 -new -nodes -key ca.key -sha256 -days 3650 \
  -subj "/CN=My Notes CA" -addext "basicConstraints=critical,CA:TRUE" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" -out ca.crt     # self-signed root cert; CA:TRUE ছাড়া কেউ বিশ্বাস করবে না (L3, L5)

# ── server cert (sbqr.api-র ভূমিকা) ──
openssl genrsa -out server.key 2048                               # server-এর private key (L4)
openssl req -new -key server.key -subj "/CN=localhost" -out server.csr   # সই-চাওয়ার আবেদন — ভেতরে key নেই (L5, CSR)
printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:localhost,IP:127.0.0.1\n' > server.ext
                                                                  # সনদ: পাত্র-মাত্র, serverAuth EKU, বৈধ নাম SAN (L3)
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -days 825 -sha256 -extfile server.ext -out server.crt           # CA-র টুপি পরে সই — Issuer এখন My Notes CA (L5, L6)
openssl verify -CAfile ca.crt server.crt                          # চেইন-হাঁটা: আউটপুট "OK" = trust anchor পর্যন্ত সব ঠিক (L6)

# ── client cert (gateway-এর ভূমিকা) ──
openssl genrsa -out client.key 2048                               # client-এর private key
openssl req -new -key client.key -subj "/CN=notes-client/O=Lab" -out client.csr
printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature\nextendedKeyUsage=clientAuth\n' > client.ext
                                                                  # clientAuth EKU — "TLS-এ client হবো" সনদ (L3)
openssl x509 -req -in client.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -days 825 -sha256 -extfile client.ext -out client.crt           # একই CA সই করল — দুই পক্ষ এখন এক anchor-এ (L8)

# ── দেখা-পড়া ──
openssl x509 -in ca.crt -text -noout | head -15                   # Issuer=Subject, CA:TRUE নিজে চোখে (L3, L5)
openssl x509 -in server.crt -text -noout | grep -E "Issuer|Subject|DNS|CA:"   # Issuer≠Subject, SAN, CA:FALSE
openssl x509 -in client.crt -noout -fingerprint -sha256           # thumbprint — pinning-এর সেই হ্যাশ (L3, L14)

# ── শূন্য-কোড mTLS: দুই টার্মিনালে ──
# T1: openssl s_server -accept 9443 -cert server.crt -key server.key -CAfile ca.crt -Verify 1 -www
#      ↑ বড়-V Verify = client cert দিতেই হবে + এই CA-র চেইনে হতে হবে — mini sbqr.api (L7, L8, L9)
# T2: openssl s_client -connect localhost:9443 -CAfile ca.crt -cert client.crt -key client.key
#      → "Verify return code: 0 (ok)" = পুরো mTLS handshake সফল
# T2-নেতিবাচক: সেই কমান্ড থেকে -cert/-key বাদ দিলে → server-এ "peer did not return a certificate" (L8)
#              অন্য CA-র cert দিলে → "unable to get local issuer certificate" (L6, L9)
```

---

## Master glossary — এক নজরে সব term (গুচ্ছে ভাগ করা)

**গুচ্ছ ১ · গণিত:** encryption · symmetric/asymmetric · hash · SHA-256 · digital signature · key pair
**গুচ্ছ ২ · Certificate-এর ভেতর:** X.509 · Subject · Issuer · CN · SAN · Validity · Public Key · Basic Constraints (CA:TRUE/FALSE) · Key Usage · EKU (serverAuth/clientAuth) · thumbprint/fingerprint · serial
**গুচ্ছ ৩ · PKI ভূমিকা:** CA · root CA · intermediate CA · self-signed · trust anchor · PKI · CSR · issuer/prover/verifier · leaf certificate
**গুচ্ছ ৪ · যাচাই:** chain of trust · chain build/verify · trust store (user/machine) · pinned CA · CustomRootTrust · ChainTrust · revocation · CRL · OCSP
**গুচ্ছ ৫ · TLS/mTLS ঘটনা:** handshake · ClientHello/ServerHello · CertificateRequest · peer certificate · session key · TLS alert · machine identity · TLS termination
**গুচ্ছ ৬ · ফাইল/টুল:** PEM · CRT · KEY · PFX/PKCS#12 · CSR · SRL · openssl · certutil · certmgr.msc
**গুচ্ছ ৭ · .NET:** `X509Certificate2` · `X509CertificateLoader` · `HttpClientHandler.ClientCertificates` · `ServerCertificateCustomValidationCallback` · Kestrel `UseHttps` · `ClientCertificateMode` (NoClientCert/AllowCertificate/RequireCertificate) · `X509Chain` · `X509TrustMode.CustomRootTrust` · `WebApplicationFactory` সত্যিকারের TLS করে না
**গুচ্ছ ৮ · Fintech/প্রজেক্ট:** defense in depth · VPN · BFF · client_credentials/JWT (আলাদা স্তর!) · CSR flow · rotation · revoke · HSM · Key Vault · RFC 8705 · `dev-certs/` · `:7443` · thumbprint allowlist · per-tenant cert registry

---

## Self-test — দ্রুত ১০ প্রশ্ন (উত্তর ভেবে খুলুন)

1. HTTPS-এ server client-কে চেনে না — তাহলে ব্রাউজাং চলে কীভাবে?
2. Certificate হাতে পেলেই কি আমি সে-ই সেজে যেতে পারি?
3. `CA:FALSE` certificate দিয়ে আরেকটা certificate সই করা যাবে?
4. Root CA self-signed — তাহলে সে নিজেই নিজের প্রমাণ? বিশ্বাস আসে কোথা থেকে?
5. `openssl verify` "OK" দিলেই কি বাকি সব (মেয়াদ, SAN, EKU) ঠিক আছে?
6. mTLS-এ client certificate-এ SAN থাকে না কেন?
7. OS trust store-এ CA ঢোকানো বনাম code-এ pin — prod-এ কোনটা, কেন?
8. `ClientCertificateMode`-এর তিন মান কার প্রয়োজনে আলাদা?
9. VPN থাকলেও mTLS কেন লাগে?
10. Cert-এর মেয়াদ শেষ হলে কী ঘটে, আগে কীভাবে জানবেন?

<details><summary><b>উত্তর</b></summary>

1. চেনে না বলেই anonymous কথা বলা যায়; পরিচয় তখন অ্যাপ-স্তরে (login/OAuth)।
2. না — private key ছাড়া handshake-এর challenge-সই দেওয়া যায় না (L4)।
3. না — Basic Constraints তা নিষেধ; নকল-চেইন এভাবেই বন্ধ (L3)।
4. প্রমাণ নয়, ঘোষণা; বিশ্বাস আসে আমার হাতে-বসানো anchor থেকে — সে জন্যই store/pin করতে হয় (L5, L9)।
5. না — verify শুধু চেইন দেখে; মেয়াদ দেখে একই সাথে, কিন্তু SAN/EKU আলাদা যাচাই (আমাদের validator করে)।
6. Client-এর "ঠিকানা" নেই — তার পরিচয় Subject নাম; hostname-মেলানোর প্রশ্নই ওঠে না (L3-পার্থক্য)।
7. Pin — স্পষ্ট, ঐ app-এ সীমাবদ্ধ, ভুলে OS-পুরোটা খুলে দেওয়া হয় না, audit-যোগ্য (L9)।
8. Health endpoint বাঁচানো / ধীরে ধীরে rollout / পুরো কড়াকড়ি — যথাক্রমে AllowCertificate-প্যাটার্ন, ধাপে ধাপে, RequireCertificate (L9)।
9. VPN-এর ভেতরের যে-কোনো মেশিন নয়, শুধু প্রমাণিত-পরিচয়ের পক্ষই ঢুকতে পারবে — স্তর আলাদা তালা (L13)।
10. সব connection একসাথে মরে (handshake-ই fail); জানার উপায় — শুরুতে `NotAfter` চেক + মেয়াদ-ক্যালেন্ডার + ৩০-দিন warning (L13)।

</details>

---

## পড়ার ক্রম (সিরিজের মধ্যে)

```
এই নোটশিট (ভিত্তি, ~২০ মিনিট)
   └─► certificate ল্যাব (হাতে-কলমে, ~৪৫ মিনিট)
         └─► মূল গাইড অংশ ৩–৪ (আসল প্রজেক্টে drill)
               └─► মূল গাইড অংশ ৬ (production) + manager repo-র mtls-guide.md
```

*doc-only deliverable — কোনো repo/code touch হয়নি। সব কমান্ড এই মেশিনের Git Bash + OpenSSL 3.x-এ যাচাইকৃত সংস্করণ থেকে নেওয়া।*
