# mTLS → ASP.NET Core — দুই API প্রজেক্টের ভাষায় (বাংলা, .NET dev-এর জন্য)

> **কার জন্য:** .NET developer, যার দুইটা ASP.NET Core Web API প্রজেক্ট — একটা **client ভূমিকায়** (FI Gateway: বাইরের API-কে কল করে), একটা **server ভূমিকায়** (sbqr.api: কল গ্রহণ করে)।
> **উদ্দেশ্য:** [certificate ল্যাবের](cert-lifecycle-hands-on-lab-bn.md) প্রতিটা জিনিস — CA, cert, চারটা যাত্রা — ঠিক কোন .NET API-র ভেতরে গিয়ে বসে, সেটা কোডসহ বোঝা।
> **নিয়ম:** প্রতিটা সেকশনে আগে **ল্যাব-এ যা দেখেছিলেন** (এক লাইনে), তারপর **.NET-এ সেটা কোথায়/কীভাবে**।
> **সিরিজ:** [Theory notes](mtls-theory-notes-bn.md) → [ল্যাব](cert-lifecycle-hands-on-lab-bn.md) → **এই ডকুমেন্ট** → [মূল গাইড](mtls-local-dev-guide-bn.md)। কোডগুলো আপনার দুই repo-র আসল implementation অনুসরণ করে — শেষে ফাইল-ম্যাপ টেবিল।

---

## ০. এক নজরে — দুই প্রজেক্ট, দুই দিক, একই জ্ঞান

| | 🔵 FI Gateway (client) | 🟢 sbqr.api (server) |
|---|---|---|
| প্রজেক্ট টাইপ | ASP.NET Core Web API (net8) | ASP.NET Core Web API (net10) |
| mTLS-এ ভূমিকা | কথা **শুরু করে** — নিজের cert পাঠায় | কথা **গ্রহণ করে** — নিজের cert দেখায় + দেখা মাত্র client-এর cert চায় |
| .NET-এর কোন জগৎ | `HttpClient` পক্ষ (outbound) | `Kestrel` পক্ষ (inbound) |
| ল্যাব-এর কোন ফাইল | `fi-gateway.pfx` (নিজের পরিচয়) + `ca.crt` (server-কে চেনার চশমা) | `sbqr-api.pfx` (নিজের পরিচয়) + `ca.crt` (client-কে চেনার চশমা) |
| মূল কনফিগ | `Mtls:Enabled/CertPath/CertPassword/ServerCaCertPath` | `Mtls:Enabled/HttpsPort/ServerCertificatePath/CaCertificatePath/AllowedClientThumbprints` |

> মনে করুন ল্যাবের কথা: **একই `ca.crt` দুই পাশে, দুই কারণে** — নিচে ঠিক এটাই দুই রকম .NET কোড হয়ে দেখবেন।

---

# পক্ষ ১ — 🔵 Client: ASP.NET Core থেকে mTLS দিয়ে বাইরে কল করা (Gateway)

## 1.1 প্রথমে ভিত্তি — বাইরের কলের পথ: `IHttpClientFactory`-র named client

**ল্যাব-এ:** আপনি নিজে `s_client` দিয়ে cert হাতে নিয়ে ঢুকেছিলেন।
**.NET-এ:** সেই "cert-হাতে-নেওয়া" কাজটা করে **`HttpClientHandler`** — আর সেই handler বানানোর স্থান `IHttpClientFactory`-র registration।

```csharp
// Program.cs (Gateway) — named client, যেটা দিয়ে সব platform-কল যায়:
var platformQrBuilder = builder.Services.AddHttpClient("PlatformQr", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<PlatformOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);        // https://localhost:7443 — ল্যাবের s_server/sbqr.api
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
})
.AddHttpMessageHandler<PlatformAuthHandler>();          // Bearer token লাগায় — খেয়াল করুন: TLS-এর উপরের স্তর

// তারপর mTLS — ল্যাবের ফাইল দুটি এখানে ঢোকে:
MtlsConfigurator.Apply(platformQrBuilder, builder.Configuration);
```

