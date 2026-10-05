# ADR-0002: Submodule strategy — pin by SHA, track by branch

- Status: accepted
- Date: 2026-10-05

## Context

Seven product repos evolve independently. The workspace must reproduce any
reviewed state exactly, while still knowing which branch each repo follows.

## Decision

- Each product repo is a git submodule pinned by SHA in root (pointer bumps
  are deliberate, reviewed commits — never drive-by).
- `repos.yaml` records the human intent beside the pin: url, tracked branch,
  purpose. `.gitmodules` carries the mechanics.
- Off-track checkouts (a worktree on a feature branch while tracking `main`)
  are allowed but must be visible: noted in `repos.yaml` and surfaced by the
  scheduled drift report — never silent.

## Consequences

- `git submodule update --remote` is a conscious sync action, followed by a
  pin commit if the new SHAs are accepted.
- The drift workflow fails (or warns) when `repos.yaml`, `.gitmodules`, and
  the recorded gitlinks disagree.
