---
name: rvl-create-branch
description: Convention-safe local branch creation for RVL SBQR submodule work. Use when the user invokes /rvl-create-branch or asks to "create a branch", "new branch", "start work on X", "branch name for", or "cut a branch" in any rvl-sbqr submodule. Runs a short questionnaire (kind of work, optional GitHub/Jira ticket, short description), derives the type prefix and base branch per the target repo's convention, proposes exactly three regex-validated candidate names with one recommendation, and — only after explicit approval — creates the local branch. Never pushes, never bypasses governance hooks.
---

# rvl-create-branch

Convention-safe branch creator for the RVL SBQR workspace. You ask three short questions, derive a **type prefix** and **base branch** from the target repo's convention, propose **three validated candidate names** (one recommended), and create the **local** branch only after explicit approval.

**Invocation:** `/rvl-create-branch [submodule]` — run from the workspace root. The submodule may also be inferred from the current working directory or conversation context; ask only if ambiguous.

## Ground rules (non-negotiable)

1. **Read-only until approval.** No branch is created until the user explicitly approves a candidate in Phase 4.
2. **Submodule only.** All git operations run via `git -C <submodule-path>` (pwsh on Windows — never `cd`). Never create branches in the root orchestrator repo.
3. **Never invent a ticket ref.** If the user gives none, candidates omit it entirely.
4. **Never propose a non-conforming name.** Every candidate must pass the governance regex (Phase 3) before it is shown. No exceptions, including "the user asked for it" — offer the compliant nearest instead.
5. **Local branch only.** Never push, never set upstream — pushing is the user's (or /rvl-commit's) job.
6. **Hook refusal is final.** If a governance hook (local `reference-transaction`/`pre-push`, or a CI gate) refuses the creation, do NOT bypass with `git -c core.hooksPath=`. Fix the name and retry.
7. **Never print secrets.** Descriptions are turned into slugs; if a description contains anything sensitive, stop and ask.

## Phase 0 — Resolve the target submodule

Candidates (one of):

- `rvl-sbqr-fi-gateway`, `rvl-secure-bqr-manager`, `rvl-bb-trust-store`, `rvl-sbqr-app-emulator`, `rvl-sbqr-mocks`

Resolution order: explicit argument → current working directory inside a submodule → the submodule the conversation is about → **ask** (list all five).

Then read the repo's convention context:

- `rvl-secure-bqr-manager`: full model — `docs/environments.md` (branch naming standard + promotion/hotfix matrix + governance gates). This is the only repo where `staging` exists and where hotfix bases vary.
- All others: track `main` (per workspace `.gitmodules`); base = `main` unless the user overrides.

## Phase 1 — Questionnaire (one round, question tool)

1. **Kind of work** — options (exactly these six):
   - New Feature
   - Bug — found in Development
   - Bug — found in Staging / UAT
   - Bug — found in Production (urgent)
   - Chore (deps, CI, tooling, build)
   - Docs (documentation only)
2. **Ticket reference (optional)** — GitHub issue number (`142`, `#142`) or Jira key (`SBQR-123`). Free text; "none" is a valid answer.
3. **Short description** — 1–6 words, free text.
4. **Base branch override (optional)** — leave blank to use the derived default.

## Phase 2 — Derive type and base

`rvl-secure-bqr-manager` (from `docs/environments.md`):

| Kind | Type | Default base |
|---|---|---|
| New Feature | `feature/` | `develop` |
| Bug — Development | `fix/` | `develop` |
| Bug — Staging / UAT | `hotfix/` | `staging` |
| Bug — Production | `hotfix/` | `main` |
| Chore | `chore/` | `develop` |
| Docs | `docs/` | `develop` |

Other submodules: same six type prefixes; default base `main` (or the submodule's tracked branch), confirm with the user in Phase 4 when unsure.

If the user supplied a base override that contradicts the matrix (e.g. hotfix-prod based on `develop`), show a one-line warning with the doctrine reason and confirm they still want it.

## Phase 3 — Slug and candidates

**Slug rules:** lowercase kebab-case; at most 5 words; drop filler words ("the", "for", "a", "add support for" → the noun-verb core); no personal names; no uppercase ever.

**Ticket normalization** (prefix, not suffix):

- GitHub issue `142` → slug starts `142-` (e.g. `feature/142-dart-sdk-export`)
- Jira `SBQR-123` → slug starts `sbqr-123-` (**lowercased** — uppercase fails governance: `feature/sbqr-123-mtls`)

**Validate every candidate** against the governance regex before proposing:

```
^(feature|fix|hotfix|chore|docs)/[a-z0-9]+(-[a-z0-9]+)*$
```

Propose **exactly three** candidates via the question tool, recommended first:

1. Recommended — ticket-prefixed (if given) + the tightest accurate slug
2. Alternate — same slug without the ticket prefix (or a second phrasing if no ticket)
3. Alternate — shorter or differently scoped slug

Each option shows the full branch name and the base branch it will be cut from. One line of rationale under the recommended option.

## Phase 4 — Approval and creation

On approval (and only then):

1. Ensure the base exists locally: if `git -C <repo> rev-parse --verify <base>` fails, run `git -C <repo> fetch origin <base>` first (ask if this is unexpected).
2. Create: `git -C <repo> switch -c <type>/<slug> <base>`
3. Success line: **⚔️ "You have my sword."** — report the branch, its base, and the repo.

If creation is refused by a governance hook → HARD STOP: report the refusal, propose compliant alternatives, re-ask. Never bypass.

## Phase 5 — Post-create hints (report, don't act)

Print for the user:

- Push command: `git -C <repo> push -u origin <branch>`
- PR target from the direction matrix (`rvl-secure-bqr-manager`: `feature|fix|chore|docs → develop`, `hotfix → staging` (UAT) / `main` (prod); others: `main`)
- Governance nuance: a **GitHub-numbered** slug (`feature/142-…`) requires the PR description to reference `#142`; lowercase **Jira-prefixed** slugs (`feature/sbqr-123-…`) do not trigger that gate (/rvl-commit parses the ref from the branch name instead)
