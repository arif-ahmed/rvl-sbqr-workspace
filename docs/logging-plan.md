# Logging Plan: Secure BQR Manager

> **Status:** Proposal, v3 (2026-09-29). v3 focuses on the manager. It replaces v2, which treated the FI Gateway as a co-equal app, and v1, which proposed a shared repo plus git submodules plus a Serilog migration.
> **Scope:** `rvl-secure-bqr-manager`, **the product we operate**. The FI Gateway is an optional reference app that an FI may choose to deploy and run itself. We don't operate it and never see its logs, so it is covered only by an **optional appendix**.
> **Effort:** about **1 dev-day** (Part A about 0.5 day, Part B about 0.5 day). The appendix adds about 0.5 day, optional.
> **After that:** **Part F** at the bottom of this doc covers the go-live upgrades: central logging on CloudWatch, OpenTelemetry, and a support lookup page. **F1 is release-blocking for production**, because a security audit needs it (§5.1).
> **Approval:** logging is not part of the BB BanglaQR spec. Under `AGENTS.md` ("Explicitly out of scope … add only via separate approved doc"), this document is that separate doc. Get it approved before merging.

---

## Start here (if you are implementing this)

You only need **Part A** and **Part B**. Skip the Appendix and Part F; they are for later.

1. **Read §0–§2 once** (about 15 minutes). They explain *why*; the steps assume you know it.
2. **Before touching code:**
   - Create a branch.
   - Start **Docker Desktop**. The local database and the integration tests need it.
   - Start the local database the usual way (see the repo `README`).
   - Build (**Ctrl+Shift+B**) and run all tests (**Test → Run All Tests**). Everything should be green *before* you start, so any later failure is yours.
3. **Do the steps in order**: A1 → A8, then B1 → B4. After each step: build, do the step's **Check**, and commit. One commit per step makes review easy.
4. **Keep §3 open while testing.** It shows what a correct log line looks like.

| Step | Files you touch | Time |
|---|---|---|
| A1 | `Program.cs`, `appsettings.json`, `launchSettings.json` | 20 min |
| A2 | `Program.cs` | 15 min |
| A3 | `CorrelationIdMiddleware.cs` | 20 min |
| A4 | `Program.cs` | 15 min |
| A5 | new `LogMask.cs` | 5 min |
| A6 | `MtlsEndpointsExtensions.cs` | 30 min |
| A7 | nothing (just read it) | 5 min |
| A8 | 2 new files, `CorrelationIdMiddleware.cs`, 2 controllers | 1.5 h |
| B1–B4 | `QrFlowHostBuilder.cs`, 3 new test files | 2–3 h |

**Seeing logs:** run the API with **F5**; the logs appear in the console window. Step A1 gives you two run profiles: readable text, or the exact JSON the server prints.
**Sending requests:** use a browser for `/health/live`. The QR endpoints need a token, so use the API explorer at `http://localhost:5001/docs/public` or the team's Postman collection.

---

## 0. TL;DR

We want **fintech-grade logging for the manager with the smallest change that gets us there**. Fintech-grade here means six things:

