# Postman + Newman API testing automation — plan

> Frozen review contract for `rvl-commit` / `rvl-reviewer`.
> Status: see `status.yaml`. Jira: TBD.

## Problem

The RVL SBQR platform is composed of several independently-shipped HTTP/REST services
(portal API, FI gateway, secure-bqr manager, BB trust store, app emulator, mocks).
Today we have:

- Manual `curl` / Postman GUI ad-hoc checks before releases — slow, error-prone, and not
  part of the build pipeline.
- No regression safety net when one submodule is bumped: a change in
  `rvl-secure-bqr-manager` can silently break the contract `rvl-sbqr-portal` relies on.
- No formal "smoke at PR time" gate. Releases rely on reviewers re-running collections by hand.
- Postman collections exist locally on individual machines but are not in source control,
  not versioned, and not reproducible.

We need a single, versioned, CI-runnable API test suite that:

1. Exercises the critical-path of every service on every PR.
2. Produces JUnit + HTML reports that drop into our existing GitHub Actions / pipeline
   reporting.
3. Can be re-run against any environment (local dev docker compose, staging, prod)
   by switching an environment file — no code changes.
4. Is authored in a tool developers already use (Postman) so the barrier to adding
   tests stays low.

## Scope

**In:**

- A workspace-level `tests/api/` directory holding the Postman collection JSON,
  per-environment JSON files, data files, and a `package.json` wrapping `newman`.
- One **smoke** collection covering the critical path of:
  - `rvl-secure-bqr-manager` (SBQR.Api) — health, auth, QR issue, QR verify
  - `rvl-sbqr-portal` API endpoints exercised by the web portal
  - `rvl-sbqr-fi-gateway` — FI-facing endpoints (if reachable from CI)
  - `rvl-bb-trust-store` — trust list fetch (if reachable from CI)
- A reusable set of pre-request + test scripts (token refresh, HMAC signing,
  schema validation) hoisted to the collection level.
- Per-environment JSON for: `local-dev` (mtls), `staging`, `prod-readonly`.
- A GitHub Actions workflow `.github/workflows/api-tests.yml` that:
  - Spins up the docker-compose dev stack (existing `dev-docker-run-playbook.md`),
  - Runs the smoke collection via Newman,
  - Publishes JUnit + HTML artifacts,
  - Uploads to the test-reporter panel.
- Documentation: `docs/features/postman-newman/` (authoring guide, env vars,
  secrets handling, troubleshooting) — linked from this plan, not duplicated.

**Out (explicit non-goals for this plan):**

- **Load / performance testing.** Newman is single-threaded and not designed for high RPS.
  Pair with k6 in a separate plan if needed.
- **In-process / unit-level API tests.** Those are xUnit / NUnit tests inside each
  submodule — out of scope here.
- **Contract testing of the OpenAPI spec** (Spectral / Dredd). Newman JSON-Schema
  assertions cover runtime contract; spec linting is a future plan.
- **Browser-level testing** of the portal UI. Handled separately (see
  `docs/agent-browser-testing.md`).
- **Postman cloud workspaces / monitors.** Postman Cloud is the default UI; the CI
  path is fully self-hosted (export → commit → run) so we are not blocked by
  Postman account / billing limits.

## Design

### High-level architecture

```
┌─────────────────────────┐    export     ┌───────────────────────────┐
│  Postman (dev machine)  │ ────────────▶ │  tests/api/collections/   │
│  + Postman Cloud (team) │               │   *.postman_collection.   │
└─────────────────────────┘               │   json (v2.1)             │
            ▲                             │                           │
            │                             │  tests/api/environments/  │
            │                             │   {local,staging,…}.json  │
            │                             │                           │
            │                             │  tests/api/data/*.csv     │
            │                             └──────────┬────────────────┘
            │                                        │
            │                                        ▼
            │                             ┌──────────────────────────┐
            │                             │  Newman CLI              │
            │                             │  (postman/newman image)  │
            │                             └──────────┬───────────────┘
            │                                        │
            │                                        ▼
            │                             ┌──────────────────────────┐
            │                             │  GitHub Actions          │
            │                             │   - junit.xml → reporter │
            │                             │   - report.html → artifact│
            │                             └──────────────────────────┘
            │
   (developers author/edit here,
    then push JSON to Git)
```

