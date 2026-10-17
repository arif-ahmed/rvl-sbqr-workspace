# Run the Admin portal and a Bank (FI) portal on your laptop

**Who this is for:** you just joined, you have a laptop and repository access, and you have never run
this system. Follow the parts in order. Every step says what you are doing, why, the exact command,
and what you should see. If what you see is different, go to [Troubleshooting](#9-troubleshooting).

**Time:** about 1 hour the first time (most of it downloads). Starting everything on later days takes
about 2 minutes.

> **Status of this guide.** It describes the setup *after* two pieces of planned work are merged
> (see [`implementation-plan.md`](../../plans/portal-iam-uam/implementation-plan.md)):
> **[Portal split]**, which makes `npm run dev` start both the Admin and the Bank portal, and
> **[API host support]**, which lets the API tell banks apart by web address. Steps that need them
> are marked with those tags. If they are not merged yet, ask your lead; until then you can still run
> today's single portal with
> [`docs/features/portal-user-login/dev-testing-guide.md`](../portal-user-login/dev-testing-guide.md).

---

## 1. First, understand what you are about to run

You do not need to know the code yet. You need this picture.

**Secure Bangla QR (SBQR)** lets banks and mobile wallets issue and verify payment QR codes. **RVL**
(our company) runs the platform. Banks and wallets are called **institutions**, or **FIs** (financial
institutions), or **tenants** (software word for "one customer of a shared system").

There are **two websites** (portals) and **one backend**:

| | Admin portal | Bank (FI) portal |
|---|---|---|
| Who uses it | RVL staff | Staff of one bank |
| What they do | Onboard banks, manage keys, billing, **create users for any bank** | See their own usage and statements, **create users for their own bank only** |
| Address (your laptop) | `http://admin.localhost:5175` | `http://dhakabank.localhost:5176` (one address per bank) |

```
 Your browser
   ├─ http://admin.localhost:5175      ──► Admin portal  ─┐
   ├─ http://dhakabank.localhost:5176  ──► Bank portal   ─┼──►  API  ──►  Database
   └─ http://bracbank.localhost:5176   ──► Bank portal   ─┘   (.NET)      (PostgreSQL)
```

**How does the system know which bank you are?** From the **web address**. The part before
`.localhost` (`dhakabank`) is the bank's **slug** (its short name). The same Bank portal code serves
every bank; the address tells the API which bank's users to look at. So `teller.karim` at Dhaka Bank
and `teller.karim` at BRAC Bank are different people and never get mixed up. You never type a bank
name on the login page.

**Why `.localhost`?** Chrome, Edge and Firefox automatically send any `something.localhost` address
to your own computer. That gives you many different web addresses with nothing to install and no
system files to edit.

**What is "the API"?** The .NET program in `rvl-secure-bqr-manager`. It owns all data and rules. The
portals are only screens: they never keep data and never decide who may do what (the API does).

**What is a "one-time password"?** When someone creates a user, the system makes a temporary
password and shows it **once**. The new user must replace it the first time they sign in. You will
see this several times below. Never paste one into chat or a ticket, and never commit one.

---

## 2. Install the tools

Install these once. After each, run the check command; the output should be similar.

| Tool | Why you need it | Check | Expect |
|---|---|---|---|
| **Git** | get the code | `git --version` | `git version 2.x` |
| **Node.js 24** (via nvm: `nvm-windows` on Windows, `nvm` on macOS/Linux) | runs the portals | `node -v` | `v24.x` (22 is the minimum) |
| **.NET SDK 10** | runs the API | `dotnet --version` | `10.x` |
| **Docker Desktop** | runs the database | `docker --version` and `docker ps` | a version, and `docker ps` works without error |
| **PowerShell 7 (`pwsh`)** | repo scripts use it (also on macOS/Linux) | `pwsh -v` | `PowerShell 7.x` |
| **A browser:** Chrome, Edge or Firefox | Safari is not recommended (see Troubleshooting) | | |

Optional: **Visual Studio 2022 17.13+** if you prefer F5 debugging for the API. Everything below
also works from the terminal.

Make sure Docker Desktop is **running** (whale icon in the tray) before Part 4.

---

## 3. Get the code

The system lives in one "workspace" repository that contains the others as **submodules** (each is
its own repository with its own history).

```bash
git clone --recursive https://github.com/arif-ahmed/rvl-sbqr-workspace.git
cd rvl-sbqr-workspace
```

You should now see these folders (among others):

| Folder | What it is |
|---|---|
| `rvl-secure-bqr-manager/` | the API (.NET) and the database scripts |
| `rvl-sbqr-portal/` | the portals (React). **This is where you will work** |
| `docs/` | plans and guides, including this one |

**Rules to remember from day one**

- Code changes are committed **inside the submodule** (`rvl-sbqr-portal`, `rvl-secure-bqr-manager`),
  never in the workspace root. The root only records which version of each submodule to use.
- Use the branches named in [`docs/plans/portal-iam-uam/status.yaml`](../../plans/portal-iam-uam/status.yaml).
  If a branch does not exist yet, ask your lead which one to use.
- Never commit `.env` files, passwords, or anything in `rvl-sbqr-keys/` or `dev-pki/`.

Check out the branches (example, adjust names to what your lead says):

```bash
cd rvl-secure-bqr-manager && git checkout <manager-branch> && cd ..
cd rvl-sbqr-portal        && git checkout <portal-branch>  && cd ..
```

---

## 4. Start the database

The API stores data in PostgreSQL. A script in the API repo starts it in Docker **and** creates all
the tables (the "schema") for you.

```bash
cd rvl-secure-bqr-manager
pwsh ./scripts/dev-stack-up.ps1 -Services postgres,rabbitmq
```

What this does: starts two containers (`postgres` for data, `rabbitmq` for messages between parts of
the system), then applies every file in `db/migrations/` in order.

**Check:** `docker ps` lists a Postgres container and it says "healthy" or "Up".

> **Use your own local database only.** Never point your API at a shared or remote database. If a
> teammate says "the dev DB is shared", that is a different thing: your laptop uses its own.

If the script complains that the host name `sbqr.postgres` cannot be found, add this line to your
hosts file and run it again (Windows: edit `C:\Windows\System32\drivers\etc\hosts` as Administrator;
macOS/Linux: `/etc/hosts` with `sudo`):

```
127.0.0.1  sbqr.postgres
```

---

## 5. Configure and start the API

### 5.1 Give the API its settings (once)

Secrets are stored per-user by .NET ("user-secrets"), so nothing is committed. The repo has a helper
that fills in the common ones (database connection, trust-store switch, and so on). Run it from the
`rvl-secure-bqr-manager` folder:

```bash
pwsh ./scripts/dev-seed-user-secrets.ps1
```

Then set the few things the helper does not:

```bash
cd src/Host/SBQR.Api

# The helper points key storage at the team's S3 bucket. On your laptop use a local folder instead
# (no AWS account needed). The signing keys you create for test banks stay on your machine.
dotnet user-secrets set "KeyCustody:Vault" "Local"

# Token signing: any long random text, at least 32 characters. Local only.
dotnet user-secrets set "Jwt:SigningKey" "local-dev-only-signing-key-change-me-0123456789"
dotnet user-secrets set "Jwt:Issuer"   "sbqr"
dotnet user-secrets set "Jwt:Audience" "sbqr-api"
```

**[API host support]** Tell the API which web address belongs to the Admin portal and how to read the
bank from the address:

```bash
dotnet user-secrets set "Portal:BaseDomain" "localhost"
dotnet user-secrets set "Portal:AdminSlug"  "admin"
```

That is all: with these two values `admin.localhost` is the Admin portal and `<anything>.localhost`
is that bank's portal. (The API reads the address from the request the portal forwards; no other
setting is needed on your laptop.)

