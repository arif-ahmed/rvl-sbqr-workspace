# লগিং পরিকল্পনা: Secure BQR Manager

> **অবস্থা:** প্রস্তাব, v3 (২০২৬-০৯-২৯)। v3 ম্যানেজারের উপর ফোকাস করে। এটি v2 কে প্রতিস্থাপন করে, যা FI Gateway কে সমমর্যাদার অ্যাপ হিসেবে বিবেচনা করত, এবং v1 কে, যা একটি shared repo প্লাস git submodules প্লাস একটি Serilog migration প্রস্তাব করেছিল।
> **পরিধি:** `rvl-secure-bqr-manager`, **যে product আমরা operate করি**। FI Gateway একটি ঐচ্ছিক reference app যা একটি FI নিজে deploy এবং run করতে বেছে নিতে পারে। আমরা এটি operate করি না এবং কখনো এর logs দেখি না, তাই এটি শুধুমাত্র একটি **ঐচ্ছিক appendix** দ্বারা কভার করা হয়েছে।
> **প্রচেষ্টা:** প্রায় **১ dev-day** (Part A প্রায় ০.৫ day, Part B প্রায় ০.৫ day)। Appendix প্রায় ০.৫ day যোগ করে, ঐচ্ছিক।
> **এরপর:** এই doc-এর নিচে **Part F** go-live upgrades কভার করে: CloudWatch-এ কেন্দ্রীয় logging, OpenTelemetry, এবং একটি support lookup page। **F1 production-এর জন্য release-blocking**, কারণ একটি security audit-এর এটি প্রয়োজন (§5.1)।
> **অনুমোদন:** logging BB BanglaQR spec-এর অংশ নয়। `AGENTS.md`-এর অধীনে ("Explicitly out of scope … add only via separate approved doc"), এই document সেই আলাদা doc। merge করার আগে এটি অনুমোদন করান।

---

## এখানে শুরু করুন (যদি আপনি এটি implement করছেন)

আপনার শুধু **Part A** এবং **Part B** দরকার। Appendix এবং Part F এড়িয়ে যান; সেগুলো পরের জন্য।

1. **§0–§2 একবার পড়ুন** (প্রায় ১৫ মিনিট)। এগুলো *কেন* ব্যাখ্যা করে; steps ধরে নেয় যে আপনি এটা জানেন।
2. **কোড স্পর্শ করার আগে:**
   - একটি branch তৈরি করুন।
   - **Docker Desktop** শুরু করুন। local database এবং integration tests-এর এটির প্রয়োজন।
   - সাধারণ পদ্ধতিতে local database শুরু করুন (repo `README` দেখুন)।
   - Build (**Ctrl+Shift+B**) এবং সব tests চালান (**Test → Run All Tests**)। শুরু করার *আগে* সবকিছু green থাকা উচিত, যাতে পরে যেকোন failure আপনারই হয়।
3. **Steps ক্রমানুসারে করুন**: A1 → A8, তারপর B1 → B4। প্রতিটি step-এর পরে: build করুন, step-এর **Check** করুন, এবং commit করুন। প্রতি step-এ একটি commit review সহজ করে।
4. **Testing-এর সময় §3 খোলা রাখুন।** এটি দেখায় সঠিক log line কেমন দেখতে হয়।

| Step | যে files আপনি স্পর্শ করেন | সময় |
|---|---|---|
| A1 | `Program.cs`, `appsettings.json`, `launchSettings.json` | ২০ মিনিট |
| A2 | `Program.cs` | ১৫ মিনিট |
| A3 | `CorrelationIdMiddleware.cs` | ২০ মিনিট |
| A4 | `Program.cs` | ১৫ মিনিট |
| A5 | নতুন `LogMask.cs` | ৫ মিনিট |
| A6 | `MtlsEndpointsExtensions.cs` | ৩০ মিনিট |
| A7 | কিছুই না (শুধু পড়ুন) | ৫ মিনিট |
| A8 | ২টি নতুন file, `CorrelationIdMiddleware.cs`, ২টি controllers | ১.৫ ঘণ্টা |
| B1–B4 | `QrFlowHostBuilder.cs`, ৩টি নতুন test files | ২-৩ ঘণ্টা |

**Logs দেখা:** **F5** দিয়ে API চালান; logs console window-তে দেখা যায়। Step A1 আপনাকে দুটি run profiles দেয়: readable text, বা server যে exact JSON print করে সেটি।
**Requests পাঠানো:** `/health/live`-এর জন্য একটি browser ব্যবহার করুন। QR endpoints-এর একটি token প্রয়োজন, তাই `http://localhost:5001/docs/public`-এ API explorer বা team-এর Postman collection ব্যবহার করুন।

---

## ০. TL;DR

আমরা **যতটুকু সম্ভব ছোট পরিবর্তনে ম্যানেজারের জন্য fintech-grade logging** চাই। এখানে fintech-grade মানে ছয়টি জিনিস:

1. **Machine-readable.** প্রতিটি log line stdout-এ লেখা একটি JSON object।
2. **খুঁজে পাওয়া যায়।** একটি request-এর সময় লেখা প্রতিটি line একটি **server-generated `correlation_id`** এবং caller-এর `tenant_id` বহন করে।
3. **পরিষ্কার।** কোন secrets নেই, কোন customer PII (personally identifiable information: names, phone এবং account numbers) নেই, কোন raw QR payloads নেই, কোন request bodies নেই। একটি CI test build fail করে দেয় যখন কোন কিছু leak হয়।
4. **একটি line পুরো গল্প বলে।** Framework noise Warning-এ রাখা হয়। প্রতিটি HTTP request ঠিক একটি **summary line** তৈরি করে (industry-এর "canonical log line") যা tenant, path, status, duration এবং business outcome (verdict, `payload_hash`) বহন করে। বেশিরভাগ investigation-এর শুধু সেই একটি line প্রয়োজন।
5. **Audit trail থেকে আলাদা।** `audit_logs` (hash-chained, PostgreSQL-এ) compliance record হিসেবেই থাকে। Application logs শুধুমাত্র debugging এবং operations-এর জন্য।
6. **একটি complaint থেকে traceable।** FI আমাদের যা দেয় (একটি error body, QR code, তাদের request ID, বা শুধু "প্রায় ৩টায়"), আমরা **আমাদের নিজস্ব database**-এ record খুঁজে পেতে পারি, এর `correlation_id` পেতে পারি, এবং এর জন্য প্রতিটি log line টানতে পারি। §4 হল playbook।

**ম্যানেজারকে স্বনির্ভর হতে হবে।** প্রতিটি investigation ম্যানেজারের নিজের database এবং logs থেকে উত্তরযোগ্য হতে হবে, আমরা control করি না এমন কোন FI system, gateway, বা log store-এর উপর কোন নির্ভরতা ছাড়াই।

**কোন নতুন repo নেই, কোন git submodules নেই, কোন shared library নেই, এখনো কোন OpenTelemetry নেই।** কাজটি `rvl-secure-bqr-manager`-এর ভিতরে কয়েকটি targeted edits।

> **Workspace root সম্পর্কে নোট।** `rvl-sbqr-workspace/` folder একটি personal convenience checkout। এটি build, CI, বা deploy-এর অংশ নয়, এবং এই plan-এর কিছুই এর উপর নির্ভর করে না। এই doc-এর team copy `rvl-secure-bqr-manager/docs/logging-plan.md`-এ থাকে।

---

## ১. Fintech-এ নতুন একজন dev-এর জন্য background

এই section একবার পড়ুন। Doc-এর পরের প্রতিটি step এই ideas-এর একটির উপর referred হয়।

### ১.১ Logs vs. audit trail

| | Application logs (এই plan) | Audit trail (`audit_logs` table) |
|---|---|---|
| উদ্দেশ্য | Debugging, operations, incident investigation | কে কি করেছে তার আইনি এবং compliance evidence |
| কোথায় | stdout, তারপর hosting platform-এর log viewer | PostgreSQL, hash-chained, append-only |
| Lines হারিয়ে যেতে পারে? | হ্যাঁ, মাঝে মাঝে। এটা গ্রহণযোগ্য। | না। এটাই table-এর মূল উদ্দেশ্য। |
| কে পড়ে | Developers এবং on-call engineers | Auditors, compliance, Bangladesh Bank |

**Rule:** কখনো একটি application log line-এর উপর নির্ভর করবেন না proof হিসেবে যে কিছু ঘটেছে। যদি এটা compliance-এর জন্য গুরুত্বপূর্ণ হয়, এটা `IAuditLogger`-এর মাধ্যমে যায়।

### ১.২ কেন PII এবং secrets logs-এর বাইরে থাকতে হবে

Database-এর চেয়ে অনেক বেশি লোক এবং systems logs পড়তে পারে: developers, log vendors, support staff, backups, chat-এ paste করা screenshots। একবার একটি secret বা customer-এর phone number একটি log-এ চলে গেলে, আপনি এটা reliably delete করতে পারবেন না। Card-industry rules (PCI DSS) card numbers-এ একই চিন্তা প্রয়োগ করে: সর্বোচ্চ প্রথম ৬ এবং শেষ ৪ digits দেখান। আমরা BanglaQR data-তে আরও কঠোর rule প্রয়োগ করি (§2 দেখুন)।

### ১.৩ Trust boundary: caller যা কিছু পাঠায় তা untrusted

ম্যানেজারকে **FI systems যা আমরা control করি না** call করে। Caller হতে পারে একটি FI-এর নিজস্ব backend, ঐচ্ছিক FI Gateway, বা সম্পূর্ণ অন্য কিছু। তাই তারা যে প্রতিটি header পাঠায় তা **attacker-controllable input**। Logging-এর জন্য, এর মানে:

| Identifier | কে তৈরি করে | Trusted? | আমরা কিভাবে ব্যবহার করি |
|---|---|---|---|
| `correlation_id` | **ম্যানেজার**, প্রতিটি request-এর জন্য একটি নতুন GUID (`CorrelationIdMiddleware`) | **হ্যাঁ**, এটা spoof করা যায় না | **প্রতিটি investigation-এর জন্য primary key।** এটা `audit_logs.correlation_id` এবং `qr_validations.correlation_id`-এও store করা হয়, এবং error bodies-এ `traceId` হিসেবে return করা হয়। |
| `caller_correlation_id` | FI, `X-Correlation-Id` header-এর মাধ্যমে | না, এটা একটি hint | Log করা হয় যাতে একটি FI বলতে পারে "**আমাদের** reference X দেখুন"। আমাদের নিজের ID হিসেবে কখনো ব্যবহার করা হয় না। |
| `request_id` (verify calls) | FI, request body-তে | না, কিন্তু এটা store করা হয় | `qr_validations`-এ store করা হয়, tenant-এ unique। |
| `TraceId` | ASP.NET Core; **যদি caller একটি `traceparent` header পাঠায় তবে সেটি থেকে continued** | না, এটা একটি hint | ফ্রি-তে log করা হয় (step A1) যাতে OpenTelemetry আসার পরে logs traces-এর সাথে join করা যায় (§8)। কখনো শুধু এটার উপর pivot করবেন না। |

**কেন ম্যানেজার caller-এর correlation ID কে নিজের হিসেবে গ্রহণ করে না।** যদি callers ID choose করতে পারত, দুটি সম্পর্কহীন requests একটি ID share করতে পারত এবং audit queries-এ একটি request-এর মতো দেখাতে পারত। এটি একটি spoofing risk। Server-generated ID `CorrelationIdMiddleware`-এ একটি deliberate design decision। **এটি পরিবর্তন করবেন না।**

### ১.৪ Structured logging, একটি example-এ

```csharp
// BAD: string interpolation. Values text-এ baked হয়ে যায়, তাই
// আপনি তাদের উপর filter করতে পারবেন না।
_logger.LogInformation($"QR issued for tenant {tenantId}");

// GOOD: message template. TenantId একটি আলাদা JSON field হয়ে যায় যা আপনি filter করতে পারেন।
_logger.LogInformation("QR issued for tenant {TenantId}", tenantId);

// BEST (ম্যানেজার ইতিমধ্যে এটা ২৩ জায়গায় করে): source-generated
// [LoggerMessage]। Compile-time checked, level disabled থাকলে zero-allocation, এবং
// এটার একটি stable EventId আছে।
[LoggerMessage(EventId = 7004, Level = LogLevel.Information,
    Message = "QR issuance accepted [{CorrelationId}] tenant {TenantId}")]
private static partial void LogQrIssued(ILogger logger, Guid correlationId, Guid tenantId);
```

**EventIds চিরকালের জন্য।** একবার `7004` মানে "QR issuance accepted" হয়ে গেলে, কখনো অন্য কিছুর জন্য reuse করবেন না। Searches এবং alerts এটার উপর নির্ভর করে।

### ১.৫ Log levels, যেভাবে আমরা ব্যবহার করি

| Level | ব্যবহার করুন | Production-এ? |
|---|---|---|
| `Trace` / `Debug` | Developer detail | Off |
| `Information` | সাধারণ business events (QR issued, QR verified, trust sync done) | On |
| `Warning` | কিছু অদ্ভুত যা আমরা handle করেছি (একটি rejected cert, একটি retry) | On |
| `Error` | একটি request বা operation ব্যর্থ হয়েছে | On, এবং এতে alert দিন |
| `Critical` | App তার কাজ করতে পারছে না (boot-এ DB-তে পৌঁছাতে পারে না) | On, এবং কাউকে page করুন |

