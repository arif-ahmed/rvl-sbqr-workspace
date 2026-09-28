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