Check what is set: `dotnet user-secrets list` (run in `src/Host/SBQR.Api`). If a `.env` file exists in
the API repo, it overrides user-secrets: remove conflicting lines rather than fighting it.

### 5.2 Create your own admin account (once)

You need one RVL staff account to sign in to the Admin portal. This command creates it, prints a
one-time password, and exits.

```bash
cd ../../..          # back to rvl-secure-bqr-manager
dotnet run --project src/Host/SBQR.Api -- --seed-admin admin --display-name "Local Admin"
```

Copy the `initial_password` printed in the console (it is shown only once). You will use it in Part 7
and the portal will make you change it immediately.

### 5.3 Start the API

```bash
dotnet run --project src/Host/SBQR.Api --launch-profile SBQR.Api
```

Leave this terminal open. **Check:** open <http://localhost:5001/health/ready> in a browser. It should
show a healthy/ready response. (In Visual Studio: open `SBQR.slnx`, set `SBQR.Api` as the startup
project with the `SBQR.Api` profile, press F5.)

---

## 6. Start the portals

Open a **new terminal** (the API keeps running in the other one).

```bash
cd rvl-sbqr-portal
nvm use               # picks Node 24 from .nvmrc
cp .env.example .env  # Windows PowerShell: Copy-Item .env.example .env
npm ci                # installs exact dependency versions (first time takes a few minutes)
npm run dev           # [Portal split] starts BOTH portals
```

