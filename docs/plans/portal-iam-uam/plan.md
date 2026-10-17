# Portal IAM / UAM — plan

> Frozen review contract for `rvl-commit` / `rvl-reviewer`.
> Status: see `status.yaml`. Jira: -.
> Backend counterpart: `rvl-secure-bqr-manager/docs/plans/2026-10-17-iam-uam-backend-plan.md`
> (evidence: `rvl-secure-bqr-manager/docs/evidence/2026-10-17-iam-uam-phase-d.md`).
> Reviewable mock: `docs/mocks/iam-portal/index.html` (static, open in a browser).

## Problem

The backend now has users, roles and a tenant-scoped FI user model, but the portal
cannot use any of it:

- Only platform roles can sign in; `FSP_MASTER_ADMIN` / `FSP_USER` are refused and the
  token's tenant is ignored.
- There is no screen to create or manage users, for RVL staff or for an institution.
- Onboarding lets an institution reach activation with nobody able to sign in; the
  backend now blocks that (`FI_END_MASTER_ADMIN_MISSING`) and the wizard has no step for it.
- Suspending or terminating an institution now cascades to its users; the confirm
  dialogs do not say so.
- The API now enforces `password_change_required`; the portal only redirects client-side.

## Scope

- In: `rvl-sbqr-portal` only.
  - Auth/session: login by username **or** email, FI roles open the `fi` surface,
    `tenantId` from `/v1/auth/me`, handle `403 password_change_required`.
  - Staff `/staff/users` (MASTER_ADMIN only): list, filters, detail drawer, create,
    edit/role change, suspend, reactivate, disable, unlock, reset password, revoke sessions.
  - Staff institutions: "FI admins" column, institution **Users** tab, onboarding step
    **FI admin** (before Review) with the new blocker, cascade wording in
    suspend/reactivate/terminate dialogs.
  - FI `/fi/users` (FSP_MASTER_ADMIN only): same screens against `/v1/fsp/users`,
    no role field, no role change, no revoke-sessions.
  - Shared users module used by both surfaces (parameterised by API base path).
  - Test backend (`src/test/fake-backend.ts`) extended; unit/component tests.
- Out:
  - Any backend change. Email invites / set-password links (decided: keep the
    one-time-password dialog after create and reset; revisit later).
  - Audit-log or per-user session-list screens (no endpoint).
  - Behaviour for OPS / AUDITOR / SUPPORT / FINANCE (provisionable, gate nothing yet).
  - MFA, SSO, permission matrix, Payments and Dues.

## Design

Mock: `docs/mocks/iam-portal/index.html` is the visual and behavioural reference
(use the "Proposal notes" toggle and the Route map page).

| Route | Who | API |
|---|---|---|
| `/login` (changed) | anyone | `POST /v1/auth/login { username or email }`; every refusal reads the same |
| `/change-password` (changed) | signed in | `POST /v1/auth/change-password`; other calls while flagged return 403 `password_change_required` and redirect here |
| `/staff/users` (new) | MASTER_ADMIN | `GET/POST /v1/admin/users`, `GET/PATCH …/{id}`, `POST …/{id}/{suspend,reactivate,disable,unlock,reset-password,revoke-sessions}` |
| `/staff/institutions/:id/users` (new tab) | MASTER_ADMIN | `GET /v1/admin/users?tenantId=` |
| `/staff/institutions/new` (changed) | MASTER_ADMIN | new step calls `POST /v1/admin/tenants/{id}/master-admin` (idempotent) |
| `/fi/users` (new) | FSP_MASTER_ADMIN | `GET/POST /v1/fsp/users`, `GET/PATCH …/{id}`, `POST …/{id}/{suspend,reactivate,disable,unlock,reset-password}` |

Decisions:

1. One users module in `src/shared/users` (table, drawer, forms, query hooks) takes the
   API base path; surfaces stay separate lazy chunks and never import each other
   (AGENTS.md).
2. UI role checks are convenience only; the API scopes are the boundary. Hidden nav,
   disabled own-row actions and the FI form without a role mirror the API, they do not replace it.
3. Rule violations (409: last FI Master Admin, self-action, illegal transition,
   duplicate) render as banners/inline errors saying what to do next, not toasts.
4. Cross-tenant is 404; the portal never sends a tenant id from an FI session.
5. One-time passwords are shown once in the existing secret dialog, held in component
   state only: never in storage, logs or the query cache.
6. `Role` type drops `FSP_OPERATOR` / `FSP_VIEWER`, gains `FSP_MASTER_ADMIN` / `FSP_USER`.
7. Use existing tokens/components only (DESIGN.md); no new UI library; no mock data in `src`.

## Submodule work items

| # | Submodule | Branch | Work item |
|---|---|---|---|
| 1 | rvl-sbqr-portal | `feature/iam-uam-portal` | Auth/session: login body, roles, `tenantId`, `password_change_required` handling in `src/shared/api/client.ts` and `src/shared/auth/session.ts` |
| 2 | rvl-sbqr-portal | `feature/iam-uam-portal` | `src/shared/users/` module + `src/surfaces/staff/users/` + nav entry |
| 3 | rvl-sbqr-portal | `feature/iam-uam-portal` | Institutions: FI admins column, Users tab, onboarding FI admin step + blocker, cascade dialog copy |
| 4 | rvl-sbqr-portal | `feature/iam-uam-portal` | `src/surfaces/fi/users/` + nav entry (FSP_MASTER_ADMIN only) |
| 5 | rvl-sbqr-portal | `feature/iam-uam-portal` | `fake-backend.ts` users endpoints; tests; DESIGN.md "Screens" update |

## Acceptance criteria

- [ ] FI Master Admin and FI User can sign in (username or email) and land on `/fi/overview`; platform roles still land on `/staff/overview`.
- [ ] A user with a one-time password is forced to `/change-password`, and a 403 `password_change_required` from any call also routes there.
- [ ] `/staff/users` is reachable by MASTER_ADMIN only; list filters, create, edit, role change and every lifecycle action work against the API.
- [ ] `/fi/users` is reachable by FSP_MASTER_ADMIN only; it lists only its own institution; create/edit forms have no role field.
- [ ] Suspending, disabling or demoting the last FI Master Admin shows the 409 banner with an "Add FI Master Admin" action (staff).
- [ ] Own-row destructive actions are disabled; terminal Disabled users show no actions.
- [ ] Onboarding has the FI admin step before Review; activation stays blocked until it is done; repeating the call shows the idempotent result without a second password.
- [ ] Institution suspend / reactivate / terminate dialogs state the user effect and head-count.
- [ ] One-time passwords are shown once and are never written to storage, logs or query cache.
- [ ] An FI session never loads staff chunks; no cross-surface imports.
- [ ] `npm run lint`, `npm test`, `npm run build` pass; no raw hex or default Tailwind palette classes.
- [ ] HTML audit report produced beside this plan

## Rollout / risks

- Depends on the backend IAM/UAM branch being deployed (`feature/iam-uam-users-subdomain`);
  without it `/v1/admin/users`, `/v1/fsp/users` and FI logins do not exist. Gate the
  portal merge on that deploy.
- Risk: an FI user briefly seeing a staff-only control. Mitigation: nav is built per
  role, routes are guarded, and the API answers 403/404 regardless.
- Risk: leaking a one-time password. Mitigation: component state only; a test asserts nothing is written to storage.
- Follow-up (not this plan): email invite / set-password link; audit-log screen.
