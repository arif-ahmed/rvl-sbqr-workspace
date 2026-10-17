# Portal IAM / UAM — implementation plan

> Companion to [`plan.md`](plan.md) (the frozen contract: problem, decision, scope, acceptance).
> This document is **how and in what order**. Local setup: [`docs/features/portal-host-tenancy/local-dev-guide.md`](../../features/portal-host-tenancy/local-dev-guide.md).
> Mock: [`docs/mocks/iam-portal/index.html`](../../mocks/iam-portal/index.html).
> Status: proposed. Date: 2026-10-17.

## 0. What is being built

Two portals on one backend, tenant chosen by the **host**:

| Environment | Admin portal | FI portal (example tenants) | Base domain |
|---|---|---|---|
| Production | `admin.sbqr.rvl.com` | `dhakabank.sbqr.rvl.com`, `bracbank.sbqr.rvl.com` | `sbqr.rvl.com` |
| UAT | `admin.uat.sbqr.rvl.com` | `dhakabank.uat.sbqr.rvl.com` | `uat.sbqr.rvl.com` |
| Staging | `admin.stg.sbqr.rvl.com` | `dhakabank.stg.sbqr.rvl.com` | `stg.sbqr.rvl.com` |
| Dev | `admin.dev.sbqr.rvl.com` | `dhakabank.dev.sbqr.rvl.com` | `dev.sbqr.rvl.com` |
| Local | `admin.localhost:5175` | `dhakabank.localhost:5176` | `localhost` |

Rules that every phase must keep true:

1. **One build per app, promoted unchanged** through dev → staging → UAT → production. Nothing
   environment-specific is baked into a bundle; the app reads `location.hostname` at runtime and
   all calls stay same-origin `/v1` (existing AGENTS.md rule).
2. **One config value per environment on the API**: `Portal:BaseDomain` (+ `Portal:AdminSlug`).
   A host is valid only if it is exactly `{one label}.{BaseDomain}`.
3. **The portal never sends a tenant id.** Tenant = host, resolved server-side; the token's
   `tenant_id` must equal the host's tenant on every request and refresh.
4. Environments share nothing: own database, API, users, tenants, secrets, certificates.
5. Wrong host, wrong password, locked, disabled, suspended institution: identical
   `invalid_credentials`. Unknown / reserved / terminated slug: identical "Portal not found".

## 1. Order of work and dependencies

```
Phase S  Spikes (de-risk the edge)                         ── blocks 5, 6
Phase 0  Portal workspaces restructure (no behaviour change)
Phase 1  Shared auth/session + api client                  ── needs 0
Phase 2  Shared users module                               ── needs 1
Phase 3  Admin portal: Users, institutions, onboarding     ── needs 2
Phase 4  FI portal app                                     ── needs 1, 2; login needs 5a
Phase 5  Backend: host tenancy (parallel from day 1)       ── 5a before 4 can sign in
Phase 6  Environments, edge, CI/CD (DevOps, parallel)      ── needs S
Phase 7  Rollout
```

Phases 0–4 are `rvl-sbqr-portal`. Phase 5 is `rvl-secure-bqr-manager` (separate branch, separate
review). Phase 6 is infra. 0 and 5 can start the same day. Each phase ends with its gate; do not
start the next phase until the gate passes (`/rvl-commit` per phase).

---

## Phase S — Spikes (≈1 day, before committing to an edge)

| # | Question | How | Decision it drives |
|---|---|---|---|
| S1 | Does the current Vercel setup expose the original host to the API when `scripts/vercel-output.mjs` proxies `/v1`? (Vercel external rewrites replace `Host`; check whether `x-forwarded-host` carries the original.) | Deploy a throwaway project with a wildcard test domain; call `/v1/...` and log request headers at the API or a request-bin. | If yes: Vercel can host both portals. If no: use CloudFront+S3+ALB or nginx (Phase 6 default). |
| S2 | Does a browser keep an HttpOnly refresh cookie per `*.localhost` host, including with `Secure`? | Chrome/Edge/Firefox against a tiny server. | Local dev works without TLS; note Safari. |
| S3 | Fallback if S1 fails and no edge change is possible: derive login scope from the `Origin` header on `POST /v1/auth/login`. Scoping only, credentials still checked, so safe; the defence-in-depth host==token-tenant check then needs the edge header. | Review only. | Whether FI portal can ship before the real edge exists. |

