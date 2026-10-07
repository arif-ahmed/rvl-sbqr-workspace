# Portal user login: dev-test guide

For a .NET full-stack dev. Run the API in Visual Studio, run the portal with Vite, and test by hand.

What changed: the portal signs in with **username + password** (`POST /v1/auth/login`) instead of `client_id`/`client_secret`. The access token (15 min) lives in memory. The refresh token (7 days, rotating) lives in an **HttpOnly cookie** `sbqr_rt` that JavaScript cannot read. A reload restores the session with `POST /v1/auth/refresh`.

| Repo | Branch |
|---|---|
| `rvl-secure-bqr-manager` | `feature/institution-onboarding` |
| `rvl-sbqr-portal` | `feature/institution-onboarding-api` |

## 0. Read this before you test

1. **Your dev DB is `sbqr_app` and it is shared.** User-secrets **override** environment variables for `ConnectionStrings:sbqr_app` (precedence: `.env` > user-secrets > env vars). Setting `ConnectionStrings__sbqr_app` in a shell does *not* point the API at another DB.
2. Use a throwaway user (`portal-dev`). The existing `master` user has a one-time password nobody may remember. Do not reset it.
3. `public.audit_logs` is **hash-chained** (`previous_hash`, `entry_hash`). Never delete audit rows; delete only the throwaway user and its `user_refresh_tokens`.
4. `/v1/auth/*` shares **10 requests / 60 s per IP**. Many logins or reloads in a minute return `429`. Wait a minute.

## 1. Prerequisites

- .NET SDK 10, Visual Studio 2022 17.10+, Node >= 22, Docker.
- Postgres up with schema applied: `pwsh ./scripts/dev-stack-up.ps1 -Services postgres,rabbitmq` (in `rvl-secure-bqr-manager`). It applies `db/migrations/*.sql` through `013_users.sql`.
- User-secrets already set (`Jwt:SigningKey`, `ConnectionStrings:sbqr_app`, ...). Check: `dotnet user-secrets list --project src/Host/SBQR.Api`.

## 2. Run the API in Visual Studio

1. Open `rvl-secure-bqr-manager/SBQR.slnx`.
2. Startup project `src/Host/SBQR.Api`, profile **SBQR.Api** (`http://localhost:5001`).
3. **F5**. `GET http://localhost:5001/health/ready` must return `200`.

## 3. Seed a throwaway user

1. Project > `SBQR.Api` Debug Properties > **Command line arguments**: `--seed-admin portal-dev --display-name "Portal Dev"`.
2. **F5**. Copy `initial_password` from the console. It is shown once. The process exits.
3. **Clear the command line arguments** again.

First login returns `"mustChangePassword": true`. The portal forces a password change.

## 4. Run the portal

```powershell
cd rvl-sbqr-portal
npm ci
$env:SBQR_API_URL = "http://localhost:5001"   # default
npm run dev                                    # http://localhost:5175
```

Use Chrome, Edge or Firefox on `http://localhost:5175`. Their `Secure` cookie handling works on localhost over http. Safari drops it: run `dotnet user-secrets set "Auth:User:RefreshCookieSecure" "false" --project src/Host/SBQR.Api`.

## 5. API smoke test (no portal)

The refresh token is a cookie, not in the body. Use curl with a cookie jar.

```bash
B=http://localhost:5001
# login -> body has accessToken + user, cookie goes to jar.txt
curl -i -c jar.txt -X POST $B/v1/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"portal-dev","password":"<initial_password>"}'
```

Expected `200`:

```json
{
  "accessToken": "eyJ...",
  "tokenType": "Bearer",
  "expiresIn": 900,
  "refreshTokenExpiresAt": "2026-10-14T06:33:44+00:00",
  "user": { "id": "<guid>", "username": "portal-dev", "displayName": "Portal Dev",
            "role": "MASTER_ADMIN", "mustChangePassword": true }
}
```

Header: `Set-Cookie: sbqr_rt=...; path=/v1/auth; secure; samesite=strict; httponly`. There is **no** `refreshToken` in the body.

```bash
# refresh: empty body, cookie only -> new accessToken + user, rotated cookie
curl -i -b jar.txt -c jar.txt -X POST $B/v1/auth/refresh -H 'Content-Type: application/json' -d '{}'

# call an admin endpoint with the access token
curl -i $B/v1/admin/tenants -H "Authorization: Bearer <accessToken>"

# change password (204). Ends every session.
curl -i -X POST $B/v1/auth/change-password -H 'Content-Type: application/json' \
  -H "Authorization: Bearer <accessToken>" \
  -d '{"currentPassword":"<initial_password>","newPassword":"Another-pass-1"}'

# logout: 204, cookie cleared
curl -i -b jar.txt -c jar.txt -X POST $B/v1/auth/logout -H 'Content-Type: application/json' \
  -H "Authorization: Bearer <accessToken>" -d '{}'
```

