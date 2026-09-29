---
name: rvl-commit
description: Guarded commit for RVL SBQR submodule work. Use when the user invokes /rvl-commit or asks to commit/push uncommitted work in any rvl-sbqr submodule after implementing a plan. Reviews the working tree against a frozen plan doc, hard-blocks on secrets, unimplemented plan items, or a missing/mismatched HTML audit report, drafts a single gitmoji commit with the Jira ref parsed from the branch name, and commits + pushes only after explicit approval. After push it dispatches the rvl-reviewer agent for an independent PASS/FAIL verdict (plan, audit report, VAPT regression, coding guidelines) and creates the PR on PASS after a separate approval.
---

# rvl-commit

Guarded commit gate for the RVL SBQR workspace. You review uncommitted changes in a **submodule** against a **frozen plan doc**, scan for secrets, reconcile the **HTML audit report**, and only then draft one commit — which is executed **solely after explicit user approval**.

**Invocation:** `/rvl-commit <plan-doc-path>` — run from the workspace root. The plan doc path is required; if omitted, ask for it. Never guess or invent a plan.

## Ground rules (non-negotiable)

1. **Read-only until approval.** No `git add`, `git commit`, `git push` before the user explicitly approves the final draft in Phase 5.
2. **Submodule only.** All git operations run via `git -C <submodule-path>` (pwsh on Windows — never `cd`). The root orchestrator repo is never committed to, ever.
3. **Scope-locked review.** Judge ONLY whether the plan is implemented. No general code-quality review, no style opinions, no suggestions beyond the plan doc.
4. **Hard stops are final.** The user cannot talk you past a hard stop. They fix the issue and re-run.
5. **Never force-push, never amend, never rewrite history, never rebase.**
6. **Never print secret values.** When reporting a finding, show file:line and at most the first 20 characters of the match.
7. Single commit covering all uncommitted changes in the target submodule (staged + unstaged + untracked).

## Phase 0 — Resolve inputs

1. **Read the plan doc fully.** Extract:
   - Target submodule (a directory name from the workspace: `rvl-sbqr-api`, `rvl-sbqr-admin-portal`, `rvl-sbqr-fi-gateway`, `rvl-secure-bqr-manager`, `rvl-bb-trust-store`, `rvl-sbqr-app-emulator`, `rvl-sbqr-mocks`). If not named, infer from file paths the plan mentions; if still ambiguous, ask.
   - Every step / task / requirement: numbered steps (A1–A8, B1–B4 style), checklists, "Files you touch" tables, and each step's Check/acceptance criterion.
   - Expected outcomes the plan says must hold (acceptance criteria, sign-off conditions).
   - Any reference to an audit / sign-off report.
2. **Detect the dirty submodule.** Run `git -C <dir> status --porcelain` across all seven submodules.
   - Exactly one dirty → that's the target (must be consistent with the plan's module; if not, ask).
   - Multiple dirty → list them and ask the user to pick.
   - None dirty → stop: nothing to commit.
3. **Locate the HTML audit report.** Required on every run.
   - If the plan references a report → resolve that path (workspace `docs/` or submodule `docs/`).
   - Else search for HTML reports sharing keywords with the plan (e.g. "signoff", "sign-off", "test-report", "e2e", feature name): workspace root `docs/**/*.html`, and inside the submodule `docs/**/*.html`, `test-results/*.html`, plus any other `*.html` report files; newest modified wins.
   - Several plausible candidates → list them and ask. **None found → HARD STOP** (an audit report is required input).

## Phase 1 — Plan compliance (scope-locked)

Gather evidence:

- `git -C <repo> diff HEAD` — all staged + unstaged changes vs HEAD.
- `git -C <repo> status --porcelain` — untracked files; read their contents too.
- If a plan item is not visible in the diff, check whether it was satisfied by **already-committed code** (search the submodule before marking it missing). Compliance is judged against the current working-tree state.

Build the **compliance matrix**: every plan item → status (`implemented` / `partial` / `missing`) → evidence (file:line or diff hunk reference).

- 100% `implemented` → verdict line: **"The Fellowship is complete."** — continue.
- Any `partial`/`missing` → verdict line: **"The Fellowship is broken."** — HARD STOP. List the gaps and stop. No commit draft.

Files in the diff **not covered by any plan item**: list neutrally under "Not covered by the plan (informational)". They are not reviewed, do not block, and are included in the single commit — but the commit body only describes plan-relevant work.

## Phase 2 — Secrets scan (never overridable)

Scan the **entire pending change set**: full `git diff HEAD` output plus contents of all untracked files. Look for:

- Private key blocks: `-----BEGIN RSA PRIVATE KEY-----`, `BEGIN PRIVATE KEY`, `BEGIN EC PRIVATE KEY`, `BEGIN OPENSSH PRIVATE KEY`, `BEGIN PGP PRIVATE KEY BLOCK`.
- Key/certificate files entering the tree: new files with extensions `.pem .ppk .pfx .p12 .jks .key .snk`.
- Cloud/token identifiers: `AKIA[0-9A-Z]{16}` (AWS), `ghp_/gho_/ghs_/ghr_…` (GitHub), `glpat-…` (GitLab), Slack `xox[baprs]-…`.
- JWTs: `eyJ…` three-part token literals.
- Connection strings with credentials: `User ID=…;Password=…`, `mongodb://user:pass@`, `postgres://user:pass@`, `redis://:password@`.
- Hardcoded secret assignments: `(apiKey|api_key|apikey|secret|secretKey|client_secret|password|passwd)\s*[:=]\s*["'][^"']{8,}["']`.
- Repo-specific leakage: any path under `dev-pki/` or `rvl-sbqr-keys/` referenced or copied in the diff; any certificate/key material from local dev PKI.