Output: a short note appended to this file (decision + evidence). **Default assumption if not
yet run: AWS CloudFront + S3 + ALB (§Phase 6).**

---

## Phase 0 — Portal workspaces restructure (behaviour-preserving)

Branch `feature/portal-workspaces` off `main` in `rvl-sbqr-portal`. Goal: same app, new layout.
Review as moves; no logic changes in this PR.

**Target layout**

```
rvl-sbqr-portal/
  package.json              workspaces: ["apps/*", "packages/*"]; shared dev deps
  tsconfig.base.json        compiler options (from today's tsconfig.app.json)
  .oxlintrc.json            + boundary rules (below)
  apps/admin/               package @sbqr/admin   (login, change-password, surfaces/staff)
    index.html  vite.config.ts  src/{main.tsx,App.tsx,pages/,surfaces/staff/}
  apps/fi/                  package @sbqr/fi      (login, change-password, surfaces/fi)
    index.html  vite.config.ts  src/{main.tsx,App.tsx,pages/,surfaces/fi/}
  packages/shared/          package @sbqr/shared  (from today's src/shared + src/test + index.css)
    src/{api,auth,billing,usage,layout,ui,theme.ts,format.ts,cn.ts,download.ts,styles.css,testing/}
    vite/base-config.ts     proxy + dev port helper used by both apps
  scripts/                  vercel-output.mjs generalised (takes APP and API URL)
  design/  docs/  DESIGN.md  AGENTS.md  README.md
```

**Steps**

1. `git mv src/shared packages/shared/src`; `src/test/*` → `packages/shared/src/testing/`;
   `src/index.css` → `packages/shared/src/styles.css`. Export via `packages/shared/package.json`
   `exports` (`"."`, `"./ui"`, `"./testing"` …) so apps import `@sbqr/shared/...`, never relative
   paths across packages.
2. Admin app: move `src/pages`, `src/surfaces/staff`, `src/App.tsx`, `src/main.tsx`, `index.html`.
   FI app: move `src/surfaces/fi`; copy the `pages/` it needs (login, change-password) as thin
   wrappers over shared components; `App.tsx` mounts only the `fi` surface. Keep route prefixes
   `/staff/*` and `/fi/*` so no screen or test changes (host decides the surface; renaming is a
   later cleanup, not part of this move).
3. Tailwind v4: each app's CSS entry `@import '@sbqr/shared/styles.css'` and adds
   `@source "../../../packages/shared/src"` so utilities used in shared components are generated.
4. `packages/shared/vite/base-config.ts` exports `appConfig({ port, name })` returning the existing
   proxy block (`/v1`, `/openapi` → `SBQR_API_URL`) **plus** a `proxyReq` hook that sets
   `x-forwarded-host` to the browser's host (see local guide). Admin `5175`, FI `5176`.
5. Root scripts: `dev:admin`, `dev:fi`, and `dev` (runs both through the `concurrently` dev
   dependency), plus `build`, `lint`, `test` across workspaces (`npm run build --workspaces`).
6. Tests: root `vitest.workspace` (one project per package/app). `tokens.test.ts` moves into
   `packages/shared` and scans `apps/*/src` and `packages/shared/src`.
7. Boundary lint in `.oxlintrc.json` (`no-restricted-imports` with `patterns`):
   - `apps/admin/**` may not import `@sbqr/fi` or `apps/fi/**`;
   - `apps/fi/**` may not import `@sbqr/admin` or `apps/admin/**`;
   - `packages/shared/**` may not import from `apps/**`.
   Add a tiny test (`scripts/boundaries.test.mjs`) that fails if a forbidden path appears.
8. Deploy scripts: generalise `scripts/vercel-output.mjs` and `vercel-config.mjs` to take the app
   (`APP=admin|fi`) and keep their tests green. Update `.env.example`, `.gitignore`, `README.md`
   (layout + commands), `AGENTS.md` (two apps; never import across apps), `DESIGN.md` (surfaces →
   apps).