**কেন এই পথ (একজন .NET dev হিসেবে মনে রাখুন):**
- `new HttpClient()` হাতে বানানো না — factory `handler` recycle করে (socket exhaustion ঠেকে), আর সব outbound কল এক জায়গায় configure করা যায়।
- `AddHttpMessageHandler<…>` গুলো হলো pipeline-এর **উপরের স্তর** (logging, auth, retry); certificate বসে সবার **নিচে** — `ConfigurePrimaryHttpMessageHandler`-এ। অর্ডারটাই যাত্রা ৩-এর স্তর-ভাগ: সবার আগে TLS, তারপর HTTP, তারপর token।

## 1.2 মূল কোড — `MtlsConfigurator` (আপনার repo-র আসল প্যাটার্ন, ব্যাখ্যাসহ)

```csharp
public static class MtlsConfigurator
{
    public static void Apply(IHttpClientBuilder builder, IConfiguration config)
    {
        var section = config.GetSection("Mtls");

        // (১) চালু/বন্ধ — off থাকলে পুরো জিনিসটা no-op: mTLS ছাড়া আজকের dev-flow অক্ষত থাকে
        if (!(section.GetValue<bool?>("Enabled") ?? false)) return;

        // (২) fail-fast: চালু করেছেন অথচ path/password নেই → চুপচাপ ব্যর্থ না, শুরুতেই exception
        var certPath     = section["CertPath"]      ?? throw new InvalidOperationException("Mtls:CertPath missing");
        var certPassword = section["CertPassword"]  ?? throw new InvalidOperationException("Mtls:CertPassword missing");

        // (৩) ল্যাব-এর যাত্রা ৩ / ধাপ ৪: fi-gateway.pfx লোড — cert + private key একসাথে
        var clientCert = new X509Certificate2(certPath, certPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

        // (৪) ল্যাব-এর যাত্রা ৩ / ধাপ ৩-এর "চশমা": ca.crt — শুধু public cert, কোনো private key নেই
        var serverCa = new X509Certificate2(section["ServerCaCertPath"]
            ?? throw new InvalidOperationException("Mtls:ServerCaCertPath missing"));

        // (৫) সবকিছু একসাথে বসানো — handler-এর একদম নিচের স্তরে
        builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // "কোন cert পাঠাব আমি নিজে বলে দিচ্ছি" (Manual) — অটো-সিলেক্ট না
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ClientCertificates = { clientCert },
            //   ↑ handshake-এর ধাপ ৪-এ TLS নিজেই সময়মতো (server চাইলে) এই cert পাঠায় —
            //     আপনার কোড প্রতি-request কিছু করে না

            // ল্যাব-এর যাত্রা ২ — server-এর cert যাচাই, আমার নিজের anchor দিয়ে:
            ServerCertificateCustomValidationCallback = (_, serverCert, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true; // প্রোডাকশনের public-CA cert — OS-ই বিশ্বাস করল
                if (serverCert is null) return false;

                using var chain = new X509Chain();                            // যাত্রা ২-এর সেই ৩ ধাপ:
                chain.ChainPolicy.TrustMode = X509TrustMode.CustomRootTrust;  // "আমার তালিকায় একটাই CA"
                chain.ChainPolicy.CustomTrustStore.Add(serverCa);             // ← সেই একটা CA = ca.crt
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // dev/internal CA-র কাছে CRL নেই
                return chain.Build(serverCert);                               // চেইন হাঁটা → true/false
            },
        });
    }
}
```

## 1.3 এই কোডের ল্যাব-ম্যাপ (এক টেবিলে মিলিয়ে নিন)

| কোডের লাইন | ল্যাব-এ যেটার রূপ ছিল |
|---|---|
| `new X509Certificate2(certPath, password, …)` | `openssl pkcs12 -export … -out fi-gateway.pfx` — ধাপ ৩.৫-এ বানানো বান্ডল খোলা |
| `ClientCertificates = { clientCert }` | `s_client -cert fi-gateway.crt -key fi-gateway.key` — যাত্রা ৩/ধাপ ৪ |
| `CustomTrustStore.Add(serverCa)` + `chain.Build(...)` | `openssl verify -CAfile ca.crt …` — যাত্রা ২; আর যাত্রা ৪-এ evil এখানেই false খায় |
| `errors == SslPolicyErrors.None → true` | ল্যাবে ছিল না — প্রোডাকশনের জন্য (public CA-র server cert) |
| `Enabled=false → return` | ল্যাবে cert না দিয়ে s_client চালানো (ঢিলা দরজার rehearsal) |

## 1.4 .NET dev-এর জানা-রাখা টিপস (client পক্ষ)

