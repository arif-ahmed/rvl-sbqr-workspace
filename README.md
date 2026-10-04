# rvl-sbqr-workspace

Central **orchestrator repo** for the RVL SBQR system. All application code lives in sibling git submodules — each an independent repo with its own history and remote. This root repo contains only workspace docs, the gitignore, and pinned submodule SHAs. It never holds application code.

## Layout

| Submodule / dir | What it is | Tracked branch |
|---|---|---|
| `rvl-sbqr-admin-portal/` | Admin portal UI | `main` |
| `rvl-sbqr-portal/` | Web portal for RVL staff (Admin, Finance) and financial institutions | `main` |
| `rvl-sbqr-fi-gateway/` | Financial-institution gateway service | `main` |
| `rvl-secure-bqr-manager/` | Secure BQR manager | `feature/mtls-server` |
| `rvl-bb-trust-store/` | Bangladesh Bank trust store service | `main` |
| `rvl-sbqr-app-emulator/` | Mobile app emulator | `main` |
| `rvl-sbqr-mocks/` | Mock services | `main` |
| `docs/` | Workspace docs (tracked in this repo) | — |
| `docs/misc/` | Consolidated legacy docs: requirements, design, security, docker | — |

Local-only (git-ignored, never commit):

- `dev-pki/` — local dev PKI scripts/artifacts
- `rvl-sbqr-keys/` — **private key material** (`*.pem`, `*.ppk`)

## Bootstrap

```bash
git clone --recursive https://github.com/arif-ahmed/rvl-sbqr-workspace.git
# or, for an existing clone:
git submodule update --init --recursive
```

## Daily workflow

- **Code changes:** make them inside the relevant submodule and commit in that submodule's own repo. The root repo is unaffected by sibling activity.
- **Sync all siblings to their latest tracked branch:**

  ```bash
  git submodule update --remote --recursive
  ```

- **Pin a new SHA in root (opt-in):** after advancing/committing in a submodule, `git add <submodule>` in root and commit the pointer bump. Siblings never touch root history unless you do this deliberately.

## Agent notes

See `AGENTS.md` for search rules (ignored dirs need explicit paths) and workspace conventions for AI agents.