`.env` tells the portals where the API is. The default (`SBQR_API_URL=http://localhost:5001`) is
already right, so you normally change nothing. `.env` is git-ignored: never commit it.

**Check:** the terminal prints two "ready" blocks, one for each portal:

```
admin  VITE ready   ➜  Local: http://admin.localhost:5175/
fi     VITE ready   ➜  Local: http://localhost:5176/
```

Now open <http://admin.localhost:5175>. You should see the **RVL staff sign-in** page. If you see
"This site can't be reached", see Troubleshooting. (Do not test with `ping`: on Windows `ping` may fail
for `.localhost` names even though browsers handle them.)

> **What `npm run dev` is doing for you.** It starts two small web servers (ports 5175 and 5176).
> When a page asks for data (`/v1/...`), the server passes the request on to your API at
> `localhost:5001` and keeps the web address the browser used, so the API can tell which bank is asking.
> That is why the portal code never contains an API address or a bank name.

---

## 7. Create your first bank and sign in as its staff

You will do what an RVL operator does when a new bank joins, then act as that bank's administrator.

### 7.1 Sign in to the Admin portal

1. Open <http://admin.localhost:5175>.
2. Username `admin`, password = the `initial_password` from step 5.2.
3. You are sent to **Change password**. Choose a new password (8+ characters) and save. You are signed
   out; sign in again with the new password. *(This is the forced password change every new account
   gets. It is not a bug.)*
4. In the left menu you see **Institutions** and, under **Access**, **Users**.

### 7.2 Onboard a bank called "Dhaka Bank"

**Institutions → Onboard institution.** The wizard has six steps; do them in order:

| Step | What to enter | Why |
|---|---|---|
| **Institution** | Name `Dhaka Bank`, an institution code (e.g. `999991`), contact details, and **Portal address `dhakabank`** | The portal address is the bank's slug: its portal will be `dhakabank.localhost`. Lowercase letters, digits, hyphens. Words like `admin`, `www`, `api` are reserved |
| **Configuration** | Turn on QR generation and/or validation | What the bank may do |
| **Credentials** | Click to issue; the **client secret is shown once**, copy it somewhere safe or just continue (local only) | Machine access for the bank's systems |
| **Signing key** | Choose generate | The key that signs this bank's QR codes (stored in the folder you set in 5.1) |
| **FI admin** | Contact name and email, then **Create FI Master Admin**. A dialog shows a **username and one-time password: copy them** | The bank's first human administrator. Without one the bank cannot be activated |
| **Review** | Check everything is ticked, then **Activate institution** | Makes the bank live |

If a step shows a red banner, read it: it names the problem and the fix.

Repeat with `BRAC Bank`, portal address `bracbank`, if you want two banks (recommended for the checks
below).

### 7.3 Sign in as the bank's administrator

1. Open <http://dhakabank.localhost:5176>. The page is titled for **Dhaka Bank**.
2. Sign in with the FI admin username and one-time password from 7.2. Choose a new password when asked,
   then sign in again.
3. You now see the **Bank portal** for Dhaka Bank, with **Users** under **Access**.
4. **Users → Add user**: create `teller.karim` (name and email). Copy the one-time password from the
   dialog.

