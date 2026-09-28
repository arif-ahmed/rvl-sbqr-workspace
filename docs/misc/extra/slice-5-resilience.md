# Slice 5 — Resilience (Polly v8 5xx retry + timeouts)

## Context

Before Slices 5+6+7 the BFF would surface any transient 5xx from sbqr.api
directly to the FI app. AR-2026-09-23-01 §5.1 mandates retry-on-transient-failure
with exponential backoff + jitter, plus per-attempt timeouts so a hung upstream
connection doesn't pin a request slot.

Slice 5 adds **Microsoft.Extensions.Http.Resilience** (the Microsoft-official
Polly v8 wrapper) to the named `"PlatformQr"` HttpClient. The existing
`PlatformAuthHandler` (Slices 1-3) still owns 401 (force-refresh + retry-once);
Polly must NEVER trigger on 401, or we'd double-retry on token expiry.

## Decisions

### 1. Polly v8 via `Microsoft.Extensions.Http.Resilience`

We use the .NET 8-first Microsoft wrapper, not raw Polly v7. Reasons:

- First-party integration with `IHttpClientBuilder`.
- `ResiliencePipeline<T>` is the Polly v8 API (more composable than v7's
  `PolicyWrap`).
- Bundled `AddResilienceHandler("name", builder => ...)` extension that wires
  the pipeline into the named client's message handler chain.

### 2. Retry predicate — 5xx + timeouts ONLY, never 401

```csharp
ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
    .Handle<HttpRequestException>()
    .Handle<TaskCanceledException>()
    .Handle<TimeoutRejectedException>()
    .HandleResult(r => (int)r.StatusCode >= 500)
```

`HandleResult` is explicit: `(int)status >= 500`. The 401 case is invisible to
Polly — `PlatformAuthHandler` owns it. A regression test (`ShouldHandle_returns_false_for_401`)
guards against accidental widening of the predicate.

### 3. Exponential backoff with jitter

```csharp
MaxRetryAttempts = 3,           // 1 initial + 3 retries = 4 total
BackoffType = DelayBackoffType.Exponential,
UseJitter = true,
Delay = 200ms                    // initial; subsequent delays double
```

Jitter avoids a fleet-wide retry thundering herd if multiple instances happen
to hit the same transient error.

### 4. Per-attempt timeout + outer HttpClient.Timeout

```csharp
// Per-attempt ceiling
builder.AddTimeout(TimeSpan.FromSeconds(opts.TimeoutSeconds));   // 10s default

// Outer HttpClient ceiling (separate setting)
client.Timeout = TimeSpan.FromSeconds(opts.HttpClientTimeoutSeconds);  // 30s default
```

The outer ceiling is the safety net — if Polly somehow fails to abort an
attempt, the outer `HttpClient.Timeout` still kicks in.

### 5. Config section

```json
"Resilience": {
  "MaxRetryAttempts": 3,
  "InitialBackoffMs": 200,
  "TimeoutSeconds": 10,
  "HttpClientTimeoutSeconds": 30
}
```

All four fields are `[Range]`-validated; invalid values fail at startup via
`ValidateOnStart()`.

## What got built

```
src/SBQR.FiGateway.Api/Resilience/
├── ResilienceOptions.cs                          ← strong-typed config
└── PlatformResiliencePipelineBuilder.cs          ← ShouldHandle + Configure

src/SBQR.FiGateway.Api/Program.cs                 ← AddResilienceHandler wiring on PlatformQr
src/SBQR.FiGateway.Api/appsettings.json          ← Resilience section

tests/SBQR.FiGateway.Tests/Resilience/
└── ResiliencePipelineTests.cs                    ← 15 unit tests on ShouldHandle
tests/SBQR.FiGateway.Tests/Integration/
└── ResilienceIntegrationTests.cs                 ← 2 integration tests via WebApplicationFactory

Directory.Packages.props                          ← + Microsoft.Extensions.Http.Resilience 8.10.0
                                                    + Microsoft.Extensions.Resilience 8.10.0
```

## Tests

105/105 passing (86 from Slices 1-4 + 19 new from Slice 5 + Slice 6).

## Out-of-scope (deferred)

- Circuit breaker (after we have SLO numbers from real traffic).
- Bulkhead / concurrency limit.
- Retry-After header parsing from sbqr.api.