**Masking level-dependent নয়।** একটি `Debug` line যা একটি secret leak করে তা এখনও একটি leak, কারণ একদিন কেউ production-এ Debug on করবে।

---

## ২. Rules: কি log করা যায় এবং কি করা যায় না

### ২.১ কখনো log করবেন না (যেকোন level-এ)

| Category | ম্যানেজারে examples |
|---|---|
| Credentials | `Authorization` header, JWT access tokens, `client_secret`, bootstrap secret, Argon2 hash, mTLS cert password, `Jwt:SigningKey` |
| Keys | Ed25519 private keys / PEM content (`-----BEGIN …`), key vault থেকে যেকোন কিছু |
| BanglaQR customer data | **Tag 59** (beneficiary name), **Tag 26.03** (account / wallet number, প্রায়ই একটি MSISDN), **full QR string** (এতে উভয়ই আছে), NID, phone numbers। `ValidateQrResponse.RecipientName` / `RecipientPan`-এ সাবধান থাকুন। |
| Bodies | HTTP request এবং response bodies, whole DTOs |
| DB parameters | EF Core `EnableSensitiveDataLogging()` কখনো একটি developer-এর machine-এর বাইরে on থাকা উচিত নয় |

### ২.২ Log করা ঠিক

| Field | কেন এটা ঠিক |
|---|---|
| `tenant_id`, institution code (`26.01+26.02`, যেমন `031008`), `key_version` | Business identifiers, একজন ব্যক্তি সম্পর্কে নয় |
| `correlation_id`, `caller_correlation_id`, `request_id`, `idempotency_key` | Request identifiers। তাদের কোনটিই credentials নয়, এবং তারাই সেই keys যা দিয়ে আপনি investigate করেন। Idempotency key ইতিমধ্যে `audit_logs.metadata`-তে লেখা হয়েছে। |
| `payload_hash` | QR string-এর SHA-256। এটি একটি QR code identify করে কোনো ব্যক্তির নাম না দেখিয়ে (§4)। এটাকে একটি *pseudonym* হিসেবে treat করুন, anonymous data নয়: যে ব্যক্তি ইতিমধ্যে QR-এর details জানে সে একটি guess confirm করতে পারে। আমাদের access-controlled logs-এর ভিতরে এটা ঠিক আছে; শুধু কখনো publish করবেন না। |
| `qr_type`, verdict, reason code, amount, MCC (`52`) | Transaction attributes, identity নয় |
| Certificate subject এবং SHA-256 thumbprint | Public certificate data, mTLS debugging-এর জন্য প্রয়োজন |
| HTTP method, path (query string ছাড়া), status, duration | Access log |

### ২.৩ যদি আপনার সত্যিই একটি log-এ একটি account identify করতে হয়

শুধু **শেষ ৪ characters** log করুন: `01711111111` → `***1111`। Step A5-এ যোগ করা helper ব্যবহার করুন, এবং কখনো নিজের substring logic inline লিখবেন না। Practice-এ আপনার খুব কমই এটির প্রয়োজন, কারণ `payload_hash` QR code identify করে (§4)।

---

## ৩. Output কেমন দেখাবে

দুই ধরনের line আছে। পার্থক্য জানাই আপনি যা কিছু trace করতে পারবেন তার অধিকাংশ।

| ধরন | প্রতি request-এ কতগুলো | কিসের জন্য |
|---|---|---|
| **Summary line** (steps A2 + A8) | **ঠিক একটি**, request শেষ হলে লেখা হয় | একটি line-এ পুরো গল্প: কে, কি, ফলাফল, কতক্ষণ, business outcome। **এখানে শুরু করুন।** |
| **Detail lines** | শূন্য বা তার বেশি | Business events (যেমন EventId 7001 "key not ACTIVE"), warnings, stack traces সহ exceptions। এগুলো শুধু তখন খুলুন যখন summary line যথেষ্ট নয় (উদাহরণস্বরূপ, status 500)। |

উভয় ধরনের একই `correlation_id` বহন করে, তাই আপনি সবসময় summary line থেকে তার detail lines-এ যেতে পারেন।

**একটি QR verification-এর জন্য summary line** (stdout-এ একটি line, এখানে wrapped এবং abbreviated):

```json
{"Timestamp":"2026-09-29T09:02:11.483Z","LogLevel":"Information",
 "Category":"Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware",
 "Message":"Request and Response: … Method: POST … Path: /v1/qr/validate … StatusCode: 200 … Duration: 14.2 …",
 "State":{"Method":"POST","Path":"/v1/qr/validate","StatusCode":200,"Duration":14.2,
          "correlation_id":"5f0c9a1e-…","caller_correlation_id":"fi-ref-8a3d","tenant_id":"3f1c…",
          "payload_hash":"9f2c…","verdict":"KEY_REVOKED","reason_code":"KEY_REVOKED","request_id":"fi-77812"},
 "Scopes":[
   {"TraceId":"4bf92f3577b34da6a3ce929d0e0e4736","SpanId":"00f067aa0ba902b7","ParentId":"…"},
   {"RequestId":"0HN9G…","RequestPath":"/v1/qr/validate"},
   {"correlation_id":"5f0c9a1e-…","caller_correlation_id":"fi-ref-8a3d"}]}
```

এটি পড়া: *FI tenant `3f1c…` (তাদের reference `fi-ref-8a3d`) QR `9f2c…` verify করেছে। আমরা ১৪ ms-এ 200 উত্তর দিয়েছি, এবং verdict ছিল `KEY_REVOKED`।* সাধারণত এটাই পুরো investigation।

### ৩.১ প্রতিটি field কোথায় পাবেন

| অর্থ | JSON field |
|---|---|
| **এটা কি summary line?** | `Category` = `Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware` |
| HTTP method / path / status / duration | `State.Method` / `State.Path` / `State.StatusCode` / `State.Duration` (summary line) |
| আমাদের request ID | `State.correlation_id` (summary line); `Scopes[].correlation_id` (প্রতিটি line) |
| FI-এর reference | `State.caller_correlation_id` (summary line); `Scopes[].caller_correlation_id` (প্রতিটি line) |
| Tenant | `State.tenant_id` (summary line); `Scopes[].tenant_id` (authentication-এর পরে detail lines) |
| QR এবং business outcome | `State.payload_hash`, `State.verdict`, `State.reason_code`, `State.request_id`, `State.qr_type` (summary line) |
| সময় (UTC) | `Timestamp` |
| Level | `LogLevel` |
| Human-readable message | `Message` |
| কোন class log করেছে | `Category` |
| Stable event number | `EventId` (detail lines, §4.4 দেখুন) |
| Exception + stack trace | `Exception` |
| Trace / span | `Scopes[].TraceId` / `SpanId` |

> **কেন summary fields-এর জন্য `State` এবং `Scopes` নয়:** `State` একটি JSON **object**, তাই `State.verdict`-এর সবসময় একই path থাকে এবং এটা filter করা সহজ। `Scopes` একটি **array** যার order পরিবর্তন হতে পারে। এটা IDs (`grep`) দ্বারা lines খুঁজতে ব্যবহার করুন, field দ্বারা filter করতে নয়।

---

## Part A: logging পরিবর্তন (প্রায় ০.৫ day)

### A1. Console output-কে JSON-এ switch করুন এবং trace IDs যোগ করুন

**File:** `src/Host/SBQR.Api/Program.cs`। **Ctrl+F** press করুন, `var builder = WebApplication.CreateBuilder(args);` খুঁজুন, এবং সরাসরি নিচে এটি paste করুন:

```csharp
using Microsoft.Extensions.Logging;

// ── Logging: একটি console provider; FORMAT configuration থেকে আসে ───
// appsettings.json "json" select করে (প্রতিটি server, dev EC2 box সহ,
// যা ASPNETCORE_ENVIRONMENT=Development চালায়)। শুধুমাত্র একটি developer-এর নিজের
// machine readable text-এ switch করে, launchSettings.json-এর মাধ্যমে।
builder.Logging.ClearProviders();
builder.Logging.AddConsole();   // Logging:Console:FormatterName + FormatterOptions পড়ে

// প্রতিটি log line-এ current Activity থেকে TraceId/SpanId রাখুন।
builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId |
    ActivityTrackingOptions.SpanId |
    ActivityTrackingOptions.ParentId);
```

**File:** `src/Host/SBQR.Api/appsettings.json`। পুরো `Logging` section এটি দিয়ে replace করুন। এটি ইতিমধ্যে step A2-এর জন্য log levels অন্তর্ভুক্ত করে, তাই আপনি এই file শুধু একবার edit করেন:

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning",
    "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware": "Information",
    "Microsoft.Hosting.Lifetime": "Information",
    "Microsoft.EntityFrameworkCore": "Warning",
    "System.Net.Http.HttpClient": "Warning"
  },
  "Console": {
    "FormatterName": "json",
    "FormatterOptions": {
      "IncludeScopes": true,
      "UseUtcTimestamp": true,
      "TimestampFormat": "yyyy-MM-ddTHH:mm:ss.fffZ"
    }
  }
},
```

**File:** `src/Host/SBQR.Api/Properties/launchSettings.json`। Visual Studio-কে দুটি run profiles দিন: daily কাজের জন্য readable text, এবং server ঠিক কি print করবে তা দেখতে JSON। এই file কখনো deploy হয় না। `"profiles"`-কে এর সাথে replace করুন:

```json
"profiles": {
  "SBQR.Api": {
    "commandName": "Project",
    "environmentVariables": {
      "ASPNETCORE_ENVIRONMENT": "Development",
      "Logging__Console__FormatterName": "simple",
      "Logging__Console__FormatterOptions__SingleLine": "true"
    },
    "dotnetRunMessages": true,
    "applicationUrl": "http://localhost:5001"
  },
  "SBQR.Api (JSON logs)": {
    "commandName": "Project",
    "environmentVariables": {
      "ASPNETCORE_ENVIRONMENT": "Development"
    },
    "dotnetRunMessages": true,
    "applicationUrl": "http://localhost:5001"
  }
},
```

> **কেন এটা যথেষ্ট (নতুন dev-এর জন্য):**
> - JSON console formatter ASP.NET Core-এর সাথে ship হয়, তাই কোন নতুন NuGet package নেই।
> - **কেন config এবং `if (IsDevelopment())` নয়:** shared dev EC2 server-ও `Development` হিসেবে চলে (`docs/deployments.md`)। একটি environment check সেই server-কে plain text দিতে পারত, এবং Part F-এর log shipping তখন এর lines parse করতে পারত না।
> - `ClearProviders()` default providers সরিয়ে দেয় যাতে প্রতিটি line একবার লেখা হয়, দুবার নয়।
> - stdout-এ লেখা পুরো contract। EC2-তে (systemd) logs `journalctl -u sbqr-api`-এ land করে, বা Part F শেষ হলে একটি file-এ। App এর যত্ন করে না কোনটি।
> - `TraceId` এখন কিছুই খরচ করে না, এবং যেদিন OpenTelemetry যোগ হয় (Part F2) সেদিন এটি logs-কে traces-এর সাথে join করবে।

**Check (Visual Studio-তে):**

1. Run dropdown-এ **SBQR.Api** pick করুন এবং **F5** press করুন। Console window readable single lines দেখায়।
2. Stop করুন, **SBQR.Api (JSON logs)** pick করুন, এবং আবার **F5** press করুন। প্রতিটি line এখন একটি JSON object। এটি ঠিক server যা print করে, কারণ server কোন launch profile ছাড়াই চলে।

---

### A2. Log levels tune করুন এবং request summary line যোগ করুন

**Log levels:** ইতিমধ্যে করা হয়েছে। Step A1-এর `appsettings.json` block এগুলো অন্তর্ভুক্ত করে। `Microsoft.AspNetCore` framework noise কমাতে Warning-এ set করা হয়েছে, এবং শুধুমাত্র HTTP logging category আবার on করা হয়েছে।

**File:** `src/Host/SBQR.Api/Program.cs`। অন্য `builder.Services.Add…` calls-এর পাশে HTTP logging register করুন (`var app = builder.Build();`-এর আগে যেকোন জায়গায়):

```csharp
using Microsoft.AspNetCore.HttpLogging;

builder.Services.AddHttpLogging(o =>
{
    // শুধু Allow-list। কখনো RequestHeaders, RequestQuery, RequestBody,
    // বা ResponseBody যোগ করবেন না: তারা Authorization, QR payloads, এবং PII বহন করে।
    o.LoggingFields = HttpLoggingFields.RequestMethod
                    | HttpLoggingFields.RequestPath
                    | HttpLoggingFields.ResponseStatusCode
                    | HttpLoggingFields.Duration;
    o.CombineLogs = true;   // প্রতি request-এ একটি line, দুটি নয়
});
```

তারপর **Ctrl+F** করুন `app.UseMiddleware<SBQR.Api.Infrastructure.CorrelationIdMiddleware>();` খুঁজতে এবং `app.UseHttpLogging();` **সরাসরি নিচে** যোগ করুন, যাতে summary line correlation scope-এর ভিতরে চলে:

```csharp
app.UseMiddleware<SBQR.Api.Infrastructure.CorrelationIdMiddleware>();
app.UseHttpLogging();
```

> **কেন:** "গত ঘণ্টায় যে প্রতিটি request 5xx return করেছে তা দেখান" সবচেয়ে সাধারণ operations প্রশ্ন। Field list একটি **allow-list**, তাই একটি ভবিষ্যত .NET version নীরবে headers log করা শুরু করতে পারবে না।
>
> এই line §3 থেকে **summary line**। A2 এটাকে HTTP facts দেয়; step A8 একই line-এ tenant, IDs এবং business outcome যোগ করে।

**Check:** **JSON logs** profile চালান এবং একটি browser-এ `http://localhost:5001/health/live` খুলুন। আপনি প্রতি request-এ একটি `HttpLoggingMiddleware` line দেখবেন, method, path, status code, এবং duration দেখাচ্ছে, কোন headers ছাড়াই। সেই line দেখুন এবং `State` key names (`Method`, `Path`, `StatusCode`, `Duration`) confirm করুন। §4 এবং Part F-এর queries এগুলো assume করে, এবং exact names .NET versions-এর মধ্যে সামান্য ভিন্ন হতে পারে।