- **`X509KeyStorageFlags` (Windows):** `MachineKeySet | PersistKeySet` মানে private key রাখা হবে machine key store-এ। শেয়ার্ড/লকড-ডাউন মেশিনে সমস্যা হলে `EphemeralKeySet` (disk-এ কিছু লেখে না) বিকল্প। লিনাক্স/কন্টেইনারে এসব flag-এর প্রভাব প্রায় নেই।
- **Cert লোড হয় একবার (startup-এ), request-এ না:** `X509Certificate2` instance সব connection share করে — এটাই ঠিক। প্রতি-request লোড = অপচয় + file lock ঝুঁকি।
- **Rotation মানে restart:** নতুন cert ফাইল বসালে পুরনো instance হাতেই থাকবে — config বদলে redeploy/restart। (পরে চাইলে cert-watch যোগ করা যায়।)
- **কখনোই `(_,_,_,_) => true` লিখবেন না** — ওটা "সব server-কে বিশ্বাস" লেখার আরেক নাম; দিন-শেষে কেউ নকল sbqr.api দাঁড় করালে ধরার উপায় থাকবে না।
- **Handler lifetime:** factory প্রতি ~২ মিনিটে handler নতুন করে — আপনার `ConfigurePrimaryHttpMessageHandler` factory তাই বারবার চালে; `clientCert` object তবু শেয়ার্ড (closure) — সমস্যা নেই।

---

# পক্ষ ২ — 🟢 Server: ASP.NET Core-এ (Kestrel) mTLS দিয়ে ঢুকতে দেওয়া (sbqr.api)

## 2.1 মূল কোড — Kestrel endpoint (আপনার repo-র আসল প্যাটার্ন)

**ল্যাব-এ:** `s_server -cert sbqr-api.crt -key sbqr-api.key -CAfile ca.crt -Verify 1`।
**.NET-এ:** সেই এক লাইনের সারমর্ম — Kestrel-এ একটা HTTPS endpoint, একটা cert, একটা নিয়ম।

```csharp
// Program.cs (sbqr.api) — MtlsOptions থেকে মান নিয়ে:
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // আগের HTTP endpoint অক্ষত — health/docs এখানে, certificate লাগে না
    kestrel.Listen(IPAddress.Loopback, mtls.HttpPort);                 // :5001

    kestrel.Listen(IPAddress.Loopback, mtls.HttpsPort, listen => listen.UseHttps(
        serverCertificate,                                            // ← ল্যাব-এর sbqr-api.pfx (ধাপ ২.৫)
        https =>
        {
            // s_server-এর "-Verify 1" এর .NET-রূপ: cert ছাড়া handshake-ই নেই (যাত্রা ৪-এর "cert নেই" মৃত্যু)
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

            // s_server-এর "-CAfile ca.crt" এর .NET-রূপ: আসা সব client-cert এই যাচাইয়ে যাবে
            https.ClientCertificateValidation = (clientCert, _, _) =>
                clientCertificateValidator.Validate(clientCert);
        }));
});
```

**`ClientCertificateMode`-এর তিন মান — কোনটা কখন:**

| মান | আচরণ | কখন |
|---|---|---|
| `NoClientCert` | কিছুই চায় না — শুধু সাধারণ HTTPS | mTLS না-লাগা endpoint |
| `AllowCertificate` | cert **চায়**, না দিলেও connection চলে; দিলে যাচাই হয় → `HttpContext.Connection.ClientCertificate` দেখে middleware ঠিক করে | ধীরে-ধীরে rollout, বা একই পোর্টে কিছু endpoint cert-মুক্ত রাখতে চাইলে |
| **`RequireCertificate`** | cert **দিতেই হবে + যাচাই পাস** — নইলে handshake মরে | আসল mTLS দরজা (এই প্রজেক্ট) |

## 2.2 মূল কোড — ClientCertificateValidator (যাত্রা ২ + ৪, কোডের ভেতরে)

