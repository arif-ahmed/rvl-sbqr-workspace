# AGENTS.md

## Workspace layout

This repo is the **orchestrator** for the RVL SBQR system. All application code lives in sibling git **submodules** — each is an independent repo with its own history. The root repo contains only workspace docs, gitignore, and submodule pointers.

| Directory | What it is |
|---|---|
| `rvl-sbqr-api/` | .NET backend API (modules: Verification, KeyCustody, Tenancy, InstitutionTrust) |
| `rvl-sbqr-admin-portal/` | Admin portal UI |
| `rvl-sbqr-fi-gateway/` | Financial-institution gateway service |
| `rvl-secure-bqr-manager/` | Secure BQR manager (tracked branch: `feature/mtls-server`) |
| `rvl-bb-trust-store/` | Bangladesh Bank trust store service |
| `rvl-sbqr-app-emulator/` | Mobile app emulator |
| `rvl-sbqr-mocks/` | Mock services |
| `docs/` | Workspace docs; `docs/misc/` = consolidated legacy docs (requirements, design, security) |

## Git rules

- Code changes belong **inside the relevant submodule** — commit there, in that submodule's repo. The root repo never holds application code.
- The root repo only records **pinned SHAs**. Sibling activity never touches root history unless you deliberately commit a pointer bump (`git add <submodule>` in root after advancing the submodule).
- All other submodules track `main`; `rvl-secure-bqr-manager` tracks `feature/mtls-server`.

## Search rules

- Repo-wide search (grep/glob from root) covers all submodules and `docs/` — each submodule's own `.gitignore` (node_modules, bin, obj…) still applies.
- `/dev-pki/` and `/rvl-sbqr-keys/` are git-ignored local-only dirs, so ripgrep **skips them by default**. When a task needs them, pass the directory explicitly to the search tool (e.g. grep with `path` set).
- `/rvl-sbqr-keys/` holds private key material: treat files as opaque, never print or copy their contents.

## Commands

Bootstrap and sibling-sync commands live in `README.md` (source of truth).

## rvl-commit + rvl-reviewer (maker-checker)

`/rvl-commit <plan-doc-path>` — guarded commit gate for submodule work (canonical: `.claude/skills/rvl-commit/SKILL.md`; opencode mirror: `.opencode/skill/rvl-commit/`). It reviews uncommitted changes in the auto-detected dirty submodule against a frozen plan doc, hard-blocks on secrets, any unimplemented plan item, or a missing/mismatched HTML audit report, then drafts a single gitmoji commit with the Jira ref parsed from the branch name — executed only after explicit approval. After push it dispatches the `rvl-reviewer` agent for an independent PASS/FAIL verdict (plan compliance, HTML audit report, VAPT regression vs `docs/misc/security/vapt-*.md`, coding guidelines); on PASS it creates the PR after a separate approval. It never commits to the root repo and never force-pushes. Prefer it over manual commits when finishing plan work.

`rvl-reviewer` (canonical: `.claude/agents/rvl-reviewer.md`; opencode: `.opencode/agent/rvl-reviewer.md`) is the read-only checker agent — it never writes files and never mutates git state, and returns a structured `VERDICT: PASS|FAIL` block with a PR draft on PASS.

## Canonical vs mirror

If you edit the rvl-commit skill, edit `.claude/skills/rvl-commit/SKILL.md` and re-copy to `.opencode/skill/rvl-commit/SKILL.md` to keep them identical. The rvl-reviewer agent files share the same body but differ in frontmatter (per-tool agent schemas): edit `.claude/agents/rvl-reviewer.md` as canonical and mirror body changes into `.opencode/agent/rvl-reviewer.md`, preserving its opencode frontmatter (`mode`, `permission`).