---

### A3. correlation ID (এবং caller-এর reference) log scope-এ রাখুন

**File:** `src/Host/SBQR.Api/Infrastructure/CorrelationIdMiddleware.cs`। **server-side minting যেভাবে আছে সেভাবেই রাখুন।** `_next`-এর চারপাশে একটি log scope যোগ করুন:

```csharp
public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
{
    ArgumentNullException.ThrowIfNull(context);

    var correlationId = Guid.NewGuid();                 // unchanged: server-mint only
    context.Items[CorrelationContract.ItemsKey] = correlationId;

    if (Activity.Current is not null)                   // unchanged
    {
        Activity.Current.AddTag("correlation_id", correlationId);
        Activity.Current.SetBaggage("correlation_id", correlationId.ToString());
    }

    // NEW: এই request-এর সময় লেখা প্রতিটি log line correlation_id বহন করে।
    // Caller-এর X-Correlation-Id শুধুমাত্র DATA হিসেবে record করা হয় (আমাদের id হিসেবে
    // কখনো ব্যবহার করা হয় না) যাতে একটি FI আমাদের "তাদের" reference lookup করতে বলতে পারে।
    var scope = new Dictionary<string, object?> { ["correlation_id"] = correlationId };
    var callerId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (IsSafeId(callerId))
    {
        scope["caller_correlation_id"] = callerId;
        // Step A8 এখানে আরেকটি line যোগ করে। A8-এ না পৌঁছানো পর্যন্ত এটা বাদ রাখুন।
    }

    using (logger.BeginScope(scope))
    {
        await _next(context).ConfigureAwait(false);
    }
}

// Caller-controlled input: length এবং charset cap করুন যাতে কেউ fake log
// lines বা megabyte-দীর্ঘ values inject করতে না পারে।
private static bool IsSafeId(string? value) =>
    !string.IsNullOrEmpty(value)
    && value.Length <= 64
    && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
```

Class-এর `<remarks>` update করুন উল্লেখ করতে যে `caller_correlation_id` log করা হয় কিন্তু কখনো trust করা হয় না।

> **কেন একটি scope এবং প্রতিটি call-এ একটি parameter নয়:** `BeginScope` value-কে `using` block-এর ভিতরে লেখা **প্রতিটি** log line-এ attach করে, EF Core এবং প্রতিটি module থেকে lines সহ, ID pass না করেই।
>
> **কেন আমরা যে header শুধু log করি সেটা validate করি:** একটি FI যা কিছু পাঠায় তা untrusted input (§1.3)। JSON formatter quotes এবং newlines escape করে, কিন্তু একটি 1 MB header এখনও সেই request-এর প্রতিটি line bloat করবে।

**Check:** app চালান, তারপর একটি PowerShell window থেকে header সহ একটি request পাঠান:

```powershell
Invoke-WebRequest http://localhost:5001/health/live -Headers @{ 'X-Correlation-Id' = 'test-123' }
Invoke-WebRequest http://localhost:5001/health/live -Headers @{ 'X-Correlation-Id' = ('x' * 500) }   # খুব লম্বা: ignore করতে হবে
```

প্রথম call-এর জন্য, console `correlation_id` (একটি নতুন GUID) plus `caller_correlation_id: test-123` দেখায়। দ্বিতীয়টির জন্য, কোন `caller_correlation_id` নেই। ProblemDetails `traceId` এবং `audit_logs.correlation_id` check করা existing tests এখনও pass হতে হবে: ID এখনও server-এ তৈরি হয়।

---

### A4. `tenant_id` log scope-এ রাখুন (authentication-এর পরে)

Tenant শুধু JWT validate হওয়ার পরে জানা যায়। `Program.cs`-এ, **Ctrl+F** করুন `app.UseAuthentication();` খুঁজতে এবং এটি এবং `app.UseAuthorization();`-এর **মধ্যে** এটি রাখুন:

```csharp
app.UseAuthentication();

// পরবর্তী প্রতিটি log line-কে caller-এর tenant_id দিয়ে tag করুন (ICurrentTenant-এর
// মাধ্যমে JWT "tenant_id" claim থেকে)। Anonymous calls-এর জন্য no-op।
app.Use(async (ctx, next) =>
{
    var tenantId = ctx.RequestServices
        .GetRequiredService<SBQR.SharedKernel.Application.ICurrentTenant>().TenantId;
    if (tenantId == Guid.Empty)
    {
        await next(ctx);
        return;
    }

    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("SBQR.Api.TenantScope");
    using (logger.BeginScope(new Dictionary<string, object?> { ["tenant_id"] = tenantId }))
    {
        await next(ctx);
    }
});

app.UseAuthorization();
```

> **কেন `UseAuthentication`-এর পরে:** `JwtClaimCurrentTenant` তার প্রথম answer পুরো request-এর জন্য cache করে। যদি আপনি এটা authentication-এর আগে read করেন, এটি পুরো request-এর জন্য `Guid.Empty` cache করে।
>
> **কেন এটা গুরুত্বপূর্ণ:** "FI X-এর জন্য আজ কি ব্যর্থ হয়েছে?" `tenant_id`-এর উপর একটি single filter হয়ে যায়।

---

### A5. Masking helper যোগ করুন

**নতুন file:** `src/SharedKernel/SBQR.SharedKernel/Logging/LogMask.cs`

```csharp
namespace SBQR.SharedKernel.Logging;

/// <summary>
/// একটি account / wallet / phone number একটি log line-এ রাখার একমাত্র approved উপায়।
/// শেষ ৪ characters রাখে: "01711111111" → "***1111"।
/// docs/logging-plan.md §2.3 দেখুন।
/// </summary>
public static class LogMask
{
    public static string Last4(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 4 ? "***" : "***" + value[^4..];
}
```

এখনো কিছুই এটা ব্যবহার করে না। এটি বিদ্যমান তাই পরবর্তী developer যার এটির প্রয়োজন সে নিজের leaky version লিখবে না।

---

### A6. mTLS logger fix করুন, যা বর্তমানে উপরের সব কিছুকে bypass করে

**File:** `src/Host/SBQR.Api/Mtls/MtlsEndpointsExtensions.cs`। **Ctrl+F** করুন `LoggerFactory.Create` খুঁজতে।

আজ code একটি **private** logger factory build করে:

```csharp
var mtlsLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
```

সেই factory JSON format, log levels, এবং scopes ignore করে, তাই mTLS lines plain text হিসেবে বের হয়। mTLS lines ঠিক সেগুলো যা আপনার একটি FI onboarding incident-এর সময় প্রয়োজন। DI-কে validator build করতে দিয়ে fix করুন, এবং Kestrel-এর config callback-এর ভিতরে এটি resolve করুন:

```csharp
// Register: DI real ILogger<ClientCertificateValidator> supply করে।
builder.Services.AddSingleton(sp => new ClientCertificateValidator(
    caCertificate,
    mtls.AllowedClientThumbprints,
    sp.GetRequiredService<ILogger<ClientCertificateValidator>>()));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // ApplicationServices এখানে available: Kestrel options builder.Build()-এর পরে
    // resolve হয়।
    var validator = kestrel.ApplicationServices.GetRequiredService<ClientCertificateValidator>();

    kestrel.Listen(IPAddress.Loopback, mtls.HttpPort);
    kestrel.Listen(IPAddress.Loopback, mtls.HttpsPort, listen => listen.UseHttps(
        serverCertificate,
        https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => validator.Validate(certificate);
        }));
});
```

`mtlsLoggerFactory` line এবং পুরানো `AddSingleton(clientCertificateValidator)` delete করুন।

> **কেন এটা safe:** এটি এখনও একটি singleton। `TenantCertificateThumbprintSyncService` DI থেকে আগের মতো একই instance resolve করে।

**Check:** `dotnet test tests/SBQR.Mtls.Tests` pass হয়, এবং একটি rejected client cert একটি **JSON** `mTLS: rejected …` line তৈরি করে।

---

### A7. Audit-failure log (EventId 6001): কোন code পরিবর্তন নেই, একটি rule

`AuditLogger.LogAuditWriteFailed` `metadata` সহ পুরো entry log করে, তাই একজন operator database write ব্যর্থ হলে **log থেকে audit row replay করতে** পারে। সেই behaviour intentional; এটি রাখুন।

আজ প্রতিটি `metadata` payload শুধুমাত্র safe fields ধারণ করে: `correlation_id`, `reason_code`, `qr_type`, `institution_code`, `key_version`, `idempotency_key`, `verdict`, `issuing_tenant_id`।

**নতুন rule (এটি §6-এর PR checklist-এ যোগ করুন):** `AuditEntry.Metadata` কখনো §2.1 data ধারণ করা উচিত নয়, কারণ ব্যর্থতার ক্ষেত্রে এটি application log-এ যায়। Part B-তে CI leak test এটি enforce করে।

---

### A8. Summary line ("canonical log line") সম্পূর্ণ করুন, প্রায় ১.৫ ঘণ্টা

**কি:** এটি একটি pattern যা Stripe জনপ্রিয় করেছে, এবং অনেক industry এটি গ্রহণ করেছে: **প্রতি request-এ একটি rich line** যা সবকিছু গুরুত্বপূর্ণ বহন করে। Step A2 ইতিমধ্যে line-কে method, path, status এবং duration দেয়। A8 যোগ করে:

| Field | কোথা থেকে আসে | উত্তর দেয় |
|---|---|---|
| `correlation_id` | `CorrelationIdMiddleware` (A3) | এটা কোন request? |
| `caller_correlation_id` | FI-এর `X-Correlation-Id`, validated (A3) | এটা কোন request, **FI-এর কথায়**? |
| `tenant_id` | JWT, `ICurrentTenant`-এর মাধ্যমে | কোন FI? |
| `payload_hash` | generate / validate result | কোন QR code? (PII-free, নিচে দেখুন) |
| `verdict`, `reason_code` | validate result | আমরা কি সিদ্ধান্ত নিয়েছি, এবং কেন? |
| `request_id` | validate request (FI-এর ID) | কোন verify call, FI-এর কথায়? |
| `qr_type` | generate result | Static নাকি dynamic? |

**`payload_hash` কি:** exact QR string-এর lowercase hex SHA-256। উভয় APIs ইতিমধ্যে এটি return করে, এবং `audit_logs.resource_id` প্রতিটি issuance এবং প্রতিটি verification-এর জন্য এটি store করে। যে কেউ QR code ধারণ করে সে এটি recompute করতে পারে, এবং এটি account holder সম্পর্কে কিছুই প্রকাশ করে না। এটি এটাকে **একটি specific QR code-এর জন্য সেরা PII-free handle** করে তোলে (§4)।

**এটি কিভাবে কাজ করে।** চারটি ছোট piece আছে, এবং প্রতিটির একটি কাজ আছে:

```
 Controller                         SummaryLineInterceptor                 Summary line (A2)
 SAFE business fields         ──►    সেগুলো copy করে, plus         ──►     সব গুরুত্বপূর্ণ সহ
 HttpContext.Items-এ রাখে           correlation_id এবং tenant_id          একটি JSON line
                                     line-এ
```

**১. Allow-list।** `src/SharedKernel/SBQR.SharedKernel/Web/SummaryLineFields.cs` তৈরি করুন। এটি সেই folder-এ `CorrelationContract.cs`-এর মতো একই pattern অনুসরণ করে: SharedKernel-এর কোন ASP.NET Core dependency নেই, তাই এটি শুধু names ধারণ করে।

```csharp
namespace SBQR.SharedKernel.Web;

/// <summary>
/// একমাত্র business fields যা per-request summary log line-এ যোগ করা যেতে পারে
/// (docs/logging-plan.md step A8)। Controllers সেগুলো HttpContext.Items-এ set করে;
/// SummaryLineInterceptor সেগুলো line-এ copy করে।
/// একটি allow-list হওয়াটাই মূল বিষয়: একজন reviewer প্রতিটি field দেখতে পারেন যা
/// কখনো appear করতে পারে, এবং তাদের কোনটিই §2.1 data (names, account numbers,
/// QR strings, secrets) বহন করতে পারে না।
/// </summary>
public static class SummaryLineFields
{
    public const string Prefix = "log.";

    public const string CallerCorrelationId = Prefix + "caller_correlation_id";
    public const string PayloadHash         = Prefix + "payload_hash";
    public const string Verdict             = Prefix + "verdict";
    public const string ReasonCode          = Prefix + "reason_code";
    public const string RequestId           = Prefix + "request_id";
    public const string QrType              = Prefix + "qr_type";

    public static readonly string[] All =
        { CallerCorrelationId, PayloadHash, Verdict, ReasonCode, RequestId, QrType };
}
```

