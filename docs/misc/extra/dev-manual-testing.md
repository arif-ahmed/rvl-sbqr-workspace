# Dev Manual Testing — End-to-End Playbook

> **Audience:** You are a developer joining the team. You have .NET 8, Node 18+, and `curl` on your machine. You want to drive the **full BFF stack end-to-end**, see every header the BFF adds, and convince yourself that the cache, retries, idempotency, contract pinning, and BootGuard all actually work.
>
> This is a **playbook**, not a checklist. Every step is part of one continuous story: a customer in our scenario (**Alice**) pays a merchant (**Bob's Coffee**) through her bank's app — which talks to this BFF — which talks to sbqr.api. As you execute each command, the doc tells you what to look at in the other panes, what you should *think*, and what you'd see if the system misbehaved.

> **Before you start:** read [`docs/dev-onboarding-guide.md`](./dev-onboarding-guide.md) (~30 min). It explains *why* the BFF exists and the security model. This playbook assumes that context.

---

## Table of contents

1. [Before you start — prereqs, files, and four-terminal layout](#1-before-you-start)
2. [Phase A — bring up the stack](#2-phase-a--bring-up-the-stack)
3. [Phase B — sanity-check the BFF](#3-phase-b--sanity-check-the-bff)
4. [Phase C — the scenario: Alice pays Bob's Coffee](#4-phase-c--the-scenario-alice-pays-bobs-coffee)
5. [Phase D — edge cases and hardening](#5-phase-d--edge-cases-and-hardening)
6. [Phase E — observability](#6-phase-e--observability)
7. [Phase F — teardown](#7-phase-f--teardown)
8. [Troubleshooting](#8-troubleshooting)
9. [Sign-off checklist](#9-sign-off-checklist)

> **Coverage:** all six shipped slices — inbound JWT auth, in-memory platform-token cache with single-flight, outbound QR proxy with `Idempotency-Key` / `X-Correlation-Id` / `X-User-Sub`, 401 force-refresh + retry-once, boot jitter, `/health/live` and `/health/ready`, Serilog masking, Polly 5xx retry, OpenAPI contract pinning, mTLS client-cert, OpenTelemetry OTLP export, BootGuard fail-fast.

---

## 1. Before you start

### 1.1 Prerequisites

| Tool | Why | Quick check |
|---|---|---|
| .NET 8 SDK | Builds + runs the BFF | `dotnet --version` |
| Node.js 18+ | Runs the fake IdP and fake sbqr.api | `node --version` |
| `curl` | Drives the BFF from the shell | `curl --version` |
| `jq` *(recommended)* | Pretty-prints JSON responses | `jq --version` |
| Git Bash / WSL / PowerShell *(any)* | Shell to run the commands | — |
| *(Optional)* VS Code or Rider | Read + edit code while testing | — |

> **No real IdP account, no real sbqr.api.** The dev stack runs entirely on your laptop.

### 1.2 The four-pane layout

You will keep **four processes** running simultaneously. Each has a distinct job; reading the right pane at the right moment is half the skill.

```
┌───────────────────────┬───────────────────────┐
│  Pane 1 — IdP         │  Pane 2 — sbqr.api    │
│  (fake identity)      │  (fake platform)      │
│  :5105                │  :5200                │
│  node tmp/fake-idp.js │  node tmp/             │
│                       │    fake-sbqr-api.js   │
├───────────────────────┴───────────────────────┤
│  Pane 3 — BFF                                  │
│  :8080                                        │
│  dotnet run --project src/SBQR.FiGateway.Api   │
├───────────────────────────────────────────────┤
│  Pane 4 — Driver (you type here)               │
│  curl / mock-mobile-app.js                     │
│  This is where every command in this playbook  │
│  is executed.                                 │
└───────────────────────────────────────────────┘
```

### 1.3 IDE / terminal recommendations

Pick whichever matches your shell:

| Shell | What to run | How |
|---|---|---|
| **VS Code** | Built-in *Terminal* panel | Use `Ctrl+` backtick to open, then `+` button to split into 4 panes. Or open four VS Code windows side-by-side. |
| **VS Code Multi-root** | Each pane = a separate VS Code workspace | Pin the *fake-sbqr-api.js* pane on top-left, *fake-idp.js* top-right, BFF bottom-left, driver bottom-right. |
| **Rider / Visual Studio** | Use *Run* window for the BFF, + Windows Terminal for the other 3 | Rider's "Run" window can attach to a launched `dotnet` process and stream stdout. |
| **iTerm2 (macOS) / Windows Terminal / tmux** | Native pane splits | `tmux` splits: `Ctrl+B %` vertical, `Ctrl+B "` horizontal. Aim for a 2×2 grid. |
| **Bash only** | Four windows you alt-tab between | Works fine, just slower at reading three panes at once. |

> **Pro tip:** give each terminal pane a distinct colour or title. In VS Code: right-click the terminal tab → *Rename* → `IdP`, `sbqr.api`, `BFF`, `Driver`.

### 1.4 Files you'll touch

| File | Used for | Don't change during a test |
|---|---|---|
| `tmp/fake-idp.js` | The OIDC stand-in | It regenerates its RSA key on every restart — that invalidates every cached JWKS in the BFF. |
| `tmp/fake-sbqr-api.js` | The 99%-happy sbqr.api stand-in | Logs every inbound request to stdout. |
| `tmp/fake-sbqr-api-401.js` | The 401-on-first-call variant — needed for Phase D | Use this *only* in Phase D. |
| `tmp/mock-mobile-app.js` | Pretends to be a customer's mobile app (JWT mode) | Optional. Replaces the raw `curl` for the "Alice pays Bob's Coffee" scenario. |
| `tmp/.env-dev` | Local-dev env vars (BFF address, IdP address, dev creds) | Don't edit; the playbook assumes the file as shipped. |
| `tmp/smoke.sh` and `tmp/smoke-401.sh` | One-shot drivers that wrap the curl recipes from Phase C and Phase D-2 | Optional. Run them once to see the canonical happy path. |

### 1.5 Workspace orientation

```bash
# from anywhere on your machine:
cd <repo-root>/rvl-sbqr-fi-gateway-v1
pwd          # confirm
ls tmp/      # you should see fake-idp.js, fake-sbqr-api.js, fake-sbqr-api-401.js, mock-mobile-app.js, .env-dev
ls docs/     # dev-onboarding-guide.md and dev-manual-testing.md (this file)
```

---

## 2. Phase A — bring up the stack

### Step A-1 — start the fake IdP

**In Pane 1:**

```bash
node tmp/fake-idp.js
```

**Expect** (within 1 second):

```
fake IdP on http://localhost:5105
```

**What it serves** (skim once, you don't need to memorise):
- `GET /.well-known/openid-configuration` — OIDC discovery
- `GET /.well-known/jwks.json` — public key (regenerated on every start)
- `POST /mint` — dev convenience: mint a JWT with whatever claims you want

> **Why this matters:** the IdP's *keypair* lives in this process's memory only. If you restart the IdP, every JWT in flight becomes unverifiable. You'll need to restart the BFF too (to clear its JWKS cache).

### Step A-2 — start the fake sbqr.api

**In Pane 2:**

```bash
node tmp/fake-sbqr-api.js
```

**Expect:**

```
fake sbqr.api (contract-conformant) on http://localhost:5200
```

**What it serves** (skim once):
- `POST /v1/oauth/token` — returns a stub `accessToken`
- `POST /v1/qr/generate/static` — returns a real-shape `GenerateQrResponse`
- `POST /v1/qr/generate/dynamic` — same shape, DYNAMIC flag
- `POST /v1/qr/validate` — returns a real-shape `ValidateQrResponse`
- `POST /__contract__/invalid` — returns a body that **violates** the contract (Slice 6 sad path)

> **Pane 2 = your ground truth.** Every request the BFF makes to sbqr.api will appear here as a JSON dump of headers + body. *This is the single most useful pane for debugging.* During the scenario, leave it visible.

### Step A-3 — start the BFF (pointing at the two fakes)

**In Pane 3**, source the dev env file, then start:

**On bash / Git-Bash / WSL:**

```bash
set -a && source tmp/.env-dev && set +a
dotnet run --project src/SBQR.FiGateway.Api -c Release --no-build --no-launch-profile
```

**On PowerShell:**

```powershell
Get-Content tmp/.env-dev | ForEach-Object {
  if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
  $name, $val = $_ -split '=', 2
  Set-Item -Path "Env:$name" -Value $val
}
dotnet run --project src/SBQR.FiGateway.Api -c Release --no-build --no-launch-profile
```

> **First-time build:** drop `-c Release --no-build` so the SDK compiles for you:
> ```bash
> dotnet run --project src/SBQR.FiGateway.Api --no-launch-profile
> ```

> **Why `--no-launch-profile`?** Because `Properties/launchSettings.json` in this project overrides your env vars with `localhost:8080` and dev defaults. If you skip this flag, your `Platform:BaseUrl=http://localhost:5200` env value gets silently replaced and the BFF tries to talk to the wrong host.

**Expect within ~2 seconds:**

```
info: SBQR.FiGateway.Api.Platform.TokenPrewarmService[0]
      Token pre-warm service starting.
info: SBQR.FiGateway.Api.Platform.HttpPlatformTokenFetcher[0]
      TokenPrewarm: client_credentials fetched in 42ms, expiresIn=3600
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:8080
```

If you see that `Token pre-warm service starting` log followed by a `client_credentials fetched in NNNms` line, **everything is wired correctly** — the BFF was able to call `POST :5200/v1/oauth/token` and cache the result.

**If you see an error instead**, jump to [§8 Troubleshooting](#8-troubleshooting) right now. Do not proceed.

---

## 3. Phase B — sanity-check the BFF

These are the smallest commands that prove the stack is alive. Run them all in Pane 4.

### Step B-1 — root + health endpoints (anonymous, 200)

```bash
curl -s -o /dev/null -w "GET /            → %{http_code}\n" http://localhost:8080/
curl -s -o /dev/null -w "GET /health/live → %{http_code}\n" http://localhost:8080/health/live
curl -s -o /dev/null -w "GET /health/ready → %{http_code}\n" http://localhost:8080/health/ready
```

**Expect:** `200 / 200 / 200`.

If `/health/ready` is `503`, the pre-warm hasn't finished or failed:

```bash
curl -s http://localhost:8080/health/ready | jq .
```

Look at Pane 3 for the message. Most often it's `No platform token has been acquired yet.` — check Pane 2 for a `POST /v1/oauth/token` log entry. If absent, the BFF is reaching a different port (look at `debug/platform-token` to see if `hasToken` is true after a few seconds).

### Step B-2 — inspect the platform-token cache

```bash
curl -s http://localhost:8080/debug/platform-token | jq .
```

**Expect:**

```json
{
  "hasToken": true,
  "expiresAtUtc": "2026-09-23T17:05:29+00:00",
  "lifetimeSeconds": 2871.76,
  "lastRefreshedUtc": "2026-09-23T16:17:29+00:00"
}
```

**What to think:**
- `hasToken: true` means the in-memory cache is warm.
- `lifetimeSeconds` should be ≤ the platform's `expires_in` (3600). If it's much smaller, the BFF is reading a stale token or the lifetime math is wrong (see §8).
- `lastRefreshedUtc` should match a `POST /v1/oauth/token` log line in **Pane 2** within the last few seconds (one entry) — that was the pre-warm fetch. If you see no entries, the token was loaded before you started watching.

### Step B-3 — confirm the BFF added the right outbound headers

Hit any QR endpoint WITHOUT a token. You'll get 401 (good — auth is enforced), but you'll also see Pane 2 was NOT hit. That's the proof that the inbound auth layer ran *before* the outbound handler:

```bash
curl -i -X POST http://localhost:8080/v1/qr/generate/static \
  -H 'content-type: application/json' \
  -d '{"recipientName":"X"}'
```

**Expect:** `HTTP/1.1 401 Unauthorized`, `WWW-Authenticate: Bearer`.

**Confirm in Pane 2:** no new entry appears. The request was rejected at the inbound auth middleware; the BFF never reached sbqr.api.

---

## 4. Phase C — the scenario: Alice pays Bob's Coffee

> **Scenario.** Alice is a customer of RVL Banking. She opens the app to pay Bob's Coffee. She first **scans** Bob's static QR (validate), then **generates** her own dynamic QR for the exact amount. Both calls go to the BFF — never to sbqr.api directly.
>
> Each sub-step below is one HTTP call. Open **Pane 2** before you start — every action you take here will appear there.

**Pick a driver:**

- **A. Use the mock-app script** (no curl commands to memorise): in Pane 4 run `node tmp/mock-mobile-app.js alice s3cret`. The script mints a JWT, generates a static QR, and validates it. Read §4.7 below for what to look at.
- **B. Use the curl recipes** here — for finer control, debugging, or to hit specific slices. This is the path this playbook walks you through step-by-step.

This playbook walks **Path B**. The mock-app script wraps the same commands (§4.6 has the mapping).

### Step C-1 — mint Alice's JWT

```bash
TOKEN=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"user-alice"}' \
  | jq -r .token)

echo "$TOKEN" | head -c 32; echo "…"
```

**Expect:** a JWT string beginning with `eyJ` (Base64Url of the header JSON).

**What happened:** the fake IdP signed a JWT with the claims `iss/aud/sub` you'll use downstream. In a real deployment Alice's FI app obtains the JWT from the bank's IdP (mobile OIDC), then sends it on every request as `Authorization: Bearer …`.

> **Look at Pane 1:** the `/mint` call log line shows the body the BFF side will read.

### Step C-2 — Alice scans Bob's static QR → validate

```bash
curl -i -H "Authorization: Bearer $TOKEN" \
  -X POST http://localhost:8080/v1/qr/validate \
  -H 'content-type: application/json' \
  -H 'X-Correlation-Id: trace-alice-scan' \
  -d '{"qrPayload":"00020101021226180016sbqr.example0109MERCHANT152004125203BDT540510005802BD5910MERCHANT6010DHAKA BD6304ABCD"}'
```

**Expect (200):**

```json
{
  "verdict": "VALID",
  "trustSource": "sbqr.api",
  "reasonCode": "OK",
  "institutionCode": "SBQR",
  "payloadHash": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "recipientName": "MERCHANT",
  "recipientPan": "1234567890123456",
  "qrClassification": "COMMERCIAL"
}
```

**And critically — look at Pane 2. You should see exactly ONE new log entry:**

```
[qr/validate] POST /v1/qr/validate
  Headers: {
    "authorization": "Bearer fake-platform-token-1763918…",
    "x-correlation-id": "trace-alice-scan",
    "x-user-sub": "user-alice",
    "content-type": "application/json; charset=utf-8",
    ...
  }
  Body: {"qrPayload":"000201010212…"}
```

**Compare against your request — verify each row:**

| Header you sent | What BFF attached | What you should see in Pane 2 |
|---|---|---|
| `Authorization: Bearer <jwt>` | (verified → `sub=user-alice`) | `x-user-sub: user-alice` ✓ |
| `X-Correlation-Id: trace-alice-scan` | echoed verbatim | `x-correlation-id: trace-alice-scan` ✓ |
| *(none)* | server-to-server bearer | `authorization: Bearer fake-platform-token-…` ✓ |
| *(none)* | NO `idempotency-key` for validate | ✓ |

**Notice:** there is NO `idempotency-key` header in Pane 2 — `validate` is read-only and the BFF never derives one.

### Step C-3 — Alice generates her own dynamic QR (with derived `Idempotency-Key`)

```bash
curl -i -H "Authorization: Bearer $TOKEN" \
  -X POST http://localhost:8080/v1/qr/generate/dynamic \
  -H 'content-type: application/json' \
  -H 'X-Correlation-Id: trace-alice-pay' \
  -d '{"recipientName":"Alice","recipientCity":"Dhaka","recipientPan":"1234567890123456","amountMinor":25000,"currency":"BDT"}'
```

**Expect (201):** response with `qrPayload`, `payloadHash`, `qrType: "DYNAMIC"`, `signatureKeyVersion`.

**In Pane 2, you should see headers matching the table below:**

| Inbound from your app | What BFF attached | Visible in Pane 2 |
|---|---|---|
| `Authorization: Bearer <jwt>` → `user-alice` | forwarded | `x-user-sub: user-alice` ✓ |
| `X-Correlation-Id: trace-alice-pay` | echoed | `x-correlation-id: trace-alice-pay` ✓ |
| *(none)* | server-to-server bearer | `authorization: Bearer fake-platform-token-…` ✓ |
| *(none)* | derived `Idempotency-Key` | `idempotency-key: <Base64Url, ~43 chars>` ✓ |

**Note** the `idempotency-key` header this time — it should be **present** for `generate/dynamic` but absent for `validate`. If you see one for validate, that's a bug.

### Step C-4 — verify idempotency: send the same payload twice

```bash
for i in 1 2; do
  echo "--- call $i ---"
  curl -s -i -H "Authorization: Bearer $TOKEN" \
    -X POST http://localhost:8080/v1/qr/generate/dynamic \
    -H 'content-type: application/json' \
    -d '{"recipientName":"Alice","recipientCity":"Dhaka","recipientPan":"1234567890123456","amountMinor":100,"currency":"BDT"}'
done
```

**Look at Pane 2.** The two POSTs to `/v1/qr/generate/dynamic` must show the **SAME** `idempotency-key` value (and **different** `x-correlation-id` only if you set one — here you didn't, so both will show the same auto-generated one).

**What this proves:** the BFF derives the `Idempotency-Key` deterministically from `HMAC-SHA256(salt, "{userSub}|{sha256(body).hex}|{unixSeconds/60}")`. Two requests with the same `(user_sub, body)` inside the same 60-second bucket collapse to the same key. The platform recognises them as retries and returns the same QR.

> **Try this:** wait 61 seconds, then run the loop again. The bucket rolls over → the `idempotency-key` differs from the previous run → sbqr.api sees a brand-new request.

### Step C-5 — caller-supplied `Idempotency-Key` is passed through

```bash
curl -i -H "Authorization: Bearer $TOKEN" \
  -X POST http://localhost:8080/v1/qr/generate/static \
  -H 'content-type: application/json' \
  -H 'Idempotency-Key: call-supplied-9c7f' \
  -d '{"recipientName":"A","recipientCity":"D","recipientPan":"1","amountMinor":1,"currency":"BDT"}'
```

**Pane 2 must show** `idempotency-key: call-supplied-9c7f` (verbatim — not derived).

**Why this matters:** an FI app that already manages its own idempotency keys (e.g. UUIDs in their retry queue) can pass them down, and the BFF will forward them unchanged.

### Step C-6 — negative inbound cases (sanity-check auth is strict)

```bash
# Wrong audience:
BAD_AUD=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"some-other-api","sub":"dev-user-001"}' | jq -r .token)
curl -i -H "Authorization: Bearer $BAD_AUD" http://localhost:8080/v1/qr/generate/static \
  -H 'content-type: application/json' -d '{}'

# Wrong issuer:
BAD_ISS=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://evil.example","aud":"sbqr-fi-gateway","sub":"dev-user-001"}' | jq -r .token)
curl -i -H "Authorization: Bearer $BAD_ISS" http://localhost:8080/v1/qr/generate/static \
  -H 'content-type: application/json' -d '{}'

# Garbage bearer:
curl -i -H "Authorization: Bearer this-is-not-a-jwt" http://localhost:8080/v1/qr/generate/static \
  -H 'content-type: application/json' -d '{}'
```

**Expect:** all three return `401 Unauthorized` and **Pane 2 stays silent** (the request never reached sbqr.api).

### Step C-7 — alternative: use the mock-app driver

If you prefer to see the full scenario in one shot:

```bash
node tmp/mock-mobile-app.js alice s3cret
```

This runs the JWT mint + Steps C-2 and C-3 in order. Compare its output to the manual steps above.

---

## 5. Phase D — edge cases and hardening

This phase exercises the BFF's *failure paths*. Each step is independent — you can run them in any order.

### D-1 — Health checks: liveness vs readiness

```bash
curl -s -i http://localhost:8080/health/live
curl -s -i http://localhost:8080/health/ready
```

**What each says:**
- `/health/live` → `200 Healthy` *as long as the process is up*. It deliberately does not check the token — a sbqr.api outage should not cause k8s to restart-loop the BFF.
- `/health/ready` → `200 Healthy` *only if a platform token is cached*. Stays `503` until the pre-warm fetch succeeds.

**Why this distinction matters:** an orchestrator routes traffic based on `/health/ready`, but only restarts the pod if `/health/live` fails. Decoupling them avoids thrashing during transient platform incidents.

### D-2 — 401 from upstream → force-refresh + retry-once

This is the most important resilience behaviour. To exercise it, you need sbqr.api to return 401 on the first call.

**Stop Pane 2** (`Ctrl+C`) and restart it with the 401 variant:

```bash
# In Pane 2:
node tmp/fake-sbqr-api-401.js
```

This variant rejects the **first** call to `/v1/qr/generate/static` with 401 and returns 200 on every subsequent call.

**Restart Pane 3** too — the BFF cached a token from the *normal* fake, and it has a different timestamp format. Re-run the startup from Step A-3.

Then send ONE static generate request from Pane 4:

```bash
TOKEN=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"dev-user-001"}' | jq -r .token)

curl -i -X POST http://localhost:8080/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H 'content-type: application/json' \
  -H 'X-Correlation-Id: trace-retry-1' \
  -d '{"recipientName":"A","recipientCity":"D","recipientPan":"1","amountMinor":1,"currency":"BDT"}'
```

**Caller sees:** `HTTP/1.1 200 OK`. The 401 is *absorbed*. To Alice, the system "just worked".

**Pane 2 will show a three-step dance:**

```
[qr/generate/static] POST /v1/qr/generate/static   ← attempt #1 → 401
  Headers: { "authorization": "Bearer fake-platform-token-AAAA…", "idempotency-key": "BB-Tur_E4O…", ... }
[oauth/token]        POST /v1/oauth/token          ← force-refresh by PlatformAuthHandler
  ...
[qr/generate/static] POST /v1/qr/generate/static   ← attempt #2 → 200
  Headers: { "authorization": "Bearer fake-platform-token-BBBB…", "idempotency-key": "BB-Tur_E4O…", ... }
```

**Key row to eyeball:** the **idempotency-key** is the SAME on attempts #1 and #2. `X-Correlation-Id` is the SAME. `X-User-Sub` is the SAME. The only thing that changed was the `authorization` header.

**Follow-up (no 401 this time):**

```bash
curl -i -X POST http://localhost:8080/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H 'content-type: application/json' \
  -d '{"recipientName":"A","recipientCity":"D","recipientPan":"1","amountMinor":1,"currency":"BDT"}'
```

**Pane 2 shows a single POST** (the BFF's cached token is valid) — no second `/v1/oauth/token`.

**When you're done with D-2**, stop Pane 2 again (`Ctrl+C`) and restart the *normal* fake for the rest of the playbook:

```bash
# In Pane 2:
node tmp/fake-sbqr-api.js
```

And restart Pane 3 once more (Step A-3).

### D-3 — Polly: 5xx retry

The BFF's outbound HttpClient is wrapped in a Polly v8 pipeline that retries on **5xx + timeout** with exponential backoff + jitter. It does **not** retry 4xx (those are owned by `PlatformAuthHandler`).

To exercise it, edit `tmp/fake-sbqr-api.js` and add a counter to `/v1/qr/generate/static` that returns 503 on the first call and 200 on the second. (You can use `tmp/fake-sbqr-api-401.js` as a template — change the 401 to 503 and you're done.) Then:

```bash
TOKEN=$(curl -s -X POST http://localhost:5105/mint \
  -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"dev-user-001"}' | jq -r .token)

time curl -i -X POST http://localhost:8080/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" \
  -H 'content-type: application/json' \
  -d '{"recipientName":"A","recipientCity":"D","recipientPan":"1","amountMinor":1,"currency":"BDT"}'
# → 200 OK after a few seconds (1 initial + N retries)
```

**Pane 3** (BFF logs) will show retry events. **Pane 2** will show two POSTs with the same `idempotency-key`.

### D-4 — Contract pinning (Slice 6)

The BFF only proxies the three fixed `/v1/qr/*` routes (there is no `/v1/qr/__contract__/*`
passthrough), so the violation path is exercised with a dedicated fake whose
`/v1/qr/generate/static` response omits the required `signatureKeyVersion`. Pinning must also
be **enabled** — `tmp/.env-dev` leaves `Platform__ContractPath` empty (pinning off) and the
contract itself lives in the sibling repo, not in this repo's source tree.

1. **Pane 2:** stop the normal fake, start the contract-violation variant:

   ```bash
   node tmp/fake-sbqr-api-contract.js
   ```

2. **Pane 3:** restart the BFF with pinning enabled (absolute path; adjust if your layout differs):

   ```bash
   set -a; . tmp/.env-dev; set +a
   export Platform__ContractPath="$PWD/../rvl-secure-bqr-manager/contracts/v1.public.json"
   dotnet run --project src/SBQR.FiGateway.Api --no-launch-profile
   ```

3. Mint a JWT (Step C-1) and call generate/static.

**Expect:** `HTTP/1.1 502 Bad Gateway` with the sanitized `ProblemDetails`
(`type: …/errors/contract-violation`). The BFF must NOT echo the (potentially PII-containing)
upstream payload back to the caller. A conformant path (`/v1/qr/validate` on the same fake)
still returns 200, confirming the 502 is drift-specific.

> Note on `nullable`: the contract uses OpenAPI 3.0 `nullable: true` (e.g. `reasonCode`).
The registry translates that to `["<type>", "null"]` before validating
(`PlatformContractRegistry.NormalizeOpenApiNullable`), otherwise the real platform's
`reasonCode: null` on every VALID verdict would fail the pinned schema.

**mTLS hint:** in Production, set `Mtls__Enabled=true`, `Mtls__CertPath=/path/to/client.pfx`,
`Mtls__CertPassword=<secret>` (env-var section is `Mtls`, not `Platform:Mtls`). In Development,
BootGuard allows `Mtls__Enabled=false`. The platform HttpClient attaches the client cert via
`MtlsConfigurator.Apply` at registration time. For a full local mTLS demonstration see the
playbook §8.3 (`tmp/make-mtls-certs.sh` + `tmp/fake-sbqr-api-mtls.js`).

### D-5 — BootGuard fail-fast

The BootGuard refuses to start the host if any well-known sentinel value is in place **when running in Production** (`ASPNETCORE_ENVIRONMENT=Production`).

**Stop Pane 3** (the BFF), then restart it with the sentinel values:

**On bash / Git-Bash:**

```bash
ASPNETCORE_ENVIRONMENT=Production \
  Platform__BaseUrl=http://localhost:5200 \
  Platform__ClientId=set-me-client-id \
  Platform__ClientSecret=any-secret \
  Auth__Authority=https://idp.example.invalid \
  Auth__Issuer=https://idp.example.invalid \
  Auth__Audience=sbqr-fi-gateway \
  Idempotency__Salt=set-me-salt-min-32-bytes-please-replace \
  dotnet run --project src/SBQR.FiGateway.Api -c Release --no-launch-profile
```

**Expect** (host refuses to start):

```
BootGuard: refusing to start in Production with sentinel configuration:
  - Platform:ClientId starts with 'set-me' ('set-me-client-id').
  - Auth:Authority is 'https://idp.example.invalid' (example.invalid).
  - Auth:Issuer is 'https://idp.example.invalid' (example.invalid).
  - Idempotency:Salt is still the placeholder (starts with 'set-me-').
```

**Then re-run with clean values** (the `appsettings.Development.json` defaults are already clean, so omit the sentinel env vars):

```bash
ASPNETCORE_ENVIRONMENT=Production \
  Auth__RequireHttpsMetadata=false \
  dotnet run --project src/SBQR.FiGateway.Api -c Release --no-launch-profile
```

(You're hitting local HTTP fakes, so set `RequireHttpsMetadata=false`; in real prod leave it `true`.)

**Expect** (host starts normally):

```
BootGuard: Production configuration looks clean.
```

Then Ctrl+C and restart with `ASPNETCORE_ENVIRONMENT=Development` for the rest of the playbook.

---

## 6. Phase E — observability

These steps turn the dev stack from "functional" to "observable." Skip any you don't need; they're independent.

### E-1 — Serilog masking

The BFF logs every QR call at Information level. Run any QR call and look at Pane 3 — the logs should show the inbound `Sub` and outbound request, but **never** the raw token value or PAN digits:

```bash
TOKEN=$(curl -s -X POST http://localhost:5105/mint -H 'content-type: application/json' \
  -d '{"iss":"http://localhost:5105","aud":"sbqr-fi-gateway","sub":"dev-user-001"}' | jq -r .token)

curl -s -X POST http://localhost:8080/v1/qr/generate/static \
  -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"recipientName":"X","recipientCity":"D","recipientPan":"1234567890123456","amountMinor":1,"currency":"BDT"}' >/dev/null
```

In Pane 3, search for the log entry — it should be:

- ✅ Includes the BFF's request, the upstream URL, the masked upstream bearer (`Bearer ***last4`), and the inbound user `Sub` (`Sub = user-alice`).
- ❌ Does NOT include the raw PAN (`1234…3456` should appear as `1234****3456` or similar), the full token, or your `Idempotency-Key` value.

If PAN is leaking in plaintext, the Serilog masking enricher is misconfigured — file a bug.

### E-2 — OpenTelemetry OTLP export

Slice 7 exports tracing + metrics via OTLP when `OpenTelemetry:Enabled=true` (default `false`).

1. Start an OTLP collector (e.g. `otel-collector` from `docker-compose.yml` if your repo has one, or any local collector).
2. Set on Pane 3:

```bash
set -a && source tmp/.env-dev && set +a
OpenTelemetry__Enabled=true
OpenTelemetry__OtlpEndpoint=http://localhost:4317
dotnet run --project src/SBQR.FiGateway.Api -c Release --no-build --no-launch-profile
```

3. Hit any endpoint; check the collector's stdout for spans with `service.name="sbqr-fi-gateway"`.

If `OpenTelemetry:Enabled=true` is set with an empty `OtlpEndpoint`, the BFF **refuses to start** with `InvalidOperationException`. Intentionally noisy — silently dropping telemetry is worse than a startup failure.

---

## 7. Phase F — teardown

### F-1 — graceful shutdown

In Pane 3, press `Ctrl+C`. The BFF logs:

```
info: Microsoft.Hosting.Lifetime[0]
      Application is shutting down...
info: SBQR.FiGateway.Api.Platform.PlatformTokenManager[0]
      Platform token cache cleared.
```

The "cache cleared" line is logged only if you implement `IDisposable` and hook it; cross-check the source if absent.

In Pane 1 and Pane 2, `Ctrl+C` each.

### F-2 — clean restart

To run the full stack again from scratch, re-execute Steps A-1 through A-3. Reminder: restarting Pane 1 invalidates the BFF's cached JWKS — **always restart Pane 3 too** if you restart Pane 1.

### F-3 — Docker quickstart (alternative)

If you have Docker installed and want a one-shot reproduction of the entire stack (IdP + sbqr.api + BFF) without four panes:

```bash
docker compose up --build
# … starts fake-idp:5105, fake-sbqr-api:5200, bff:8080
curl -i http://localhost:8080/health/live    # 200
curl -i http://localhost:8080/health/ready   # 200 after pre-warm completes
docker compose down
```

Image characteristics:

- Built from `mcr.microsoft.com/dotnet/sdk:8.0` with NuGet cache mounts.
- Runtime image is `mcr.microsoft.com/dotnet/aspnet:8.0`, running as the built-in non-root `app` user (uid 1000).
- Includes a Docker `HEALTHCHECK` against `/health/live` so `docker ps` shows container liveness directly.
- Telemetry-friendly env vars (`DOTNET_gcServer=1`, `DOTNET_GCConserveMemory=5`, `DOTNET_EnableDiagnostics=0`) are set by default; override per environment.

---

## 8. Troubleshooting

### 8.1 `start-bff` refuses with `'file' scheme is not supported`

**Symptom:** QR call returns 500 with `System.NotSupportedException: The 'file' scheme is not supported` from `HttpPlatformTokenFetcher`.

**Cause:** a stale `dotnet` process is bound to port 8080 with an empty `Platform:BaseUrl`. The HttpClient then constructs a relative URI against an empty BaseAddress, which .NET 8 on Windows resolves as `file://`.

**Fix:** kill all dotnet processes, then restart.

```bash
# PowerShell:
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Bash (WSL):
pkill -f "dotnet.*SBQR.FiGateway"
```

Re-run Step A-3 only this time — and **double-check** that `--no-launch-profile` is on the command line.

### 8.2 `/health/ready` is permanently `503`

**In order of frequency:**

1. **Pane 2 (sbqr.api) is not running.** Check Pane 2 — is the listener up? Restart with `node tmp/fake-sbqr-api.js`.
2. **The BFF was started with the wrong `Platform:BaseUrl`.** Without `--no-launch-profile` (Step A-3), `launchSettings.json` overrides it. Re-run with the flag.
3. **Stale .NET process holding the previous config.** See §8.1.
4. **Pane 2 (sbqr.api) is on a different port than the BFF expects.** Confirm with:
   ```bash
   ss -tnlp 2>/dev/null | grep :5200  # or netstat -an | grep 5200
   ```

### 8.3 All requests return 401

**Cause:** the IdP's keypair was regenerated (you restarted Pane 1). The BFF cached the old JWKS. Wait 5 minutes for auto-refresh, or restart Pane 3.

### 8.4 `Idempotency-Key` is missing from upstream call

- For `validate` calls: **expected.** Validate is read-only and the BFF never derives one.
- For `generate/static` or `generate/dynamic` calls: check that the request had a JSON body. The BFF only derives when there is a non-empty body to hash.

### 8.5 Two identical calls get DIFFERENT `Idempotency-Key`

**Cause:** you crossed a 60-second bucket boundary. Wait 61 seconds and run the same call twice — keys will be *the same*. Or run twice within the same wall-clock minute.

### 8.6 Token keeps refreshing every minute

**Symptom:** `debug/platform-token` shows `lastRefreshedUtc` advancing every ~60s.

**Cause:** the BFF is reading `lifetimeSeconds` as ~60 instead of ~3600. Check that `tmp/fake-sbqr-api.js` returns `expiresIn: 3600` (the camelCase field is required by the contract). If you accidentally edited it down, restore the value.

### 8.7 401-once test (D-2) shows TWO `/v1/oauth/token` calls but no force-refresh

The 401-mode fake increments a counter on **only** `qr/generate/static`. If your request went to `qr/generate/dynamic` or `qr/validate`, the fake returns 200 normally and no force-refresh is exercised. Re-target the test:

```bash
curl -i -X POST http://localhost:8080/v1/qr/generate/static ...
```

### 8.8 Pane 2 shows entries from a previous session

The BFF was restarted but the fake wasn't. Restart **both Pane 2 and Pane 3** to start with clean logs.

### 8.9 `InnerHandler must be null` at startup

This is an internal `IHttpClientFactory` constraint when a `DelegatingHandler` is registered via `AddHttpMessageHandler<T>()`. The handler's constructor must **not** pre-set `InnerHandler`. If you see this in your own code (not from the BFF source), check your `DelegatingHandler` subclass.

---

## 9. Sign-off checklist

Before you merge a slice, walk this list and tick every row. Each tick is a small `curl` (or a glance at the right pane).

| ✓ | Check | How | Expected |
|---|---|---|---|
| ☐ | Pane 1 starts on `:5105` | `node tmp/fake-idp.js` | banner + silent |
| ☐ | Pane 2 starts on `:5200` | `node tmp/fake-sbqr-api.js` | banner + silent |
| ☐ | Pane 3 binds `:8080` | `dotnet run --no-launch-profile …` | "Now listening on…" |
| ☐ | `/health/live` always 200 | Step B-1 | 200 |
| ☐ | `/health/ready` 200 after pre-warm | Step B-1 | 200 within ~2s |
| ☐ | Anonymous QR call rejected at auth | Step B-3 | 401, Pane 2 silent |
| ☐ | JWT minted from `/mint` is accepted | Step C-1 | (variable ready for next steps) |
| ☐ | `validate` forwards body & adds `auth`/`x-correlation-id`/`x-user-sub` | Step C-2 | all 3 in Pane 2 |
| ☐ | `validate` does NOT add `idempotency-key` | Step C-2 | absent in Pane 2 |
| ☐ | `generate/dynamic` DOES add a derived `idempotency-key` | Step C-3 | present in Pane 2 |
| ☐ | Same body twice → same derived key in same minute | Step C-4 | identical key in Pane 2 |
| ☐ | Caller-supplied `Idempotency-Key` is passed through | Step C-5 | exact value echoed in Pane 2 |
| ☐ | Wrong `aud`/`iss`/garbage bearer → 401 | Step C-6 | all 401, Pane 2 silent |
| ☐ | `/health/ready` flips to 503 if Pane 2 dies | kill Pane 2 | Pane 3 logs cache eviction |
| ☐ | 401-once from upstream → force-refresh + retry | Step D-2 | 200 to caller; 2 upstream calls in Pane 2 |
| ☐ | Retried request reuses the same `Idempotency-Key` | Step D-2 | identical key in Pane 2 |
| ☐ | 5xx retry recovers without surfacing to caller | Step D-3 | 200 to caller after backoff |
| ☐ | Schema-violating upstream body → 502 sanitized ProblemDetails | Step D-4 | 502; conformant path still 200 |
| ☐ | BootGuard refuses Production with sentinels | Step D-5 | host exits at startup |
| ☐ | BootGuard passes Production with real values | Step D-5 | host starts |
| ☐ | Serilog logs never include raw token / PAN | Step E-1 | only masked values in Pane 3 |
| ☐ | OTel spans visible in collector (if enabled) | Step E-2 | `service.name=sbqr-fi-gateway` |

If every row is ticked, your slice is ready for review.

---

## See also

- [`docs/dev-onboarding-guide.md`](./dev-onboarding-guide.md) — the *why* and *how-it-flows*. Read first.
- [`docs/dev-e2e-simulation-playbook-bn.md`](./dev-e2e-simulation-playbook-bn.md) — a shorter, Bangla-language, linear version of this playbook, run against the **real** `rvl-secure-bqr-manager` sbqr.api for the happy-path sections instead of the Node fake.
- [`docs/e2e-playbook-bn.md`](./e2e-playbook-bn.md) — the command-verified edition of the same Bangla playbook, real-sbqr.api-only: every command and expected output executed against the real platform, plus a troubleshooting table drawn from real incidents.
- `docs/0001-fi-direct-oauth-to-bff-model.md` — the full architectural decision (Bangla; deep context for the BFF rewrite).
- `docs/slice-4-boot-guard-and-observability.md` … `docs/slice-6-contract-pinning-and-mtls.md` — one doc per slice. After you run this playbook, read the relevant slice doc for the design rationale behind each behaviour you exercised.
- `tmp/smoke.sh` and `tmp/smoke-401.sh` — single-shot drivers that wrap Phase C and D-2. Run them once after setting up the stack to see the canonical transcript.