### Source of truth: Git, with Postman as editor

We **commit the exported JSON** to `tests/api/` so that:

- Diff are reviewable in PRs alongside code.
- CI is reproducible without a Postman API key.
- The collection is portable (works on any developer machine).

Optional later enhancement: a CI step that pulls the latest from Postman Cloud
(via `X-Api-Key` + the Postman API) to catch "Postman has the latest, Git
doesn't" drift — but the default mode is Git-as-truth.

### Folder / collection layout

```
tests/api/
├── README.md                       # how to add a request / env
├── package.json                    # newman + reporters
├── package-lock.json
├── collections/
│   ├── rvlsbqr-smoke.postman_collection.json
│   └── rvlsbqr-tenant-onboarding.postman_collection.json   # phase 2
├── environments/
│   ├── local-dev.postman_environment.json     # https://localhost:5xxx + mtls
│   ├── staging.postman_environment.json       # placeholder until staging exists
│   └── prod-readonly.postman_environment.json # GET-only smoke
├── data/
│   └── onboard-institutions.csv       # data-driven iterations
├── scripts/                        # node helpers (report aggregation, etc.)
│   └── package.json
└── reports/                        # .gitignored, generated
```

### Reusable scripts (collection-level)

Hoisted into the collection's root **Scripts** tab so they cascade to every request:

- **Pre-request `00-bootstrap`:** if a `bearer` env var is present and expired
  (decode JWT `exp`), refresh from `/connect/token` and `pm.environment.set('bearer', …)`.
- **Pre-request `10-mtls-hint`:** for `local-dev`, add `Authorization: Bearer …
  X-Client-Cert: <thumbprint>` if the portal expects it. (Cert is read from
  `/dev-pki/...`, NOT from committed JSON — see Secrets section.)
- **Test `00-status`:** `pm.expect(pm.response.code).to.be.oneOf([200, 201, 204])`
  unless the request has overridden `expectedStatus`.
- **Test `10-schema`:** if the request has a `schema` collection variable, run
  `pm.response.to.have.jsonSchema(schema)`.