```csharp
public class ClientCertificateValidator
{
    private readonly X509Certificate2 _ca;                    // ল্যাব-এর ca.crt — আমার একমাত্র trust anchor
    private readonly IReadOnlyCollection<string> _allowedThumbprints;  // ল্যাবের fingerprint কমান্ডের আউটপুট

    public bool Validate(X509Certificate2? clientCert)
    {
        if (clientCert is null) return false;

        // (ক) চেইন-যাচাই — ল্যাব-এর যাত্রা ২, হুবহু:
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509TrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(clientCert)) return false;           // যাত্রা ৪: evil-cert এখানেই মরে (false)

        // (খ) সনদ-যাচাই — ল্যাবের ext ফাইলে যে clientAuth লিখেছিলেন, সেটাই আসলে:
        var eku = clientCert.Extensions
            .OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Contains(new Oid("1.3.6.1.5.5.7.3.2"))) // clientAuth OID
            return false;                                     // "server-দের cert দিয়ে ঢুকতে হবে" ঠেকায়

        // (গ) ঐচ্ছিক কড়া নিয়ম — শুধু অনুমোদিত আঙুলের ছাপ:
        if (_allowedThumbprints.Count > 0
            && !_allowedThumbprints.Contains(clientCert.Thumbprint, StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
```

**ল্যাব-ম্যাপ:** (ক) = `openssl verify -CAfile ca.crt …`; (খ) = `extendedKeyUsage = clientAuth` লাইনটা; (গ) = `openssl x509 … -fingerprint -sha256`। আপনার repo-তে (গ)-এর তালিকাটা আরও স্মার্ট — `TenantCertificateThumbprintSyncService` নামের background service DB থেকে per-FI thumbprint sync করে, ফলে নতুন FI-র cert onboard করা = config edit না, একটা DB row।

## 2.3 Request-এর ভেতরে certificate ধরা — `HttpContext.Connection.ClientCertificate`

Handshake-এ পাওয়া client certificate প্রতিটা request-এর সাথে `HttpContext`-এ বসে থাকে — এটাই server-side কোডে cert "দেখার" একমাত্র জায়গা:

```csharp
// যেমন একটা সাধারণ logging middleware:
app.Use(async (ctx, next) =>
{
    var cert = ctx.Connection.ClientCertificate;      // যাচাই-পাস করা cert; RequireCertificate মোডে কখনো null না
    logger.LogInformation("mTLS peer: {Subject} ({Thumbprint})", cert?.Subject, cert?.Thumbprint);
    await next();
});

// বা per-request authorization হিসেবে — কার thumbprint, সে কোন tenant:
var tenant = await tenantRegistry.FindByThumbprint(ctx.Connection.ClientCertificate!.Thumbprint);
```

## 2.4 Server-পক্ষের জানা-রাখা টিপস

- **`ASPNETCORE_URLS`/launchSettings-এর সাথে সংঘর্ষ:** একবার `ConfigureKestrel`-এ `Listen(...)` লিখলে URL-ভিত্তিক endpoint-গুলো **বাতিল হয়ে যায়** — তাই দুটো পোর্টই (HTTP+HTTPS) কোডে লিখতে হয়। এই বাঁধনটাই ভুলে যাওয়ার কমন ভুল।
- **Health endpoint-এর নিরাপদ আশ্রয়:** health/docs আগের HTTP পোর্টে রাখাই সবচেয়ে সহজ — orchestrator-এর probe-এর কাছে কোনো cert নেই। (একই পোর্টে রাখতেই হলে `AllowCertificate` + middleware-exemption প্যাটার্ন।)
- **Reverse proxy সতর্কতা:** সামনে nginx/IIS/envoy TLS **terminate** করলে client-cert ওখানেই শেষ — Kestrel আর দেখে না। mTLS চাইলে proxy-তে SSL-passthrough বা client-cert-forward কনফিগ লাগবে; নিজেদের মতো সরাসরি Kestrel-এ আনাই সবচেয়ে সহজ ও নিরাপদ (এখানে একমাত্র client-ই gateway)।
- **Cert লোড startup-এ + fail-fast:** ফাইল না পেলে অর্থহীন crypto-error না — বলে দিন `Mtls:Enabled=true but ServerCertificatePath was not found: '<path>' — generate first (scripts/dev-certs-generate.sh)`। আপনার repo ঠিক এটাই করে।

---

# ৩. Config — দুই প্রজেক্টে key-গুলো যেভাবে যায়

`.NET-এর নিয়ম:` `Mtls__CertPath` (env var / `.env`) = `Mtls:CertPath` (appsettings section) — `__` মানে নেস্টিং।