| Case | Request | Response |
|---|---|---|
| Wrong password, unknown/locked/disabled user | login | `401 {"error":"invalid_credentials"}` |
| Empty or over-long field | login | `401 {"error":"invalid_request"}` |
| Unknown JSON property | any | `400` ProblemDetails |
| 5 wrong passwords | login | account locked 15 min, same `401` |
| No cookie / revoked / expired / replayed cookie | refresh | `401 {"error":"invalid_refresh_token"}` and the cookie is cleared |
| Wrong current password | change-password | `401 {"error":"invalid_credentials"}` (not an expired token) |
| Over rate limit | any `/v1/auth/*` | `429` |
| Human token on `/v1/crypto-keys` | GET | `200` (master admin passes `KeyAdminOrMasterAdmin`) |

**VS Endpoints Explorer / `.http` files do not keep cookies.** To test refresh from them, set `dotnet user-secrets set "Auth:User:ReturnRefreshTokenInBody" "true" --project src/Host/SBQR.Api`, restart, and send `{"refreshToken":"<value>"}`. Remove the secret afterwards (`dotnet user-secrets remove "Auth:User:ReturnRefreshTokenInBody" --project src/Host/SBQR.Api`). Never leave it on for the portal.

## 6. Breakpoints in Visual Studio

All paths under `rvl-secure-bqr-manager/src/Modules/IdentityAccess/`.

| Where | Why |
|---|---|
| `SBQR.Modules.IdentityAccess.Api/Controllers/AuthController.cs:65` `LoginAsync` | request enters; line 90 `SetRefreshCookie` is where the cookie is written |
| `AuthController.cs:117` `RefreshAsync` | line 123 `ResolveRefreshToken`: Watch `Request.Cookies["sbqr_rt"]` to see the incoming cookie |
| `AuthController.cs:147` | rotated cookie + `user` in the response |
| `AuthController.cs:174` `LogoutAsync` | cookie cleared via `ClearRefreshCookie` (line 275) |
| `AuthController.cs:222` `ChangePasswordAsync` | revokes every refresh family |
| `SBQR.Modules.IdentityAccess.Application/Commands/RefreshUserSession/RefreshUserSessionCommandHandler.cs:71` | **reuse detection** branch: replayed token revokes the whole family |
| `...RefreshUserSessionCommandHandler.cs:114` | happy path: new access token + rotation |
| `SBQR.Modules.IdentityAccess.Infrastructure/Persistence/Repositories/UserRepository.cs:83` `FindUserAndTokenByHashAsync` | token lookup by SHA-256 hash |

Tip: add `Request.Cookies` as a Watch, and `_options` (`UserLoginOptions`) to see `RefreshCookieSecure`.

Portal side (browser DevTools, no VS): `src/shared/api/client.ts` `doRefresh` (line 131), `scheduleRefresh` (91), `send` (273); `src/shared/auth/session.ts` `restoreSession` (161).

## 7. Manual test matrix (browser, `http://localhost:5175`)

Open DevTools: Application tab (Cookies, Local storage) and Network tab.