You have now run both portals against your own API and database.

---

## 8. Check that it really works (10 minutes)

These confirm the important rules. Use two banks (`dhakabank`, `bracbank`).

| # | Do | You should see |
|---|---|---|
| 1 | In `bracbank.localhost:5176`, as BRAC's admin, also create a user named `teller.karim` | Allowed: usernames only need to be unique **within a bank** |
| 2 | Sign in as `teller.karim` on `dhakabank.localhost:5176` | Dhaka's Karim; forced password change on first sign-in |
| 3 | Try Dhaka's `teller.karim` details on `bracbank.localhost:5176` | **"Invalid credentials"** (same message as a wrong password: it never reveals why) |
| 4 | Try your `admin` account on `dhakabank.localhost:5176` | **"Invalid credentials"**: staff accounts do not work on a bank portal |
| 5 | Try a bank account on `admin.localhost:5175` | **"Invalid credentials"** |
| 6 | Open <http://nobank.localhost:5176> | **"Portal not found"** (no information about which banks exist) |
| 7 | Sign in at `dhakabank.localhost:5176`, then open `bracbank.localhost:5176` in the same browser | **Not signed in**: each address has its own session |
| 8 | As Dhaka's admin, open your own row in Users and try to disable yourself | Button disabled: you cannot disable your own account |
| 9 | In the Admin portal, open Users and try to disable Dhaka's **only** FI Master Admin | A red banner: the bank must keep at least one |
| 10 | In the Admin portal, suspend Dhaka Bank (Institutions → Dhaka Bank) | Its users are suspended and signed out; sign-in at `dhakabank.localhost` is refused |

If all ten behave as shown, your environment is correct.

---

## 9. Troubleshooting

| What you see | Why / what to do |
|---|---|
| `admin.localhost` "can't be reached" | Use Chrome, Edge or Firefox. Check `npm run dev` is running and shows the port. On an old OS, add `127.0.0.1 admin.localhost` to your hosts file |
| Terminal: "Port 5175 is already in use" | Another program uses it. Stop it, or set `PORT_ADMIN` / `PORT_FI` in `rvl-sbqr-portal/.env`. (5173 and 5174 belong to other apps in the workspace) |
| Page loads but sign-in says "Could not sign in" / network error | The API is not running or `SBQR_API_URL` in `.env` is wrong. Open <http://localhost:5001/health/ready> |
| Vite says "Blocked request. This host is not allowed" | You opened an address that does not end in `.localhost`. Use `*.localhost` |
| Every bank address shows "Portal not found" | **[API host support]** Check `Portal:BaseDomain` is `localhost` (`dotnet user-secrets list`), restart the API, and make sure the bank was created with exactly that portal address (slug) |
| "Portal not found" for a bank you just made | The bank's status is not Pending/Active, or the portal address was typed differently. Check it on the bank's page in the Admin portal |
| API will not start; error mentions a connection | Docker/Postgres is not running. Start Docker Desktop, re-run Part 4, check `docker ps` |
| Script says it cannot find host `sbqr.postgres` | Add `127.0.0.1  sbqr.postgres` to your hosts file (see Part 4) |
| Every protected request returns 401 | `Jwt:SigningKey` is missing or shorter than 32 characters. Set it (5.1) and restart the API |
| Signing-key step fails or asks for AWS credentials | `KeyCustody:Vault` must be `Local` (the seed script sets it to `S3`). Fix with the command in 5.1 and restart the API |
| 429 "Too many sign-in attempts" | The API allows 10 sign-in calls per minute per IP. Wait a minute |
| Sign-in works but a reload signs you out | You mixed addresses (`localhost` vs `admin.localhost`), or used Safari. The session cookie belongs to one exact address. Use one address consistently and a supported browser (Safari: set `Auth:User:RefreshCookieSecure` to `false` in user-secrets for local only) |
| "403 password_change_required" in the browser console | The account still has its one-time password. Open `/change-password` and set a new one |
| Lost the admin password | Run 5.2 again with a different username (e.g. `admin2`). Do not edit the database by hand: the audit log is tamper-evident |
| Styles look broken (unstyled boxes) | Stop `npm run dev`, run `npm ci`, start it again. If you are editing shared components, the app stylesheet must include the shared source folder (see `packages/shared`) |
| `npm run dev` starts only one portal | You are on a branch from before **[Portal split]**; ask your lead for the right branch |