1. **Machine-readable.** Every log line is one JSON object written to stdout.
2. **Findable.** Every line written during a request carries a **server-generated `correlation_id`** and the caller's `tenant_id`.
3. **Clean.** No secrets, no customer PII (personally identifiable information: names, phone and account numbers), no raw QR payloads, no request bodies. A CI test fails the build when one leaks.
4. **One line tells the story.** Framework noise is kept at Warning. Each HTTP request produces exactly one **summary line** (the industry's "canonical log line") carrying tenant, path, status, duration and the business outcome (verdict, `payload_hash`). Most investigations need only that one line.
5. **Separate from the audit trail.** `audit_logs` (hash-chained, in PostgreSQL) stays the compliance record. Application logs are for debugging and operations only.
6. **Traceable from a complaint.** Whatever the FI gives us (an error body, the QR code, their request ID, or just "around 3 pm"), we can find the record in **our own database**, get its `correlation_id`, and pull every log line for it. §4 is the playbook.

**The manager must be self-sufficient.** Every investigation must be answerable from the manager's own database and logs, with no dependency on any FI system, gateway, or log store we don't control.

**No new repo, no git submodules, no shared library, no OpenTelemetry yet.** The work is a handful of targeted edits inside `rvl-secure-bqr-manager`.

> **Note on the workspace root.** The `rvl-sbqr-workspace/` folder is a personal convenience checkout. It is not part of the build, CI, or deploy, and nothing in this plan depends on it. The team copy of this doc belongs in `rvl-secure-bqr-manager/docs/logging-plan.md`.

---

## 1. Background for a dev new to fintech

Read this section once. Every step later in the doc refers back to one of these ideas.

### 1.1 Logs vs. the audit trail

| | Application logs (this plan) | Audit trail (`audit_logs` table) |
|---|---|---|
| Purpose | Debugging, operations, incident investigation | Legal and compliance evidence of who did what |
| Where | stdout — captured by the wrapper (shell redirect on a dev laptop, Docker logging driver in a container, kubelet + DaemonSet in Kubernetes, CloudWatch agent on a VM). **See §3.5** for the per-environment medium. | PostgreSQL, hash-chained, append-only |
| Can lines be lost? | Yes, occasionally. That is acceptable. | No. That is the point of the table. |
| Who reads it | Developers and on-call engineers | Auditors, compliance, Bangladesh Bank |

**Rule:** never rely on an application log line as proof that something happened. If it matters for compliance, it goes through `IAuditLogger`.

### 1.2 Why PII and secrets must stay out of logs

Many more people and systems can read logs than can read the database: developers, log vendors, support staff, backups, screenshots pasted into chat. Once a secret or a customer's phone number is in a log, you cannot reliably delete it. Card-industry rules (PCI DSS) apply the same thinking to card numbers: show at most the first 6 and last 4 digits. We apply an even stricter rule to BanglaQR data (see §2).

### 1.3 Trust boundary: everything the caller sends is untrusted

The manager is called by **FI systems we don't control**. The caller might be an FI's own backend, the optional FI Gateway, or something else entirely. So every header they send is **attacker-controllable input**. For logging, that means:

| Identifier | Who creates it | Trusted? | How we use it |
|---|---|---|---|
| `correlation_id` | **The manager**, a new GUID for every request (`CorrelationIdMiddleware`) | **Yes**, it can't be spoofed | **The primary key for every investigation.** It is also stored in `audit_logs.correlation_id` and `qr_validations.correlation_id`, and returned as `traceId` in error bodies. |
| `caller_correlation_id` | The FI, via the `X-Correlation-Id` header | No, it's a hint | Logged so an FI can say "look up **our** reference X". Never used as our own ID. |
| `request_id` (verify calls) | The FI, in the request body | No, but it's stored | Stored in `qr_validations`, unique per tenant. |
| `TraceId` | ASP.NET Core; **continued from the caller's `traceparent` header if one is sent** | No, it's a hint | Logged for free (step A1) so logs can join to traces once OpenTelemetry arrives (§8). Never pivot on it alone. |

**Why the manager doesn't accept a caller's correlation ID as its own.** If callers could choose the ID, two unrelated requests could share one ID and look like a single request in audit queries. That is a spoofing risk. The server-generated ID is a deliberate design decision in `CorrelationIdMiddleware`. **Do not change it.**

### 1.4 Structured logging, in one example

```csharp
// BAD: string interpolation. The values are baked into the text, so you
// can't filter on them.
_logger.LogInformation($"QR issued for tenant {tenantId}");

// GOOD: message template. TenantId becomes a separate JSON field you can filter on.
_logger.LogInformation("QR issued for tenant {TenantId}", tenantId);

// BEST (the manager already does this in 23 places): source-generated
// [LoggerMessage]. Compile-time checked, zero-allocation when the level is
// disabled, and it has a stable EventId.
[LoggerMessage(EventId = 7004, Level = LogLevel.Information,
    Message = "QR issuance accepted [{CorrelationId}] tenant {TenantId}")]
private static partial void LogQrIssued(ILogger logger, Guid correlationId, Guid tenantId);
```

**EventIds are forever.** Once `7004` means "QR issuance accepted", never reuse it for something else. Searches and alerts rely on it.

### 1.5 Log levels, as we use them

| Level | Use for | Production? |
|---|---|---|
| `Trace` / `Debug` | Developer detail | Off |
| `Information` | Normal business events (QR issued, QR verified, trust sync done) | On |
| `Warning` | Something odd that we handled (a rejected cert, a retry) | On |
| `Error` | A request or operation failed | On, and alert on it |
| `Critical` | The app cannot do its job (cannot reach the DB at boot) | On, and page someone |

**Masking is not level-dependent.** A `Debug` line that leaks a secret is still a leak, because someone will turn Debug on in production one day.

---

## 2. The rules: what may and may not be logged

### 2.1 NEVER log (at any level)

| Category | Examples in the manager |
|---|---|
| Credentials | `Authorization` header, JWT access tokens, `client_secret`, the bootstrap secret, the Argon2 hash, the mTLS cert password, the `Jwt:SigningKey` |
| Keys | Ed25519 private keys / PEM content (`-----BEGIN …`), anything from the key vault |
| BanglaQR customer data | **Tag 59** (beneficiary name), **Tag 26.03** (account / wallet number, often an MSISDN), **the full QR string** (it contains both), NID, phone numbers. Watch out for `ValidateQrResponse.RecipientName` / `RecipientPan`. |
| Bodies | HTTP request and response bodies, whole DTOs |
| DB parameters | EF Core `EnableSensitiveDataLogging()` must never be on outside a developer's machine |

### 2.2 OK to log

| Field | Why it is OK |
|---|---|
| `tenant_id`, institution code (`26.01+26.02`, e.g. `031008`), `key_version` | Business identifiers, not about a person |
| `correlation_id`, `caller_correlation_id`, `request_id`, `idempotency_key` | Request identifiers. None of them are credentials, and they are the keys you investigate with. The idempotency key is already written to `audit_logs.metadata`. |
| `payload_hash` | The SHA-256 of the QR string. It identifies one QR code without showing who it belongs to (§4). Treat it as a *pseudonym*, not anonymous data: someone who already knows the QR's details could confirm a guess. That's fine inside our access-controlled logs; just never publish it. |
| `qr_type`, verdict, reason code, amount, MCC (`52`) | Transaction attributes, not identity |
| Certificate subject and SHA-256 thumbprint | Public certificate data, needed for mTLS debugging |
| HTTP method, path (without query string), status, duration | The access log |

### 2.3 If you really need to identify an account in a log

Log only the **last 4 characters**: `01711111111` → `***1111`. Use the helper added in step A5, and never write your own substring logic inline. In practice you rarely need it, because `payload_hash` identifies the QR code (§4).

---

## 3. What the output will look like

There are two kinds of line. Knowing the difference is most of what you need to trace anything.

| Kind | How many per request | What it's for |
|---|---|---|
| **Summary line** (steps A2 + A8) | **Exactly one**, written when the request finishes | The whole story in one line: who, what, result, how long, business outcome. **Start here.** |
| **Detail lines** | Zero or more | Business events (e.g. EventId 7001 "key not ACTIVE"), warnings, exceptions with stack traces. Open these only when the summary line isn't enough (for example, status 500). |

Both kinds carry the same `correlation_id`, so you can always go from the summary line to its detail lines.

**The summary line for one QR verification** (one line on stdout, wrapped and abbreviated here):

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

Reading it: *FI tenant `3f1c…` (their reference `fi-ref-8a3d`) verified QR `9f2c…`. We answered 200 in 14 ms, and the verdict was `KEY_REVOKED`.* That's usually the whole investigation.

### 3.1 Where to find each field

| Meaning | JSON field |
|---|---|
| **Is this the summary line?** | `Category` = `Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware` |
| HTTP method / path / status / duration | `State.Method` / `State.Path` / `State.StatusCode` / `State.Duration` (summary line) |
| Our request ID | `State.correlation_id` (summary line); `Scopes[].correlation_id` (every line) |
| The FI's reference | `State.caller_correlation_id` (summary line); `Scopes[].caller_correlation_id` (every line) |
| Tenant | `State.tenant_id` (summary line); `Scopes[].tenant_id` (detail lines after authentication) |
| QR and business outcome | `State.payload_hash`, `State.verdict`, `State.reason_code`, `State.request_id`, `State.qr_type` (summary line) |
| Time (UTC) | `Timestamp` |
| Level | `LogLevel` |
| Human-readable message | `Message` |
| Which class logged it | `Category` |
| Stable event number | `EventId` (detail lines, see §4.4) |
| Exception + stack trace | `Exception` |
| Trace / span | `Scopes[].TraceId` / `SpanId` |

> **Why `State` and not `Scopes` for the summary fields:** `State` is a JSON **object**, so `State.verdict` always has the same path and is easy to filter on. `Scopes` is an **array** whose order can vary. Use it to find lines by ID (`grep`), not to filter by field.

---

## 3.5 Where the JSON log lives (dev machine, container, orchestrator)

The application **only writes to stdout** — there is no `Microsoft.Extensions.Logging.File` provider, no in-process rolling, and no Serilog sink in the current host. That is deliberate: stdout is the universal contract every container runtime and orchestrator knows how to capture, and it keeps the app 12-factor and stateless. The job of *retaining* and *querying* the log belongs to whatever is wrapping the process. **Each environment writes the log to a different medium; the choice matters because a process crash, a container restart, or a pod eviction erases the buffer.** Pick the right medium for the environment, or you will lose the only evidence you have the moment you need it.

| Environment | Who captures stdout | Where it lands | Retention | PII guard |
|---|---|---|---|---|
| **Dev machine** (`dotnet run` from a developer's laptop) | The shell. The repo's launch profile does NOT redirect. | `rvl-secure-bqr-manager/logs/api-stdout.log` — the file is created by the shell `>` redirection in the run command (see §3.5.1). Not by the app. | Until the developer deletes it. **There is no rotation.** Do not ship this file anywhere. | None — never leave the file on a shared drive. |
| **Single-VM / bare-metal deploy** (the current staging box; this is what F1 below covers) | The process supervisor (systemd unit, `nohup`, or a CloudWatch agent) — see §3.5.2 | A rolling file under `/var/log/sbqr/` (or the CloudWatch log group) | Daily roll, 30 days, then delete | The shipper (CloudWatch agent / Fluent Bit) is the one place that enforces masking. The app stays strict; the shipper is the safety net. |
| **Containerized deploy** (Docker / Compose — what we run for the dev DB and may use for one-off stacks) | The container runtime captures stdout via the configured **logging driver**. Default driver is `json-file`. | Either a file on the Docker host (rotated by the driver) or a remote sink like `awslogs`, `gcplogs`, `loki`, `splunk` | Set per-driver: `--log-opt max-size=50m --log-opt max-file=10` for `json-file`; or the remote sink's retention policy | Done by the driver / shipper, not by the app. |
| **Orchestrated deploy** (Kubernetes / AKS / EKS) | kubelet tails the container's stdout to `/var/log/pods/<ns>_<pod>/<container>/0.log` on the node | Picked up by a **DaemonSet** (Fluent Bit, Vector, Filebeat) and shipped to Loki / Elasticsearch / CloudWatch | Cluster-wide retention set in the sink (e.g. Loki compactor) | The DaemonSet is the only PII scrub point. |

**One rule across every environment:** the app never opens a log file itself. If you ever feel the need to add a file sink inside the host, stop — the answer is to fix the wrapper, not the app.

### 3.5.1 Dev machine: how the file under `logs/` actually gets created

The repo's `launchSettings.json` has two profiles (text, JSON) and **neither of them redirects stdout to a file**. The `logs/api-stdout.log` you see is created by the **outer** command you use to run the API — for example, the shell helper used by the e2e trace tool:

```bash
# manual: redirects stdout into a file the shell owns, not the app
mkdir -p logs
dotnet run --project src/Host/SBQR.Api > logs/api-stdout.log 2>&1
```

The trace tool in `reports/logging-plan-e2e/trace-tool/Trace/Program.cs` then tails that file via `FileShare.ReadWrite | FileShare.Delete` so it can read while `dotnet run` is still appending. **If you start the API from inside Visual Studio or Rider, no file is created at all — output just stays in the IDE's debug console**, and that's fine for development.

This is fragile on purpose: it makes it obvious the file is not a production artifact. For the dev-machine flow, the standards are:

- **Always start `dotnet run` from the repo root** so the `logs/` folder lands inside the submodule (it is already gitignored in effect — formalize the entry, see §3.5.4).
- **Delete the file when you're done** with an investigation; it grows unbounded.
- **Never copy it into a release artifact or a shared folder.** PII containment starts with "this file never leaves your laptop".
- **The trace tool's `logPath` is hard-coded to the developer's path** (`Program.cs` line ~17). Update it when you re-run on another machine, or pass it in via an env var (recommended follow-up).

### 3.5.2 Single-VM deploy: rolling file + log shipper

The pattern on the current dev EC2 box (and what §F1 below elaborates on) is:

1. The app writes to stdout via `Microsoft.Extensions.Logging.Console`.
2. A supervisor (systemd unit, or the CloudWatch agent reading stdout) writes those lines to `/var/log/sbqr/sbqr-api-YYYYMMDD.log` with **daily rotation** and a **30-day `retainedFileCountLimit`**.
3. The CloudWatch agent / Fluent Bit shipper tails the rotated files and forwards to the central log store, **scrubbing PII fields one more time** at the shipper as a defense in depth.

**Why the app does not write the rotated file itself.** Adding `Serilog.Sinks.File` (or `Microsoft.Extensions.Logging.File`, or a custom rolling sink) inside the host would:

- Couple the app to a filesystem layout the orchestrator does not know about.
- Make container images non-portable (the path `/var/log/sbqr/` does not exist in a Kubernetes pod).
- Duplicate the rotation logic that the platform already does better (logrotate / CloudWatch agent / Docker driver / kubelet).

The only place an in-app file sink is acceptable is a long-running bare-metal VM that lacks any log shipper — and even there, prefer `logrotate(8)` + stdout redirection over adding a sink, so the format stays identical to every other environment.

### 3.5.3 Containerized deploy: pick the driver, set rotation, no app change

For a single-host Docker or Compose stack, the **container runtime** owns the log. The standard pattern:

```bash
# docker run: pin rotation on the default json-file driver
docker run --name sbqr-api \
  --log-driver json-file \
  --log-opt max-size=50m \
  --log-opt max-file=10 \
  -p 5001:5001 \
  sbqr-api:latest
```

For Docker Compose:

```yaml
services:
  sbqr-api:
    image: sbqr-api:latest
    logging:
      driver: json-file
      options:
        max-size: "50m"
        max-file: "10"
```

`max-size=50m` and `max-file=10` caps each container at roughly 500 MB on disk, with the oldest file rolled out when the cap is hit. That is enough for ~30 days at the current request volume; tune to your retention requirement.

If you are deploying to AWS / GCP / a centralized Loki stack, **switch the driver instead of adding a sink**:

```yaml
# AWS — push straight to CloudWatch Logs
logging:
  driver: awslogs
  options:
    awslogs-region: us-east-1
    awslogs-group: sbqr-api
    awslogs-create-group: "true"

# Grafana Loki — push via the Loki Docker driver
logging:
  driver: loki
  options:
    loki-url: "http://loki:3100/loki/api/v1/push"
    loki-batch-size: "400"
```

In every case the **app stays unchanged**. The driver is configuration on the wrapper, not code in the host.

### 3.5.4 Orchestrated deploy: DaemonSet + Loki, not in-app files

For Kubernetes / AKS / EKS (the eventual target), the convention is:

1. Pod spec sets `restartPolicy: Always`, no `volumeMount`, no log path. Container just writes to stdout/stderr.
2. kubelet tails each container's stdout to `/var/log/pods/<namespace>_<pod>/<container>/0.log` on the node.
3. A **Fluent Bit (or Vector) DaemonSet** runs one pod per node, reads every container's log file under `/var/log/pods/`, parses the JSON, **applies a PII scrubber**, and ships to Loki (or Elasticsearch / CloudWatch).
4. Grafana queries Loki; retention is set by the Loki compactor (e.g. 30 days hot, 1 year cold in S3).

This pattern is the 12-factor answer. The app remains a black box; the platform owns retention, rotation, scrubbing, and access control. Implementing it is §F1 below; **do not** start writing files from inside the app "to make Loki work" — that defeats the whole architecture.

### 3.5.5 What to add to `.gitignore`

The dev-machine flow drops files under `rvl-secure-bqr-manager/logs/`. Formalize the ignore so a developer can never accidentally commit one:

```gitignore
# Local API logs — created by shell redirection of dotnet run stdout.
# Never committed. PII containment rule §3.5.1.
logs/
*.log
!.gitkeep
```

(Add a one-byte `.gitkeep` if you want the directory to exist on a fresh clone.)

### 3.5.6 Decision: when IS it right to add a sink inside the app?

Rare, but there are two:

1. **A long-running Windows service** on a partner bank's own machine, where there is no container runtime and no log shipper, and the bank's ops team will only ever read files. In that case add `Serilog.Sinks.File` with `rollingInterval: Day`, `retainedFileCountLimit: 30`, `outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"` so the file is still parseable JSON-line-per-line, and document the path in the partner's runbook.
2. **A crash dump** (`Serilog.Sinks.Debug` / `SelfLog`) gated to a debug-only path, never enabled in production.

For everything else, **fix the wrapper**. The line between "the app should log to a file" and "the platform should capture the app's log" is the line between a 12-factor app and one that you cannot move to a new orchestrator.

---

## 3.6 Run the API with logs captured (concrete recipes)

§3.5 explained the rule. This section is the recipe. There are **three
sinks** corresponding to the three layers of the deployment pyramid; the
app image is identical at every layer.

> **Supersedes §3.5.1 and §3.5.5.** §3.5.1 described a `logs/api-stdout.log`
> created by shell redirection from `dotnet run`, and §3.5.5 recommended a
> `logs/` gitignore entry. After review we **rejected both**: the dev's
> terminal is the sink for `dotnet run`, and the Compose `json-file`
> driver (rotation-capped) is the sink for the Compose stack. There is
> no `logs/` directory in the repo by design — `**/*.log` is the
> defense-in-depth rule in `.gitignore`. The narrative below is the
> authoritative recipe.

### 3.6.1 Dev laptop — bare `dotnet run` (terminal sink)

```bash
# From the submodule root
dotnet run --project src/Host/SBQR.Api
```

Stdout goes to the terminal. `Ctrl+C` to stop. There is **no `logs/`
directory in this repo by design** — see §3.6.5 for why. This is the
default mode for a developer running a quick smoke test or stepping
through a single request with the debugger attached.

### 3.6.2 Dev laptop — Docker Compose (json-file sink with rotation)

`docker/docker-compose.yml` pins `sbqr.api` to the `json-file` driver
with `max-size: 50m`, `max-file: 10` (~500 MB cap). Access:

```bash
docker compose -f docker/docker-compose.yml logs -f sbqr.api
docker compose -f docker/docker-compose.yml logs --tail=200 sbqr.api
```

The container's stdout flows through Docker's driver; the dev reads it
via `docker compose logs`. No file sink in the app, no sidecar, no extra
container.

### 3.6.3 Deployed — staging ACA and prod AKS

See `docs/misc/docker/DEPLOYMENT.md` §6 for the deployed sink table:

| Env | Wrapper | Sink | Retention |
|---|---|---|---|
| Dev (`dotnet run`) | The terminal emulator | Terminal scroll buffer | None |
| Dev (Compose) | Docker `json-file` driver | Container json-file under `/var/lib/docker/containers/...` | 50 MB × 10 files (~500 MB) |
| Staging (ACA) | ACA log stream → Log Analytics workspace | Application Insights / Log Analytics | 30 days hot, configurable |
| Prod (AKS) | kubelet tails container stdout → Fluent Bit DaemonSet → Loki | Loki + Grafana | 30 days hot in Loki; 1 year cold in S3 |

### 3.6.4 Cheat sheet for "where is the log right now?"

| Question | Answer |
|---|---|
| I ran `dotnet run` on my laptop | The terminal — there is no log file |
| I ran Compose on my laptop | `docker compose logs sbqr.api` (driver stores under `/var/lib/docker/containers/...`) |
| I deployed to staging (ACA) | `az containerapp logs show -n sbqr-api -g rg-sbqr-staging` OR Application Insights → `traces` / `customEvents` |
| I deployed to prod (AKS) | `kubectl logs -n sbqr deploy/sbqr-api` OR Grafana → Loki datasource → `{app="sbqr-api"}` |

### 3.6.5 What we do NOT support, by design

- A `logs/` directory in the repo (file sink — same anti-pattern as a
  Serilog file sink; the orchestrator doesn't know about it; survives
  across restarts on the dev's laptop; no rotation policy).
- A Serilog `Sinks.File` / `Sinks.RollingFile` configuration inside the
  app.
- A custom `IFileLogSink` abstraction "so devs can plug in their own."
- Per-environment branching in the host
  (`if (env.IsDevelopment()) WriteTo.File(...)`).
- A `Serilog.Sinks.Seq` package or a Seq sidecar container. The dev's
  terminal is the dev's sink; the platform's stdout-shipping is the
  deployed sink. Anything in between is a hidden filesystem sink in
  disguise.

If you find yourself wanting any of the above, **the wrapper is wrong**.
Console for the dev laptop, `json-file` rotation for Compose, ACA Log
Analytics for staging, Fluent Bit + Loki for prod — pick the layer, the
layer picks the sink.

---

## Part A: logging changes (about 0.5 day)

### A1. Switch the console output to JSON and add trace IDs

**File:** `src/Host/SBQR.Api/Program.cs`. Press **Ctrl+F**, find `var builder = WebApplication.CreateBuilder(args);`, and paste this directly below it:

```csharp
using Microsoft.Extensions.Logging;

// ── Logging: one console provider; the FORMAT comes from configuration ───
// appsettings.json selects "json" (every server, including the dev EC2 box,
// which runs ASPNETCORE_ENVIRONMENT=Development). Only a developer's own
// machine switches to readable text, via launchSettings.json.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();   // reads Logging:Console:FormatterName + FormatterOptions

// Put TraceId/SpanId from the current Activity on every log line.
builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId |
    ActivityTrackingOptions.SpanId |
    ActivityTrackingOptions.ParentId);
```

**File:** `src/Host/SBQR.Api/appsettings.json`. Replace the whole `Logging` section with this. It already includes the log levels for step A2, so you edit this file only once:

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

**File:** `src/Host/SBQR.Api/Properties/launchSettings.json`. Give Visual Studio two run profiles: readable text for everyday work, and JSON to see exactly what the server will print. This file is never deployed. Replace `"profiles"` with:

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

> **Why this is enough (for the new dev):**
> - The JSON console formatter ships with ASP.NET Core, so there is no new NuGet package.
> - **Why config and not `if (IsDevelopment())`:** the shared dev EC2 server runs as `Development` too (`docs/deployments.md`). An environment check would give that server plain text, and Part F's log shipping would then be unable to parse its lines.
> - `ClearProviders()` removes the default providers so each line is written once, not twice.
> - Writing to stdout is the whole contract. On EC2 (systemd) the logs land in `journalctl -u sbqr-api`, or in a file once Part F is done. The app does not care which.
> - `TraceId` costs nothing now, and it will join logs to traces the day OpenTelemetry is added (Part F2).

**Check (in Visual Studio):**

1. Pick **SBQR.Api** in the run dropdown and press **F5**. The console window shows readable single lines.
2. Stop, pick **SBQR.Api (JSON logs)**, and press **F5** again. Every line is now one JSON object. This is exactly what the server prints, because the server runs without a launch profile.

---

### A2. Tune log levels and add the request summary line

**Log levels:** already done. The `appsettings.json` block in step A1 includes them. `Microsoft.AspNetCore` is set to Warning to cut framework noise, and only the HTTP logging category is turned back on.

**File:** `src/Host/SBQR.Api/Program.cs`. Register HTTP logging next to the other `builder.Services.Add…` calls (anywhere before `var app = builder.Build();`):

```csharp
using Microsoft.AspNetCore.HttpLogging;

builder.Services.AddHttpLogging(o =>
{
    // Allow-list only. Never add RequestHeaders, RequestQuery, RequestBody,
    // or ResponseBody: they carry Authorization, QR payloads, and PII.
    o.LoggingFields = HttpLoggingFields.RequestMethod
                    | HttpLoggingFields.RequestPath
                    | HttpLoggingFields.ResponseStatusCode
                    | HttpLoggingFields.Duration;
    o.CombineLogs = true;   // one line per request, not two
});
```

Then **Ctrl+F** for `app.UseMiddleware<SBQR.Api.Infrastructure.CorrelationIdMiddleware>();` and add `app.UseHttpLogging();` **directly below it**, so the summary line runs inside the correlation scope:

```csharp
app.UseMiddleware<SBQR.Api.Infrastructure.CorrelationIdMiddleware>();
app.UseHttpLogging();
```

> **Why:** "show me every request that returned 5xx in the last hour" is the most common operations question. The field list is an **allow-list**, so a future .NET version cannot quietly start logging headers.
>
> This line is the **summary line** from §3. A2 gives it the HTTP facts; step A8 adds the tenant, the IDs and the business outcome to the same line.

**Check:** run the **JSON logs** profile and open `http://localhost:5001/health/live` in a browser. You see one `HttpLoggingMiddleware` line per request, showing method, path, status code, and duration, with no headers. Look at that line and confirm the `State` key names (`Method`, `Path`, `StatusCode`, `Duration`). The queries in §4 and Part F assume them, and the exact names can differ slightly between .NET versions.

---

### A3. Put the correlation ID (and the caller's reference) into the log scope

**File:** `src/Host/SBQR.Api/Infrastructure/CorrelationIdMiddleware.cs`. **Keep the server-side minting exactly as it is.** Add a log scope around `_next`:

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

    // NEW: every log line written during this request carries correlation_id.
    // The caller's X-Correlation-Id is recorded as DATA ONLY (never used as
    // our id) so an FI can ask us to look up "their" reference.
    var scope = new Dictionary<string, object?> { ["correlation_id"] = correlationId };
    var callerId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (IsSafeId(callerId))
    {
        scope["caller_correlation_id"] = callerId;
        // Step A8 adds one more line here. Leave it out until you reach A8.
    }

    using (logger.BeginScope(scope))
    {
        await _next(context).ConfigureAwait(false);
    }
}

// Caller-controlled input: cap the length and charset so nobody can inject
// fake log lines or megabyte-long values.
private static bool IsSafeId(string? value) =>
    !string.IsNullOrEmpty(value)
    && value.Length <= 64
    && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
```

Update the class `<remarks>` to mention that `caller_correlation_id` is logged but never trusted.

> **Why a scope and not a parameter on every call:** `BeginScope` attaches the value to **every** log line written inside the `using` block, including lines from EF Core and every module, without passing the ID around.
>
> **Why validate a header we only log:** anything an FI sends is untrusted input (§1.3). The JSON formatter escapes quotes and newlines, but a 1 MB header would still bloat every line of that request.

**Check:** run the app, then send a request with the header from a PowerShell window:

```powershell
Invoke-WebRequest http://localhost:5001/health/live -Headers @{ 'X-Correlation-Id' = 'test-123' }
Invoke-WebRequest http://localhost:5001/health/live -Headers @{ 'X-Correlation-Id' = ('x' * 500) }   # too long: must be ignored
```

For the first call, the console shows `correlation_id` (a new GUID) plus `caller_correlation_id: test-123`. For the second, there is no `caller_correlation_id` at all. Existing tests that check the ProblemDetails `traceId` and `audit_logs.correlation_id` must still pass: the ID is still created on the server.

---

### A4. Put `tenant_id` into the log scope (after authentication)

The tenant is known only after the JWT has been validated. In `Program.cs`, **Ctrl+F** for `app.UseAuthentication();` and put this **between** it and `app.UseAuthorization();`:

```csharp
app.UseAuthentication();

// Tag every subsequent log line with the caller's tenant_id (from the JWT
// "tenant_id" claim via ICurrentTenant). No-op for anonymous calls.
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

> **Why after `UseAuthentication`:** `JwtClaimCurrentTenant` caches its first answer for the whole request. If you read it before authentication, it caches `Guid.Empty` for the entire request.
>
> **Why it matters:** "what failed for FI X today?" becomes a single filter on `tenant_id`.

---

### A5. Add the masking helper

**New file:** `src/SharedKernel/SBQR.SharedKernel/Logging/LogMask.cs`

```csharp
namespace SBQR.SharedKernel.Logging;

/// <summary>
/// The only approved way to put an account / wallet / phone number into a
/// log line. Keeps the last 4 characters: "01711111111" → "***1111".
/// See docs/logging-plan.md §2.3.
/// </summary>
public static class LogMask
{
    public static string Last4(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 4 ? "***" : "***" + value[^4..];
}
```

Nothing uses it yet. It exists so the next developer who needs it does not write a leaky version of their own.

---

### A6. Fix the mTLS logger, which currently bypasses all of the above

**File:** `src/Host/SBQR.Api/Mtls/MtlsEndpointsExtensions.cs`. **Ctrl+F** for `LoggerFactory.Create`.

Today the code builds a **private** logger factory:

```csharp
var mtlsLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
```

That factory ignores the JSON format, the log levels, and the scopes, so mTLS lines come out as plain text. mTLS lines are exactly the ones you need during an FI onboarding incident. Fix it by letting DI build the validator, and resolve it inside Kestrel's config callback:

```csharp
// Register: DI supplies the real ILogger<ClientCertificateValidator>.
builder.Services.AddSingleton(sp => new ClientCertificateValidator(
    caCertificate,
    mtls.AllowedClientThumbprints,
    sp.GetRequiredService<ILogger<ClientCertificateValidator>>()));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // ApplicationServices is available here: Kestrel options are resolved
    // after builder.Build().
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

Delete the `mtlsLoggerFactory` line and the old `AddSingleton(clientCertificateValidator)`.

> **Why this is safe:** it is still one singleton. `TenantCertificateThumbprintSyncService` resolves the same instance from DI as before.

**Check:** `dotnet test tests/SBQR.Mtls.Tests` passes, and a rejected client cert produces a **JSON** `mTLS: rejected …` line.

---

### A7. Audit-failure log (EventId 6001): no code change, one rule

`AuditLogger.LogAuditWriteFailed` logs the whole entry, including `metadata`, so an operator can **replay the audit row from the log** if the database write fails. That behaviour is intentional; keep it.

Today every `metadata` payload contains only safe fields: `correlation_id`, `reason_code`, `qr_type`, `institution_code`, `key_version`, `idempotency_key`, `verdict`, `issuing_tenant_id`.

**New rule (add it to the PR checklist in §6):** `AuditEntry.Metadata` must never contain §2.1 data, because on failure it goes to the application log. The CI leak test in Part B enforces this.

---

### A8. Complete the summary line ("canonical log line"), about 1.5 hours

**What:** this is a pattern Stripe popularised, and much of the industry has adopted it: **one rich line per request** carrying everything important. Step A2 already gives the line method, path, status and duration. A8 adds:

| Field | Where it comes from | Answers |
|---|---|---|
| `correlation_id` | `CorrelationIdMiddleware` (A3) | Which request is this? |
| `caller_correlation_id` | The FI's `X-Correlation-Id`, validated (A3) | Which request is this, **in the FI's words**? |
| `tenant_id` | The JWT, via `ICurrentTenant` | Which FI? |
| `payload_hash` | The generate / validate result | Which QR code? (PII-free, see below) |
| `verdict`, `reason_code` | The validate result | What did we decide, and why? |
| `request_id` | The validate request (the FI's ID) | Which verify call, in the FI's words? |
| `qr_type` | The generate result | Static or dynamic? |

**What `payload_hash` is:** the lowercase hex SHA-256 of the exact QR string. Both APIs already return it, and `audit_logs.resource_id` stores it for every issuance and every verification. Anyone holding the QR code can recompute it, and it reveals nothing about the account holder. That makes it the best **PII-free handle for one specific QR code** (§4).

**How it works.** There are four small pieces, and each has one job:

```
 Controller                         SummaryLineInterceptor                 Summary line (A2)
 puts SAFE business fields   ──►    copies them, plus correlation_id  ──►  one JSON line with
 into HttpContext.Items             and tenant_id, onto the line           everything important
```

**1. The allow-list.** Create `src/SharedKernel/SBQR.SharedKernel/Web/SummaryLineFields.cs`. It follows the same pattern as `CorrelationContract.cs` in that folder: SharedKernel has no ASP.NET Core dependency, so it holds only the names.

```csharp
namespace SBQR.SharedKernel.Web;

/// <summary>
/// The ONLY business fields that may be added to the per-request summary log
/// line (docs/logging-plan.md step A8). Controllers set them on
/// HttpContext.Items; SummaryLineInterceptor copies them onto the line.
/// Being an allow-list is the point: a reviewer can see every field that can
/// ever appear, and none of them may carry §2.1 data (names, account numbers,
/// QR strings, secrets).
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

**2. The interceptor.** Create `src/Host/SBQR.Api/Infrastructure/SummaryLineInterceptor.cs`. `IHttpLoggingInterceptor` is built into .NET 8+ and exists exactly for adding fields to the HTTP log line.

```csharp
using Microsoft.AspNetCore.HttpLogging;
using SBQR.SharedKernel.Application;
using SBQR.SharedKernel.Web;

namespace SBQR.Api.Infrastructure;

/// <summary>
/// Turns the one-line-per-request HTTP log (step A2) into a "canonical log
/// line": adds correlation_id, tenant_id and the allow-listed business fields
/// from <see cref="SummaryLineFields"/>. See docs/logging-plan.md step A8.
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

Register it in `Program.cs`, next to `AddHttpLogging` from step A2:

```csharp
builder.Services.AddHttpLoggingInterceptor<SBQR.Api.Infrastructure.SummaryLineInterceptor>();
```

**3. One line in `CorrelationIdMiddleware`** (the placeholder comment you left in step A3). Replace the comment with:

```csharp
        context.Items[SummaryLineFields.CallerCorrelationId] = callerId;   // for the summary line
```

**4. The controllers fill in the business fields.** Add `using SBQR.SharedKernel.Web;` to both controllers.

`src/Modules/Verification/SBQR.Modules.Verification.Api/Controllers/QrValidationController.cs`, in `ValidateAsync`. Pull the request ID into a variable so the line logs the value actually used; the controller generates one when the FI sends none:

```csharp
var requestId = request.RequestId ?? Guid.NewGuid().ToString("N");

var result = await _mediator.Send(
    new ValidateQrCommand(
        request.QrPayload,
        requestId,
        request.RequestTimestamp ?? DateTimeOffset.UtcNow),
    cancellationToken);

// Summary line (docs/logging-plan.md A8). Safe fields ONLY:
// never RecipientName / RecipientPan / QrPayload.
HttpContext.Items[SummaryLineFields.PayloadHash] = result.PayloadHash;
HttpContext.Items[SummaryLineFields.Verdict]     = result.Verdict.ToString();
HttpContext.Items[SummaryLineFields.ReasonCode]  = result.ReasonCode;
HttpContext.Items[SummaryLineFields.RequestId]   = requestId;

return Ok(new ValidateQrResponse( /* unchanged */ ));
```

`src/Modules/QrGeneration/SBQR.Modules.QrGeneration.Api/Controllers/QrGenerationController.cs`, in `ToActionResult`, the success branch (one place covers both static and dynamic):

```csharp
if (result.IsSuccess)
{
    // Summary line (docs/logging-plan.md A8).
    HttpContext.Items[SummaryLineFields.PayloadHash] = result.Value.PayloadHash;
    HttpContext.Items[SummaryLineFields.QrType]      = result.Value.QrType;

    return StatusCode(StatusCodes.Status201Created, new GenerateQrResponse( /* unchanged */ ));
}
```

Generation **failures** need no extra field. The status code on the summary line already says why: 403 means tenant not active, 422 means key not ACTIVE, 409 means duplicate idempotency key, and 500 means signing or generation failed. The detail line for the same `correlation_id` (EventIds 7000–7003) has the specifics.

> **Why this design (for the new dev):**
> - **Controllers, not handlers, fill the fields.** The Application layer must not depend on `HttpContext` (the architecture tests enforce this). The controller is the thin HTTP edge.
> - **`HttpContext.Items`** is per-request storage built into ASP.NET Core. There is nothing to clean up afterwards.
> - **An allow-list, not "log anything".** A new field can't reach the line without editing `SummaryLineFields`, which a reviewer will notice.
> - **No new EventId to remember.** It is the same line as A2, just richer. Find it by its `Category` (§3.1).
> - **Rejected QRs are still status 200.** The verify endpoint returns every verdict as a normal result, so the line always has a verdict for a completed verification. A 400 means the request itself was invalid, so there is no verdict.

> **Watch out:** `ValidateQrResponse` also contains `RecipientName` (Tag 59) and `RecipientPan` (Tag 26.03). They are tempting to add to the summary line. **Don't.** They are §2.1 data, and the Part B canary test will fail if you do.

**Check:** the QR endpoints need a bearer token. Use the team's Postman collection (generate it with the `postman-export` skill if you don't have one), or the API explorer at `http://localhost:5001/docs/public`. Watch the console, using the **JSON logs** profile.

| Call | Expected summary line |
|---|---|
| Verify a valid QR | Status 200 with `verdict=VALID`, `payload_hash`, `request_id`, `correlation_id`, `tenant_id` |
| Verify a tampered QR | Status 200 with `verdict=INVALID_SIGNATURE` (or similar) and a `reason_code` |
| Generate a static QR | Status 201 with `payload_hash`, `qr_type`, `tenant_id` |
| Any call without a token | Status 401, with `correlation_id` and no `tenant_id` or business fields |
| Any call with `X-Correlation-Id: test-1` | `caller_correlation_id=test-1` |

---

## Part B: the CI leak test ("canary"), about 0.5 day

The built-in .NET logger has no hook that can scrub every log line before it's written, so the manager relies on developers never passing sensitive values to the logger. A **canary test** proves that holds. It runs the real QR flow with unmistakable fake values (a fake name, a fake account number), captures every log line, and fails the build if any of those values appears.

> **Before you start Part B:** the QR integration tests run against a real PostgreSQL in Docker (Testcontainers). **Docker Desktop must be running**, otherwise the tests fail at startup with a Docker connection error.
>
> **How these tests work:** they don't go through HTTP. `QrFlowHostBuilder` builds the real services, and the test calls the command handlers through MediatR. See `tests/SBQR.Qr.IntegrationTests/QrRoundTripTests.cs`: the canary test is a copy of its setup with fake values and one extra log capture.

### B1. A capturing logger for tests

**New file:** `tests/SBQR.Qr.IntegrationTests/Infrastructure/CapturingLoggerProvider.cs` (in **Solution Explorer**: right-click the `Infrastructure` folder → **Add → Class**)

```csharp
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SBQR.Qr.IntegrationTests.Infrastructure;

/// Captures every log line (message + structured state + scopes) in memory.
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
        public bool IsEnabled(LogLevel level) => true;   // capture Debug/Trace too

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

### B2. Let a test add its own log capture to the test host

**File:** `tests/SBQR.Qr.IntegrationTests/Infrastructure/QrFlowHostBuilder.cs`. Add an **optional** parameter to `Build`, and call it just before the service provider is built. Existing tests keep working unchanged.

```csharp
public (ServiceProvider Services, MutableCurrentTenant CurrentTenant, CapturingAuditLogger Audit)
    Build(PostgreSqlFixture postgres, Action<IServiceCollection>? configure = null)   // ← new parameter
{
    // … everything already here stays the same …

    configure?.Invoke(services);   // ← new: lets a test add e.g. a log capture

    return (services.BuildServiceProvider(validateScopes: true), currentTenant, audit);
}
```

### B3. The canary test

**New file:** `tests/SBQR.Qr.IntegrationTests/LogLeakCanaryTests.cs`. The setup (tenant + signing key) is copied from `QrRoundTripTests`; only the fake values and the capture are new.

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
/// Log-leak canary (docs/logging-plan.md Part B): runs the real QR flow with
/// unmistakable fake customer data and fails if any of it reaches a log line.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogLeakCanaryTests : IAsyncLifetime, IDisposable
{
    // Unmistakable fake values. If any of these shows up in a log line, it leaked.
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

            // Setup: same as QrRoundTripTests steps 1–2.
            var tenant = await mediator.Send(new CreateTenantCommand(
                InstitutionName: "ACME Bank", InstitutionCode: "031008"));
            var tenantId = tenant.Value.TenantId.Value;
            (await mediator.Send(new GenerateOrAdoptCryptoKeyCommand(
                TenantId: tenantId, Mode: CryptoKeyMode.Generate))).IsSuccess.Should().BeTrue();

            // Issue a QR with the fake customer data.
            currentTenant.TenantId = tenantId;
            var generated = await mediator.Send(new GenerateStaticQrCommand(
                RecipientName: CanaryName, RecipientCity: "Dhaka", RecipientPan: CanaryAccount));
            generated.IsSuccess.Should().BeTrue(generated.ErrorMessage);
            qr = generated.Value.QrPayload;

            // Verify it (success path), then a corrupted copy (rejection path).
            await mediator.Send(NewValidateCommand(qr));
            await mediator.Send(NewValidateCommand(qr[..^4] + "0000"));   // breaks the CRC
        }

        var all = string.Join('\n', capture.Lines);
        capture.Lines.Should().NotBeEmpty("the capture must actually be wired up");
        all.Should().NotContain(CanaryName);
        all.Should().NotContain(CanaryAccount);
        all.Should().NotContain(qr);                  // the full QR string
        all.Should().NotContain("-----BEGIN");        // any PEM / private key
    }

    private static ValidateQrCommand NewValidateCommand(string qrPayload) =>
        new(qrPayload, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}
```

Run it from **Test Explorer** (Test → Test Explorer, search `LogLeakCanary`), or with `dotnet test --filter LogLeakCanary`.

> **Why `LogLevel.Trace`:** it forces `Debug` and `Trace` lines to be produced too. A leak at Debug is still a leak (see §1.5).
>
> **If the test fails on first run:** good, that is what it is for. The failure message shows the line containing the value. Fix that log call (remove the value, or use `LogMask.Last4`), and never weaken the test.
>
> **Why `NotBeEmpty` first:** if the capture were wired up wrong, "no line contains the canary" would pass trivially. That assertion proves the test is really looking at logs.

### B4. Lock the summary-line allow-list

The canary test runs handlers, not HTTP, so it doesn't see the summary line from step A8. That line is protected by the `SummaryLineFields` allow-list instead. This tiny test makes any change to that list a deliberate, reviewed decision:

**New file:** `tests/SBQR.Qr.IntegrationTests/SummaryLineFieldsTests.cs`

```csharp
using FluentAssertions;
using SBQR.SharedKernel.Web;
using Xunit;

namespace SBQR.Qr.IntegrationTests;

public sealed class SummaryLineFieldsTests
{
    // If you add a field, this test fails ON PURPOSE. Update the list below
    // and ask the reviewer to confirm the new field is §2.2-safe
    // (docs/logging-plan.md): never a name, account number, QR string or secret.
    [Fact]
    public void Only_approved_fields_can_reach_the_summary_line() =>
        SummaryLineFields.All.Should().BeEquivalentTo(
            "log.caller_correlation_id", "log.payload_hash", "log.verdict",
            "log.reason_code", "log.request_id", "log.qr_type");
}
```

**Part B is done when** both new tests are green in Test Explorer, and the whole suite (`dotnet test`) is still green.

---

## 4. Tracing a complaint (the playbook)

**The idea in one sentence: our database is the index, and our logs are the detail.**

We never search logs by a customer's name or account number. Those values are not in the logs, by design (§2). Instead, we turn whatever the FI gives us into a `correlation_id` with one SQL query against **our** database, then look at that request's **summary line** (§3). Only if the summary line isn't enough do we pull the detail lines. Nothing depends on the FI's systems or the gateway.

> **Shortcut:** for anchors **B**, **C** and **D** below, you can also skip the SQL and find the summary line directly, because it carries `payload_hash`, `request_id` and `caller_correlation_id` (§4.2).

```
 Complaint ──► 1. What did the FI give us? ──► 2. Get the correlation_id ──► 3. Pull every log line
                  (the "anchor")                  (one SQL query)                (one log search)
```

### 4.1 Steps 1 + 2: from the anchor to a `correlation_id`

| # | What the FI gives you | How you get the `correlation_id` |
|---|---|---|
| **A** | An **error body** we returned | Its `traceId` field **is** our `correlation_id`. Go straight to step 3. |
| **B** | **The QR code itself**: a photo, an image, or the scanned string. This is the most common case. | Scan it to get the string, compute its `payload_hash`, and run query B below. You get the issuance **and every verification attempt** of that exact QR. |
| **C** | Their **`request_id`** for a verify call | Run query C below. The `verdict` and `reason_code` often answer the complaint without opening the logs at all. |
| **D** | **Their own reference** (the `X-Correlation-Id` they sent us) | Find the summary line with `caller_correlation_id = <ref>`. It shows our `correlation_id` and the outcome. |
| **E** | Only **"FI X, around 3 pm, it failed"** | Run query E (tenant + time window) and pick the failing row, or list that tenant's summary lines with status ≥ 400 in the window (§4.2). |

```sql
-- B. From the QR code.
--    payload_hash = lowercase hex SHA-256 of the EXACT QR string (no trailing newline):
--      bash:        printf '%s' '<qr string>' | sha256sum
--      PowerShell:  (Get-FileHash -Algorithm SHA256 -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('<qr string>')))).Hash.ToLower()
SELECT created_at,
       event_type,                              -- qr.generated | qr.validated | qr.validation.rejected
       correlation_id,
       metadata::jsonb ->> 'verdict'     AS verdict,
       metadata::jsonb ->> 'reason_code' AS reason_code,
       tenant_id                                -- who issued / who scanned
FROM   audit_logs
WHERE  resource_id = '<payload_hash>'
ORDER  BY created_at;

-- C. From the FI's verification request_id.
SELECT created_at, verdict, reason_code, institution_code, correlation_id
FROM   qr_validations
WHERE  request_id = '<request_id>';

-- E. Only "FI X, around 3 pm". First turn the institution code into a tenant_id,
--    then list what happened in the window.
SELECT tenant_id, institution_name FROM tenants WHERE institution_code = '<e.g. 031008>';

SELECT created_at, event_type, resource_id, correlation_id, metadata
FROM   audit_logs
WHERE  tenant_id = '<tenant_id>'
  AND  created_at BETWEEN '<from, UTC>' AND '<to, UTC>'
ORDER  BY created_at;
```

> **Time zones:** the database and the logs are both in **UTC**. A complaint about "3 pm" in Dhaka (UTC+6) means about **09:00 UTC**. This is the most common reason for "I can't find anything."

### 4.2 Step 3: read the summary line, then the details if needed

Until central logging exists (Part F1), the logs live on the server, and `grep` plus `jq` is enough. **Always start with the summary line.** `jq '.State'` prints just its useful part.

```bash
# (1) The summary line for one request
journalctl -u sbqr-api --since "2026-09-29 08:30" --until "2026-09-29 09:30" -o cat \
  | grep '"correlation_id":"<correlation_id>"' | jq 'select(.State.StatusCode) | .State'

# (2) Every summary line for one QR code (anchor B, no SQL needed)
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | grep '"payload_hash":"<payload_hash>"' | jq '.State'

# (3) The summary line for the FI's own reference (anchor D)
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | grep '"caller_correlation_id":"<fi-ref>"' | jq 'select(.State.StatusCode) | .State'

# (4) All failed requests for one FI in a window (anchor E)
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | jq -c 'select(.State.tenant_id == "<tenant_id>" and .State.StatusCode >= 400) | .State'

# (5) The DETAIL lines for one request: every line, including exceptions.
#     Use this only when the summary line isn't enough (e.g. status 500).
journalctl -u sbqr-api --since "…" --until "…" -o cat \
  | grep '<correlation_id>' | jq .
```

> **Why `select(.State.StatusCode)`:** the FI's reference and our `correlation_id` also appear in the `Scopes` of every detail line. Only the summary line has `State.StatusCode`, so this keeps just that one line. **Once Part F1 is done**, you stop using SSH: each of these becomes a saved CloudWatch Logs Insights query (F1.7), and the fields stay the same.

### 4.3 Worked example

> *Complaint from FI 031008: "Our customer says a shop's QR was rejected at about 3 pm today."*

1. **Anchor:** ask the FI for the QR image (anchor **B**). Scan it and compute the hash.
2. **Query B** returns two rows:
   - `2026-09-20 04:10Z  qr.generated`: the QR was issued nine days ago.
   - `2026-09-29 09:02Z  qr.validation.rejected  verdict=KEY_REVOKED`: rejected at 15:02 Dhaka time.
3. **That is the answer.** The issuer's signing key was revoked, and Annex B requires "reject & block". The system worked as intended; the shop needs a newly issued QR. **You never had to open the logs.**
4. If the verdict had been `VALID` but the FI still saw a failure, find that request's **summary line** (§4.2 command 1 or 2). It shows the status we returned and how long we took. If it says 200 in 14 ms, the problem is on the FI's side, and you can say so with evidence. If it says 500, run command 5 to see the exception.

### 4.4 EventId quick reference (detail lines in the manager code today)

The summary line has no EventId you need to remember; find it by its `Category` (§3.1). These are the **detail** events:

| EventId | Meaning | Level |
|---|---|---|
| 1001–1002 | Bootstrap client secret misconfigured (startup) | Warning / Error |
| 6001 | **Audit write failed.** The primary operation succeeded; replay the row from this log line. | Error |
| 6101–6111 | Bangladesh Bank trust-store sync (`DailyTrustSyncService`, `HttpTrustStoreClient`) | mixed |
| 7000–7003 | QR issuance **rejected or failed** (tenant not active, key not ACTIVE, signing failed, unexpected) | Warning / Error |
| 7004 | QR issuance accepted | Information |
| 7100–7103 | mTLS client certificate **rejected** (no cert, untrusted CA, missing EKU, unregistered thumbprint). These happen during the TLS handshake, so there is **no summary line**. | Warning |
| 7104–7106 | mTLS certificate accepted, thumbprint cache refreshed or refresh failed | Information / Debug / Warning |

### 4.5 What to ask FIs for (goes into the FI onboarding guide)

These don't need any code on our side. They just make complaints much faster to resolve:

1. **Send a unique `X-Correlation-Id` on every call and store it** against the transaction. It becomes their support reference (anchor **D**).
2. **Keep the `payload_hash`** we return from generate and validate (anchor **B** without needing the QR image).
3. **Keep their verify `request_id`** (anchor **C**).

An FI that does all three never has to fall back to anchor **E** ("around 3 pm").

---

## 5. Verification: done means all of these are true

| # | Check | How |
|---|---|---|
| 1 | Every non-Development log line is valid JSON | `journalctl … -o cat \| jq -c . > /dev/null` (or `docker logs … \| jq`) shows no parse errors |
| 2 | One summary line per request, with business fields | Call any endpoint 3 times and count the `HttpLoggingMiddleware` lines. Run the A8 check table. |
| 3 | `correlation_id` is still server-generated | Send `X-Correlation-Id: test-1`. The logs show `correlation_id=<guid>` and `caller_correlation_id=test-1`, and the audit row has the GUID. |
| 4 | Oversized or garbage caller IDs are dropped | Send a 500-character `X-Correlation-Id`. It must not appear in the logs. |
| 5 | `tenant_id` appears on authenticated requests | Call a tenant endpoint with a valid JWT and check the scope |
| 6 | mTLS lines are JSON | Present a bad cert and check the output |
| 7 | The complaint playbook works end to end | Generate a QR, verify it once with a valid key and once tampered, then follow §4 anchor **B** from the QR string: SQL finds the rows, and §4.2 command 2 finds the same three requests' summary lines (201 for the issuance, then 200 with `VALID` and 200 with a rejection verdict) under the same `correlation_id`s as the audit rows. |
| 8 | The leak canary passes in CI | `dotnet test` |
| 9 | Nothing else regressed | All existing tests pass, including `SBQR.Mtls.Tests` and the audit chain tests |

### 5.1 Security audit (VAPT) at a glance

What a security auditor checks, and where this plan answers it. The references in brackets are the OWASP ASVS / PCI DSS item numbers, for the audit report.

| The auditor asks | Our answer | Where |
|---|---|---|
| Are secrets and personal data kept out of logs? *(ASVS 7.1.1–7.1.2)* | Yes, and a CI test proves it on every build | §2, Part B |
| Are logins, access denials and bad input logged? *(ASVS 7.1.3, 7.2)* | Yes: 401/403/400 on every summary line; mTLS rejections; token decisions in `audit_logs` | A8, §4.4 |
| Can one event be rebuilt as a timeline? *(ASVS 7.1.4)* | Yes: UTC time, `correlation_id`, tenant, status, duration on one line | §3 |
| Can an attacker inject fake log lines? *(ASVS 7.3.1)* | No: JSON escaping, and caller headers are length- and character-checked | A1, A3 |
| Do users get an error ID instead of internal details? *(ASVS 7.4.1)* | Yes: the error body's `traceId` is our `correlation_id` | A3 |
| Is there a last-resort handler for unexpected errors? *(ASVS 7.4.3)* | Check that `AddProblemDetails()` + `UseExceptionHandler()` are registered. This is an existing VAPT item, not added by this plan. | — |
| Are logs protected, kept long enough, and watched? *(ASVS 7.3.3, OWASP A09, PCI 10.3–10.7)* | **Only after F1**: access control, retention, alerts, review routine | F1 |

---

## 6. PR checklist: add this to the repo's review rules

- [ ] No `$"…{value}"` interpolation in log calls. Use templates or `[LoggerMessage]`.
- [ ] New events use `[LoggerMessage]` with a **new, unique** EventId (update §4.4).
- [ ] Nothing from §2.1 is logged at **any** level, including Debug.
- [ ] Account or phone numbers, if genuinely needed, go through `LogMask.Last4`.
- [ ] No whole DTOs or request bodies in logs.
- [ ] `AuditEntry.Metadata` contains no §2.1 data (it is logged on audit failure).
- [ ] Exception messages you throw contain no §2.1 data. Exceptions get logged.
- [ ] Nothing from a request header is trusted as an identifier. Validate its length and charset before logging it.
- [ ] New summary-line fields are added **only** through `SummaryLineFields` (the allow-list), and must be §2.2-safe.
- [ ] If you add a new sensitive field, add a canary for it in Part B.

---

## 7. What we deliberately did NOT do, and why

| Idea (from v1/v2 or common advice) | Why not |
|---|---|
| A shared `rvl-sbqr-observability` repo via git submodules | Only one app we operate needs it. It would add repo, CI, and Docker plumbing for nothing. |
| Cross-app log joins with the FI Gateway | The gateway is run by the FI, so we never see its logs. The manager must be self-sufficient. We record the FI's reference (`caller_correlation_id`) instead. |
| Trusting the caller's `X-Correlation-Id` or `traceparent` as our ID | Untrusted input (§1.3). It is a spoofing risk to the audit trail. We record it as a hint only. |
| A custom JSON formatter with snake_case field names | Cosmetic. The built-in JSON console plus the §3.1 table are enough. |
| A runtime redaction "processor" for the built-in .NET logger | It has no such hook. Typed `[LoggerMessage]` call sites plus the canary test are stronger and simpler. |
| Replacing audit-failure metadata with a digest | That would break the documented replay path. The metadata is PII-free today, and a rule plus the canary keep it that way. |
| Searching logs by customer name or account (`last4` fields everywhere) | Not needed. `payload_hash`, `request_id`, and `caller_correlation_id` find the exact record without any PII (§4). |
| OpenTelemetry (traces, metrics, log export) | Needs a backend to send data to, and we have none yet. `TraceId` is already on every line, so adding OTel later needs no log rework (Part F2). |

---

## 8. Later, when there is a reason

- **Central logging, retention, alerts, OpenTelemetry, and a support lookup page** are all specified step by step in **Part F** at the bottom of this doc.
- **`Microsoft.Extensions.Compliance.Redaction`** (Microsoft's data-classification plus redaction for `[LoggerMessage]`). Worth adopting if the number of sensitive log sites grows. It is overkill today.

---

## Appendix: the reference FI Gateway (optional, about 0.5 day)

> **Context.** `rvl-sbqr-fi-gateway` is a reference app **we ship and an FI may choose to run**. Once deployed, its logs, and the PII in them, are the **FI's** responsibility, and we never see them. Our only responsibility is to **ship it clean**, so an FI that adopts it doesn't inherit a data leak. Do this after Parts A and B, or skip it if the gateway isn't being shipped to anyone yet.

Problems in today's gateway code, with the fix for each:

| # | Problem | Fix |
|---|---|---|
| G1 | Output is a plain-text template, not machine-readable | In `Observability/SerilogBootstrap.cs`, outside Development use `cfg.WriteTo.Console(new RenderedCompactJsonFormatter())` (from `Serilog.Formatting.Compact`, already a dependency of `Serilog.AspNetCore`). |
| G2 | `appsettings.json` sets `Microsoft` / `Microsoft.AspNetCore` to `Information`. Because `ReadFrom.Configuration` runs last, this **undoes** the Warning overrides written in code. | Set both to `Warning`. |
| G3 | The masking enricher only walks one level deep and misses spelling variants such as `access-token` | Replace it with the recursive, name-normalising version below. |
| G4 | `RequestLogContextMiddleware` pushes the raw `UserSub` (the IdP `sub` claim) onto **every** line. If the IdP puts a phone number in `sub`, every line leaks it. | If `sub` could be personal data, log an HMAC of it instead (`HMACSHA256.HashData(key, UTF8(sub))[..16]`, with the key from config). A plain SHA-256 of a phone number is reversible in minutes. |
| G5 | `QrProxyController.cs:71` logs `userSub` directly | Remove that argument. It's already on every line through the log scope (G4). |
| G6 | `OutboundHttpLoggingHandler` logs full URLs, including the query string | Log `request.RequestUri?.GetLeftPart(UriPartial.Path)` instead. |
| G7 | `CorrelationIdMiddleware` accepts any inbound `X-Correlation-Id` | Apply the same `IsSafeId` check as step A3, and generate a new ID when the check fails. |
| G8 | `UseSerilogRequestLogging()` runs **before** the correlation middleware, so the "request finished" line has no `CorrelationId` | Add `o.EnrichDiagnosticContext = (d, http) => d.Set("CorrelationId", http.Items[CorrelationIdMiddleware.ItemKey] as string);` |
| G9 | No leak test | Register a capturing `ILogEventSink` in tests via `cfg.ReadFrom.Services(services)` in `UseSerilog((ctx, services, cfg) => …)`, then assert the canaries never appear, the same approach as Part B. |

Recursive masking enricher (G3), replacing the body of `Observability/SensitiveDataMaskingEnricher.cs`:

```csharp
public sealed class SensitiveDataMaskingEnricher : ILogEventEnricher
{
    private const string Mask = "***";
    private const int MaxDepth = 8;

    // Compared AFTER Normalize(): lower-case, letters/digits only.
    // So "access_token", "AccessToken" and "Access-Token" are one entry.
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "authorization", "cookie", "setcookie", "xusersub",
        "pan", "primaryaccountnumber", "cardnumber", "accountnumber",
        "msisdn", "phone", "mobile", "nid", "nationalid", "pin", "otp",
        "qrpayload", "qrstring", "rawqr", "beneficiaryname", "accountname",
        "body", "requestbody", "responsebody", "rawbody",
    };

    // Any name CONTAINING one of these is masked (clientsecret, refreshtoken, …).
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

> **Why the gateway gets an enricher but the manager doesn't:** Serilog provides a hook that can rewrite every event before it is written. The built-in .NET logger does not. The manager relies on typed `[LoggerMessage]` call sites plus the canary test instead.

---

## Part F (future): go-live upgrades

Parts A and B make the logs **well-shaped**: JSON, a correlation ID on every line, no PII. Part F makes them **operable**: stored centrally, kept for a defined period, access-controlled, alerting on problems, and searchable without SSH. This is how modern teams run production. The steps are ordered from "must have before go-live" to "nice to have later".

| Step | What | Effort | When |
|---|---|---|---|
| **F1** | Central logging on CloudWatch: retention, access control, alerts, saved queries | about 0.5 day per environment, **infrastructure only, no app code** | **Before production go-live.** Do dev first, then staging, then prod. |
| **F2** | OpenTelemetry tracing | about 0.5 day of code, plus a backend decision | When "which step is slow?" becomes a real question |
| **F3** | A support lookup page | about 1–2 days | When complaint volume justifies it (it needs its own approved requirement) |

**How this fits the industry maturity ladder:** level 1 is structured logs with a summary line per request (Parts A and B). Level 2 is a central log platform (F1). Level 3 is full observability with linked traces (F2). Level 4 is support tooling (F3).

---

### F1. Central logging on CloudWatch

> **Release-blocking for production.** A security audit (VAPT) will check that logs are protected, retained and monitored. Parts A and B can't show that on their own; F1 does. See §5.1.

**Why grep on the server is not enough for a live fintech service:**

- Logs die with the server. If the EC2 instance is replaced or its disk fills up, the logs are gone.
- There is no guaranteed retention period, and auditors ask for one.
- Anyone with SSH can read everything, and nothing records who looked.
- Nobody is alerted when something breaks. You find out from complaints.
- It breaks down as soon as a second instance exists.

**The target picture:**

```
sbqr-api ──stdout JSON──► systemd ──► /var/log/sbqr/sbqr-api.log ──► CloudWatch agent
                                       (7-day local buffer)              │
                                                                         ▼
                                              Log group /rvl-sbqr/<env>/sbqr-api  (retention N days)
                                                 │                         │
                                     Logs Insights (search)        Metric filters ──► Alarms ──► SNS email
```

**Names used below** (they follow the `rvl-sbqr-…-<env>` convention in `docs/deployments.md`; `<env>` is `dev`, `staging` or `prod`):

| Thing | Name |
|---|---|
| Log group | `/rvl-sbqr/<env>/sbqr-api` |
| EC2 instance role | `rvl-sbqr-ec2-role-<env>` |
| Alert topic | `rvl-sbqr-alerts-<env>` |
| Metric namespace | `RVL/SBQR/<env>` |
| Reader group | `rvl-sbqr-log-readers-<env>` |

The `aws …` commands run from an **admin's machine** (region `ap-southeast-1`), not from the server. The `sudo …` commands run **on the server** over SSH.

#### F1.1 Let the EC2 instance write logs (IAM role)

1. **IAM → Roles → Create role.** Trusted entity: **AWS service → EC2**. Attach the managed policy **`CloudWatchAgentServerPolicy`**. Name the role `rvl-sbqr-ec2-role-<env>`.
2. **EC2 → Instances →** `rvl-sbqr-api-<env>` **→ Actions → Security → Modify IAM role**, and select the new role.

> **Note:** the S3 key vault keeps using its own explicit credentials (the `Storage` settings in `/etc/sbqr/sbqr.env`), so attaching this role does not change vault access. Moving the vault to the instance role, and deleting the access keys from disk, is a worthwhile separate change later.

#### F1.2 Create the log group and set retention (before starting the agent)

If the agent creates the group itself, the group **never expires**. That is expensive, and it is not what compliance asks for. So create it first:

```bash
aws logs create-log-group      --log-group-name /rvl-sbqr/prod/sbqr-api
aws logs put-retention-policy  --log-group-name /rvl-sbqr/prod/sbqr-api --retention-in-days 400
```

| Environment | Retention | Why |
|---|---|---|
| dev | 14 days | Debugging only |
| staging | 30 days | Covers a UAT cycle |
| prod | **400 days** (about 13 months) | **Placeholder: confirm with compliance.** The common benchmark (PCI DSS) is 12 months kept, 3 months immediately searchable. CloudWatch keeps all of it searchable. |

CloudWatch Logs encrypts data at rest by default. If the security policy requires a customer-managed key for production, add `--kms-key-id <key-arn>` to `create-log-group`.

#### F1.3 Send the app's output to a file

The CloudWatch agent reads **files**, not the systemd journal. So tell systemd to append the app's stdout to a file. Use a **drop-in override**: the deploy script never touches it, so it survives every deploy.

```bash
sudo install -d -o root -g adm -m 0750 /var/log/sbqr
sudo systemctl edit sbqr-api
```

In the editor that opens, add the following, then save:

```ini
[Service]
StandardOutput=append:/var/log/sbqr/sbqr-api.log
StandardError=append:/var/log/sbqr/sbqr-api.log
```

```bash
sudo systemctl restart sbqr-api
sudo tail -n 3 /var/log/sbqr/sbqr-api.log | jq .     # JSON lines should appear
```

Then rotate the local file so the disk never fills up. Create `/etc/logrotate.d/sbqr-api`:

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

> **Why `copytruncate`:** systemd keeps the file open while the app runs. `copytruncate` copies the file, then empties it in place, so systemd keeps writing without a restart. The local file is only a **7-day buffer**; CloudWatch is the real store.
>
> **Heads-up:** after this change, `journalctl -u sbqr-api` shows only service start and stop messages. The app's lines are in the file and in CloudWatch. Update the deploy skill's "tail the journal" step to `sudo tail -n 50 /var/log/sbqr/sbqr-api.log`.

#### F1.4 Install and start the CloudWatch agent

```bash
cd /tmp
wget https://amazoncloudwatch-agent.s3.amazonaws.com/ubuntu/amd64/latest/amazon-cloudwatch-agent.deb
sudo dpkg -i -E ./amazon-cloudwatch-agent.deb
sudo usermod -aG adm cwagent          # lets the agent read /var/log/sbqr
```

Create `/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json` (set the `<env>` part of the group name):

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

Start it, and make it start on boot:

```bash
sudo /opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent-ctl \
  -a fetch-config -m ec2 -s \
  -c file:/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json
systemctl is-active amazon-cloudwatch-agent
```

> **If nothing arrives in CloudWatch,** read `/opt/aws/amazon-cloudwatch-agent/logs/amazon-cloudwatch-agent.log`. "permission denied" means the `adm` group step was missed. "AccessDenied" means the IAM role from F1.1 is not attached.

#### F1.5 Control who can read the logs

Even without PII, logs are sensitive operational data. Create an IAM group `rvl-sbqr-log-readers-<env>`, attach this policy (fill in `<account-id>`), and add only the people who investigate:

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

> **Why:** this is least privilege, and CloudTrail records these API calls, so there is a record of who searched the logs. Investigators no longer need SSH access to the server at all, which also lets you tighten who has SSH.

**Optional hardening: nobody can delete or shorten the logs.** Add a deny statement to every IAM role except one emergency ("break-glass") admin role:

```json
{ "Effect": "Deny",
  "Action": ["logs:DeleteLogGroup", "logs:DeleteLogStream", "logs:PutRetentionPolicy"],
  "Resource": "arn:aws:logs:ap-southeast-1:<account-id>:log-group:/rvl-sbqr/prod/*" }
```

Auditors like this because it proves nobody can quietly erase evidence.

#### F1.6 Alerts: be told before the FI calls

**Step 1: create an alert topic and subscribe the team's email** (confirm the email AWS sends you):

```bash
aws sns create-topic --name rvl-sbqr-alerts-prod
aws sns subscribe --topic-arn arn:aws:sns:ap-southeast-1:<account-id>:rvl-sbqr-alerts-prod \
  --protocol email --notification-endpoint <team-email>
```

**Step 2: create a metric filter plus an alarm for each signal below.** A metric filter counts log lines that match a pattern, and the alarm fires when the count crosses a threshold:

| Metric | Alarm when | Why it matters |
|---|---|---|
| `AuditWriteFailed` | ≥ 1 in 5 min | An audit row is missing. Replay it from the log line (§4.4). **Compliance-critical.** |
| `AppErrors` | ≥ 5 in 5 min | Something is broken |
| `Http5xx` | ≥ 5 in 5 min | Callers are receiving server errors |
| `MtlsRejected` | ≥ 20 in 5 min | An FI certificate expired or is misconfigured, or someone is probing the endpoint |
| `TrustSyncIssue` | ≥ 1 in 60 min | The Bangladesh Bank trust-store sync is failing, so verifications may use stale keys |
| `AuthFailures` | ≥ 20 in 5 min | Many 401/403 responses: someone may be guessing credentials on `/v1/oauth/token` |

Here is the pattern for each metric:

```text
AuditWriteFailed  { $.EventId = 6001 }
AppErrors         { ($.LogLevel = "Error") || ($.LogLevel = "Critical") }
Http5xx           { ($.Category = "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware") && ($.State.StatusCode >= 500) }
MtlsRejected      { ($.EventId >= 7100) && ($.EventId <= 7103) }
TrustSyncIssue    { ($.EventId >= 6101) && ($.EventId <= 6111) && (($.LogLevel = "Warning") || ($.LogLevel = "Error")) }
AuthFailures      { ($.State.StatusCode = 401) || ($.State.StatusCode = 403) }
```

This is the full command pair for one metric. Repeat it for the others, changing the name, pattern, threshold and period:

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

**Step 3: test the wiring without waiting for a real incident:**

```bash
aws cloudwatch set-alarm-state --alarm-name rvl-sbqr-prod-audit-write-failed \
  --state-value ALARM --state-reason "wiring test"      # the team email should arrive
```

> **Before creating `Http5xx`,** open one real summary line in Logs Insights and confirm the status-code field is really `State.StatusCode`. Check the actual field name instead of assuming it.
>
> **Tune thresholds** after two weeks of real traffic. An alarm that fires every day gets ignored.

#### F1.7 Saved investigation queries (Logs Insights)

**How to use Logs Insights:** go to **CloudWatch → Logs Insights**, select the log group, **pick the narrowest time range you can** (queries are billed per GB scanned, and narrow ranges are also faster), paste a query, and click **Run**. Click **Save** to keep it in a folder named `sbqr-investigation`.

**How fields work:** Insights reads the JSON automatically. Top-level fields (`EventId`, `LogLevel`, `Category`, `Message`) and nested object fields (`State.verdict`, `State.StatusCode`) can be used directly. That's why the summary line keeps its fields in `State` (§3.1). `Scopes` is an **array whose order varies**, so search for IDs inside it with `@message like "<id>"`. IDs are unique GUIDs or hashes, so a substring match is exact in practice.

**Time zone:** set the console to **UTC**, or remember that logs are stored in UTC. "3 pm Dhaka" is 09:00 UTC.

**The same two-step habit as §4.2:** first read the **summary line** (Q1–Q5), and only if needed open the **detail lines** (Q6).

**Q1: the summary line for one request** (anchor **A**, or any `correlation_id` from SQL)

```
filter State.correlation_id = "<correlation_id>"
| display @timestamp, State.Path, State.StatusCode, State.Duration, State.tenant_id, State.verdict, State.reason_code, State.payload_hash
```

**Q2: the summary line for the FI's own reference** (anchor **D**)

```
filter State.caller_correlation_id = "<fi-ref>"
| display @timestamp, State.correlation_id, State.Path, State.StatusCode, State.verdict
```

**Q3: every request that touched one QR code** (anchor **B**, no SQL needed)

```
filter State.payload_hash = "<payload_hash>"
| display @timestamp, State.Path, State.StatusCode, State.tenant_id, State.verdict, State.reason_code, State.correlation_id
| sort @timestamp asc
```

**Q4: all failed requests for one FI in a window** (anchor **E**)

```
filter State.tenant_id = "<tenant_id>" and State.StatusCode >= 400
| display @timestamp, State.Path, State.StatusCode, State.correlation_id
| sort @timestamp asc
```

**Q5: all server errors, newest first**

```
filter State.StatusCode >= 500
| display @timestamp, State.Path, State.tenant_id, State.correlation_id
| sort @timestamp desc
```

**Q6: the DETAIL lines for one request** (use this when the summary line isn't enough, e.g. status 500)

```
fields @timestamp, LogLevel, EventId, Category, Message, Exception
| filter @message like "<correlation_id>"
| sort @timestamp asc
```

**Q7: verification verdicts per FI.** A business dashboard in one query.

```
filter State.Path like "/qr/validate" and ispresent(State.verdict)
| stats count(*) as calls by State.tenant_id, State.verdict
| sort calls desc
```

**Q8: health overview, top errors by EventId**

```
filter LogLevel in ["Error", "Critical"]
| stats count(*) as errors by EventId, Category
| sort errors desc
```

**Q9: mTLS rejections by certificate subject.** "Which FI can't connect?" (There is no summary line for these; see §4.4.)

```
filter EventId >= 7100 and EventId <= 7103
| stats count(*) as rejections by EventId, State.Subject
| sort rejections desc
```

**Q10: audit write failures to replay**

```
fields @timestamp, Message
| filter EventId = 6001
| sort @timestamp asc
```

#### F1.8 The complaint flow after F1

The §4 playbook doesn't change. Only step 3 changes: **no more SSH**, just run a saved query in the browser. In most cases, the summary line (Q1–Q4) is the answer.

| Anchor (§4.1) | Step 2 (SQL) | Step 3 (Logs Insights) |
|---|---|---|
| **A**: error body `traceId` | — | Q1, then Q6 if needed |
| **B**: QR code / `payload_hash` | Query B (optional) | Q3, then Q6 if needed |
| **C**: FI `request_id` | Query C | Q1, then Q6 if needed |
| **D**: FI's `X-Correlation-Id` | — | Q2, then Q6 if needed |
| **E**: "FI X, around 3 pm" | Query E (optional) | Q4, then Q6 if needed |

#### F1.9 Done means

| # | Check |
|---|---|
| 1 | A request made now can be found with Q1 within about 1 minute |
| 2 | The log group shows the agreed retention (not "Never expire") |
| 3 | The `set-alarm-state` test email arrived |
| 4 | Someone outside `rvl-sbqr-log-readers-<env>` gets AccessDenied on Logs Insights |
| 5 | After `sudo logrotate -f /etc/logrotate.d/sbqr-api`, new lines still arrive in CloudWatch |
| 6 | The deploy skill's "tail the log" step has been updated (F1.3 heads-up) |

#### F1.10 Who looks at the logs, and when

Alerts only help if someone acts on them. Agree on this once and write the names down:

- **Alert emails:** one named on-call person responds the same working day. An `AuditWriteFailed` alert is handled the same day, always.
- **Weekly (15 minutes):** run Q8 (top errors) and Q9 (mTLS rejections), and note anything unusual in the team channel.
- **Monthly:** confirm retention is unchanged and that the reader group contains only current staff.

> **Cost:** CloudWatch charges per GB ingested, per GB stored, and per GB scanned by queries. For this service's volume it is small, but check the AWS pricing page for `ap-southeast-1` and set a billing alarm. Narrow query time ranges keep the scan cost down.

---

### F2. OpenTelemetry tracing (when latency questions appear)

**What it adds:** logs tell you *what happened*. A trace tells you *where the time went*: a timeline of one request, for example "DB 3 ms → trust store HTTP 120 ms → signing 1 ms". Because every log line already carries `TraceId` (step A1), you can click from a log line straight to its trace.

**1. Choose a backend first** (this is the real decision; the code is small):

| Option | Notes |
|---|---|
| **AWS X-Ray** | Stays in AWS. The CloudWatch agent (or the AWS Distro for OpenTelemetry collector) can receive OTLP on the server and forward to X-Ray. Check the current agent docs for the `traces` section. |
| Grafana Tempo (self-hosted or Grafana Cloud) | Pairs well with Loki if you ever leave CloudWatch |
| Datadog, Honeycomb, New Relic | Commercial SaaS, fastest to get value, and it costs money |

**2. Packages** (add to `Directory.Packages.props`; use current versions, and the OTLP exporter must be **≥ 1.15.3**, per the advisory noted in the gateway's `Directory.Packages.props`):
`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `Npgsql.OpenTelemetry`.

**3. Code** in `Program.cs`, behind a switch that defaults to off:

```csharp
if (builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled"))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "sbqr-api",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o =>
                o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))   // skip health-check noise
            .AddHttpClientInstrumentation()   // trust store + S3 vault calls
            .AddNpgsql()                      // DB calls: SQL text only, never parameter values
            .AddOtlpExporter());              // endpoint from OTEL_EXPORTER_OTLP_ENDPOINT
}
```

Enable it per environment in `/etc/sbqr/sbqr.env`: `OpenTelemetry__Enabled=true` and `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`.

**Good to know:**

- `CorrelationIdMiddleware` already tags every request's Activity with `correlation_id`, so in the trace backend you can search traces by the same ID the logs use.
- **Trust boundary (§1.3):** if an FI sends a `traceparent` header, our request becomes part of *their* trace. That's useful for FIs that run tracing, but keep pivoting on our `correlation_id`. Revisit if it causes confusion.
- **§2 applies to traces too.** Never add request bodies or headers as span tags. Keep the instrumentation default that redacts URL query values.

---

### F3. Support lookup page (when complaint volume grows)

**What:** an **internal-admin** endpoint on the existing `v1.internal-admin` API surface, for example `GET /v1/admin/support/lookup?payloadHash=…` (or `requestId=…`, or `correlationId=…`). It returns one merged timeline from `audit_logs`, `qr_validations` and `qr_generations`, plus the ready-to-run Q1 query text for each `correlation_id` it found.

**Why:** support staff go from complaint to answer **without SQL or AWS console access**. Engineers are pulled in only for real escalations. This is how mature payment companies handle support: the business record is the entry point, and logs are the drill-down.

**Rules for building it:**

- Internal-admin authentication only.
- The lookup itself writes an audit row (`support.lookup`), recording who looked up what.
- It returns only §2.2 data, never the QR string, name or account number.
- It is a product feature, so it needs its own approved requirement doc (`AGENTS.md`) before it is built.
