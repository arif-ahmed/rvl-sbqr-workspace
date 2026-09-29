# SBQR Logging-Plan — E2E Evidence

This folder contains the **end-to-end evidence** for the implementation of
[`docs/logging-plan.md`](../docs/logging-plan.md) in the
`rvl-secure-bqr-manager` submodule.

It is the durable, workspace-level copy of the artifacts produced during the
e2e test + live investigation trace against **Dhaka Bank** (institution
code `000085`) on 2026-09-29.

`reports/` sits at the root of the orchestrator repo as a sibling of
`docs/` and the `rvl-*` submodule folders — it is the durable home for
cross-repo test evidence that needs to be reviewed without cloning into a
submodule.

## Source of truth

The code changes live in the `rvl-secure-bqr-manager` submodule, on branch
`feature/logging-plan`. The submodule-local copies of these same artifacts
remain in:

```
rvl-secure-bqr-manager/test-results/
├── e2e-report.html
├── trace-output.txt
├── 01-gen-static.json … 12-replay-2.json
└── trace-tool/Trace/{Trace.csproj, Program.cs}
```

The copies in this folder are byte-identical (verified via `diff -q`); keep
them in sync if you regenerate the evidence.

## What's in here

| File / folder                       | What it is                                                                                              |
|-------------------------------------|---------------------------------------------------------------------------------------------------------|
| `e2e-report.html`                   | The full human-readable report. 9 sections covering test matrix, evidence per scenario, PII canary, allow-list, developer/ops playbook, and the new live-investigation trace. |
| `trace-output.txt`                  | Verbatim stdout from one run of the trace tool against the live API + Postgres.                          |
| `per-scenario-json/`                | The 12 per-request JSON responses (`POST /v1/qr/generate/static`, `/v1/qr/validate`, etc.).             |
| `trace-tool/Trace/`                 | The re-runnable .NET console program that produced `trace-output.txt`. Reproducible evidence.           |

## How to read the evidence

1. Start with **`e2e-report.html`** — section §1 is the test matrix, §2–§4
   walk through every scenario with the exact response body and the matching
   canonical summary line, §5 is the PII-leak canary, §6 is the locked
   summary-line allow-list, §7 is the investigation playbook (anchors A–E),
   §8 is the live trace transcript.
2. The trace transcript in §8 cross-references the raw output in
   **`trace-output.txt`** line-by-line.
3. The 12 JSONs in **`per-scenario-json/`** are what an investigator would
   receive from the bank's gateway during a real complaint.

## How to re-run the trace

```bash
cd reports/logging-plan-e2e/trace-tool/Trace
dotnet run
```

The program assumes:
- The SBQR.Api is running at `http://127.0.0.1:5001`
- Postgres is reachable at `localhost:5432` with the dev credentials
  `postgres / postgres`
- The Dhaka Bank dev client (`000085-46a766da`) is provisioned in
  `IdentityAccess`

It prints the exact `curl` command before every HTTP request and the exact
response after, so you can copy-paste any individual step into a shell.

## Why this folder exists at the repo root

The SBQR workspace (this repo) is the **root orchestrator** (see
`README.md`). `docs/` holds plans, mTLS guides, and design notes;
`reports/` holds the durable proof that those plans were implemented —
HTML reports, raw transcripts, re-runnable trace tools, and per-scenario
evidence — one subfolder per feature. A reviewer landing on this repo
sees the proof of the logging-plan implementation without having to
clone into the submodule.

## Out of scope

- Application code (lives in `rvl-secure-bqr-manager/`)
- Build artifacts (`bin/`, `obj/` of the trace tool are gitignored)
- Local dev logs (`logs/`, `dev-pki/`, `rvl-sbqr-keys/` are gitignored)