**Gate 0**: `npm ci && npm run lint && npm test && npm run build` green at root; the admin app
behaves exactly as today (manual click-through with the existing dev guide
`docs/features/portal-user-login/dev-testing-guide.md`); `git diff -M` is overwhelmingly renames.

---

## Phase 1 — Shared auth and API client

Branch `feature/iam-uam-portal` (continues after Gate 0 merge).

Files (all under `packages/shared/src`):

- `api/client.ts`: `AuthUser` gains `tenantId?: string`; login body is `{ username | email, password }`
  (value containing `@` → `email`); treat `403 { error: 'password_change_required' }` from any
  call as "go to /change-password" (single handler, same pattern as `setSessionExpiredHandler`);
  refresh path unchanged (`refreshSession()` stays the only caller).
- `auth/session.ts`: `Role` = `MASTER_ADMIN | OPS | AUDITOR | SUPPORT | FINANCE | FSP_MASTER_ADMIN | FSP_USER`
  (drop `FSP_OPERATOR`, `FSP_VIEWER`); `Session.tenantId` filled from the user; factory
  `createSessionStore({ allowedRoles, surface })` so each app declares what it accepts. A role
  outside the app's allow-list gets the same generic sign-in error (call logout to drop the cookie,
  as today's `sessionFromUser` failure path does).
- `auth/portal-context.ts` (FI app only consumer): `usePortalContext()` →
  `GET /v1/public/portal-context` → `{ institutionName, status }`; 404 → "Portal not found" page.
- Login page becomes a shared `<LoginForm />` + per-app wrappers (admin hero vs FI hero with
  institution name). Field label "Username or email". Banner copy unchanged and generic.

Tests (Vitest, `fake-backend.ts`): login sends `email` vs `username`; 403 `password_change_required`
redirects; admin store rejects `FSP_*`; FI store rejects platform roles; refresh serialisation
tests untouched and green.

**Gate 1**: lint/test/build green; admin app unchanged for MASTER_ADMIN.

## Phase 2 — Shared users module

`packages/shared/src/users/` (base-path parameterised; surfaces never fork it):

- `api.ts` — `createUsersApi(basePath)` + TanStack hooks: `useUsers(filters)`, `useUser(id)`,
  `useCreateUser`, `useUpdateUser`, and one `useUserAction(action)` for
  `suspend|reactivate|disable|unlock|reset-password|revoke-sessions`. Capabilities object
  (`{ roleChange, revokeSessions, scopeFilter }`) decides what the UI offers: admin = all, FI = none.
- `users-table.tsx`, `user-drawer.tsx`, `user-form.tsx` (zod + react-hook-form; FI variant has no
  role field), `user-actions.tsx` (confirm dialogs: suspend, reset, revoke, disable = solid red),
  `status.ts` (Active ok / Pending info / Suspended warn / Locked warn / Disabled bad, DESIGN.md).
- Move `surfaces/staff/onboarding/secret-dialog.tsx` to `shared/ui` and reuse it for every
  one-time password (create, reset, master-admin). Password lives in component state only; add a
  test that nothing is written to `localStorage`/`sessionStorage`.
- Error mapping: `409` (last FI Master Admin, self-action, illegal transition, duplicate) → banner
  or inline field error with the next step; `404` (cross-tenant) → generic not-found; never a toast.
- `fake-backend.ts`: add `/v1/admin/users*` and `/v1/fsp/users*` handlers enforcing the same rules
  (last-admin, self-guard, containment, duplicate) so UI tests exercise them.

**Gate 2**: unit + component tests for table, drawer, each action, each error mapping, OTP
single-show; token test passes.

## Phase 3 — Admin portal screens

`apps/admin/src/surfaces/staff/`:

- `users/users-page.tsx` + route `users`; `nav.ts` adds **Access → Users** for `MASTER_ADMIN` only.
  Filters (scope, institution, role, status, search) live in the URL query string.
