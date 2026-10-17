# Portal IAM / UAM — plan (two portals: Admin + per-tenant FI)

> Frozen review contract for `rvl-commit` / `rvl-reviewer`.
> Status: see `status.yaml`. Jira: -.
> Backend counterpart: `rvl-secure-bqr-manager/docs/plans/2026-10-17-iam-uam-backend-plan.md`
> (evidence: `rvl-secure-bqr-manager/docs/evidence/2026-10-17-iam-uam-phase-d.md`).
> Reviewable mock: `docs/mocks/iam-portal/index.html` (static, open in a browser; use the
> Address switch, "Proposal notes" and the Route map page).

## Problem

The backend now has users, roles and a tenant-scoped FI user model, but the portal cannot use
any of it:

- Only platform roles can sign in; `FSP_MASTER_ADMIN` / `FSP_USER` are refused and the token's
  tenant is ignored.
- There is no screen to create or manage users, for RVL staff or for an institution.
- Onboarding lets an institution reach activation with nobody able to sign in; the backend now
  blocks that (`FI_END_MASTER_ADMIN_MISSING`) and the wizard has no step for it.
- Suspending or terminating an institution now cascades to its users; the confirm dialogs do
  not say so.
- The API now enforces `password_change_required`; the portal only redirects client-side.
- **A single shared login cannot tell tenants apart.** The backend takes the tenant from the
  user row and stamps it into the JWT; nothing at login selects one. Username is unique only per
  tenant and `FindByLoginIdentifierAsync` takes no tenant (`FirstOrDefault`), so two banks with
  an `alice` collide. Email is globally unique but email-only is not enough for a B2B bank
  platform (one person cannot hold accounts at two institutions, shared mailboxes, no domain
  binding). The backend has no host/subdomain tenant resolution today.

## Decision

Split into **two portals**:

| | Admin portal | FI portal |
|---|---|---|
| Address | `admin.<domain>` | `<slug>.<domain>` (e.g. `sonali.<domain>`) |
| Users | MASTER_ADMIN, OPS, AUDITOR, SUPPORT, FINANCE | FSP_MASTER_ADMIN, FSP_USER of that tenant only |
| Login | username or email, platform scope | username or email, scoped to the host's tenant |
| API area | `/v1/admin/**` | `/v1/fsp/**` + the institution's own billing/usage |
| Branding | RVL | institution name from the host (public portal-context call) |

The **host fixes the tenant**: nobody types an institution code, and username-per-tenant becomes
unambiguous. One repo, two apps; the API resolves the tenant from the forwarded host.

Rules (server side, never client-asserted; the portal sends no tenant id):

