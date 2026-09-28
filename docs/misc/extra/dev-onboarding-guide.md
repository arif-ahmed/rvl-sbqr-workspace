# FI Backend (BFF) — Developer Onboarding Guide

> **Audience:** You just joined the team. You can read C#, run `dotnet`, and you have a
> rough idea of what a QR code is. This guide gives you the *why*, the *how it flows*,
> and the *how to run it locally* — so that by the end of it you can answer
> "what does this service do and why does it exist?" without reading 200 pages of ADRs.

> **Time to read:** ~30 min. **Time to run end-to-end:** ~10 min after that.

> **Companion docs** (read *after* this one):
> - `docs/0001-fi-direct-oauth-to-bff-model.md` — the full architectural decision (in Bangla). Read this for the VAPT finding that triggered the rewrite.
> - `docs/slice-N-*.md` — per-feature deep dives.
> - `docs/dev-manual-testing.md` — the cookbook of `curl` recipes to run the stack. You will *use* this every day.
> - [`docs/dev-e2e-simulation-playbook-bn.md`](./dev-e2e-simulation-playbook-bn.md) — a shorter, Bangla-language walkthrough of this same stack, wired against the **real** `rvl-secure-bqr-manager` sbqr.api instead of the Node fake for the happy-path sections.
> - [`docs/e2e-playbook-bn.md`](./e2e-playbook-bn.md) — the command-verified edition of that playbook (real sbqr.api only, incident-based troubleshooting).

---

## 0. Table of contents