- `institutions-page.tsx`: columns **Portal address** and **FI admins** (from the onboarding view).
- `institutions/institution-detail.tsx`: **Users** tab (`?tenantId=`), health banner when no active
  FI Master Admin, **Open FI portal ↗** (builds `https://{slug}.{baseDomain}` from the
  tenant response; base domain comes from the API, not a build-time constant).
- Cascade copy in `use-institution-actions.tsx` dialogs (Suspend / Reactivate / Terminate) with the
  affected-user count (from `GET /v1/admin/users?tenantId=&status=`).
- `onboarding/steps.tsx`: Institution step gains **Portal address** (slug) with client-side
  validation mirroring the server (3–30, `[a-z0-9-]`, reserved list returned by the API, not
  hard-coded); new **FI admin** step between Signing key and Review →
  `POST /v1/admin/tenants/{id}/master-admin`, idempotent result handling (`alreadyExisted`);
  Review gains rows for Portal address and FI Master Admin and the
  `FI_END_MASTER_ADMIN_MISSING` blocker link.

**Gate 3**: component tests per screen; manual run against the local API with the walkthrough in
the local guide §6; mock parity check against `docs/mocks/iam-portal/index.html`.

## Phase 4 — FI portal app

`apps/fi/`:

- Host-aware login: `usePortalContext()` supplies the institution name for the hero; 404 or
  network error on context → "Portal not found".
- Surfaces: existing Overview / Usage / Statements plus `users/users-page.tsx` (shared module,
  FI capabilities) for `FSP_MASTER_ADMIN` only; `nav.ts` builds per role; direct visit by
  `FSP_USER` shows "Page not found" (API still 403).
- `data-surface="fi"` set once; accent from tokens (no per-tenant colours in this plan).

**Gate 4**: tests for role-based nav, not-found, scoped list (fake backend returns only own tenant);
bundle check script asserts `apps/fi/dist` contains none of the admin screen chunks.

---

## Phase 5 — Backend: host tenancy (`rvl-secure-bqr-manager`, branch `feature/portal-host-tenancy`)

Builds on `feature/iam-uam-users-subdomain`. Hand over as separate tickets; portal Phases 1–3 do
not need them, Phase 4 sign-in does (5a–5c).