| # | Do | Expect |
|---|---|---|
| 1 | Open `/staff/rates` signed out | Redirect to `/login` |
| 2 | Sign in `portal-dev` + initial password | Land on `/change-password` (forced). Staff pages redirect back here until done |
| 3 | Wrong current password | Inline "Current password is incorrect." Still signed in; no `/v1/auth/refresh` call |
| 4 | Short or mismatched new password | Inline errors, no request sent |
| 5 | Valid change | Back on `/login` with "Password changed. Sign in again..." (server revoked all sessions) |
| 6 | Sign in with the new password | Lands on the page you first asked for (`/staff/rates`) |
| 7 | Application tab | Cookie `sbqr_rt`: HttpOnly yes, SameSite Strict, Path `/v1/auth`. Local storage has only `sbqr-session = 1`. No token anywhere. `document.cookie` is empty |
| 8 | **Reload** (F5) on `/staff/rates` | Brief blank, then the same page, still signed in. Network: one `POST /v1/auth/refresh` 200, no login |
| 9 | Reload 3 times quickly | Stays signed in each time (rotation works; each refresh gets a new cookie) |
| 10 | Proactive refresh: `dotnet user-secrets set "Auth:User:AccessTokenLifetimeMinutes" "2" --project src/Host/SBQR.Api`, restart, sign in, wait | `POST /v1/auth/refresh` at ~60 s, then every ~60 s. No UI flicker. Remove the secret after |
| 11 | Background the tab for 3+ min, return | Refresh fires as soon as the tab is visible |
| 12 | Two tabs open, reload both at once | Both stay signed in (Web Lock serializes refresh) |
| 13 | Log out in tab A | Tab B returns to `/login` within a moment; `sbqr_rt` cookie gone |
| 14 | After logout, reload | `/login`, **no** `/v1/auth/refresh` call (no hint) |
| 15 | Revoke sessions (SQL below), wait for next refresh | Redirect to `/login` with "Your session expired" |
| 16 | Stop the API, reload | `/login`, hint kept; start API, reload: signed in again |
| 17 | Sign in with a wrong password | "Invalid username or password." |
| 18 | 11 logins in a minute | "Too many sign-in attempts." (429) |
| 19 | Onboarding > signing key step | Key is created (`POST /v1/crypto-keys` 2xx); Review step then activates the tenant |

## 8. DB checks

```sql
-- users (psql: docker exec -it sbqr.postgres psql -U postgres -d sbqr_app)
select username, role, status, must_change_password, failed_login_count, locked_until from public.users;

-- rotation + reuse: one row per refresh; revoked_reason = rotation / logout / reuse_detected / admin_revoke
select family_id, issued_at, revoked_at, revoked_reason, replaced_by_id
from public.user_refresh_tokens
where user_id = (select user_id from public.users where username = 'portal-dev')
order by issued_at;

-- force "session expired" (matrix #15)
update public.user_refresh_tokens set revoked_at = now(), revoked_reason = 'admin_revoke'
where revoked_at is null
  and user_id = (select user_id from public.users where username = 'portal-dev');

-- unlock the throwaway user
update public.users set status = 'ACTIVE', failed_login_count = 0, locked_until = null where username = 'portal-dev';

-- audit trail (read only; never delete: hash-chained)
select created_at, event_type, created_by from public.audit_logs order by sequence desc limit 20;

-- clean up when done
delete from public.user_refresh_tokens where user_id = (select user_id from public.users where username = 'portal-dev');
delete from public.users where username = 'portal-dev';
```

Audit events to expect: `auth.login.succeeded`, `auth.token.refreshed`, `auth.token.refresh_reuse_detected`, `auth.logout`, `auth.password.changed`.

## 9. Known gaps and troubleshooting

| Symptom | Cause |
|---|---|
| Institution (tenant) users cannot sign in | Only platform users log in; token has no tenant id. The `fi` portal surface is built but unreachable |
| No users list/create/disable UI | The API has no users CRUD. Accounts come from `--seed-admin` |
| `/v1/auth/me` always says `mustChangePassword: false` | Claim is never issued. The portal reads `mustChangePassword` from the login/refresh `user` object |
| Cookie not stored on Safari / plain-http non-localhost | `Secure` cookie: set `Auth:User:RefreshCookieSecure=false` |
| Logged out right after a reload with two tabs | Refresh token reuse. The portal serializes refreshes; if you see this, a second client is using the same cookie |
| `429` while testing | Shared 10/60 s bucket on `/v1/auth/*` |

Fixed on the way (they broke the committed backend): login 500 (`UserRefreshToken.RevokedReason` EF mapping), refresh 500 (`FindUserAndTokenByHashAsync` query), every `/v1/admin/**` call 500 (`[Authorize(Policy = "a,b")]` names a non-existent policy; now `PolicyNames.AdminOrMasterAdmin`), and the role claim (`MASTERADMIN` instead of `MASTER_ADMIN`).

## 10. References

- Backend manual (flow, DB, audit): `rvl-secure-bqr-manager/docs/dev-manual/auth-master-login.md`
- API: `AuthController.cs`, `UserLoginOptions.cs`, `appsettings.json` > `Auth:User`
- OpenAPI: `http://localhost:5001/docs/public` (Scalar)
- Portal: `src/shared/api/client.ts`, `src/shared/auth/session.ts`, `src/pages/login.tsx`, `src/pages/change-password.tsx`, `src/App.tsx` (guards)
- Portal tests: `src/App.test.tsx`, `src/shared/auth/session.test.ts`, `src/shared/api/client.test.ts`, fake API in `src/test/fake-backend.ts`
- Portal run + test: `npm run dev`, `npm test`