1. The edge forwards the original host; the API maps slug → tenant.
2. Admin host accepts platform users only; an FI host accepts that tenant's users only. Any
   mismatch (staff on an FI host, an FI user on the admin host or on another bank's host), wrong
   password, locked, disabled or suspended institution returns the same `invalid_credentials`.
3. Every authenticated request and refresh on an FI host must carry a token whose `tenant_id`
   equals the host's tenant (defence in depth).
4. Unknown, reserved or terminated slug: generic "Portal not found", no tenant enumeration.
5. Refresh cookie stays host-only (no `Domain=`), so sessions never cross subdomains.

## Scope

- In:
  - `rvl-sbqr-portal`: restructure into npm workspaces (`packages/shared`, `apps/admin`,
    `apps/fi`) with no behaviour change; Admin portal IAM; new FI portal app.
  - Admin portal: `/staff/users` (MASTER_ADMIN only: list, filters, detail drawer, create, edit /
    role change, suspend, reactivate, disable, unlock, reset password, revoke sessions);
    institutions list gets Portal address + FI admins columns; institution Users tab and
    "Open FI portal" link; onboarding Portal address field and **FI admin** step (before Review)
    with the new blocker; cascade wording in suspend / reactivate / terminate dialogs.
  - FI portal: host-aware login, `/fi/users` (FSP_MASTER_ADMIN only) against `/v1/fsp/users`
    (no role field, no role change, no revoke-sessions), existing Overview / Usage / Statements.
  - Shared users module (table, drawer, forms, query hooks) parameterised by API base path.
  - Test backend (`fake-backend.ts`) extended with host-scoped login and the users endpoints.
  - Backend handover list (below); the backend work itself is out of this plan's code changes.
- Out:
  - Any backend code change in this plan. Email invites / set-password links (decided: keep the
    one-time-password dialog after create and reset; revisit later).
  - Audit-log or per-user session-list screens (no endpoint).
  - Behaviour for OPS / AUDITOR / SUPPORT / FINANCE (provisionable, gate nothing yet).
  - Custom domains, per-institution theming beyond name, SSO, MFA, Payments and Dues.

## Design

Mock: `docs/mocks/iam-portal/index.html` is the visual and behavioural reference.

| Route | Portal | Who | API |
|---|---|---|---|
| `/login` (changed) | both | anyone | `POST /v1/auth/login { username or email }`; no tenant in the body |
| `/` unknown host (new) | FI | anyone | `GET /v1/public/portal-context` → institution name, or 404 |
| `/change-password` (changed) | both | signed in | `POST /v1/auth/change-password`; other calls while flagged return 403 `password_change_required` and redirect here |
| `/staff/users` (new) | admin | MASTER_ADMIN | `GET/POST /v1/admin/users`, `GET/PATCH …/{id}`, `POST …/{id}/{suspend,reactivate,disable,unlock,reset-password,revoke-sessions}` |
| `/staff/institutions/:id/users` (new tab) | admin | MASTER_ADMIN | `GET /v1/admin/users?tenantId=` |
| `/staff/institutions`, `/new`, `/:id` (changed) | admin | staff | Portal address + FI admins columns; slug field; FI admin step → `POST /v1/admin/tenants/{id}/master-admin` (idempotent); cascade dialog copy |
| `/fi/users` (new) | FI | FSP_MASTER_ADMIN | `GET/POST /v1/fsp/users`, `GET/PATCH …/{id}`, `POST …/{id}/{suspend,reactivate,disable,unlock,reset-password}` |

Decisions:

1. `packages/shared` holds today's `src/shared` (ui, api client, session, layout, theme, format,
   billing, usage, new users module) and test helpers. `apps/admin` and `apps/fi` are separate
   Vite builds and deployments; **apps never import each other** (package boundary plus lint rule).
   AGENTS.md / DESIGN.md / README.md are updated to match.
2. Each app has its own session role allow-list (admin: platform roles; FI: `FSP_*`). `Role`
   drops `FSP_OPERATOR` / `FSP_VIEWER`, gains `FSP_MASTER_ADMIN` / `FSP_USER`.
3. UI role checks are convenience only; the API scopes and host binding are the boundary.
4. Rule violations (409: last FI Master Admin, self-action, illegal transition, duplicate) render
   as banners / inline errors that say what to do next, not toasts.
5. One-time passwords are shown once in the existing secret dialog, held in component state only:
   never in storage, logs or the query cache.
6. Portal address (slug): lowercase 3–30 chars, unique, reserved list (admin, www, api, app, mail,
   portal, static, docs, status); fixed once the institution is Active.
7. Dev: `*.localhost` (e.g. `sonali.localhost:5176`) with the proxy forwarding the original host.
   Existing tokens, components and same-origin `/v1` rule are unchanged; no mock data in `src`.

## Submodule work items