**Classification:**

- **CRITICAL** (real key material, high-entropy tokens, credentials, PKI leakage) → **🧙 "You shall not pass!"** — HARD STOP. List findings (file:line, pattern, masked snippet). Instruct: remove/rotate, then re-run. Never overridable.
- **Ignorable** — values that are obviously fake test fixtures (`"test"`, `"dummy"`, `"changeme"`, `"REPLACE_ME"`, `localhost` samples) → listed as warnings, do not block.

## Phase 3 — Audit report reconciliation

1. Read the HTML report (raw HTML is fine — extract text): TL;DR verdict badges, PASS/FAIL markers, result tables, sign-off metadata.
2. Extract the plan's expected outcomes (from Phase 0).
3. Match every expectation to evidence in the report.
4. Also check the report for **open FAIL / REJECT / critical items**:
   - Failure maps to a plan acceptance criterion → **HARD STOP** (report contradicts "plan fully implemented").
   - Open items the plan explicitly defers or declares out of scope → informational note only.

No report, or the report doesn't cover the plan's scope → HARD STOP.

## Phase 4 — Commit draft

1. **Jira ref.** `git -C <repo> branch --show-current`, parse `([A-Z][A-Z0-9]+-\d+)` (e.g. `feature/SBQR-123-mtls` → `SBQR-123`). No match → ask the user once; if unresolved → HARD STOP.
2. **Gitmoji** — first match wins, judged from plan type + diff:
   - 🔒 `:lock:` security work (mTLS, key custody, VAPT remediation, hardening)
   - 🐛 `:bug:` bugfix
   - ✨ `:sparkles:` new feature/capability (default)
   - ♻️ `:recycle:` refactor
   - 📝 `:memo:` docs-only
   - 👷 `:construction_worker:` CI/build/tooling
3. **Message** — title ≤ 72 chars total, imperative; body = one short bullet per plan step group; footers:

```
:sparkles: (SBQR-123) add correlation-id middleware

- A1: dual run profiles (readable text / JSON)
- A3: CorrelationIdMiddleware with fail-closed parsing
- B2: audit-line assertions in QrFlow integration tests

Refs: SBQR-123
Audit: banglaqr-qr-verification-signoff-report.html
```

## Phase 5 — Approval gate

Present the final package:

- Target repo, branch, upstream (if any)
- Compliance matrix summary — "The Fellowship is complete."
- Secrets scan: clean
- Audit reconciliation: matched
- Files to be committed (list + count), including the informational out-of-scope list
- The full commit message draft
- 🚪 **"Speak, friend, and enter."** — ask for explicit approval.

On approval (and only then):

1. `git -C <repo> add -A`
2. Write the commit message to a temp file (`$env:TEMP\rvl-commit-msg.txt`) and commit with `git -C <repo> commit -F $env:TEMP\rvl-commit-msg.txt` — avoids quoting issues on pwsh.
3. Push:
   - Upstream exists (`git -C <repo> rev-parse --abbrev-ref @{u}`) → show **"One does not simply push to `<branch>`"** and confirm the target, then `git -C <repo> push`.
   - No upstream → propose `git -C <repo> push -u origin HEAD` and get confirmation first.
4. Success line: **⛵ "Sailed West to the Grey Havens."** — report repo, branch, short SHA, and remote URL.

If the user requests edits to the draft → revise and re-present the gate. Any requested edit that would violate a hard stop (e.g. "just commit it anyway") → refuse, explain, offer to fix the blocker instead.

## Phase 6 — Dispatch the reviewer (maker-checker handoff)

Immediately after the successful push, dispatch the `rvl-reviewer` **agent** (subagent/Task tool — it is read-only) with a structured handoff containing:

- Target repo path, pushed commit SHA, branch name
- Plan doc path
- HTML audit report path (from Phase 0)
- VAPT baselines (workspace-root relative): `docs/misc/security/vapt-report-2026-09-21.md`, `docs/misc/security/vapt-oauth-mobile-verdict-2026-09-22.md`

The reviewer is the independent checker — it has not seen this conversation's reasoning. Do not answer for it, do not soften its findings, do not skip it.

If the host CLI cannot dispatch subagents: say so, then run the same review inline in this conversation following the reviewer agent's dimensions, and note that independence is degraded.

## Phase 7 — Verdict and PR

Present the reviewer's verdict block verbatim.

- **VERDICT: FAIL** → stop. No PR. List what failed, by dimension. Guidance: fix the findings, then re-run `/rvl-commit` fresh.
- **VERDICT: PASS** (guidelines `WARN` allowed) →
  1. Show the reviewer's `PR_DRAFT` (title + body).
  2. Confirm the base branch explicitly (suggest the repo's default, e.g. `main`). Never guess silently.
  3. On approval: resolve the remote (`git -C <repo> remote get-url origin`). If `gh` is available and the remote is GitHub, write the body to `$env:TEMP\rvl-commit-pr.md` and run from the workspace root:
     `gh pr create --repo <owner/repo> --head <branch> --base <base> --title "…" --body-file $env:TEMP\rvl-commit-pr.md`
     then report the returned PR URL.
  4. If `gh` is missing or the remote isn't GitHub → print the drafted title + body for manual use instead.
  5. PR creation requires its own explicit approval, separate from the commit approval.