- **Test `20-latency`:** warn (don't fail) if `pm.response.responseTime > 2000`.

### Data-driven iterations

For the tenant-onboarding collection, `data/onboard-institutions.csv` will
feed `{{institutionName}}`, `{{bin}}`, `{{expectedStatus}}` etc. so one request
runs N times.

### CI workflow (GitHub Actions)

`.github/workflows/api-tests.yml`:

- **Trigger:** `pull_request` and `workflow_dispatch` (manual).
- **Runner:** `ubuntu-latest`.
- **Job `api-smoke`:**
  1. Checkout workspace (with submodules).
  2. Set up docker compose stack using existing `dev-docker-run-playbook.md`.
  3. `npm ci` in `tests/api/`.
  4. `docker run --rm -v "$PWD/tests/api:/etc/newman" postman/newman:latest \
       run /etc/newman/collections/rvlsbqr-smoke.postman_collection.json \
       -e /etc/newman/environments/local-dev.postman_environment.json \
       --env-var "bearer=${{ secrets.LOCAL_DEV_BEARER }}" \
       --env-var "clientCertThumb=${{ secrets.LOCAL_DEV_CERT_THUMB }}" \
       -r cli,junit,htmlextra \
       --reporter-junit-export /etc/newman/reports/junit.xml \
       --reporter-htmlextra-export /etc/newman/reports/report.html`
  5. `actions/upload-artifact@v4` for the report directory.
  6. `dorny/test-reporter@v1` to surface JUnit in the PR check UI.
  7. `--bail` so the first failure aborts (fast feedback).

- **Job `api-regression` (scheduled nightly + manual):** runs the full
  collection (no `--folder` filter) against staging once that env exists.

### Secrets handling

**Hard rule: nothing secret is committed in JSON.**

- Environment files contain only public values (URLs, public client IDs).
- Tokens, signing keys, cert material are passed at run time via:
  - `--env-var key=value` flag, OR
  - `pm.sendRequest` to a secrets-vault endpoint, OR
  - GitHub Actions secrets + `--env-var "key=${{ secrets.… }}"`.
- `dev-pki/` and `rvl-sbqr-keys/` stay git-ignored. Cert thumbprints are passed
  through `--env-var`; raw certs are never read by Newman directly.
- The CI workflow has a `secrets:` block scoped narrowly to the `api-smoke` job.

### Reporting

- **JUnit XML** → consumed by `dorny/test-reporter` (or GitHub's native test
  reporter) so failures appear in the PR check UI.
- **HTML (`htmlextra`)** → uploaded as a build artifact; humans click it from the
  Actions run page.
- **JSON** → not retained unless a downstream plan needs it (kept off by
  default to avoid leaking response bodies).
- Failures abort the run (`--bail`); warnings (latency, soft assertions) do not.

### Why Newman over alternatives

| Tool | Verdict |
|---|---|
| **Newman** | ✅ chosen — reuses Postman GUI artifacts, JS sandbox, JUnit/HTML out of the box, language-agnostic. |
| REST Assured (Java) | ❌ — requires JVM test project; doesn't reuse Postman collections. |
| Supertest (Node) | ❌ — couples tests to Node app process; we test deployed services. |
| k6 | ➖ — out of scope here; pair with Newman for load testing later. |
| Playwright | ❌ — overkill for pure HTTP; already considered for browser tests separately. |

## Submodule work items

| # | Submodule | Branch | Work item |
|---|---|---|---|
| 1 | `rvl-sbqr-workspace` (root) | `main` | Add `tests/api/` skeleton, `package.json`, `package-lock.json`, `.gitignore` additions, `.github/workflows/api-tests.yml`, `docs/features/postman-newman/` author guide, and this `docs/plans/postman-newman-api-testing/plan.md` + `status.yaml`. |
| 2 | `rvl-secure-bqr-manager` | `feature/postman-smoke` | Expose a documented `GET /health/live` and `GET /health/ready` (if not already), document any auth quirks needed by the smoke collection, ensure swagger schema is committed for JSON-Schema assertion inputs. |
| 3 | `rvl-sbqr-portal` | `feature/postman-smoke` | Document the auth flow + tenant-id header requirements so the portal folder in the smoke collection can be authored. No production code change expected unless a gap is found. |
| 4 | `rvl-sbqr-fi-gateway` | `feature/postman-smoke` | Same as #3 — document the public FI-facing endpoints + auth, add any missing CORS / smoke affordances only if found blocking. |
| 5 | `rvl-bb-trust-store` | `feature/postman-smoke` | Document the trust-list fetch endpoint; no code change expected. |

> **Note:** work items #2–#5 may turn out to be **doc-only** PRs in those
> submodules (a `docs/api-smoke.md` describing endpoints, headers, and auth).
> If so, the submodule PR shrinks to a markdown file and no production code
> ships.

## Acceptance criteria

- [ ] `tests/api/collections/rvlsbqr-smoke.postman_collection.json` exists,
      imports cleanly into Postman, and contains folders: `Health`, `Auth`,
      `SecureBQR Manager`, `Portal`, `FI Gateway`, `BB Trust Store`.
- [ ] `tests/api/environments/local-dev.postman_environment.json` exists and
      contains only non-secret values (URLs, public client IDs).
- [ ] `newman run tests/api/collections/rvlsbqr-smoke.postman_collection.json \
      -e tests/api/environments/local-dev.postman_environment.json` exits 0
      when run against the local dev docker compose stack.
- [ ] `.github/workflows/api-tests.yml` runs the smoke collection on every PR
      against `main`, produces a JUnit report that surfaces in the PR check
      UI, and uploads an HTML artifact.
- [ ] The workflow honours the secrets rule: no token / cert material is
      committed under `tests/api/`.
- [ ] `docs/features/postman-newman/README.md` exists with: how to add a
      request, how to run locally, how to add a new environment, troubleshooting.
- [ ] At least one `pm.test` per request exercises an assertion beyond
      status-code (schema, header, or business field).
- [ ] HTML audit report produced beside this plan (`postman-newman-api-testing-audit.html`).

## Rollout / risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **Service under test isn't reachable from CI** (no docker compose, or sandboxed runners) | Medium | Phased rollout: phase 1 = local-only (`workflow_dispatch`); phase 2 = PR-time once docker compose is wired into Actions. |
| **Postman sandbox divergence** (something works in Postman GUI, fails in Newman) | Low | Avoid GUI-only features (`pm.visualizer`); use only `pm.*` API + `pm.sendRequest`. Smoke run locally before pushing. |
| **Secrets leak via `--verbose` headers** | Low | Default to non-verbose CI; document redacting in author guide; rely on CI secret masking. |
| **Collection drift** (Postman has newer, Git has older) | Medium | Optional CI step: nightly job compares `git HEAD` collection hash vs Postman Cloud API and posts a PR comment if drift detected. Defer to phase 2. |
| **mTLS breaks Newman** (cert/key files in `dev-pki` not readable in CI) | Medium | Pass thumbprint + token via `--env-var` only; do not have Newman read cert files directly. If a flow needs raw mTLS, fall back to the existing `mtls-postman-vs-fullflow-guide-bn.md` setup. |
| **Flaky external dependency** (BB trust store outage during smoke) | Medium | Mark external calls as `soft` (warn, don't fail) in collection via a `isCritical` variable; require explicit `--bail` override to escalate. |
| **Long runtime as collection grows** | Low | Use `--folder "Smoke"` for PR-time; full regression stays nightly. |

### Rollout phases

- **Phase 1 (this plan):** workspace skeleton + smoke collection for `local-dev`
  running via `workflow_dispatch` (manual). Proves the toolchain end-to-end.
- **Phase 2:** wire `api-smoke` into PR-time CI (every PR), once docker-compose
  CI runner is confirmed stable. Add tenant-onboarding data-driven collection.
- **Phase 3:** staging environment; nightly full-regression job. Optional
  Postman-Cloud drift check.
- **Phase 4:** k6 load testing plan, paired with the smoke collection.

### Verification steps

1. Local developer runs `docker compose up` (per `dev-docker-run-playbook.md`).
2. Developer runs `npm test` inside `tests/api/` — exit code 0.
3. Developer pushes a branch → GitHub Actions `api-smoke` job runs → JUnit
   shows in PR → HTML report downloadable from the run page.
4. Reviewer confirms: no secrets in `tests/api/`, collection imports cleanly
   in Postman, all folders have at least one assertion beyond status code.
5. HTML audit report committed beside this plan for the `rvl-reviewer` check.

## References

- Existing Postman + mTLS notes: `docs/mtls-postman-vs-fullflow-guide-bn.md`,
  `docs/mtls-local-dev-guide-bn.md`.
- Local dev stack bring-up: `docs/dev-docker-run-playbook.md`.
- Security / VAPT context: `docs/misc/security/vapt-*.md` (cert handling must
  not regress).
- Plan template: `docs/plans/_template/plan.md`.
- Workspace conventions: `AGENTS.md`, `repos.yaml`.
- Long-form author guide lives at: `docs/features/postman-newman/README.md`
  (created in work item #1).