**২. Interceptor।** `src/Host/SBQR.Api/Infrastructure/SummaryLineInterceptor.cs` তৈরি করুন। `IHttpLoggingInterceptor` .NET 8+-এ built-in এবং HTTP log line-এ fields যোগ করার জন্যই বিদ্যমান।

```csharp
using Microsoft.AspNetCore.HttpLogging;
using SBQR.SharedKernel.Application;
using SBQR.SharedKernel.Web;

namespace SBQR.Api.Infrastructure;

/// <summary>
/// one-line-per-request HTTP log (step A2) কে একটি "canonical log
/// line"-এ পরিণত করে: correlation_id, tenant_id এবং <see cref="SummaryLineFields"/>
/// থেকে allow-listed business fields যোগ করে। docs/logging-plan.md step A8 দেখুন।
/// </summary>
internal sealed class SummaryLineInterceptor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext) => ValueTask.CompletedTask;

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext)
    {
        var http = logContext.HttpContext;

        if (http.Items[CorrelationContract.ItemsKey] is Guid correlationId)
            logContext.AddParameter("correlation_id", correlationId);

        var tenantId = http.RequestServices.GetService<ICurrentTenant>()?.TenantId ?? Guid.Empty;
        if (tenantId != Guid.Empty)
            logContext.AddParameter("tenant_id", tenantId);

        foreach (var key in SummaryLineFields.All)
        {
            if (http.Items[key] is string value)
                logContext.AddParameter(key[SummaryLineFields.Prefix.Length..], value);   // "log.verdict" → "verdict"
        }

        return ValueTask.CompletedTask;
    }
}
```

`Program.cs`-এ, step A2 থেকে `AddHttpLogging`-এর পাশে register করুন:

```csharp
builder.Services.AddHttpLoggingInterceptor<SBQR.Api.Infrastructure.SummaryLineInterceptor>();
```

**৩. `CorrelationIdMiddleware`-এ একটি line** (placeholder comment যা আপনি step A3-এ রেখেছেন)। Comment-কে এর সাথে replace করুন:

```csharp
        context.Items[SummaryLineFields.CallerCorrelationId] = callerId;   // summary line-এর জন্য
```

**৪. Controllers business fields fill করে।** উভয় controllers-এ `using SBQR.SharedKernel.Web;` যোগ করুন।

`src/Modules/Verification/SBQR.Modules.Verification.Api/Controllers/QrValidationController.cs`, `ValidateAsync`-এ। request ID একটি variable-এ pull করুন যাতে line প্রকৃতপক্ষে ব্যবহৃত value log করে; controller যখন FI কিছুই পাঠায় না তখন একটি generate করে:

```csharp
var requestId = request.RequestId ?? Guid.NewGuid().ToString("N");

var result = await _mediator.Send(
    new ValidateQrCommand(
        request.QrPayload,
        requestId,
        request.RequestTimestamp ?? DateTimeOffset.UtcNow),
    cancellationToken);

// Summary line (docs/logging-plan.md A8)। শুধু Safe fields:
// কখনো RecipientName / RecipientPan / QrPayload নয়।
HttpContext.Items[SummaryLineFields.PayloadHash] = result.PayloadHash;
HttpContext.Items[SummaryLineFields.Verdict]     = result.Verdict.ToString();
HttpContext.Items[SummaryLineFields.ReasonCode]  = result.ReasonCode;
HttpContext.Items[SummaryLineFields.RequestId]   = requestId;

return Ok(new ValidateQrResponse( /* unchanged */ ));
```

`src/Modules/QrGeneration/SBQR.Modules.QrGeneration.Api/Controllers/QrGenerationController.cs`, `ToActionResult`-এ, success branch (একটি জায়গা static এবং dynamic উভয় cover করে):

```csharp
if (result.IsSuccess)
{
    // Summary line (docs/logging-plan.md A8)।
    HttpContext.Items[SummaryLineFields.PayloadHash] = result.Value.PayloadHash;
    HttpContext.Items[SummaryLineFields.QrType]      = result.Value.QrType;

    return StatusCode(StatusCodes.Status201Created, new GenerateQrResponse( /* unchanged */ ));
}
```

Generation **failures**-এর কোন extra field প্রয়োজন নেই। Summary line-এ status code ইতিমধ্যে কেন বলে: 403 মানে tenant not active, 422 মানে key not ACTIVE, 409 মানে duplicate idempotency key, এবং 500 মানে signing বা generation failed। একই `correlation_id` (EventIds 7000–7003)-এর জন্য detail line-এ specifics আছে।

> **কেন এই design (নতুন dev-এর জন্য):**
> - **Controllers, handlers নয়, fields fill করে।** Application layer-কে `HttpContext`-এর উপর নির্ভর করতে হবে না (architecture tests এটি enforce করে)। Controller thin HTTP edge।
> - **`HttpContext.Items`** ASP.NET Core-এ built-in per-request storage। পরে clean up করার কিছুই নেই।
> - **একটি allow-list, "যা কিছু log করুন" নয়।** একটি নতুন field `SummaryLineFields` edit না করে line-এ পৌঁছাতে পারে না, যা একজন reviewer লক্ষ্য করবেন।
> - **মনে রাখার কোন নতুন EventId নেই।** এটি A2-এর মতো একই line, শুধু richer। এটাকে এর `Category` দ্বারা খুঁজুন (§3.1)।
> - **Rejected QRs এখনও status 200।** verify endpoint প্রতিটি verdict-কে একটি normal result হিসেবে return করে, তাই একটি complete verification-এর জন্য line-এ সবসময় একটি verdict থাকে। একটি 400 মানে request নিজেই invalid ছিল, তাই কোন verdict নেই।

> **সাবধান:** `ValidateQrResponse`-ও `RecipientName` (Tag 59) এবং `RecipientPan` (Tag 26.03) ধারণ করে। এগুলো summary line-এ যোগ করার জন্য tempting। **করবেন না।** এগুলো §2.1 data, এবং যদি করেন Part B canary test fail হবে।

**Check:** QR endpoints-এর একটি bearer token প্রয়োজন। team-এর Postman collection ব্যবহার করুন (যদি না থাকে `postman-export` skill দিয়ে generate করুন), অথবা `http://localhost:5001/docs/public`-এ API explorer। **JSON logs** profile ব্যবহার করে console দেখুন।

| Call | প্রত্যাশিত summary line |
|---|---|
| একটি valid QR verify করুন | Status 200 `verdict=VALID`, `payload_hash`, `request_id`, `correlation_id`, `tenant_id` সহ |
| একটি tampered QR verify করুন | Status 200 `verdict=INVALID_SIGNATURE` (বা অনুরূপ) এবং একটি `reason_code` সহ |
| একটি static QR generate করুন | Status 201 `payload_hash`, `qr_type`, `tenant_id` সহ |
| Token ছাড়া যেকোন call | Status 401, `correlation_id` সহ এবং কোন `tenant_id` বা business fields নেই |
| `X-Correlation-Id: test-1` সহ যেকোন call | `caller_correlation_id=test-1` |

---

## Part B: CI leak test ("canary"), প্রায় ০.৫ day

.NET built-in logger-এর কোন hook নেই যা প্রতিটি log line লেখার আগে scrub করতে পারে, তাই ম্যানেজার developers কখনো logger-এ sensitive values pass না করার উপর নির্ভর করে। একটি **canary test** এটি প্রমাণ করে। এটি real QR flow চালায় ভুলে-চেনা fake values (একটি fake name, একটি fake account number) দিয়ে, প্রতিটি log line capture করে, এবং যদি সেই values-এর কোনটি appear করে তাহলে build fail করে।

> **Part B শুরু করার আগে:** QR integration tests Docker-এ (Testcontainers) একটি real PostgreSQL-এর বিরুদ্ধে চলে। **Docker Desktop চলতে হবে**, অন্যথায় tests Docker connection error দিয়ে startup-এ fail হয়।
>
> **এই tests কিভাবে কাজ করে:** তারা HTTP-এর মাধ্যমে যায় না। `QrFlowHostBuilder` real services build করে, এবং test MediatR-এর মাধ্যমে command handlers call করে। `tests/SBQR.Qr.IntegrationTests/QrRoundTripTests.cs` দেখুন: canary test হল fake values এবং একটি extra log capture সহ এর setup-এর একটি copy।

### B1. Tests-এর জন্য একটি capturing logger

**নতুন file:** `tests/SBQR.Qr.IntegrationTests/Infrastructure/CapturingLoggerProvider.cs` (**Solution Explorer**-এ: `Infrastructure` folder-এ right-click → **Add → Class**)

```csharp
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SBQR.Qr.IntegrationTests.Infrastructure;

/// প্রতিটি log line (message + structured state + scopes) memory-তে capture করে।
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string category) => new Capture(this, category);
    public void SetScopeProvider(IExternalScopeProvider scopes) => _scopes = scopes;
    public void Dispose() { }

    private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel level) => true;   // Debug/Trace ও capture করুন

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            var sb = new StringBuilder().Append(level).Append(' ').Append(category).Append(' ')
                .Append(formatter(state, ex)).Append(' ').Append(ex);
            Append(sb, state);
            owner._scopes.ForEachScope((s, b) => Append(b, s), sb);
            owner.Lines.Enqueue(sb.ToString());
        }

        private static void Append(StringBuilder sb, object? state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
                foreach (var kv in kvs) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            else
                sb.Append(' ').Append(state);
        }
    }
}
```

### B2. একটি test-কে test host-এ তার নিজস্ব log capture যোগ করতে দিন

**File:** `tests/SBQR.Qr.IntegrationTests/Infrastructure/QrFlowHostBuilder.cs`। `Build`-এ একটি **optional** parameter যোগ করুন, এবং service provider build-এর ঠিক আগে এটি call করুন। বিদ্যমান tests unchanged চলতে থাকে।

```csharp
public (ServiceProvider Services, MutableCurrentTenant CurrentTenant, CapturingAuditLogger Audit)
    Build(PostgreSqlFixture postgres, Action<IServiceCollection>? configure = null)   // ← নতুন parameter
{
    // … ইতিমধ্যে এখানে সবকিছু একই থাকে …

    configure?.Invoke(services);   // ← নতুন: একটি test-কে যেমন একটি log capture যোগ করতে দেয়

    return (services.BuildServiceProvider(validateScopes: true), currentTenant, audit);
}
```

### B3. Canary test

**নতুন file:** `tests/SBQR.Qr.IntegrationTests/LogLeakCanaryTests.cs`। Setup (tenant + signing key) `QrRoundTripTests` থেকে copy করা হয়েছে; শুধু fake values এবং capture নতুন।

```csharp
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SBQR.Modules.KeyCustody.Application.Commands.GenerateOrAdoptCryptoKey;
using SBQR.Modules.QrGeneration.Application.Commands.GenerateStaticQr;
using SBQR.Modules.Tenancy.Application.Commands.CreateTenant;
using SBQR.Modules.Verification.Application.Commands;
using SBQR.Qr.IntegrationTests.Infrastructure;
using Xunit;

namespace SBQR.Qr.IntegrationTests;

/// <summary>
/// Log-leak canary (docs/logging-plan.md Part B): ভুলে-চেনা fake customer
/// data দিয়ে real QR flow চালায় এবং যদি এর কোনটি একটি log line-এ পৌঁছায় তাহলে fail হয়।
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogLeakCanaryTests : IAsyncLifetime, IDisposable
{
    // ভুলে-চেনা fake values। যদি এর কোনটি একটি log line-এ দেখা যায়, এটা leak হয়েছে।
    private const string CanaryName    = "CANARYNAMEZX";   // Tag 59
    private const string CanaryAccount = "01799999917";    // Tag 26.03

    private readonly PostgreSqlFixture _postgres;
    private readonly QrFlowHostBuilder _hostBuilder = new();

    public LogLeakCanaryTests(PostgreSqlFixture postgres) => _postgres = postgres;
    public Task InitializeAsync() => _postgres.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _hostBuilder.Dispose();

    [Fact]
    public async Task Customer_data_never_reaches_the_logs()
    {
        var capture = new CapturingLoggerProvider();
        var (services, currentTenant, _) = _hostBuilder.Build(_postgres, s =>
            s.AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace)));

        string qr;
        await using (services)
        await using (var scope = services.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<ISender>();

            // Setup: QrRoundTripTests steps 1–2-এর মতোই।
            var tenant = await mediator.Send(new CreateTenantCommand(
                InstitutionName: "ACME Bank", InstitutionCode: "031008"));
            var tenantId = tenant.Value.TenantId.Value;
            (await mediator.Send(new GenerateOrAdoptCryptoKeyCommand(
                TenantId: tenantId, Mode: CryptoKeyMode.Generate))).IsSuccess.Should().BeTrue();

            // fake customer data সহ একটি QR issue করুন।
            currentTenant.TenantId = tenantId;
            var generated = await mediator.Send(new GenerateStaticQrCommand(
                RecipientName: CanaryName, RecipientCity: "Dhaka", RecipientPan: CanaryAccount));
            generated.IsSuccess.Should().BeTrue(generated.ErrorMessage);
            qr = generated.Value.QrPayload;

            // এটি verify করুন (success path), তারপর একটি corrupted copy (rejection path)।
            await mediator.Send(NewValidateCommand(qr));
            await mediator.Send(NewValidateCommand(qr[..^4] + "0000"));   // CRC ভাঙে
        }

        var all = string.Join('\n', capture.Lines);
        capture.Lines.Should().NotBeEmpty("capture-কে সত্যিই wired up হতে হবে");
        all.Should().NotContain(CanaryName);
        all.Should().NotContain(CanaryAccount);
        all.Should().NotContain(qr);                  // full QR string
        all.Should().NotContain("-----BEGIN");        // যেকোন PEM / private key
    }

    private static ValidateQrCommand NewValidateCommand(string qrPayload) =>
        new(qrPayload, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}
```

