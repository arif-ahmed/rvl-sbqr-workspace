# Plans index — the workspace watch surface

Each row is one unit of reviewable work. The frozen plan doc is the
contract `rvl-commit`/`rvl-reviewer` check against; `status.yaml` in each
plan dir is the machine-readable status (see `_template/`).

Status values: `proposed` → `active` → `implementing` → `in-review` → `done`.

| Plan | Plan dir / frozen doc | Submodules · branches | Jira | Status |
|---|---|---|---|---|
| Institution onboarding | `docs/features/institution-onboarding/implementation-guide.md` + `.zcode/plans/plan-sess_14917fdb-*.md` (wizard split) | `rvl-sbqr-portal` · `feature/institution-onboarding-api`, `rvl-secure-bqr-manager` · `feature/institution-onboarding` | — | implementing |
| Metering & billing | `docs/features/metering-billing/prd.md`, signoff reports (`*-plan-signoff-report.html`) | `rvl-secure-bqr-manager` (metering/billing modules) | see `docs/JIRA-FACTS.md` + `docs/features/metering-billing/traceability.md` | in-review |
| Logging plan | `docs/logging-plan.md` | cross-cutting | — | done (evidence: `reports/logging-plan-e2e/`) |

## Convention for new plans

1. Create `docs/plans/<feature>/` with `plan.md` (copy `_template/plan.md`),
   `status.yaml` (copy `_template/status.yaml`), and the HTML audit report
   beside them when produced.
2. Add a row to the table above.
3. `docs/features/<name>/` keeps long-form design material (DDD, DB design);
   the plan dir holds the frozen, reviewable contract. Link, don't duplicate.
4. `.zcode/plans/` is scratch output from planning sessions — promote the
   agreed version into `docs/plans/<feature>/plan.md`, don't review from `.zcode`.