| # | Change | Where (existing anchors) |
|---|---|---|
| 5a | `tenants.portal_slug` (unique, lowercase), migration `016_tenant_portal_slug.sql`; `Tenant` aggregate + `TenantConfiguration`; returned in tenant responses; set on registration; immutable once Active; reserved list incl. env and infra labels (`admin www api app mail portal static docs status svc dev stg uat staging`, plus any `gw-*` / `idp-*` prefix) | `db/migrations/`, `Tenancy.Domain/Aggregates/Tenant.cs`, `Tenancy.Infrastructure/.../TenantConfiguration.cs` |
| 5b | `PortalOptions` (`Portal:BaseDomain`, `Portal:AdminSlug`, startup validation) and `IPortalHostResolver`: takes the effective host (`X-Forwarded-Host` only when the caller is a known proxy **or** presents the edge's origin-verify secret header, since CloudFront egress IPs are not stable; otherwise `Host`), strips port, requires `{label}.{BaseDomain}`; returns `Admin`, `Tenant(slug)` or `Unknown`. Extend the existing forwarded-headers block to include `XForwardedHost`; set `AllowedHosts` per env | `src/Host/SBQR.Api/Program.cs` (≈L499–525, L798), `appsettings*.json` |
| 5c | Login/refresh scoped by resolved portal: admin portal → platform users only; tenant portal → `GetByUsernameAsync(tenantId, username)` or email within the tenant; retire tenant-user use of `FindByLoginIdentifierAsync`; all refusals identical; audit reason `portal_mismatch`; `Terminated`/unknown → 404 context | `IdentityAccess.Application/Commands/LoginUser/`, `RefreshUserSession/`, `UserRepository.cs` |
| 5d | Middleware after `UseAuthentication`: authenticated requests on a tenant portal require token `tenant_id` == portal tenant; admin portal requires no `tenant_id`; otherwise `403`-as-`404` per containment rule | beside `ForcePasswordChangeMiddleware` |
| 5e | `GET /v1/public/portal-context` (anonymous, rate-limited): `{ institutionName, status }` or `404` for unknown/reserved/terminated | new controller in Tenancy.Api |
| 5f | Tests: same username at two tenants resolves per host; staff on tenant host refused; tenant user on admin host refused; token/host mismatch refused; identical error bodies; port stripping; spoofed `X-Forwarded-Host` from untrusted caller ignored | `tests/SBQR.Modules.IdentityAccess.Tests`, `tests/SBQR.Tenancy.IntegrationTests` |
| 5g | Decision (flag only): relax `ix_users_email_unique` to `(tenant_id, email)` | not bundled |

Per-environment API config:

```
Portal__BaseDomain=sbqr.rvl.com            # uat.sbqr.rvl.com, stg..., dev..., localhost
Portal__AdminSlug=admin
ForwardedHeaders__Enabled=true
ForwardedHeaders__KnownNetworks__0=<edge/ALB CIDR>
AllowedHosts=*.sbqr.rvl.com;sbqr.rvl.com    # per env
```

## Phase 6 — Environments, edge and CI/CD (DevOps)

Per environment (dev, staging, UAT, production) — default design on AWS; replace if S1 proves
Vercel is sufficient:

1. **DNS**: `admin.{env}`/`admin` record plus wildcard `*.{base}` to the distribution(s). Production
   `*.sbqr.rvl.com`; others `*.uat.sbqr.rvl.com` etc. Define every environment's own wildcard:
   DNS wildcards also match deeper names (`x.uat.sbqr.rvl.com` would fall to production's
   `*.sbqr.rvl.com` if UAT had none), whereas a TLS wildcard certificate covers one label only.
   Reserved env labels stop `uat.sbqr.rvl.com` being claimed as a slug in production.
2. **TLS**: ACM wildcard certificate per base domain (`*.{base}`), DNS-validated. CloudFront
   certificates must be issued in `us-east-1`; ALB certificates are regional.
3. **Static hosting**: two S3 buckets (`admin`, `fi`) with CloudFront; SPA fallback to
   `index.html`; security headers (HSTS, CSP, X-Frame-Options, Referrer-Policy) via response headers policy.
4. **API path**: CloudFront behaviour `/v1/*`, `/openapi/*` → ALB origin; a CloudFront Function sets
   `x-forwarded-host` from the viewer `Host` (overwriting any client value) and the origin adds a
   secret header the ALB/API requires, so only the edge is trusted (`KnownNetworks`).
5. **Access control**: non-production environments restricted to an IP allow-list or VPN at the
   edge; production admin host restricted (allow-list/VPN) and fronted by WAF with per-host+IP
   rate limits on `/v1/auth/*` and `/v1/public/*`.
6. **Isolation**: separate AWS accounts (preferred) or separate stacks/databases/secrets per
   environment; no shared data stores.
7. **Align with the existing AWS dev plan**
   ([`docs/features/dev-aws-deployment/deployment-aws-plan-bn.md`](../../features/dev-aws-deployment/deployment-aws-plan-bn.md)).
   It already decides: portal = private S3 + CloudFront (OAC) + ACM, `/v1/*` and `/openapi/*` as a
   second origin with caching off and cookies/`Authorization` forwarded, SPA fallback through a
   CloudFront Function, security headers via a Response Headers Policy, Terraform for everything.
   Keep all of that; the changes are:
   - **Host collision to resolve first.** That plan points `*.dev.sbqr.<domain>` at the EC2/Elastic
     IP for `gw-<fi>.dev…` and `idp-<fi>.dev…`. FI portal hosts (`dhakabank.dev.sbqr.<domain>`)
     would hit that wildcard. Fix: keep human portals on `*.{env}.sbqr.<domain>` and move machine
     endpoints to their own zone, e.g. `gw-dhakabank.svc.dev.sbqr.<domain>` /
     `idp-dhakabank.svc.dev.sbqr.<domain>` under `*.svc.dev.sbqr.<domain>`; `api.{env}…` stays an
     explicit record (explicit names beat wildcards). Add `svc` to the reserved slugs and update
     the gateway/IdP names in that plan and in the fi-gateway/app-emulator configs.
   - `portal.dev.sbqr.<domain>` is replaced by `admin.dev.sbqr.<domain>` (admin) plus the FI wildcard.
     Two CloudFront distributions (admin, FI wildcard), same bucket/OAC pattern, one bucket each.
   - Add the `x-forwarded-host` viewer-request function and list that header in the `/v1/*`
     origin request policy so it reaches the origin. CloudFront egress IPs are not stable, so the
     API trusts the header only when the request carries the edge's origin-verify secret header
     (Phase 5b), with the security group limited to the CloudFront managed prefix list.
   - Dev uses a single EC2 behind Elastic IP (no ALB); production uses ALB + ASG as in that plan.
     The same header/secret rule applies to both.
   - Implement as Terraform modules in the structure from that plan's step 1 (`dns`, `cert`,
     `portal_static` instantiated twice, `edge_api_origin`), parameterised by `env` and `base_domain`.