**Test Explorer** থেকে চালান (Test → Test Explorer, `LogLeakCanary` search করুন), অথবা `dotnet test --filter LogLeakCanary` দিয়ে।

> **কেন `LogLevel.Trace`:** এটি `Debug` এবং `Trace` lines-কেও produce করতে বাধ্য করে। Debug-এ একটি leak এখনও একটি leak (§1.5 দেখুন)।
>
> **যদি প্রথম run-এ test fail হয়:** ভাল, এটাই এটার জন্য। Failure message value ধারণকারী line দেখায়। সেই log call fix করুন (value সরিয়ে ফেলুন, বা `LogMask.Last4` ব্যবহার করুন), এবং কখনো test-কে weaken করবেন না।
>
> **কেন `NotBeEmpty` প্রথমে:** যদি capture wrong wire-up হয়, "কোন line canary ধারণ করে না" trivially pass হবে। সেই assertion প্রমাণ করে test সত্যিই logs দেখছে।

### B4. summary-line allow-list লক করুন

Canary test handlers চালায়, HTTP নয়, তাই এটি step A8 থেকে summary line দেখে না। সেই line পরিবর্তে `SummaryLineFields` allow-list দ্বারা protected। এই ছোট test সেই list-এ যেকোন পরিবর্তনকে একটি deliberate, reviewed decision করে:

**নতুন file:** `tests/SBQR.Qr.IntegrationTests/SummaryLineFieldsTests.cs`

```csharp
using FluentAssertions;
using SBQR.SharedKernel.Web;
using Xunit;

namespace SBQR.Qr.IntegrationTests;

public sealed class SummaryLineFieldsTests
{
    // যদি আপনি একটি field যোগ করেন, এই test ON PURPOSE fail হয়। নিচে list update করুন
    // এবং reviewer-কে নতুন field §2.2-safe (docs/logging-plan.md) confirm করতে বলুন:
    // কখনো একটি name, account number, QR string বা secret নয়।
    [Fact]
    public void Only_approved_fields_can_reach_the_summary_line() =>
        SummaryLineFields.All.Should().BeEquivalentTo(
            "log.caller_correlation_id", "log.payload_hash", "log.verdict",
            "log.reason_code", "log.request_id", "log.qr_type");
}
```

**Part B done হয় যখন** উভয় নতুন tests Test Explorer-এ green হয়, এবং পুরো suite (`dotnet test`) এখনও green থাকে।

---

## ৪. একটি complaint trace করা (playbook)

**এক বাক্যে idea: আমাদের database হল index, এবং আমাদের logs হল detail।**

আমরা কখনো একটি customer-এর name বা account number দিয়ে logs search করি না। সেই values logs-এ নেই, by design (§2)। বরং, আমরা FI আমাদের যা দেয় সেটাকে **আমাদের** database-এর বিরুদ্ধে একটি SQL query দিয়ে একটি `correlation_id`-এ পরিণত করি, তারপর সেই request-এর **summary line** দেখি (§3)। শুধু যদি summary line যথেষ্ট না হয় তবে আমরা detail lines টানি। কিছুই FI-এর systems বা gateway-এর উপর নির্ভর করে না।

> **Shortcut:** নিচে anchors **B**, **C** এবং **D**-এর জন্য, আপনি SQL এড়িয়ে সরাসরি summary line খুঁজতে পারেন, কারণ এটি `payload_hash`, `request_id` এবং `caller_correlation_id` বহন করে (§4.2)।

```
 Complaint ──► ১. FI আমাদের কি দিয়েছে? ──► ২. correlation_id পান ──► ৩. প্রতিটি log line টানুন
                  ("anchor")                  (একটি SQL query)              (একটি log search)
```

### ৪.১ Steps 1 + 2: anchor থেকে একটি `correlation_id`-এ

| # | FI আপনাকে কি দেয় | কিভাবে `correlation_id` পাবেন |
|---|---|---|
| **A** | আমরা যে **error body** return করেছি | এর `traceId` field **হচ্ছে** আমাদের `correlation_id`। সরাসরি step 3-এ যান। |
| **B** | **QR code নিজেই**: একটি photo, একটি image, বা scanned string। এটি সবচেয়ে সাধারণ case। | String পেতে এটি scan করুন, এর `payload_hash` compute করুন, এবং নিচে query B চালান। আপনি issuance **এবং সেই exact QR-এর প্রতিটি verification attempt** পাবেন। |
| **C** | একটি verify call-এর জন্য তাদের **`request_id`** | নিচে query C চালান। `verdict` এবং `reason_code` প্রায়ই logs না খুলেই complaint-এর উত্তর দেয়। |
| **D** | **তাদের নিজস্ব reference** (`X-Correlation-Id` যা তারা আমাদের পাঠিয়েছে) | `caller_correlation_id = <ref>` সহ summary line খুঁজুন। এটি আমাদের `correlation_id` এবং outcome দেখায়। |
| **E** | শুধু **"FI X, প্রায় ৩টায়, এটি ব্যর্থ হয়েছে"** | Query E চালান (tenant + time window) এবং failing row বাছাই করুন, অথবা window-এ status ≥ 400 সহ সেই tenant-এর summary lines list করুন (§4.2)। |

```sql
-- B. QR code থেকে।
--    payload_hash = EXACT QR string-এর lowercase hex SHA-256 (কোন trailing newline নেই):
--      bash:        printf '%s' '<qr string>' | sha256sum
--      PowerShell:  (Get-FileHash -Algorithm SHA256 -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('<qr string>')))).Hash.ToLower()
SELECT created_at,
       event_type,                              -- qr.generated | qr.validated | qr.validation.rejected
       correlation_id,
       metadata::jsonb ->> 'verdict'     AS verdict,
       metadata::jsonb ->> 'reason_code' AS reason_code,
       tenant_id                                -- কে issue করেছে / কে scan করেছে
FROM   audit_logs
WHERE  resource_id = '<payload_hash>'
ORDER  BY created_at;

-- C. FI-এর verification request_id থেকে।
SELECT created_at, verdict, reason_code, institution_code, correlation_id
FROM   qr_validations
WHERE  request_id = '<request_id>';

-- E. শুধু "FI X, প্রায় ৩টায়"। প্রথমে institution code-কে একটি tenant_id-এ পরিণত করুন,
--    তারপর window-এ কি ঘটেছে তা list করুন।
SELECT tenant_id, institution_name FROM tenants WHERE institution_code = '<যেমন 031008>';

SELECT created_at, event_type, resource_id, correlation_id, metadata
FROM   audit_logs
WHERE  tenant_id = '<tenant_id>'
  AND  created_at BETWEEN '<from, UTC>' AND '<to, UTC>'
ORDER  BY created_at;
```

> **Time zones:** database এবং logs উভয়ই **UTC**-তে। Dhaka-তে "৩টা" (UTC+6) সম্পর্কে একটি complaint মানে প্রায় **09:00 UTC**। এটি "আমি কিছুই খুঁজে পাচ্ছি না"-এর সবচেয়ে সাধারণ কারণ।

### ৪.২ Step 3: summary line পড়ুন, তারপর প্রয়োজনে details

কেন্দ্রীয় logging (Part F1) না থাকা পর্যন্ত, logs server-এ থাকে, এবং `grep` plus `jq` যথেষ্ট। **সবসময় summary line দিয়ে শুরু করুন।** `jq '.State'` শুধুমাত্র এর useful part print করে।

```bash
# (1) একটি request-এর জন্য summary line
journalctl -u sbqr-api --since "2026-09-29 08:30" --until "2026-09-29 09:30" -o cat \
  | grep '"correlation_id":"<correlation_id>"' | jq 'select(.State.StatusCode) | .State'

# (2) একটি QR code-এর জন্য প্রতিটি summary line (anchor B, কোন SQL প্রয়োজন নেই)
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | grep '"payload_hash":"<payload_hash>"' | jq '.State'

# (3) FI-এর নিজস্ব reference-এর জন্য summary line (anchor D)
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | grep '"caller_correlation_id":"<fi-ref>"' | jq 'select(.State.StatusCode) | .State'

# (4) একটি window-এ একটি FI-এর জন্য সমস্ত failed requests (anchor E)
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | jq -c 'select(.State.tenant_id == "<tenant_id>" and .State.StatusCode >= 400) | .State'

# (5) একটি request-এর জন্য DETAIL lines: exceptions সহ প্রতিটি line।
#     এটি শুধু ব্যবহার করুন যখন summary line যথেষ্ট নয় (যেমন status 500)।
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | grep '<correlation_id>' | jq .
```

> **কেন `select(.State.StatusCode)`:** FI-এর reference এবং আমাদের `correlation_id` প্রতিটি detail line-এর `Scopes`-এও appear করে। শুধুমাত্র summary line-এ `State.StatusCode` আছে, তাই এটি শুধু সেই একটি line রাখে। **Part F1 done হলে**, আপনি SSH ব্যবহার বন্ধ করেন: এর প্রতিটি একটি saved CloudWatch Logs Insights query হয়ে যায় (F1.7), এবং fields একই থাকে।

### ৪.৩ Worked example

> *FI 031008 থেকে complaint: "আমাদের customer বলছে আজ প্রায় ৩টায় একটি দোকানের QR reject হয়েছে।"*

1. **Anchor:** FI-কে QR image চান (anchor **B**)। এটি scan করুন এবং hash compute করুন।
2. **Query B** দুটি rows return করে:
   - `2026-09-20 04:10Z  qr.generated`: QR নয় দিন আগে issue করা হয়েছিল।
   - `2026-09-29 09:02Z  qr.validation.rejected  verdict=KEY_REVOKED`: Dhaka time 15:02-এ reject হয়েছে।
3. **এটাই উত্তর।** Issuer-এর signing key revoked হয়েছিল, এবং Annex B "reject & block" প্রয়োজন। System intended-এর মতো কাজ করেছে; দোকানের একটি নতুন issued QR প্রয়োজন। **আপনাকে কখনো logs খুলতে হয়নি।**
4. যদি verdict `VALID` হত কিন্তু FI এখনও একটি failure দেখত, সেই request-এর **summary line** খুঁজুন (§4.2 command 1 বা 2)। এটি আমরা যে status return করেছি এবং কতক্ষণ সময় নিয়েছে তা দেখায়। যদি এটি ১৪ ms-এ 200 বলে, সমস্যা FI-এর side-এ, এবং আপনি evidence সহ তা বলতে পারেন। যদি এটি 500 বলে, exception দেখতে command 5 চালান।

### ৪.৪ EventId quick reference (আজ ম্যানেজার code-এ detail lines)

Summary line-এর মনে রাখার কোন EventId নেই; এটাকে এর `Category` দ্বারা খুঁজুন (§3.1)। এগুলো **detail** events:

| EventId | অর্থ | Level |
|---|---|---|
| 1001–1002 | Bootstrap client secret misconfigured (startup) | Warning / Error |
| 6001 | **Audit write failed.** Primary operation সফল হয়েছে; এই log line থেকে row replay করুন। | Error |
| 6101–6111 | Bangladesh Bank trust-store sync (`DailyTrustSyncService`, `HttpTrustStoreClient`) | mixed |
| 7000–7003 | QR issuance **rejected বা failed** (tenant not active, key not ACTIVE, signing failed, unexpected) | Warning / Error |
| 7004 | QR issuance accepted | Information |
| 7100–7103 | mTLS client certificate **rejected** (no cert, untrusted CA, missing EKU, unregistered thumbprint)। এগুলো TLS handshake-এর সময় ঘটে, তাই **কোন summary line নেই**। | Warning |
| 7104–7106 | mTLS certificate accepted, thumbprint cache refreshed বা refresh failed | Information / Debug / Warning |

### ৪.৫ FIs-এর কাছে কি জিজ্ঞাসা করবেন (FI onboarding guide-এ যায়)

এগুলোর আমাদের side-এ কোন code প্রয়োজন নেই। এগুলো শুধু complaints resolve করা অনেক দ্রুত করে:

1. **প্রতিটি call-এ একটি unique `X-Correlation-Id` পাঠান এবং store করুন** transaction-এর বিপরীতে। এটি তাদের support reference হয়ে যায় (anchor **D**)।
2. **generate এবং validate থেকে আমরা যে `payload_hash` return করি সেটি রাখুন** (QR image-এর প্রয়োজন ছাড়া anchor **B**)।
3. **তাদের verify `request_id` রাখুন** (anchor **C**)।

একটি FI যে তিনটি করে সে কখনো anchor **E** ("প্রায় ৩টায়")-এ fallback করতে হয় না।

---

## ৫. Verification: done মানে এই সব সত্য

