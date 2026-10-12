# Browser testing with agent-browser

How to prepare for and run browser checks on `rvl-sbqr-portal` with the `agent-browser` skill, where the evidence goes, and worked examples.

Browser checks are the last layer. They **do not replace** `npm run lint`, `npm test` and `npm run build`; those run first and must pass.

## 1. Preparation

### One-time machine setup

```bash
npm i -g agent-browser
agent-browser install
agent-browser --version
```

### Per-session setup

1. **Env.** Copy `.env.example` to `.env` (never commit `.env`). `SBQR_API_URL` points at the backend (`rvl-secure-bqr-manager`, default `http://localhost:5001`).
2. **Backend.** Start the API (`dotnet run` in `rvl-secure-bqr-manager`). Without it only the login screen renders and sign-in returns `502`.
3. **Portal.**
   ```bash
   npm ci
   npm run dev          # http://localhost:5175, proxies /v1 and /openapi to SBQR_API_URL
   ```
4. **Test accounts.** One staff (Admin or Finance) and one FI user. Credentials live **outside the repo** (`agent-browser auth` vault or a state file kept elsewhere). Never put real credentials in commands, logs, screenshots or committed files.
5. **Own browser session.** Never use the shared default session.
   ```bash
   export AGENT_BROWSER_SESSION="$(agent-browser session id --scope worktree --prefix task)"
   ```
6. **Load the real workflow** (the skill file in `.claude/skills/agent-browser` is only a stub):
   ```bash
   agent-browser skills get core       # snapshot/ref workflow, commands, troubleshooting
   agent-browser skills get dogfood    # exploratory QA / bug hunts
   ```

### Pre-flight checklist

- [ ] `npm run lint`, `npm test`, `npm run build` pass
- [ ] Backend reachable (or you have noted which checks are limited to the API boundary)
- [ ] Dev server answers on `:5175`
- [ ] Test accounts available, credentials not in the repo
- [ ] Named agent-browser session set
- [ ] Reference open: `design/portal-prototype.html` (match behaviour and copy)

## 2. The core loop

```bash
agent-browser open http://localhost:5175
agent-browser snapshot -i          # interactive elements with refs: @e1, @e2 ...
agent-browser fill @e4 "value"
agent-browser click @e5
agent-browser snapshot -i          # re-snapshot after every page change
agent-browser get url
agent-browser get text @e3
agent-browser screenshot qa-screenshots/<feature>/<date>/01-<step>.png
agent-browser close                # when finished
```

Refs are only valid for the snapshot they came from; re-snapshot after navigation. `snapshot -i` hides plain text such as validation messages. Use `snapshot` (full tree) or `get text` to read them.

## 3. Verifying output

Verify from the page, not from a screenshot alone.

| What to verify | How |
|---|---|
| Route / redirect | `agent-browser get url` |
| Message / error text | `agent-browser snapshot \| grep -i "<text>"` or `get text @eN` |
| Field state | `get value @eN`, `get attr @eN type` |
| Visual layout | `screenshot` (evidence for a human, not the assertion) |
| Request outcome | Banner text, e.g. `Sign-in failed (502).` |

Cover for each flow: happy path, validation errors, failure states (API down, 4xx), and role differences (staff vs FI).

## 4. Screenshots: where and how

- **Location:** `qa-screenshots/<feature>/<YYYY-MM-DD>/` in the repo. The folder is git-ignored; copy selected images into a PR description or the `rvl-commit` audit report only when needed.
- **Naming:** `NN-<step>-<outcome>.png`, numbered in flow order, e.g. `01-login-empty-submit.png`, `03-login-api-down-502.png`.
- **When:** one per checked state: initial, validation error, failure, success, each role.
- **Never capture** tokens, passwords, provisioned secrets (shown once), keys, or real institution data.
- The report lists each saved path next to its pass/fail result.

## 5. Worked example: login flow

Verified against the live dev server with the API down (validation, redirect and failure paths). Success sign-in needs the backend.

```bash
export AGENT_BROWSER_SESSION="$(agent-browser session id --scope worktree --prefix login)"
SHOTS=qa-screenshots/login/$(date +%F); mkdir -p "$SHOTS"

# 1. Signed-out visit to a protected route redirects to /login
agent-browser open http://localhost:5175/
agent-browser get url                                  # expect http://localhost:5175/login
agent-browser snapshot -i                              # Username, Password, Show password, Sign in
agent-browser screenshot "$SHOTS/01-login-initial.png"

# 2. Empty submit shows both validation errors
agent-browser find role button click --name "Sign in"
agent-browser snapshot | grep -iE "Enter your (username|password)"
agent-browser screenshot "$SHOTS/02-login-empty-submit.png"

# 3. Show/hide password toggles the input type
agent-browser fill @e6 "dummy-pass"
agent-browser click @e7
agent-browser get attr @e6 type                        # expect: text

# 4. Failed sign-in surfaces an error and stays on /login
agent-browser fill @e4 "test.user"
agent-browser click @e5
agent-browser wait 1500
agent-browser get url                                  # expect /login
agent-browser snapshot | grep -i "Sign-in failed"      # API down: "Sign-in failed (502)."
agent-browser screenshot "$SHOTS/03-login-failed.png"

agent-browser close
```

Results from the run:

| Check | Result |
|---|---|
| Signed-out `/` redirects to `/login` | Pass |
| Empty submit shows "Enter your username." and "Enter your password." | Pass |
| Show password changes input type to `text` | Pass |
| Failed sign-in keeps `/login`, shows "Sign-in failed (502)." (API down) | Pass |

### Still to cover once the backend is up

- Successful staff sign-in lands on the staff console; FI user lands on the FI surface (an FI user must not load staff screens).
- `mustChangePassword` accounts are sent to the change-password screen.
- `from` return path follows in-app paths only (`/x`), never `//evil.com` or an absolute URL.
- Reload restores the session through the refresh cookie (access token is in memory only).
- Session expiry shows the notice banner; logout clears the session.

## 6. Other example: staff screen with a role check

```bash
agent-browser open http://localhost:5175/login
agent-browser snapshot -i
agent-browser auth login <profile>        # credentials come from the vault, not the command line
agent-browser open http://localhost:5175/
agent-browser snapshot -i -u              # nav links with hrefs
agent-browser get url
agent-browser screenshot qa-screenshots/staff-nav/$(date +%F)/01-landing.png
```

Remember the UI role check is a convenience only. If a change touches permissions, also confirm the API rejects the call for an out-of-scope user.

## 7. Reporting

State what was checked, the result, and what was **not** checked (e.g. "login verified up to the API boundary; success sign-in unverified, backend down"). On failure, include the output; never claim done on an unrun check.

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| Sign-in shows `502` | Backend not running / `SBQR_API_URL` wrong. Start the API, restart `npm run dev` after editing `.env`. |
| Ref not found | Page changed; take a new `snapshot -i`. |
| Validation text missing from snapshot | Use `snapshot` (not `-i`) or `get text`. |
| Landed in someone else's page | You used the default session; set `AGENT_BROWSER_SESSION`. |
| Stale daemon / odd state | `agent-browser close` (or `close --all`) and reopen. |

## 9. Related

- `AGENTS.md`: portal rules and the browser testing section
- `design/portal-prototype.html`: expected behaviour and copy
- `/rvl-commit <plan-doc>` and the `rvl-reviewer` agent: commit gate and independent verdict
- Live dashboard while a session runs: http://localhost:4848
