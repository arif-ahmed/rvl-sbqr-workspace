# Slice 4 — BootGuard, Health checks, Serilog, Docker

> Slice 4 of the BFF. Builds on Slices 1-3.

## Context

Before the BFF can be safely rolled to Production it needs four things:

1. **A liveness / readiness probe** so an orchestrator (k8s, ECS, …) can tell
   when an instance is safe to receive traffic. AR-2026-09-23-01 §4.4
   requires `/health/ready` to be **false until the first platform token is
   acquired** — otherwise the BFF accepts inbound traffic before it has any
   upstream credentials.

2. **Fail-fast on sentinel configuration** in Production. The
   `appsettings.json` is intentionally full of placeholder values
   (`set-me-client-id`, `https://idp.example.invalid`, …) — these must not
   accidentally make it to Production. AR §4.6 calls this out: "বুট-গার্ড —
   ফরসা কনফিগ বা প্রোডাকশনে dev-default থাকলে fail-fast".

3. **Structured logging with masking** so platform tokens, JWTs, and PII
   (PAN / MSISDN / NID) never reach a log sink. AR §4.6: "QR payload/PAN
   মাস্কিং-সহ স্ট্রাকচার্ড লগ".

4. **A boot-able Docker image + compose stack** so the BFF can be deployed
   in a CI/CD pipeline (or reproduced locally in one command).

## Decisions

### 1. Health checks — `MapHealthChecks` with two predicates

We use ASP.NET Core's built-in `Microsoft.Extensions.Diagnostics.HealthChecks`
package (in-box with .NET 8). Two endpoints, two tag-based predicates:

| Endpoint | Predicate | What it checks |
|---|---|---|
| `GET /health/live`  | `Tags.Contains("live")` | `SelfLivenessHealthCheck` — always returns `Healthy` if the process is up. |
| `GET /health/ready` | `Tags.Contains("ready")` | `PlatformTokenHealthCheck` — `HasToken && ExpiresAt > UtcNow`. |

A custom `HealthResponseWriter` serializes results as JSON:

```json
{
  "status": "Healthy",
  "totalDurationMs": 7.86,
  "checks": [
    { "name": "self", "status": "Healthy", "description": "BFF process is live." }
  ]
}
```

### 2. BootGuard — Production-only sentinel scan

A simple class run once during `builder.Build()`. It iterates over
`PlatformOptions`, `AuthOptions`, `IdempotencyOptions` and aggregates findings
into a single `InvalidOperationException`. **No-op in Development / Staging.**

The sentinel check list:

| Config key | Sentinel trigger |
|---|---|
| `Platform:ClientId`       | starts with `set-me` (case-insensitive) |
| `Platform:ClientSecret`   | starts with `set-me` |
| `Platform:BaseUrl`        | contains `example.invalid` |
| `Auth:Authority`          | contains `example.invalid` |
| `Auth:Issuer`             | contains `example.invalid` |
| `Idempotency:Salt`        | starts with `set-me-` |

The `set-me-` prefix for the Idempotency salt is distinct from the
`set-me` prefix for client_id/secret so the operator can distinguish "this
is a placeholder I forgot to replace" from "this is a real prod client id".

### 3. Boot jitter — random delay before first token fetch

`PlatformOptions.BootJitterSecondsMax` (default `30`) controls the maximum
random delay in seconds that `TokenPrewarmService` waits before the first
`/v1/oauth/token` fetch. AR §4.4: "boot jitter বাধ্যতামূলক" — without this,
a fleet of N instances restarting together all hit the token endpoint in
the same wall-clock second and trip the per-`client_id` rate limit
(10 requests / 60s per AR §4.4).

Implementation: `Random.Shared.Next(0, BootJitterSecondsMax)` then
`Task.Delay(...)`. Default 30s in Production (`appsettings.json`), 2s in
Development.

### 4. Serilog — structured logging + masking enricher

`builder.Host.UseSerilog((ctx, _, cfg) => SerilogBootstrap.Build(ctx.Configuration))`.

Minimum-level + overrides are bound from the `Serilog:` configuration section
so operators can tune without code changes (e.g. drop
`Microsoft.AspNetCore.Http` to `Debug` while debugging a routing bug).

