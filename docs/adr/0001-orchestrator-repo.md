# ADR-0001: Workspace repo is an orchestrator, not a codebase

- Status: accepted
- Date: 2026-10-05

## Context

Application code lives in sibling submodule repos. Plans were scattered
across `.zcode/plans/`, `docs/features/`, and `reports/`, with no index
mapping plans to the submodules and branches that implement them.

## Decision

`rvl-sbqr-workspace` is the **orchestrator repo**: it watches submodule
state and advises on plans executed inside submodules. It holds:

- submodule pins (`.gitmodules` + `repos.yaml`),
- frozen plan contracts (`docs/plans/`),
- cross-submodule contracts (`contracts/`),
- compliance inputs (VAPT baselines, `docs/JIRA-FACTS.md`),
- review gates (`.claude/skills/rvl-commit`, `rvl-reviewer` agent).

It never holds application code.

## Consequences

- New plans go through `docs/plans/<feature>/` (see `docs/plans/README.md`).
- Root CI reports drift; it does not build or test products.