Still stuck? Copy the exact error text (never passwords) and ask in the team channel with: what you
ran, what you expected, what you saw.

---

## 10. Your daily routine

**Start (about 2 minutes)**
1. Start Docker Desktop.
2. `rvl-secure-bqr-manager`: `pwsh ./scripts/dev-stack-up.ps1 -Services postgres,rabbitmq`, then `dotnet run --project src/Host/SBQR.Api --launch-profile SBQR.Api`.
3. `rvl-sbqr-portal`: `npm run dev`.
4. Open `admin.localhost:5175` and `<bank>.localhost:5176`.

**Stop:** `Ctrl+C` in each terminal; `pwsh ./scripts/dev-stack-down.ps1` stops the containers. Data stays in the Docker volume.

**Start from scratch** (wipe local data): stop the API, then in `rvl-secure-bqr-manager` run
`pwsh ./scripts/dev-stack-down.ps1 -Mode Reset` (it asks before deleting the database volume), then
repeat Part 4, 5.2 and 7. Also clear site data in the browser for `admin.localhost` and each bank address.

**Before you open a pull request** (from `rvl-sbqr-portal`):

```bash
npm run lint
npm test
npm run build
```

All three must pass. Tests do not need the API: they use a built-in fake (`fake-backend.ts`). Commit
inside the submodule; use the workspace's `/rvl-commit` flow when your lead tells you to.

**Rules the portal code follows (so reviews go smoothly)**
- Never import code from one portal into the other. Shared code goes in `packages/shared`.
- Colours and components come from the design system (`DESIGN.md`). No raw hex colours.
- Calls go to `/v1/...` on the same address. Never write an API URL or a bank name in code.
- Never store tokens or passwords in the browser or in logs. One-time passwords are shown once and kept
  only in memory.
- Hiding a button is a courtesy; the API is what actually enforces permissions.

---

## 11. Where things live (for when you start reading code)

```
rvl-sbqr-portal/
  apps/admin/          the Admin portal app (login page + staff screens)
  apps/fi/             the Bank portal app (login page + bank screens)
  packages/shared/     code both portals use: components, API client, sign-in/session, users screens
  .env                 your local settings (not committed)
  DESIGN.md            colours, components, rules
  AGENTS.md            conventions for people and AI assistants
rvl-secure-bqr-manager/
  src/Host/SBQR.Api/                         starts the API
  src/Modules/IdentityAccess/                users, roles, sign-in
  src/Modules/Tenancy/                       banks, onboarding
  db/migrations/                             database scripts
docs/plans/portal-iam-uam/                   the plan this work follows
docs/mocks/iam-portal/index.html              clickable mock-up: open it to see the intended screens
```

**Suggested first reading (1 hour):** open the mock-up in a browser and click through; then read
`docs/plans/portal-iam-uam/plan.md`; then `DESIGN.md`.

---

## 12. Glossary

| Word | Meaning |
|---|---|
| **Tenant / FI / institution** | One bank or wallet using the platform |
| **Slug / portal address** | A bank's short name in its web address (`dhakabank`) |
| **Admin portal** | RVL staff website (`admin.…`) |
| **Bank (FI) portal** | A bank's own website (`<slug>.…`) |
| **FI Master Admin** | The bank's own administrator; can create users for that bank only |
| **FI User** | Ordinary bank staff account |
| **Platform / staff roles** | RVL accounts: Master Admin, Ops, Auditor, Support, Finance |
| **One-time password** | Temporary password shown once; must be changed at first sign-in |
| **Submodule** | A repository nested inside the workspace repository |
| **User-secrets** | Per-developer settings storage for .NET that is never committed |
| **Vite** | The tool that serves the portals while you develop |
| **Proxy** | The dev server passing `/v1` requests to your API |
| **Migration** | A database script that creates or changes tables |