| # | Check | কিভাবে |
|---|---|---|
| 1 | প্রতিটি non-Development log line valid JSON | `journalctl … -o cat \| jq -c . > /dev/null` (বা `docker logs … \| jq`) কোন parse errors দেখায় না |
| 2 | Business fields সহ প্রতি request-এ একটি summary line | যেকোন endpoint 3 বার call করুন এবং `HttpLoggingMiddleware` lines count করুন। A8 check table চালান। |
| 3 | `correlation_id` এখনও server-generated | `X-Correlation-Id: test-1` পাঠান। Logs `correlation_id=<guid>` এবং `caller_correlation_id=test-1` দেখায়, এবং audit row-তে GUID আছে। |
| 4 | Oversized বা garbage caller IDs drop হয় | একটি 500-character `X-Correlation-Id` পাঠান। এটি logs-এ appear করতে হবে না। |
| 5 | `tenant_id` authenticated requests-এ appear হয় | একটি valid JWT সহ একটি tenant endpoint call করুন এবং scope check করুন |
| 6 | mTLS lines JSON | একটি bad cert present করুন এবং output check করুন |
| 7 | Complaint playbook end to end কাজ করে | একটি QR generate করুন, এটি একটি valid key দিয়ে একবার এবং tampered একবার verify করুন, তারপর QR string থেকে §4 anchor **B** follow করুন: SQL rows খুঁজে পায়, এবং §4.2 command 2 একই তিনটি requests-এর summary lines খুঁজে পায় (issuance-এর জন্য 201, তারপর `VALID` সহ 200 এবং একটি rejection verdict সহ 200) audit rows-এর মতো একই `correlation_id`s-এর অধীনে। |
| 8 | Leak canary CI-তে pass হয় | `dotnet test` |
| 9 | অন্য কিছু regress হয়নি | সমস্ত existing tests pass হয়, `SBQR.Mtls.Tests` এবং audit chain tests সহ |

### ৫.১ Security audit (VAPT) এক নজরে

একজন security auditor কি check করেন, এবং এই plan কোথায় এর উত্তর দেয়। বন্ধনীর references OWASP ASVS / PCI DSS item numbers, audit report-এর জন্য।

| Auditor জিজ্ঞাসা করেন | আমাদের answer | কোথায় |
|---|---|---|
| Secrets এবং personal data কি logs-এর বাইরে রাখা হয়? *(ASVS 7.1.1–7.1.2)* | হ্যাঁ, এবং একটি CI test প্রতিটি build-এ এটি প্রমাণ করে | §2, Part B |
| Logins, access denials এবং bad input কি logged? *(ASVS 7.1.3, 7.2)* | হ্যাঁ: প্রতিটি summary line-এ 401/403/400; mTLS rejections; `audit_logs`-এ token decisions | A8, §4.4 |
| একটি event কে কি একটি timeline হিসেবে rebuild করা যায়? *(ASVS 7.1.4)* | হ্যাঁ: UTC time, `correlation_id`, tenant, status, একটি line-এ duration | §3 |
| একজন attacker কি fake log lines inject করতে পারে? *(ASVS 7.3.1)* | না: JSON escaping, এবং caller headers length- এবং character-checked | A1, A3 |
| Users কি internal details-এর পরিবর্তে একটি error ID পান? *(ASVS 7.4.1)* | হ্যাঁ: error body-এর `traceId` আমাদের `correlation_id` | A3 |
| Unexpected errors-এর জন্য কি একটি last-resort handler আছে? *(ASVS 7.4.3)* | Check করুন `AddProblemDetails()` + `UseExceptionHandler()` registered। এটি একটি existing VAPT item, এই plan দ্বারা যোগ করা হয়নি। | — |
| Logs কি protected, যথেষ্ট দীর্ঘ কেপ্ট, এবং watched? *(ASVS 7.3.3, OWASP A09, PCI 10.3–10.7)* | **শুধু F1-এর পরে**: access control, retention, alerts, review routine | F1 |

---

## ৬. PR checklist: এটি repo-এর review rules-এ যোগ করুন

- [ ] log calls-এ কোন `$"…{value}"` interpolation নেই। Templates বা `[LoggerMessage]` ব্যবহার করুন।
- [ ] নতুন events একটি **নতুন, unique** EventId (§4.4 update করুন) সহ `[LoggerMessage]` ব্যবহার করে।
- [ ] §2.1 থেকে কিছুই **কোন** level-এ log করা হয় না, Debug সহ।
- [ ] Account বা phone numbers, যদি সত্যিই প্রয়োজন হয়, `LogMask.Last4`-এর মধ্য দিয়ে যায়।
- [ ] logs-এ কোন whole DTOs বা request bodies নেই।
- [ ] `AuditEntry.Metadata`-তে কোন §2.1 data নেই (এটি audit failure-এ log হয়)।
- [ ] আপনি যে exception messages throw করেন তাতে কোন §2.1 data নেই। Exceptions log হয়।
- [ ] Request header থেকে কিছুই একটি identifier হিসেবে trust করা হয় না। Log করার আগে এর length এবং charset validate করুন।
- [ ] নতুন summary-line fields **শুধুমাত্র** `SummaryLineFields` (allow-list)-এর মাধ্যমে যোগ করা হয়, এবং §2.2-safe হতে হবে।
- [ ] যদি আপনি একটি নতুন sensitive field যোগ করেন, Part B-তে এর জন্য একটি canary যোগ করুন।

---

## ৭. আমরা কি deliberately করিনি, এবং কেন

| Idea (v1/v2 থেকে বা সাধারণ advice) | কেন না |
|---|---|
| git submodules-এর মাধ্যমে একটি shared `rvl-sbqr-observability` repo | আমরা যে শুধুমাত্র একটি app operate করি সেটির প্রয়োজন। এটি কিছুই না জন্য repo, CI, এবং Docker plumbing যোগ করবে। |
| FI Gateway-এর সাথে cross-app log joins | Gateway FI দ্বারা run হয়, তাই আমরা কখনো এর logs দেখি না। ম্যানেজারকে self-sufficient হতে হবে। আমরা পরিবর্তে FI-এর reference (`caller_correlation_id`) record করি। |
| Caller-এর `X-Correlation-Id` বা `traceparent`-কে আমাদের ID হিসেবে trust করা | Untrusted input (§1.3)। এটি audit trail-এর জন্য একটি spoofing risk। আমরা এটি শুধুমাত্র একটি hint হিসেবে record করি। |
| snake_case field names সহ একটি custom JSON formatter | Cosmetic। Built-in JSON console plus §3.1 table যথেষ্ট। |
| Built-in .NET logger-এর জন্য একটি runtime redaction "processor" | এর কোন এমন hook নেই। Typed `[LoggerMessage]` call sites plus canary test শক্তিশালী এবং সহজ। |
| Audit-failure metadata-কে একটি digest দিয়ে replace করা | সেটি documented replay path ভাঙবে। Metadata আজ PII-free, এবং একটি rule plus canary এটিকে সেভাবে রাখে। |
| Customer name বা account দ্বারা logs search করা (সর্বত্র `last4` fields) | প্রয়োজন নেই। `payload_hash`, `request_id`, এবং `caller_correlation_id` কোন PII ছাড়াই exact record খুঁজে পায় (§4)। |
| OpenTelemetry (traces, metrics, log export) | Data পাঠানোর জন্য একটি backend প্রয়োজন, এবং আমাদের এখনও একটি নেই। `TraceId` ইতিমধ্যে প্রতিটি line-এ, তাই পরে OTel যোগ করার কোন log rework প্রয়োজন নেই (Part F2)। |

---

## ৮. পরে, যখন একটি কারণ আছে

- **Central logging, retention, alerts, OpenTelemetry, এবং একটি support lookup page** সবগুলো এই doc-এর নিচে **Part F**-এ step by step specified।
- **`Microsoft.Extensions.Compliance.Redaction`** (Microsoft-এর data-classification plus `[LoggerMessage]`-এর জন্য redaction)। যদি sensitive log sites-এর সংখ্যা বৃদ্ধি পায় তাহলে adopt করার যোগ্য। এটি আজ overkill।

---

## Appendix: reference FI Gateway (ঐচ্ছিক, প্রায় ০.৫ day)

> **Context.** `rvl-sbqr-fi-gateway` হল একটি reference app **যা আমরা ship করি এবং একটি FI run করতে বেছে নিতে পারে**। একবার deployed, এর logs, এবং সেগুলোর PII, **FI-এর** দায়িত্ব, এবং আমরা কখনো সেগুলো দেখি না। আমাদের একমাত্র দায়িত্ব হল **এটি clean ship করা**, যাতে একটি FI যে এটি গ্রহণ করে সে একটি data leak inherit না করে। Parts A এবং B-এর পরে এটি করুন, অথবা যদি gateway এখনো কারো কাছে shipped না হয় তবে এটি skip করুন।

আজকের gateway code-এ problems, প্রতিটির জন্য fix:

| # | Problem | Fix |
|---|---|---|
| G1 | Output একটি plain-text template, machine-readable নয় | `Observability/SerilogBootstrap.cs`-এ, Development-এর বাইরে `cfg.WriteTo.Console(new RenderedCompactJsonFormatter())` ব্যবহার করুন (`Serilog.Formatting.Compact` থেকে, ইতিমধ্যে `Serilog.AspNetCore`-এর একটি dependency)। |
| G2 | `appsettings.json` `Microsoft` / `Microsoft.AspNetCore` কে `Information`-এ set করে। কারণ `ReadFrom.Configuration` শেষে চলে, এটি code-এ লেখা Warning overrides-কে **undoes** করে। | উভয়কে `Warning`-এ set করুন। |
| G3 | Masking enricher শুধুমাত্র এক level deep walk করে এবং `access-token`-এর মতো spelling variants miss করে | নিচে recursive, name-normalising version দিয়ে এটি replace করুন। |
| G4 | `RequestLogContextMiddleware` **প্রতিটি** line-এ raw `UserSub` (IdP `sub` claim) push করে। যদি IdP `sub`-এ একটি phone number রাখে, প্রতিটি line এটি leak করে। | যদি `sub` personal data হতে পারে, এর একটি HMAC log করুন (`HMACSHA256.HashData(key, UTF8(sub))[..16]`, config থেকে key সহ)। একটি phone number-এর একটি plain SHA-256 কয়েক মিনিটে reversible। |
| G5 | `QrProxyController.cs:71` সরাসরি `userSub` log করে | সেই argument সরিয়ে ফেলুন। এটি ইতিমধ্যে log scope-এর মাধ্যমে প্রতিটি line-এ (G4)। |
| G6 | `OutboundHttpLoggingHandler` query string সহ full URLs log করে | পরিবর্তে `request.RequestUri?.GetLeftPart(UriPartial.Path)` log করুন। |
| G7 | `CorrelationIdMiddleware` যেকোন inbound `X-Correlation-Id` গ্রহণ করে | Step A3-এর মতো একই `IsSafeId` check apply করুন, এবং যখন check fail হয় তখন একটি নতুন ID generate করুন। |
| G8 | `UseSerilogRequestLogging()` correlation middleware-এর **আগে** চলে, তাই "request finished" line-এর কোন `CorrelationId` নেই | `o.EnrichDiagnosticContext = (d, http) => d.Set("CorrelationId", http.Items[CorrelationIdMiddleware.ItemKey] as string);` যোগ করুন |
| G9 | কোন leak test নেই | Tests-এ `UseSerilog((ctx, services, cfg) => …)`-এ `cfg.ReadFrom.Services(services)`-এর মাধ্যমে একটি capturing `ILogEventSink` register করুন, তারপর assert করুন canaries কখনো appear করে না, Part B-এর একই approach। |

Recursive masking enricher (G3), `Observability/SensitiveDataMaskingEnricher.cs`-এর body replace করে:

```csharp
public sealed class SensitiveDataMaskingEnricher : ILogEventEnricher
{
    private const string Mask = "***";
    private const int MaxDepth = 8;

    // Normalize()-এর পরে compared: lower-case, শুধুমাত্র letters/digits।
    // তাই "access_token", "AccessToken" এবং "Access-Token" একটি entry।
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "authorization", "cookie", "setcookie", "xusersub",
        "pan", "primaryaccountnumber", "cardnumber", "accountnumber",
        "msisdn", "phone", "mobile", "nid", "nationalid", "pin", "otp",
        "qrpayload", "qrstring", "rawqr", "beneficiaryname", "accountname",
        "body", "requestbody", "responsebody", "rawbody",
    };

    // এগুলোর একটি CONTAINING যেকোন name masked হয় (clientsecret, refreshtoken, …)।
    private static readonly string[] Fragments =
        { "secret", "password", "token", "privatekey", "signingkey", "apikey" };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            var scrubbed = IsSensitive(name) ? new ScalarValue(Mask) : Scrub(value, 0);
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, scrubbed));
        }
    }

    private static LogEventPropertyValue Scrub(LogEventPropertyValue value, int depth)
    {
        if (depth >= MaxDepth) return value;
        return value switch
        {
            StructureValue s => new StructureValue(
                s.Properties.Select(p => new LogEventProperty(p.Name,
                    IsSensitive(p.Name) ? new ScalarValue(Mask) : Scrub(p.Value, depth + 1))),
                s.TypeTag),
            DictionaryValue d => new DictionaryValue(
                d.Elements.Select(kv => new KeyValuePair<ScalarValue, LogEventPropertyValue>(kv.Key,
                    IsSensitive(kv.Key.Value?.ToString()) ? new ScalarValue(Mask) : Scrub(kv.Value, depth + 1)))),
            SequenceValue q => new SequenceValue(q.Elements.Select(e => Scrub(e, depth + 1))),
            _ => value,
        };
    }

    private static bool IsSensitive(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return Exact.Contains(n) || Fragments.Any(n.Contains);
    }
}
```