```jsonc
// Gateway — appsettings বা env (আসল: appsettings.MtlsTesting.json)
{
  "Mtls": {
    "Enabled": true,
    "CertPath":      "…/dev-certs/fi-gateway.pfx",   // ল্যাব ধাপ ৩.৫
    "CertPassword":  "fi-gateway-dev",
    "ServerCaCertPath": "…/dev-certs/ca.crt"          // ল্যাব ধাপ ১.২ — চশমা
  }
}
```

```jsonc
// sbqr.api — appsettings বা repo-root .env
{
  "Mtls": {
    "Enabled": true,
    "HttpPort": 5001,
    "HttpsPort": 7443,
    "ServerCertificatePath":     "dev-certs/sbqr-api.pfx",  // ল্যাব ধাপ ২.৫
    "ServerCertificatePassword": "sbqr-dev",
    "CaCertificatePath":         "dev-certs/ca.crt",         // আবার সেই চশমা
    "AllowedClientThumbprints": []                            // খালি = শুধু চেইন+EKU; ভরলে = pinning
  }
}
```

Options-প্যাটার্ন সাজানোর নিয়ম (দুই repo-ই মানে): একটা options class (`MtlsOptions`) + `ValidateOnStart()` — ভুল config হলে app শুরুতেই মরবে, প্রথম কলে না।

---

# ৪. টেস্ট — .NET দিয়েই, ফাইল-ছাড়া

## 4.1 Unit স্তরে: যাত্রা ২ কোডে প্রমাণ (in-memory PKI)

ল্যাবে openssl দিয়ে যা করেছিলেন, .NET-এ `CertificateRequest` API দিয়ে — **কোনো ফাইল ছাড়াই**, তাই CI-safe:

```csharp
[Fact]
public void Client_cert_signed_by_our_ca_passes_chain_validation()
{
    // মিনি-CA বানান — ★ BasicConstraints CA:TRUE ছাড়া chain কখনো পাস করবে না (ল্যাব ধাপ ১.২-এর সেই কথা!)
    using var caRsa = RSA.Create(2048);
    var caReq = new CertificateRequest("CN=test-ca", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, false, null));
    var ca = caReq.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));

    // leaf বানান + CA সই করায় (ল্যাব ধাপ ৩.৪-এর in-memory রূপ)
    using var leafRsa = RSA.Create(2048);
    var leafReq = new CertificateRequest("CN=client", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var clientCert = leafReq.Create(ca, ca.NotBefore, ca.NotAfter, Guid.NewGuid().ToByteArray());

    // যাত্রা ২ — ঠিক সেই chain-walk:
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509TrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(ca);
    chain.Build(clientCert).Should().BeTrue();

    // আর যাত্রা ৪ — অচেনা CA-র cert:
    using var rogueRsa = RSA.Create(2048);
    var rogueReq = new CertificateRequest("CN=rogue-ca", rogueRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    rogueReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, false, null));
    var rogueCa = rogueReq.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    var rogueCert = new CertificateRequest("CN=attacker", RSA.Create(2048),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        .Create(rogueCa, rogueCa.NotBefore, rogueCa.NotAfter, Guid.NewGuid().ToByteArray());

    var sameChain = new X509Chain();
    sameChain.ChainPolicy.TrustMode = X509TrustMode.CustomRootTrust;
    sameChain.ChainPolicy.CustomTrustStore.Add(ca);           // আমার তালিকায় তো rogue-ca নেই
    sameChain.Build(rogueCert).Should().BeFalse();            // ← মৃত্যু, কোডে প্রমাণিত
}
```

## 4.2 Integration স্তরে — একটা .NET-জগতের বাঁধন জেনে রাখুন

**`WebApplicationFactory`/`TestServer` সত্যিকারের TLS করে না** — সে in-memory HTTP pipe, handshake-ই হয় না। তাই mTLS-এর আসল integration test চাইলে **আসল Kestrel socket** লাগে: ephemeral পোর্টে (`Listen(127.0.0.1, 0)`) ছোট্ট একটা `WebApplication` তুলে একই configurator/validator বসিয়ে দিন, তারপর `HttpClientHandler`-এ client-cert দিয়ে ঢুকুন — সফল/ব্যর্থ দুই রকমই assert করুন। আপনার দুই repo-র আসল test-suite এই প্যাটার্নেই চলে।

---

# ৫. Error-অভিধান — .NET exception দেখেই বুঝে যান কোন যাত্রা কোথায় ভাঙল