The `SensitiveDataMaskingEnricher` walks both the top-level property bag and
the sub-properties of any `StructureValue` or `DictionaryValue`, replacing
values for these sensitive keys:

```
Authorization, X-User-Sub, Idempotency-Key,
ClientSecret, client_secret, AccessToken, access_token,
RefreshToken, refresh_token, PAN, MSISDN, NID
```

Both top-level (e.g. `LogContext.PushProperty("Authorization", token)`) and
nested (e.g. `Log.Information("X {Detail}", new { Authorization = ... })`)
forms are covered.

### 5. Docker — multi-stage, non-root, distroless-friendly

```
sdk:8.0  → dotnet restore + publish → /app/publish
aspnet:8.0 (USER app) → COPY --from=build /app/publish
```

- NuGet cache mounted via `RUN --mount=type=cache,target=/root/.nuget/packages`.
- Runtime image is the standard `aspnet:8.0`, running as the built-in
  non-root `app` user (uid 1000). The BFF's appsettings do not bind to any
  privileged port; 8080 is fine inside the container, mapped externally
  by compose.
- Docker `HEALTHCHECK` runs `wget --spider http://localhost:8080/health/live`
  every 10s, timeout 2s, 3 retries — matches the ASP.NET Core HealthCheck
  contract.
- `DOTNET_gcServer=1` + `DOTNET_GCConserveMemory=5` for better behavior in
  memory-constrained containers.

### 6. docker-compose — three-service stack

`docker-compose.yml` brings up:
- `fake-idp`      (Node:20 alpine + the `tmp/fake-idp.js` script)
- `fake-sbqr-api` (Node:20 alpine + `tmp/fake-sbqr-api.js`)
- `bff`           (the published image, env-overridden for dev URLs)

The BFF's `depends_on` waits for the two fakes to start, but the BFF is
resilient to them not being ready — BootGuard stays in Production-mode for
dev convenience, but in Production the BFF would only need the real platform.

## What got built

```
src/SBQR.FiGateway.Api/
├── Observability/
│   ├── BootGuard.cs                    ← fail-fast on sentinel config in Production
│   ├── HealthResponseWriter.cs         ← JSON shape for health endpoints
│   ├── PlatformTokenHealthCheck.cs     ← /health/ready predicate
│   ├── SelfLivenessHealthCheck.cs      ← /health/live predicate (always healthy)
│   ├── SensitiveDataMaskingEnricher.cs ← masks Authorization, Idempotency-Key, PAN, etc.
│   └── SerilogBootstrap.cs             ← Serilog LoggerConfiguration
├── Platform/
│   ├── PlatformOptions.cs              ← + BootJitterSecondsMax
│   └── TokenPrewarmService.cs          ← + boot-jitter delay before first fetch
├── Program.cs                          ← UseSerilog, AddHealthChecks, MapHealthChecks, BootGuard run
└── appsettings*.json                   ← + Serilog section + Platform:BootJitterSecondsMax

Dockerfile                              ← multi-stage, non-root
.dockerignore                           ← keep bin/obj/tests out of the image
docker-compose.yml                      ← IdP + sbqr.api + BFF stack
.env.example                            ← documentation of required env-vars

tests/SBQR.FiGateway.Tests/Observability/
├── BootGuardTests.cs                   ← 9 tests: Production-sentinel enforcement
├── HealthEndpointTests.cs              ← 5 tests: live/ready behavior
└── SensitiveDataMaskingEnricherTests.cs ← 14 tests: top-level masking

README.md                               ← replaced placeholder with real intro
docs/dev-manual-testing.md              ← + Steps 10-12 (health, BootGuard, Docker)
docs/slice-4-boot-guard-and-observability.md ← this file
```

## Tests

69/69 passing (41 from Slices 1-3 + 28 new). All green under
`TreatWarningsAsErrors=true`.

## Out-of-slice (deliberately deferred)

- Polly / 5xx retry.
- Contract pinning + mTLS.
- Multi-tenant routing + web-cookie auth scheme.
- OpenTelemetry export.
- k8s manifests / Helm chart (only compose for now).