> **কেন gateway একটি enricher পায় কিন্তু ম্যানেজার পায় না:** Serilog একটি hook provide করে যা প্রতিটি event লেখার আগে rewrite করতে পারে। Built-in .NET logger পারে না। ম্যানেজার পরিবর্তে typed `[LoggerMessage]` call sites plus canary test-এর উপর নির্ভর করে।

---

## Part F (future): go-live upgrades

Parts A এবং B logs-কে **well-shaped** করে: JSON, প্রতিটি line-এ একটি correlation ID, কোন PII নেই। Part F তাদের **operable** করে: কেন্দ্রীয়ভাবে stored, একটি defined period-এর জন্য কেপ্ট, access-controlled, problems-এ alerting, এবং SSH ছাড়া searchable। এটি কিভাবে modern teams production চালায়। Steps "go-live-এর আগে অবশ্যই থাকতে হবে" থেকে "পরে nice to have" পর্যন্ত ordered।

| Step | কি | প্রচেষ্টা | কখন |
|---|---|---|---|
| **F1** | CloudWatch-এ কেন্দ্রীয় logging: retention, access control, alerts, saved queries | প্রতি environment-এ প্রায় ০.৫ day, **শুধুমাত্র infrastructure, কোন app code নেই** | **Production go-live-এর আগে।** প্রথমে dev করুন, তারপর staging, তারপর prod। |
| **F2** | OpenTelemetry tracing | প্রায় ০.৫ day code, plus একটি backend decision | যখন "কোন step slow?" একটি real question হয় |
| **F3** | একটি support lookup page | প্রায় ১-২ days | যখন complaint volume এটি ন্যায্যতা দেয় (এর নিজস্ব approved requirement প্রয়োজন) |

**এটি কিভাবে industry maturity ladder-এ fit করে:** level 1 হল প্রতি request-এ একটি summary line সহ structured logs (Parts A এবং B)। Level 2 হল একটি কেন্দ্রীয় log platform (F1)। Level 3 হল linked traces সহ full observability (F2)। Level 4 হল support tooling (F3)।

---

### F1. CloudWatch-এ কেন্দ্রীয় logging

> **Production-এর জন্য Release-blocking।** একটি security audit (VAPT) check করবে যে logs protected, retained এবং monitored। Parts A এবং B এটি নিজে show করতে পারে না; F1 করে। §5.1 দেখুন।

**কেন server-এ grep একটি live fintech service-এর জন্য যথেষ্ট নয়:**

- Logs server-এর সাথে মারা যায়। যদি EC2 instance replaced হয় বা এর disk পূর্ণ হয়, logs চলে যায়।
- কোন guaranteed retention period নেই, এবং auditors একটি জিজ্ঞাসা করেন।
- SSH সহ যে কেউ সব পড়তে পারে, এবং কিছুই record করে না কে দেখেছে।
- যখন কিছু ভেঙে যায় তখন কেউ alert পায় না। আপনি complaints থেকে খুঁজে পান।
- একটি second instance বিদ্যমান হওয়ার সাথে সাথে এটি break down হয়।

**Target picture:**

```
sbqr-api ──stdout JSON──► systemd ──► /var/log/sbqr/sbqr-api.log ──► CloudWatch agent
                                       (7-day local buffer)              │
                                                                         ▼
                                              Log group /rvl-sbqr/<env>/sbqr-api  (retention N days)
                                                 │                         │
                                     Logs Insights (search)        Metric filters ──► Alarms ──► SNS email
```

**নিচে ব্যবহৃত Names** (তারা `docs/deployments.md`-এ `rvl-sbqr-…-<env>` convention অনুসরণ করে; `<env>` হল `dev`, `staging` বা `prod`):

| জিনিস | Name |
|---|---|
| Log group | `/rvl-sbqr/<env>/sbqr-api` |
| EC2 instance role | `rvl-sbqr-ec2-role-<env>` |
| Alert topic | `rvl-sbqr-alerts-<env>` |
| Metric namespace | `RVL/SBQR/<env>` |
| Reader group | `rvl-sbqr-log-readers-<env>` |

`aws …` commands একজন **admin-এর machine** থেকে চলে (region `ap-southeast-1`), server থেকে নয়। `sudo …` commands SSH-এর উপর **server-এ** চলে।

#### F1.1 EC2 instance-কে logs লিখতে দিন (IAM role)

1. **IAM → Roles → Create role.** Trusted entity: **AWS service → EC2**। managed policy **`CloudWatchAgentServerPolicy`** attach করুন। role-এর name দিন `rvl-sbqr-ec2-role-<env>`।
2. **EC2 → Instances →** `rvl-sbqr-api-<env>` **→ Actions → Security → Modify IAM role**, এবং নতুন role select করুন।

> **Note:** S3 key vault নিজস্ব explicit credentials ব্যবহার করা চালিয়ে যায় (`/etc/sbqr/sbqr.env`-এ `Storage` settings), তাই এই role attach করা vault access পরিবর্তন করে না। Vault-কে instance role-এ move করা, এবং disk থেকে access keys delete করা, পরে একটি worthwhile আলাদা change।

#### F1.2 Log group তৈরি করুন এবং retention set করুন (agent শুরু করার আগে)

যদি agent নিজে group তৈরি করে, group **কখনো expire হয় না**। এটি expensive, এবং এটি compliance যা জিজ্ঞাসা করে তা নয়। তাই প্রথমে এটি তৈরি করুন:

```bash
aws logs create-log-group      --log-group-name /rvl-sbqr/prod/sbqr-api
aws logs put-retention-policy  --log-group-name /rvl-sbqr/prod/sbqr-api --retention-in-days 400
```

| Environment | Retention | কেন |
|---|---|---|
| dev | 14 days | শুধুমাত্র Debugging |
| staging | 30 days | একটি UAT cycle cover করে |
| prod | **400 days** (প্রায় ১৩ মাস) | **Placeholder: compliance-এর সাথে confirm করুন।** সাধারণ benchmark (PCI DSS) হল 12 months কেপ্ট, 3 months immediately searchable। CloudWatch এর সব searchable রাখে। |

CloudWatch Logs default দ্বারা rest-এ data encrypt করে। যদি security policy production-এর জন্য একটি customer-managed key প্রয়োজন হয়, `create-log-group`-এ `--kms-key-id <key-arn>` যোগ করুন।

#### F1.3 App-এর output একটি file-এ পাঠান

CloudWatch agent **files** পড়ে, systemd journal নয়। তাই systemd-কে app-এর stdout একটি file-এ append করতে বলুন। একটি **drop-in override** ব্যবহার করুন: deploy script কখনো এটি স্পর্শ করে না, তাই এটি প্রতিটি deploy বেঁচে থাকে।

```bash
sudo install -d -o root -g adm -m 0750 /var/log/sbqr
sudo systemctl edit sbqr-api
```

editor-এ যা খোলে, নিম্নলিখিত যোগ করুন, তারপর save করুন:

```ini
[Service]
StandardOutput=append:/var/log/sbqr/sbqr-api.log
StandardError=append:/var/log/sbqr/sbqr-api.log
```

```bash
sudo systemctl restart sbqr-api
sudo tail -n 3 /var/log/sbqr/sbqr-api.log | jq .     # JSON lines appear হবে
```

তারপর local file rotate করুন যাতে disk কখনো পূর্ণ না হয়। `/etc/logrotate.d/sbqr-api` তৈরি করুন:

```
/var/log/sbqr/sbqr-api.log {
    daily
    rotate 7
    compress
    delaycompress
    missingok
    notifempty
    copytruncate
}
```

> **কেন `copytruncate`:** app চলার সময় systemd file open রাখে। `copytruncate` file copy করে, তারপর in place empty করে, তাই systemd একটি restart ছাড়াই writing চালিয়ে যায়। Local file শুধুমাত্র একটি **7-day buffer**; CloudWatch real store।
>
> **Heads-up:** এই পরিবর্তনের পরে, `journalctl -u sbqr-api` শুধুমাত্র service start এবং stop messages দেখায়। App-এর lines file-এ এবং CloudWatch-এ। Deploy skill-এর "tail the journal" step-কে `sudo tail -n 50 /var/log/sbqr/sbqr-api.log`-এ update করুন।

#### F1.4 CloudWatch agent install এবং start করুন

```bash
cd /tmp
wget https://amazoncloudwatch-agent.s3.amazonaws.com/ubuntu/amd64/latest/amazon-cloudwatch-agent.deb
sudo dpkg -i -E ./amazon-cloudwatch-agent.deb
sudo usermod -aG adm cwagent          # agent-কে /var/log/sbqr পড়তে দেয়
```

`/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json` তৈরি করুন (group name-এর `<env>` part set করুন):

```json
{
  "agent": { "run_as_user": "cwagent" },
  "logs": {
    "logs_collected": {
      "files": {
        "collect_list": [
          {
            "file_path": "/var/log/sbqr/sbqr-api.log",
            "log_group_name": "/rvl-sbqr/prod/sbqr-api",
            "log_stream_name": "{instance_id}"
          }
        ]
      }
    }
  }
}
```

এটি start করুন, এবং boot-এ start করুন:

```bash
sudo /opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent-ctl \
  -a fetch-config -m ec2 -s \
  -c file:/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json
systemctl is-active amazon-cloudwatch-agent
```

> **যদি CloudWatch-এ কিছুই না পৌঁছায়,** `/opt/aws/amazon-cloudwatch-agent/logs/amazon-cloudwatch-agent.log` পড়ুন। "permission denied" মানে `adm` group step miss হয়েছে। "AccessDenied" মানে F1.1 থেকে IAM role attached নয়।

#### F1.5 কে logs পড়তে পারে তা control করুন

PII ছাড়াও, logs sensitive operational data। একটি IAM group `rvl-sbqr-log-readers-<env>` তৈরি করুন, এই policy attach করুন (`<account-id>` fill in করুন), এবং শুধুমাত্র যে ব্যক্তিরা investigate করেন তাদের যোগ করুন:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "logs:DescribeLogGroups", "logs:DescribeQueries", "logs:DescribeQueryDefinitions",
        "logs:GetQueryResults", "logs:StopQuery"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "logs:StartQuery", "logs:FilterLogEvents", "logs:GetLogEvents",
        "logs:DescribeLogStreams", "logs:GetLogGroupFields"
      ],
      "Resource": "arn:aws:logs:ap-southeast-1:<account-id>:log-group:/rvl-sbqr/prod/sbqr-api:*"
    }
  ]
}
```

> **কেন:** এটি least privilege, এবং CloudTrail এই API calls record করে, তাই logs কে search করেছে তার একটি record আছে। Investigators-দের আর server-এ SSH access-এর প্রয়োজন নেই, যা আপনাকে কে SSH আছে তা tighten করতেও দেয়।

**ঐচ্ছিক hardening: কেউ logs delete বা shorten করতে পারে না।** একটি emergency ("break-glass") admin role ছাড়া প্রতিটি IAM role-এ একটি deny statement যোগ করুন:

```json
{ "Effect": "Deny",
  "Action": ["logs:DeleteLogGroup", "logs:DeleteLogStream", "logs:PutRetentionPolicy"],
  "Resource": "arn:aws:logs:ap-southeast-1:<account-id>:log-group:/rvl-sbqr/prod/*" }
```

Auditors এটি পছন্দ করেন কারণ এটি প্রমাণ করে কেউ quietly evidence মুছে ফেলতে পারে না।

#### F1.6 Alerts: FI call করার আগে বলা হোক

**Step 1: একটি alert topic তৈরি করুন এবং team-এর email subscribe করুন** (AWS যে email পাঠায় সেটি confirm করুন):

```bash
aws sns create-topic --name rvl-sbqr-alerts-prod
aws sns subscribe --topic-arn arn:aws:sns:ap-southeast-1:<account-id>:rvl-sbqr-alerts-prod \
  --protocol email --notification-endpoint <team-email>
```

**Step 2: নিচে প্রতিটি signal-এর জন্য একটি metric filter plus একটি alarm তৈরি করুন।** একটি metric filter log lines count করে যা একটি pattern match করে, এবং alarm fire হয় যখন count একটি threshold অতিক্রম করে:

| Metric | Alarm যখন | কেন এটা গুরুত্বপূর্ণ |
|---|---|---|
| `AuditWriteFailed` | ৫ মিনিটে ≥ 1 | একটি audit row missing। log line থেকে এটি replay করুন (§4.4)। **Compliance-critical।** |
| `AppErrors` | ৫ মিনিটে ≥ 5 | কিছু broken |
| `Http5xx` | ৫ মিনিটে ≥ 5 | Callers server errors পাচ্ছেন |
| `MtlsRejected` | ৫ মিনিটে ≥ 20 | একটি FI certificate expired বা misconfigured, বা কেউ endpoint probing করছে |
| `TrustSyncIssue` | 60 মিনিটে ≥ 1 | Bangladesh Bank trust-store sync failing, তাই verifications stale keys ব্যবহার করতে পারে |
| `AuthFailures` | ৫ মিনিটে ≥ 20 | অনেক 401/403 responses: কেউ `/v1/oauth/token`-এ credentials guessing করতে পারে |

প্রতিটি metric-এর জন্য pattern এখানে:

```text
AuditWriteFailed  { $.EventId = 6001 }
AppErrors         { ($.LogLevel = "Error") || ($.LogLevel = "Critical") }
Http5xx           { ($.Category = "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware") && ($.State.StatusCode >= 500) }
MtlsRejected      { ($.EventId >= 7100) && ($.EventId <= 7103) }
TrustSyncIssue    { ($.EventId >= 6101) && ($.EventId <= 6111) && (($.LogLevel = "Warning") || ($.LogLevel = "Error")) }
AuthFailures      { ($.State.StatusCode = 401) || ($.State.StatusCode = 403) }
```

এটি একটি metric-এর জন্য full command pair। name, pattern, threshold এবং period পরিবর্তন করে অন্যদের জন্য repeat করুন:

```bash
ENV=prod
LG=/rvl-sbqr/$ENV/sbqr-api
NS=RVL/SBQR/$ENV
TOPIC=arn:aws:sns:ap-southeast-1:<account-id>:rvl-sbqr-alerts-$ENV