| দেখবেন | মানে | ল্যাব-এ যার নাম |
|---|---|---|
| `SslPolicyErrors.None` | সব ঠিক | `Verify return code: 0 (ok)` |
| `RemoteCertificateChainErrors` | চেইন anchor-এ পৌঁছায়নি — CA তালিকায় নেই / cert-এ `CA:TRUE` নেই / মেয়াদ শেষ | যাত্রা ২-এর ধাপ ২; evil-এর `unable to get local issuer certificate` |
| `RemoteCertificateNameMismatch` | সার্ভার cert-এর SAN-এ আপনার BaseUrl-এর নাম নেই | ল্যাব ধাপ ২.৩-এর SAN তালিকা |
| `RemoteCertificateNotAvailable` | সার্ভার certificate-ই পাঠায়নি | — |
| `AuthenticationException: … remote certificate …` (client পক্ষে) | ওপরের যেকোনো কারণে server-verify ফেল | যাত্রা ২, gateway-এর চশমায় সমস্যা |
| সার্ভার-লগে handshake alert, client-এ `HttpRequestException` | সার্ভার aapনার client-cert ফেল করল | যাত্রা ৪ |
| `Win32Exception` / "specified network password is not correct" | PFX password ভুল, বা Windows key-store পারমিশন | ধাপ ৩.৫-এর password |
| `FileNotFoundException` (startup-এ) | cert path ভুল/CWD ভুল | absolute path + forward-slash নিয়ম |

---

# ৬. তিন-কলাম মাস্টার-ম্যাপ — ল্যাব → .NET API → আপনার আসল ফাইল

| ল্যাব-এর জিনিস | .NET API / ধারণা | আসল ফাইল (repo) |
|---|---|---|
| `fi-gateway.pfx` (ধাপ ৩.৫) | `X509Certificate2(path, password)` → `ClientCertificates` | `rvl-sbqr-fi-gateway/src/…/Mtls/MtlsConfigurator.cs` |
| `ca.crt` — gateway-এর চশমা | `ServerCertificateCustomValidationCallback` + `CustomRootTrust` | একই ফাইল (`ServerCaCertPath`) |
| cert লোড/config | Options pattern + `ValidateOnStart` | `…/Mtls/MtlsOptions.cs` + `appsettings.MtlsTesting.json` |
| Bearer/timeout/retry (TLS-এর উপরে) | `AddHttpMessageHandler` chain | `…/Platform/PlatformAuthHandler.cs`, `…/Resilience/` |
| `sbqr-api.pfx` (ধাপ ২.৫) | Kestrel `UseHttps(serverCertificate)` | `rvl-secure-bqr-manager/src/Host/SBQR.Api/Mtls/MtlsEndpointsExtensions.cs` |
| `-Verify 1` | `ClientCertificateMode.RequireCertificate` | একই ফাইল |
| `-CAfile ca.crt` (server) | `ClientCertificateValidation` → validator | `…/Mtls/ClientCertificateValidator.cs` |
| fingerprint pinning | `AllowedClientThumbprints` | `…/Mtls/MtlsOptions.cs` + `TenantCertificateThumbprintSyncService.cs` (+ DB) |
| evil pair | negative tests | দুই repo-র `Mtls` test suites |
| handshake-এর ধাপ ৫-এর সাক্ষ্য | `HttpContext.Connection.ClientCertificate` | যেকোনো middleware/controller |

---

## শেষ কথা — দুই লাইনে পুরো .NET-গল্প

- **Gateway (client):** `HttpClient`-factory-র **primary handler**-এ cert বসান (`ClientCertificates`) + server-verify callback-এ নিজের CA pin করুন — ব্যস, বাকি সব TLS নিজে করে।
- **sbqr.api (server):** Kestrel-এ HTTPS endpoint + `RequireCertificate` + validation callback — আর `HttpContext.Connection.ClientCertificate` দিয়ে request-স্তরে পরিচয় ধরুন।

*doc-only deliverable — কোনো repo/code পরিবর্তন নেই। কোড-নমুনাগুলো আপনার দুই repo-র বর্তমান implementation (commit `ae111de`, `a9facee`, `e027dcf`) অনুসরণ করে — সর্বশেষ সত্যের উৎস repo-গুলোর নিজেদের ফাইল।*