8. **CI/CD** (pipeline per app): lint → test → build once → store artifact → deploy dev →
   automated smoke (below) → promote the **same artifact** to staging → UAT → production on approval.
   Environment differences exist only in API config and edge, never in bundles.
9. **Smoke per environment** (scripted, `newman`/`agent-browser`): `admin` host login page loads;
   FI host login shows institution name; wrong-host login refused; unknown host shows "Portal not
   found"; `GET /v1/public/portal-context` 200/404.

## Phase 7 — Rollout

- No FI portal users exist today (FI login is not possible yet), so there is **no user cutover**.
  Existing staff move from the current portal URL to `admin.` — keep the old URL redirecting for one
  release.
- Order: dev → staging → UAT with two test tenants (`dhakabank`, `bracbank`) → production with one
  pilot institution → remaining institutions onboarded by setting their slug.
- Rollback: portal bundles are immutable artifacts, so redeploy the previous one. The backend
  change is additive (the admin host keeps today's platform-user login), so rolling the portal back
  does not require rolling the API back.

## Test strategy

| Level | What | Where |
|---|---|---|
| Unit/component | users module, session store allow-lists, 403 handler, portal-context | Vitest + `fake-backend.ts` |
| Architecture | no cross-app imports; FI bundle has no admin chunks; no raw hex | `scripts/boundaries.test.mjs`, `tokens.test.ts`, bundle check |
| Backend | host resolution, scoped login, token/host binding | xUnit (Phase 5f) |
| End-to-end (local) | local guide §6 scenarios, `agent-browser` on `*.localhost` | manual + scripted |
| Per-environment smoke | Phase 6 item 9 | pipeline |

## Definition of done

All `plan.md` acceptance criteria met; Gates 0–4 passed; backend 5a–5f merged and deployed to the
environment under test; Phase 6 smoke green in dev, staging and UAT; HTML audit report beside
`plan.md`; `AGENTS.md`, `DESIGN.md`, `README.md` updated for two apps.

## Risks and open items

| Risk / open item | Mitigation / owner |
|---|---|
| Edge cannot forward the original host (Vercel rewrites replace `Host`) | Spike S1 first; AWS CloudFront default; `Origin`-based login scoping as stop-gap (S3) |
| Large move diff in Phase 0 | Own PR, rename-only, merged before features |
| Tailwind v4 not scanning shared package sources | `@source` rule + a visual smoke in Gate 0 |
| Existing `*.dev.sbqr.<domain>` wildcard (gateways/IdP mocks) swallows FI portal hosts | Move machine endpoints to `*.svc.{env}.sbqr.<domain>` before the first FI portal deploy (Phase 6.7) |
| Wildcard certificate/DNS per environment missed | Phase 6 checklist; smoke test fails loudly on unknown host |
| `X-Forwarded-Host` spoofing | Trusted only from `KnownNetworks`/secret origin header; test 5f |
| Slug changes after go-live break bookmarks | Immutable once Active |
| Global unique email blocks one person at two banks | Flagged (5g), decide before multi-bank staff exist |