aws logs put-metric-filter --log-group-name $LG --filter-name AuditWriteFailed \
  --filter-pattern '{ $.EventId = 6001 }' \
  --metric-transformations metricName=AuditWriteFailed,metricNamespace=$NS,metricValue=1,defaultValue=0

aws cloudwatch put-metric-alarm --alarm-name rvl-sbqr-$ENV-audit-write-failed \
  --namespace $NS --metric-name AuditWriteFailed --statistic Sum \
  --period 300 --evaluation-periods 1 --threshold 1 \
  --comparison-operator GreaterThanOrEqualToThreshold \
  --treat-missing-data notBreaching --alarm-actions $TOPIC
```

**Step 3: real incident-এর জন্য অপেক্ষা না করে wiring test করুন:**

```bash
aws cloudwatch set-alarm-state --alarm-name rvl-sbqr-prod-audit-write-failed \
  --state-value ALARM --state-reason "wiring test"      # team email আসা উচিত
```

> **`Http5xx` তৈরি করার আগে,** Logs Insights-এ একটি real summary line খুলুন এবং confirm করুন status-code field সত্যিই `State.StatusCode` কিনা। ধরে নেওয়ার পরিবর্তে actual field name check করুন।
>
> **Real traffic-এর দুই সপ্তাহ পরে** thresholds tune করুন। একটি alarm যা প্রতিদিন fire হয় সেটি ignored হয়।

#### F1.7 Saved investigation queries (Logs Insights)

**Logs Insights কিভাবে ব্যবহার করবেন:** **CloudWatch → Logs Insights**-এ যান, log group select করুন, **আপনি যতটা সংকীর্ণ time range করতে পারেন সেটি pick করুন** (queries scanned GB প্রতি billed, এবং সংকীর্ণ ranges দ্রুততর), একটি query paste করুন, এবং **Run** click করুন। এটিকে `sbqr-investigation` নামের একটি folder-এ রাখতে **Save** click করুন।

**Fields কিভাবে কাজ করে:** Insights JSON automatically পড়ে। Top-level fields (`EventId`, `LogLevel`, `Category`, `Message`) এবং nested object fields (`State.verdict`, `State.StatusCode`) সরাসরি ব্যবহার করা যায়। সেজন্য summary line তার fields `State`-এ রাখে (§3.1)। `Scopes` একটি **array যার order পরিবর্তন হয়**, তাই `@message like "<id>"` দিয়ে এর ভিতরে IDs search করুন। IDs unique GUIDs বা hashes, তাই একটি substring match practice-এ exact।

**Time zone:** console-কে **UTC**-এ set করুন, বা মনে রাখুন logs UTC-তে stored। "৩টা Dhaka" হল 09:00 UTC।

**§4.2-এর মতো একই two-step habit:** প্রথমে **summary line** পড়ুন (Q1–Q5), এবং শুধু প্রয়োজনে **detail lines** খুলুন (Q6)।

**Q1: একটি request-এর জন্য summary line** (anchor **A**, বা SQL থেকে যেকোন `correlation_id`)

```
filter State.correlation_id = "<correlation_id>"
| display @timestamp, State.Path, State.StatusCode, State.Duration, State.tenant_id, State.verdict, State.reason_code, State.payload_hash
```

**Q2: FI-এর নিজস্ব reference-এর জন্য summary line** (anchor **D**)

```
filter State.caller_correlation_id = "<fi-ref>"
| display @timestamp, State.correlation_id, State.Path, State.StatusCode, State.verdict
```

**Q3: একটি QR code স্পর্শকারী প্রতিটি request** (anchor **B**, কোন SQL প্রয়োজন নেই)

```
filter State.payload_hash = "<payload_hash>"
| display @timestamp, State.Path, State.StatusCode, State.tenant_id, State.verdict, State.reason_code, State.correlation_id
| sort @timestamp asc
```

**Q4: একটি window-এ একটি FI-এর জন্য সমস্ত failed requests** (anchor **E**)

```
filter State.tenant_id = "<tenant_id>" and State.StatusCode >= 400
| display @timestamp, State.Path, State.StatusCode, State.correlation_id
| sort @timestamp asc
```

**Q5: সমস্ত server errors, newest first**

```
filter State.StatusCode >= 500
| display @timestamp, State.Path, State.tenant_id, State.correlation_id
| sort @timestamp desc
```

**Q6: একটি request-এর জন্য DETAIL lines** (এটি ব্যবহার করুন যখন summary line যথেষ্ট নয়, যেমন status 500)

```
fields @timestamp, LogLevel, EventId, Category, Message, Exception
| filter @message like "<correlation_id>"
| sort @timestamp asc
```

**Q7: FI প্রতি verification verdicts।** একটি query-এ একটি business dashboard।

```
filter State.Path like "/qr/validate" and ispresent(State.verdict)
| stats count(*) as calls by State.tenant_id, State.verdict
| sort calls desc
```

**Q8: Health overview, EventId দ্বারা top errors**

```
filter LogLevel in ["Error", "Critical"]
| stats count(*) as errors by EventId, Category
| sort errors desc
```

**Q9: Certificate subject দ্বারা mTLS rejections।** "কোন FI connect করতে পারছে না?" (এগুলোর জন্য কোন summary line নেই; §4.4 দেখুন।)

```
filter EventId >= 7100 and EventId <= 7103
| stats count(*) as rejections by EventId, State.Subject
| sort rejections desc
```

**Q10: Replay করার জন্য audit write failures**

```
fields @timestamp, Message
| filter EventId = 6001
| sort @timestamp asc
```

#### F1.8 F1-এর পরে complaint flow

§4 playbook পরিবর্তন হয় না। শুধু step 3 পরিবর্তন হয়: **আর SSH নেই**, শুধু browser-এ একটি saved query চালান। বেশিরভাগ ক্ষেত্রে, summary line (Q1–Q4) হল উত্তর।

| Anchor (§4.1) | Step 2 (SQL) | Step 3 (Logs Insights) |
|---|---|---|
| **A**: error body `traceId` | — | Q1, তারপর প্রয়োজনে Q6 |
| **B**: QR code / `payload_hash` | Query B (ঐচ্ছিক) | Q3, তারপর প্রয়োজনে Q6 |
| **C**: FI `request_id` | Query C | Q1, তারপর প্রয়োজনে Q6 |
| **D**: FI-এর `X-Correlation-Id` | — | Q2, তারপর প্রয়োজনে Q6 |
| **E**: "FI X, প্রায় ৩টায়" | Query E (ঐচ্ছিক) | Q4, তারপর প্রয়োজনে Q6 |

#### F1.9 Done মানে

| # | Check |
|---|---|
| 1 | এখন করা একটি request প্রায় 1 মিনিটের মধ্যে Q1 দিয়ে খুঁজে পাওয়া যায় |
| 2 | Log group সম্মত retention দেখায় ("Never expire" নয়) |
| 3 | `set-alarm-state` test email এসেছে |
| 4 | `rvl-sbqr-log-readers-<env>`-এর বাইরে কেউ Logs Insights-এ AccessDenied পায় |
| 5 | `sudo logrotate -f /etc/logrotate.d/sbqr-api`-এর পরে, নতুন lines এখনও CloudWatch-এ পৌঁছায় |
| 6 | Deploy skill-এর "tail the log" step update করা হয়েছে (F1.3 heads-up) |

#### F1.10 কে logs দেখে, এবং কখন

Alerts শুধু তখনই সাহায্য করে যদি কেউ তাদের উপর কাজ করে। এটি একবার সম্মত হন এবং names লিখে রাখুন:

- **Alert emails:** একজন named on-call person একই কাজের দিনে respond করে। একটি `AuditWriteFailed` alert একই দিনে handle করা হয়, সবসময়।
- **সাপ্তাহিক (15 মিনিট):** Q8 (top errors) এবং Q9 (mTLS rejections) চালান, এবং team channel-এ কোন কিছু অস্বাভাবিক note করুন।
- **মাসিক:** confirm করুন retention unchanged এবং reader group শুধুমাত্র বর্তমান staff ধারণ করে।

> **Cost:** CloudWatch ingested GB প্রতি, stored GB প্রতি, এবং queries দ্বারা scanned GB প্রতি charge করে। এই service-এর volume-এর জন্য এটি ছোট, কিন্তু `ap-southeast-1`-এর জন্য AWS pricing page check করুন এবং একটি billing alarm set করুন। সংকীর্ণ query time ranges scan cost কম রাখে।

---

### F2. OpenTelemetry tracing (যখন latency questions appear)

**এটি কি যোগ করে:** logs আপনাকে বলে *কি ঘটেছে*। একটি trace আপনাকে বলে *সময় কোথায় গেছে*: একটি request-এর একটি timeline, উদাহরণস্বরূপ "DB 3 ms → trust store HTTP 120 ms → signing 1 ms"। কারণ প্রতিটি log line ইতিমধ্যে `TraceId` বহন করে (step A1), আপনি একটি log line থেকে সরাসরি এর trace-এ click করতে পারেন।

**১. প্রথমে একটি backend choose করুন** (এটি real decision; code ছোট):

| Option | Notes |
|---|---|
| **AWS X-Ray** | AWS-এ থাকে। CloudWatch agent (বা AWS Distro for OpenTelemetry collector) server-এ OTLP receive করতে পারে এবং X-Ray-তে forward করতে পারে। `traces` section-এর জন্য current agent docs check করুন। |
| Grafana Tempo (self-hosted বা Grafana Cloud) | Loki-এর সাথে ভাল pairs যদি আপনি কখনো CloudWatch ছেড়ে যান |
| Datadog, Honeycomb, New Relic | Commercial SaaS, value পেতে দ্রুততম, এবং এতে খরচ হয় |

**২. Packages** (`Directory.Packages.props`-এ যোগ করুন; current versions ব্যবহার করুন, এবং OTLP exporter **≥ 1.15.3** হতে হবে, gateway-এর `Directory.Packages.props`-এ উল্লেখিত advisory অনুসারে):
`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `Npgsql.OpenTelemetry`।

**৩. Code** `Program.cs`-এ, একটি switch-এর পিছনে যা default off:

```csharp
if (builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled"))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "sbqr-api",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o =>
                o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))   // health-check noise skip করুন
            .AddHttpClientInstrumentation()   // trust store + S3 vault calls
            .AddNpgsql()                      // DB calls: শুধুমাত্র SQL text, কখনো parameter values নয়
            .AddOtlpExporter());              // OTEL_EXPORTER_OTLP_ENDPOINT থেকে endpoint
}
```

`/etc/sbqr/sbqr.env`-এ per environment enable করুন: `OpenTelemetry__Enabled=true` এবং `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`।

**জানার জন্য ভাল:**

- `CorrelationIdMiddleware` ইতিমধ্যে প্রতিটি request-এর Activity-কে `correlation_id` দিয়ে tag করে, তাই trace backend-এ আপনি logs যে একই ID ব্যবহার করে সেটি দিয়ে traces search করতে পারেন।
- **Trust boundary (§1.3):** যদি একটি FI একটি `traceparent` header পাঠায়, আমাদের request *তাদের* trace-এর অংশ হয়। এটি FIs-এর জন্য useful যারা tracing চালায়, কিন্তু আমাদের `correlation_id`-এর উপর pivoting রাখুন। যদি এটি confusion সৃষ্টি করে তাহলে revisit করুন।
- **§2 traces-এর জন্যও প্রযোজ্য।** কখনো span tags হিসেবে request bodies বা headers যোগ করবেন না। URL query values redact করে এমন default instrumentation রাখুন।

---

### F3. Support lookup page (যখন complaint volume বাড়ে)

**কি:** বিদ্যমান `v1.internal-admin` API surface-এ একটি **internal-admin** endpoint, উদাহরণস্বরূপ `GET /v1/admin/support/lookup?payloadHash=…` (বা `requestId=…`, বা `correlationId=…`)। এটি `audit_logs`, `qr_validations` এবং `qr_generations` থেকে একটি merged timeline return করে, এবং এটি যে প্রতিটি `correlation_id` খুঁজে পেয়েছে তার জন্য ready-to-run Q1 query text।

**কেন:** Support staff SQL বা AWS console access ছাড়া complaint থেকে answer-এ যায়। Engineers শুধুমাত্র real escalations-এর জন্য টানা হয়। এভাবেই mature payment companies support handle করে: business record entry point, এবং logs হল drill-down।

**Building-এর জন্য rules:**

- শুধুমাত্র Internal-admin authentication।
- Lookup নিজেই একটি audit row লেখে (`support.lookup`), record করে কে কি lookup করেছে।
- এটি শুধুমাত্র §2.2 data return করে, কখনো QR string, name বা account number নয়।
- এটি একটি product feature, তাই এটি তৈরি হওয়ার আগে এর নিজস্ব approved requirement doc প্রয়োজন (`AGENTS.md`)।