1. [Why does this service exist?](#1-why-does-this-service-exist)
2. [The two-token model — what the BFF keeps secret](#2-the-two-token-model--what-the-bff-keeps-secret)
3. [End-to-end request flow](#3-end-to-end-request-flow)
4. [Concrete worked scenario](#4-concrete-worked-scenario)
5. [What the codebase looks like](#5-what-the-codebase-looks-like)
6. [Run the whole stack locally](#6-run-the-whole-stack-locally)
7. [How to test it without a real phone](#7-how-to-test-it-without-a-real-phone)
8. [Where things can go wrong](#8-where-things-can-go-wrong)
9. [Glossary](#9-glossary)

---

## 1. Why does this service exist?

### 1.1 One-sentence answer

**This service is the BFF (Backend-for-Frontend) that every FI's mobile and web app
calls when they need to generate or validate a Secure BQR.** It is the only
piece of code that ever holds the FI's `client_secret` and the only piece that
ever holds the sbqr.api **platform token** — both live in this server's memory
and **never** leave it.

### 1.2 Why we had to build it

We used to let the FI mobile app talk **directly** to sbqr.api:

```
[FI mobile app]  →  POST /v1/oauth/token (with embedded client_secret)
                 →  POST /v1/qr/generate/static (with the JWT)
```

VAPT (the external security auditor) flagged this as a P1 finding: any binary
(`apk`, `ipa`) on a phone can be decompiled in minutes with free tools
(`jadx`, `apktool`), so the embedded `client_secret` is effectively public. Once
a secret leaks, an attacker can mint QR generation requests as the FI for the
lifetime of that secret.

The fix that aligns with industry standards (RFC 6749 §10.1, RFC 8252, OWASP
MASVS, NIST SP 800-63B) is the **BFF pattern**:

- The `client_secret` moves **out** of the mobile binary and **into** a
  server-side component that the FI runs in their own data centre.
- The sbqr.api **platform token** (which replaces the secret for actual API
  calls) also lives **only** in that server-side component.
- The mobile app calls the BFF, never sbqr.api directly.

This service is that server-side component.

### 1.3 What the BFF is and isn't

| ✅ The BFF *is* | ❌ The BFF *isn't* |
|---|---|
| A thin gateway / proxy in front of sbqr.api `v1.public` | A domain application — it has no merchant, customer, or QR-rule logic of its own |
| The holder of the platform token for its FI | The IdP for end users — user auth is delegated to the FI's existing login system |
| A single ASP.NET Core 8 service with three controllers | A multi-tenant SaaS — each FI runs their **own** instance of it |
| Stateless across requests except for the in-memory token | A database — it stores nothing persistent; restart = cold token fetch |

### 1.4 The boundary it enforces

```
┌─────────── FI's own network ──────────────────────────────────────────────┐
│                                                                          │
│   [FI mobile / web app]                                                   │
│        │                                                                 │
│        │  (only ever sees: HTTPS + JWT, never sbqr.api creds)              │
│        ▼                                                                 │
│   [FI Backend / BFF]   ←  client_secret + platform token live HERE       │
│        │                                                                 │
└────────┼─────────────────────────────────────────────────────────────────┘
         │  (mTLS / VPN tunnel)
         ▼
┌────────────────────── RVL platform ──────────────────────────────────────┐
│  sbqr.api /v1/oauth/token                                                │
│  sbqr.api /v1/qr/generate/static                                         │
│  sbqr.api /v1/qr/generate/dynamic                                        │
│  sbqr.api /v1/qr/validate                                                │
│  (lives behind VPN — only FI BFFs can reach it)                           │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 2. The two-token model — what the BFF keeps secret

The BFF juggles **two different kinds of token**. Confusing them is the #1
newcomer mistake. Here is the cheat sheet:

| | **User JWT (inbound)** | **Platform token (outbound)** |
|---|---|---|
| **Direction** | Mobile app → BFF | BFF → sbqr.api |
| **Format** | OIDC ID-token / access-token (signed JWT, RS256) | OAuth2 access-token (opaque string, format depends on platform) |
| **Subject** | A specific end user (`sub = user-12345`) | A specific FI tenant (`sub = client:{client_id}`) |
| **Lifetime** | Issued by the FI's IdP (typically 5–15 min) | Issued by sbqr.api's `/v1/oauth/token` (typically 10 min – 1 hour) |
| **Where stored** | Verifier only — BFF does not store; it just validates signature + claims on every request | Held **only** in the BFF process memory (singleton). Never logged, never returned to the client, never persisted |
| **Refresh** | App refreshes via FI's IdP | BFF refreshes itself proactively at ~80% of `expires_in`, with **single-flight** so concurrent calls share one fetch |
| **What it proves** | "The caller is end user X" | "The caller is allowed to use sbqr.api on behalf of FI Y" |

### 2.1 The cache lifecycle (the important part)

```
        BFF starts
            │
            │   ┌──────────────────────────────────────────────┐
            │   │  TokenPrewarmService (BackgroundService)    │
            ▼   ▼                                              │
       sleep(jitter)  ←── random 0..BootJitterSecondsMax (default 30s)
            │                                              ↑
            ▼                                              │
    GET https://sbqr.api/v1/oauth/token                   │
    (client_credentials, Basic auth)                      │
            │                                              │
            ▼                                              │
    TokenResponse { accessToken, expiresIn }              │
            │                                              │
            ▼                                              │
   store in PlatformTokenManager (singleton)  ─────────────┘
            │
            ▼
   mark /health/ready = 200
```

Key things to internalise:

- **Single-flight:** If 50 QR calls arrive in the same millisecond and no
  token is cached, only **one** HTTP call goes to sbqr.api. The other 49 wait
  on the same `Task`.
- **Refresh policy:** Refresh when **80% of lifetime has passed**. So a 10-min
  token refreshes at ~8 min, never later than that. This gives a 2-min cushion
  for clock drift and slow networks.
- **Force-refresh on 401:** If a QR call returns 401 (server says "your token
  is bad"), the BFF calls `ForceRefreshAsync()` to invalidate the cached
  token, fetches a new one, and retries the QR call **once**. This handles
  token rotation / revocation without any user-visible failure.
- **`Idempotency-Key` reuse on retry:** When the BFF retries after a 401, it
  sends the **same** `Idempotency-Key` it computed the first time. Never a
  fresh UUID — otherwise the platform would create two QR codes for one user
  action.
- **Pre-warm boot jitter:** When N instances restart together (e.g. CI deploy),
  the random jitter prevents all N from hammering `/v1/oauth/token` at once
  and tripping the platform's per-`client_id` rate-limit (10/60s in our spec).
- **Memory-only:** No Redis, no DB. Restart = a cold fetch — `Platform__BootJitterSecondsMax`
  exists *precisely* so a fleet restart doesn't synchronise those cold fetches.

### 2.2 Where to find the code

| File | Responsibility |
|---|---|
| `src/SBQR.FiGateway.Api/Platform/PlatformTokenManager.cs` | The singleton cache. Thread-safe `GetTokenAsync` with single-flight + pre-emptive refresh. |
| `src/SBQR.FiGateway.Api/Platform/HttpPlatformTokenFetcher.cs` | One method: POST `/v1/oauth/token` with `client_credentials`, parse the camelCase `TokenResponse`. |
| `src/SBQR.FiGateway.Api/Platform/PlatformAuthHandler.cs` | `DelegatingHandler` that wraps every outbound HTTP call to sbqr.api: attaches `Authorization: Bearer <token>`; on 401, calls `ForceRefreshAsync` and retries the inner request **once**. |
| `src/SBQR.FiGateway.Api/Platform/TokenPrewarmService.cs` | The `IHostedService` that runs at boot, sleeps for jitter, calls `GetTokenAsync` so `/health/ready` flips to 200. |
| `src/SBQR.FiGateway.Api/Platform/HttpPlatformQrClient.cs` | The outbound HTTP client for `/v1/qr/*`. Adds `Idempotency-Key`, `X-Correlation-Id`, `X-User-Sub` headers. |
| `src/SBQR.FiGateway.Api/Observability/PlatformTokenHealthCheck.cs` | The `/health/ready` check: green iff `manager.HasToken`. |

---

## 3. End-to-end request flow

Let's trace a single QR-generation call, mobile app → BFF → sbqr.api → response.

### 3.1 Sequence diagram

```
                  ┌──────────────┐       ┌──────────────┐       ┌──────────────┐
                  │  FI mobile   │       │  FI BFF      │       │  sbqr.api    │
                  │  app         │       │  (this svc)  │       │  (RVL)       │
                  └──────┬───────┘       └──────┬───────┘       └──────┬───────┘
                         │                      │                      │
  (0) User logs in      │                      │                      │
      against FI IdP    │                      │                      │
                         │                      │                      │
  (1) set Bearer JWT (mobile OIDC)              │                      │
                         ├─────────────────────►│                      │
                         │                      │ validate JWT         │
                         │  ◄──── 200 OK                                 │
                         │                      │                      │
  (2) POST /v1/qr/generate/static               │                      │
      Bearer JWT + body                         │                      │
                         ├─────────────────────►│                      │
                         │                      │ ① validate JWT again │
                         │                      │ ② contract-check body│
                         │                      │ ③ derive Idempotency-Key
                         │                      │ ④ getCachedPlatformToken()
                         │                      │    (or fetch + cache)│
                         │                      │ ⑤ POST /v1/oauth/token (cold)
                         │                      ├─────────────────────►│  (only on cold start)
                         │                      │ ◄─── 200 {accessToken, expiresIn} ─┤
                         │                      │                      │
                         │                      │ ⑥ POST /v1/qr/generate/static
                         │                      │    Authorization: Bearer <token>
                         │                      │    Idempotency-Key: ...
                         │                      │    X-Correlation-Id: ...
                         │                      │    X-User-Sub: ...
                         │                      ├─────────────────────►│
                         │                      │ ◄── 201 {qrPayload, payloadHash, ...}
                         │                      │                      │
                         │                      │ ⑦ validate response  │
                         │                      │    against pinned    │
                         │                      │    v1.public.json    │
                         │                      │ ⑧ mask + log         │
                         │                      │                      │
                         │ ◄─── 201 {qrPayload, payloadHash, ...}         │
                         │                      │                      │
                         │ ⑨ app shows QR                              │
                         ▼                      ▼                      ▼
```

### 3.2 Headers the BFF adds on every outbound call

| Header | Source | Purpose |
|---|---|---|
| `Authorization: Bearer <platformToken>` | `PlatformTokenManager` | Auth to sbqr.api |
| `Idempotency-Key` | `HMAC-SHA256(salt, "{user_sub}|{sha256(body).hex}|{unixSeconds / 60}")` → Base64Url — or **passed through** from the client if it supplied one | Safe retry of `generate` calls |
| `X-Correlation-Id` | Per-request GUID, also returned to the app | Cross-system trace |
| `X-User-Sub` | The user `sub` from the inbound JWT | Tenant-attribute user attribution on the platform side (hashed server-side; never raw PII) |

### 3.3 Inbound headers the BFF *expects* from the mobile/web app

| Header | Required | Notes |
|---|---|---|
| `Authorization: Bearer <JWT>` | ✅ required | OIDC ID/access token from the FI IdP, validated against its JWKS |
| `Idempotency-Key` | optional (will be derived if absent) | Echoed if present; otherwise the BFF derives one (see §3.2) |

A 401 from the BFF means **either** the JWT is missing or its signature
couldn't be verified (e.g. IdP key rotation raced — wait a few seconds and
retry).

### 3.4 Error contract

The BFF forwards the upstream `v1.public` error body unchanged. So if sbqr.api
returns `400 {"error":"..."}`, the BFF returns the same shape and same status
code. The only BFF-only status is `500` from misconfiguration (e.g. boot
guard triggered) — those return a masked `{"error":"internal"}` and get logged
in full server-side.

---

## 4. Concrete worked scenario

A customer walks into a coffee shop. They open their bank's app to pay. The
merchant shows a QR. The customer scans it. The customer's phone **does not**
talk to sbqr.api — it talks only to their own bank's BFF. Let's trace it.

### 4.1 Actors

- **Alice** — the customer. Her bank's app is `RVL Banking` (an FI mobile app).
- **Bob's Coffee** — the merchant. Their QR is sitting on the counter.
- **RVL Banking BFF** — *this service*, deployed in RVL Banking's data centre.
- **SBQR Platform** — the RVL-hosted sbqr.api that issues and validates QRs.

### 4.2 Step-by-step

| # | What happens | Where it happens |
|---|---|---|
| 1 | Alice opens `RVL Banking` and logs in with her credentials. The app talks to RVL Banking's IdP (not this service) and gets a 5-min JWT. | RVL Banking mobile app |
| 2 | Alice taps "Scan to pay". The phone camera reads `bob-coffee-qr-payload-string`. | Phone |
| 3 | The app POSTs to `https://api.rvlbank.example/v1/qr/validate` with the scanned payload, carrying Alice's JWT. | App → BFF |
| 4 | The BFF's `JwtBearerHandler` verifies Alice's JWT against RVL Banking's IdP's JWKS (signature, expiry, audience). Sub = `user-alice`. | BFF (inbound auth middleware) |
| 5 | The BFF checks the `X-Correlation-Id` (generates one if absent), the `Idempotency-Key` (derives one if absent), then asks `PlatformTokenManager` for a token. | BFF |
| 6 | `PlatformTokenManager` finds a token in cache (refreshed 2 min ago, 6 min to go). Returns it immediately. **No call to sbqr.api's `/oauth/token`.** | BFF |
| 7 | The BFF POSTs to `https://sbqr.api/v1/qr/validate` with `Authorization: Bearer <platformToken>`, `Idempotency-Key`, `X-Correlation-Id`, `X-User-Sub: user-alice`, body `{qrPayload: "..."}`. | BFF → sbqr.api (over mTLS/VPN) |
| 8 | sbqr.api validates the QR cryptographically and returns `200 {verdict: "VALID", recipientName: "Bob's Coffee", recipientPan: "1234…5678", ...}`. | sbqr.api |
| 9 | The BFF validates the response body against the pinned `v1.public.json` contract (catches upstream drift), masks PAN in logs, returns the body to Alice's app. | BFF |
| 10 | The app shows "Bob's Coffee — ৳250.00 — VALID" with a "Pay" button. | Phone |
| 11 | Alice taps "Pay". The app POSTs `/v1/qr/generate/dynamic` with `{qrPayload: <the scanned payload>, amountMinor: 25000, currency: "BDT"}`. | App → BFF |
| 12 | Steps 4–9 replay. The `Idempotency-Key` this time is `HMAC-SHA256(salt, "user-alice|sha256({...body...})|{unix/60}")`; if Alice accidentally double-taps within the same 60 s bucket, the second call computes the *same* key, sbqr.api replays the cached response, and Alice sees only one transaction. | Same as above |
| 13 | sbqr.api returns `201 {qrPayload: <new payment QR>, payloadHash: "...", qrType: "DYNAMIC", signatureKeyVersion: 1}`. | sbqr.api |
| 14 | The app shows the new payment QR. Bob's terminal scans it, settlement happens off-network. | — |

### 4.3 What *never* happens

- ❌ Alice's phone never sees `client_secret`.
- ❌ Alice's phone never sees the platform token.
- ❌ Alice's phone never talks to `sbqr.api`.
- ❌ The BFF never stores the token to disk.
- ❌ The BFF never logs the token value (only its last 4 chars + lifetime, in masked form).

### 4.4 What *can* go wrong, and how the BFF handles it

| Failure | Detection | BFF behaviour | Visible to user |
|---|---|---|---|
| sbqr.api is down (5xx) | Polly retry policy | Up to 3 retries with exponential backoff, then 502 to client | "Service unavailable, please retry" |
| Token expired mid-call | 401 from sbqr.api | `PlatformAuthHandler` forces a refresh and retries **once**, with same `Idempotency-Key` | Same response as a fresh call — no duplicate |
| Two requests in same 60 s with identical body | Deterministic `Idempotency-Key` | Both calls hit sbqr.api with same key; sbqr.api deduplicates | User sees only one transaction |
| Upstream changed response shape | `v1.public.json` schema check in `HttpPlatformQrClient` | 502 + critical log; alert fires | "Internal error" + SRE pages on-call |
| Cold start with no cached token | First call after boot | `PlatformTokenManager` fetches synchronously, holds all concurrent callers on the same `Task` | First user waits ~200 ms; everyone else is fast |
| Boot jitter prevents stampede | `TokenPrewarmService` waits 0–30 s before first fetch | Fleet restart doesn't trip sbqr.api's 10/60s rate limit | None |

---

## 5. What the codebase looks like

```
src/SBQR.FiGateway.Api/
├── Program.cs                            ← composition root; DI, middleware, config
├── appsettings.json                      ← base config
├── appsettings.Development.json          ← dev-only overrides (URLs, dev secrets)
├── Controllers/
│   └── QrProxyController.cs              ← /v1/qr/{generate/{static,dynamic},validate}
├── Platform/
│   ├── PlatformOptions.cs                ← bound from "Platform" config section
│   ├── PlatformTokenManager.cs           ← the in-memory cache + single-flight
│   ├── HttpPlatformTokenFetcher.cs       ← POST /v1/oauth/token
│   ├── HttpPlatformQrClient.cs           ← POST /v1/qr/* (with contract pinning)
│   ├── PlatformAuthHandler.cs            ← DelegatingHandler that adds Bearer + 401-retry
│   ├── TokenPrewarmService.cs            ← background boot-time fetch
│   └── IPlatformTokenManager.cs + IPlatformTokenFetcher.cs + IPlatformQrClient.cs
├── InboundAuth/
│   └── JwtBearer config in Program.cs    ← inbound JWT validation (Slice 2)
├── Idempotency/
│   └── IdempotencyKeyDeriver.cs          ← HMAC(sub + body + 60s bucket)
├── Observability/
│   ├── PlatformTokenHealthCheck.cs       ← /health/ready = "have a platform token?"
│   ├── SelfLivenessHealthCheck.cs        ← /health/live = "process is up?"
│   └── Serilog config (masking rules)    ← PAN / token / secret auto-masking in logs
├── Middleware/
│   ├── CorrelationIdMiddleware.cs        ← ensure every request has X-Correlation-Id
│   └── BootGuardMiddleware.cs            ← fail-fast if dev config used in Production
├── Auth/
│   └── ConfigureJwtBearerOptions.cs      ← inbound OIDC JWT bearer wiring
└── Contracts/
    └── v1.public.json                    ← pinned OpenAPI schema for response validation

tests/SBQR.FiGateway.Tests/
├── Platform/                             ← token manager + handler unit tests
├── InboundAuth/                          ← inbound JWT bearer tests
└── Integration/                          ← full BFF → WireMock.Net → /v1/qr/* flows

docs/
├── 0001-fi-direct-oauth-to-bff-model.md  ← the "why" (Bangla, read after this guide)
├── slice-2-inbound-jwt.md
├── slice-3-qr-proxy.md
├── slice-4-boot-guard-and-observability.md
├── slice-5-resilience.md
├── slice-6-contract-pinning-and-mtls.md
├── dev-manual-testing.md                 ← the curl cookbook; USE THIS for hands-on
└── dev-onboarding-guide.md               ← (this file)

tmp/
├── fake-idp.js                           ← dev-only IdP at :5105
├── fake-sbqr-api.js                      ← dev-only sbqr.api at :5200
├── fake-sbqr-api-401.js                  ← 401-once variant for retry-path tests
├── mock-mobile-app.js                    ← Node script that acts like a customer's phone
├── smoke.sh                              ← dev-manual-testing.md's Step 8 driver
├── smoke-401.sh                          ← dev-manual-testing.md's Step 9 driver
└── .env-dev / start-bff.ps1              ← local-dev env helpers
```

### 5.1 Where to read the code in this order

1. `Program.cs` — composition root. See what's wired up.
2. `Platform/PlatformTokenManager.cs` — the heart of the service.
3. `Platform/PlatformAuthHandler.cs` — how 401 retries work.
4. `Controllers/QrProxyController.cs` — the single endpoint group.
5. `Observability/PlatformTokenHealthCheck.cs` — the simplest test of "is the cache warm?"

---

## 6. Run the whole stack locally

You need **three terminals** (plus optionally a fourth for the mock mobile app).
This is the dev stack — nothing here is production.

### 6.1 Prerequisites

- .NET 8 SDK
- Node.js 18+ (for the fake IdP and fake sbqr.api)
- `curl`
- Optional: `jq` for pretty JSON

### 6.2 Terminal 1 — fake IdP

```bash
cd rvl-sbqr-fi-gateway-v1
node tmp/fake-idp.js
# → "fake IdP on http://localhost:5105"
```

Serves `/.well-known/openid-configuration`, `/.well-known/jwks.json`, and `/mint`
(the local JWT-mint endpoint used by `tmp/mock-mobile-app.js jwt`). The dev users
are `alice/s3cret` and `bob/bobs-pass`.

### 6.3 Terminal 2 — fake sbqr.api

```bash
node tmp/fake-sbqr-api.js
# → "fake sbqr.api (contract-conformant) on http://localhost:5200"
```

Serves `/v1/oauth/token`, `/v1/qr/generate/static`, `/v1/qr/generate/dynamic`,
`/v1/qr/validate`. **Every inbound request is logged to stdout** — that's how
you see the `Authorization`, `Idempotency-Key`, `X-Correlation-Id`, and
`X-User-Sub` headers the BFF attached.

For testing the 401-retry path, swap to:

```bash
node tmp/fake-sbqr-api-401.js
```

This version returns 401 on the **first** call to `/v1/qr/generate/static` and
200 on every subsequent call — exactly what forces the BFF's force-refresh
path.

### 6.4 Terminal 3 — the BFF

Use the env file `tmp/.env-dev` that ships with the repo, which contains
sane defaults for local dev:

**On bash / WSL / Git-Bash:**

```bash
set -a && source tmp/.env-dev && set +a
dotnet run --project src/SBQR.FiGateway.Api -c Release --no-build --no-launch-profile
```

**On Windows PowerShell:**

```powershell
Get-Content tmp/.env-dev | ForEach-Object {
  if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
  $name, $val = $_ -split '=', 2
  Set-Item -Path "Env:$name" -Value $val
}
dotnet run --project src/SBQR.FiGateway.Api -c Release --no-build --no-launch-profile
```

The `--no-launch-profile` flag is important: it skips `Properties/launchSettings.json`,
which would otherwise override your env vars with `localhost:8080` and dev defaults.

You should see (within ~2 seconds of boot):

```
info: SBQR.FiGateway.Api.Platform.TokenPrewarmService[0]
      Token pre-warm service starting.
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:8080
```

### 6.5 Confirm the stack is healthy

```bash
curl -s http://localhost:8080/health/live
# → Healthy
curl -s http://localhost:8080/health/ready
# → {"status":"Healthy","checks":[{"name":"platform-token","status":"Healthy",...}]}
curl -s http://localhost:8080/debug/platform-token
# → {"hasToken":true,"expiresAtUtc":"...","lifetimeSeconds":3600,"lastRefreshedUtc":"..."}
```

If `/health/ready` is still `Unhealthy` after a couple of seconds, check the
BFF log — the prewarm fetch may have failed (most often: `Platform:BaseUrl`
is wrong, or the fake sbqr.api isn't on the port you think).

### 6.6 The full curl cookbook

`docs/dev-manual-testing.md` has 10+ ready-to-paste `curl` snippets covering
every slice (JWT auth, token cache, idempotency, 401-retry, contract pinning,
boot guard, OpenTelemetry). **Use that doc as your day-to-day
reference.** This onboarding guide only covers the high-level tour.

---

## 7. How to test it without a real phone

The repo ships with `tmp/mock-mobile-app.js` — a tiny Node script that pretends
to be the customer's mobile app. It saves you from building an APK or a SPA
just to verify the BFF works.

### 7.1 JWT flow (mobile-style)

```bash
node tmp/mock-mobile-app.js jwt alice s3cret
```

Expected output:

```
================================================================
  Mock mobile app — mode=jwt
================================================================
[mock-app] mint: POST http://localhost:5105/mint as alice
[mock-app] mint: 200 OK — JWT (alg=RS256, kid=dev-key-1)

================================================================
  Step 1 — Generate a static QR
================================================================
[mock-app] generate-static: POST /v1/qr/generate/static
[mock-app] generate-static: 201 OK
[mock-app]   qrPayload          = 00020101021226180016sbqr.example0109MERCHANT1520041252…
[mock-app]   payloadHash        = 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
[mock-app]   qrType             = STATIC
[mock-app]   signatureKeyVersion= 1

================================================================
  Step 2 — Validate the same QR (read-back)
================================================================
[mock-app] validate: POST /v1/qr/validate (round-trip the QR back to sbqr.api)
[mock-app] validate: 200 OK
[mock-app]   verdict        = VALID
[mock-app]   trustSource    = sbqr.api
[mock-app]   recipientName  = MERCHANT
[mock-app]   recipientPan   = 1234…5678 (masked)
[mock-app]   qrClassification = COMMERCIAL
```

While the script runs, watch **Terminal 2** (fake sbqr.api) — you'll see each
upstream call with all the headers the BFF attached (`Authorization: Bearer …`,
`Idempotency-Key`, `X-Correlation-Id`, `X-User-Sub`).

### 7.2 What the script does NOT do (intentionally)

- **No retries / no offline queue.** It's a single round-trip demo.
- **No concurrent requests.** For those, use `tmp/smoke.sh` (10-step smoke) or
  write a small load test.
- **No auth-flow happy path only.** Negative paths are covered by the test
  project (`tests/SBQR.FiGateway.Tests/`) and the curl snippets in
  `docs/dev-manual-testing.md`.

---

## 8. Where things can go wrong

A few concrete failure modes you will hit. These are not exhaustive — they're
the ones that wasted the most time during development.

### 8.1 `curl /health/ready` returns Unhealthy forever

**Symptom:** `platform-token` check is `Unhealthy: No platform token has been acquired yet.`

**Most likely causes (in order of frequency):**

1. **Wrong `Platform:BaseUrl`** — the BFF is calling some other port, getting ECONNREFUSED, the prewarm fails silently. Check the BFF log for a 5xx from `HttpPlatformTokenFetcher`.
2. **Fake sbqr.api not running** — Terminal 2 is dead. Restart it.
3. **`Platform__ContractPath` points to a missing file** — if you've set this to a contract-pinning JSON and the file doesn't exist, the platform options validation may fail at startup. Set it to `""` to disable pinning.
4. **Stale BFF instance with old config** — `dotnet run` doesn't always kill the previous instance. Kill all `dotnet.exe` processes (`Get-Process dotnet | Stop-Process` on PowerShell) and start fresh.

### 8.2 All requests return 401

- The fake IdP's keypair was regenerated (you restarted Terminal 1). The BFF
  cached the old JWKS. Wait 5 min for auto-refresh, or restart the BFF.

### 8.3 The QR call returns 502 with `internal` error

The BFF is healthy and the JWT is valid, but the call to sbqr.api is failing.
Check **fake-sbqr-api's stdout** — if you don't see the request arrive there,
the BFF couldn't reach it (BaseAddress mismatch). If you see it arrive and
respond, but the BFF returns 502, the **contract pin** rejected the response
shape. The contract is NOT in this repo — it lives in the sibling repo at
`../rvl-secure-bqr-manager/contracts/v1.public.json` (`Platform:ContractPath`).
Note that pinning is only active when that env/config key is set; `tmp/.env-dev`
leaves it empty (pinning off).

### 8.4 Token keeps refreshing every minute

- **Symptom:** `debug/platform-token` shows `lastRefreshedUtc` changing every ~60s.
- **Cause:** The lifetime is being read as 60s instead of 3600s. Check that your
  fake sbqr.api is returning `expiresIn: 3600` (or whatever your real platform
  returns). The BFF uses `Platform__Scope` and `Platform__ClientId` only on the
  outbound `/oauth/token` call; the parsing of `expiresIn` happens
  case-sensitively against the camelCase `TokenResponse` contract.

### 8.5 `Idempotency-Key` is missing from upstream calls

- The BFF only derives one for **non-empty** request bodies, and only when the
  client did not already supply one.
- For `validate` calls, an `Idempotency-Key` is optional (validate is naturally
  idempotent). You'll see no `Idempotency-Key` header — that's correct.

---

## 9. Glossary

| Term | Meaning |
|---|---|
| **BFF** | Backend-for-Frontend. A server-side component that sits between a client app and one or more upstream APIs, hiding secrets and protocol details from the client. |
| **FI** | Financial Institution. A bank, MFS, or payment processor that uses sbqr.api on behalf of its customers. Each FI runs its own BFF instance. |
| **VAPT** | Vulnerability Assessment and Penetration Testing. The external audit that found the embedded `client_secret` flaw and triggered the BFF rewrite. |
| **Platform token** | The OAuth2 access token issued by sbqr.api's `/v1/oauth/token` to the FI BFF via `client_credentials`. Used to call all `/v1/qr/*` endpoints. Held only in BFF memory. |
| **User JWT** | The OAuth2 / OIDC ID or access token issued by the FI's IdP to its end-user app. Verified by the BFF on every request. |
| **single-flight** | A pattern where N concurrent callers for the same resource share one underlying fetch. The other N-1 callers await the same `Task`. Prevents thundering-herd against `/v1/oauth/token`. |
| **Idempotency-Key** | A header the BFF attaches to every `/v1/qr/generate/*` call so the platform can deduplicate retries (e.g. double-tap, network retry) within the same time bucket. Derived deterministically as `HMAC-SHA256(salt, "{user_sub}|{sha256(body).hex}|{unixSeconds/60}")` → Base64Url if the client didn't supply one. |
| **boot jitter** | A random 0–30s delay before the first `/v1/oauth/token` fetch at startup. Prevents fleet restarts from hammering the token endpoint. |
| **force-refresh** | The action the BFF takes when it receives a 401 from sbqr.api: invalidate the cached token, fetch a new one synchronously, and retry the failed call once. |
| **contract pinning** | Validating every upstream response against a frozen `v1.public.json` OpenAPI schema. If sbqr.api changes a field name, the BFF catches it on the next call instead of silently breaking the app. |
| **`v1.public`** | The name of the sbqr.api surface that FI BFFs call. Distinct from `v1.partner` (used by RVL internally) and `v1.merchant` (used by merchants). |
| **`X-Correlation-Id`** | A per-request GUID echoed back to the client and forwarded upstream. Lets you correlate one user action across BFF logs, platform logs, and FI IdP logs. |
| **`X-User-Sub`** | The end-user `sub` claim from the inbound JWT, forwarded to sbqr.api so it can attribute `qr.generated` events to a user (the platform hashes this server-side; it never sees raw PII). |
| **wire schema** | The exact JSON shape of a request or response. Pinned contracts enforce it; anything else fails the call. |