| # | Submodule | Branch | Work item |
|---|---|---|---|
| 0 | rvl-sbqr-portal | `feature/portal-workspaces` | Behaviour-preserving restructure: `packages/shared`, `apps/admin`, `apps/fi`; per-app Vite config, ports 5175 / 5176; boundary lint; docs |
| 1 | rvl-sbqr-portal | `feature/iam-uam-portal` | Auth/session: login body, roles, `tenantId`, `password_change_required`, per-app role allow-list |
| 2 | rvl-sbqr-portal | `feature/iam-uam-portal` | `packages/shared/users` + Admin Users screen, nav entry |
| 3 | rvl-sbqr-portal | `feature/iam-uam-portal` | Institutions: columns, Users tab, Portal address field, FI admin step + blocker, cascade copy |
| 4 | rvl-sbqr-portal | `feature/iam-uam-portal` | FI app: host-aware login + portal-context, Users screen (FSP_MASTER_ADMIN only) |
| 5 | rvl-sbqr-portal | `feature/iam-uam-portal` | `fake-backend.ts` host-scoped login and users endpoints; tests; DESIGN.md "Screens" |
| 6 | rvl-secure-bqr-manager | `feature/portal-host-tenancy` | **Handover (not in this plan's code):** see below |

Backend handover (item 6):
- `tenants.portal_slug` (unique, lowercase, reserved list) set at onboarding and returned by the
  tenant API; migration.
- Host resolver (forwarded host → tenant) and `GET /v1/public/portal-context` returning only
  `{ institutionName, status }`; 404 for unknown / reserved / terminated.
- Login and refresh scoped by host: FI host uses `GetByUsernameAsync(tenantId, username)` or email
  within the tenant; admin host platform-only; retire the ambiguous `FindByLoginIdentifierAsync`
  path for tenant users.
- Enforce token tenant == host tenant on every request; audit entries include the host.
- Flag only: relax global email uniqueness to (tenant, email)?

## Acceptance criteria

- [ ] Item 0 lands first with unchanged behaviour: `npm run lint`, `npm test`, `npm run build` pass for all workspaces.
- [ ] Admin app: only platform roles sign in; an FI account gets the generic `invalid_credentials`.
- [ ] FI app on `<slug>.<domain>`: only that tenant's users sign in; the same username at two institutions signs in to the right one; another institution's account or a staff account is refused generically.
- [ ] Unknown / reserved / terminated slug shows "Portal not found" and no tenant details.
- [ ] A user with a one-time password is forced to `/change-password`; a 403 `password_change_required` from any call also routes there.
- [ ] `/staff/users` is reachable by MASTER_ADMIN only; filters, create, edit, role change and every lifecycle action work against the API.
- [ ] `/fi/users` is reachable by FSP_MASTER_ADMIN only; lists only its own institution; create / edit forms have no role field.
- [ ] Suspending, disabling or demoting the last FI Master Admin shows the 409 banner with an "Add FI Master Admin" action (admin portal).
- [ ] Own-row destructive actions are disabled; terminal Disabled users show no actions.
- [ ] Onboarding has the Portal address field and the FI admin step before Review; activation stays blocked until the FI admin exists; repeating the create shows the idempotent result without a second password.
- [ ] Institution suspend / reactivate / terminate dialogs state the user effect and head-count.
- [ ] One-time passwords are shown once and never written to storage, logs or query cache.
- [ ] The FI bundle contains no admin code and vice versa; no cross-app imports.
- [ ] No raw hex or default Tailwind palette classes (`tokens.test.ts`).
- [ ] HTML audit report produced beside this plan

## Rollout / risks

- Depends on item 6 and on the backend IAM/UAM branch being deployed; without them FI logins,
  `/v1/admin/users`, `/v1/fsp/users`, portal-context and host-scoped login do not exist. Gate the
  portal merge on that deploy.
- **Edge must forward the original `Host`.** Vercel rewrites replace the host, so the production
  edge (nginx / Cloudflare / ALB) must be confirmed before items 4 and 6 are scheduled. Wildcard
  DNS and a wildcard TLS certificate are required for `*.<domain>`.
- Restructure risk: large move diff. Mitigation: item 0 is its own branch and commit with no
  behaviour change, reviewed before any feature work.
- Risk: an FI user briefly seeing a staff-only control. Mitigation: separate apps and bundles,
  per-app role allow-list, guarded routes, API 403/404 regardless.
- Risk: leaking a one-time password. Mitigation: component state only; a test asserts nothing is
  written to storage.
- Follow-up (not this plan): email invite / set-password link; audit-log screen; custom domains.
