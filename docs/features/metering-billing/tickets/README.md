# Metering-Billing Implementation Tickets

**Date created:** 30 September 2026  
**Status:** ready-for-agent  
**Target repository:** `rvl-secure-bqr-manager` (feature/mtls-server)  

---

## Overview

7 vertical-slice tickets to implement usage-based billing for QR generation and validation. Each ticket delivers complete end-to-end functionality that can be independently tested and deployed once its dependencies are met.

Both **audit logging** (`audit_logs` table for compliance) and **infrastructure logging** (application logs for observability) are integrated into each ticket.

---

## Tickets by Dependency

### Tier 0: Foundation (no dependencies)

| Ticket | Scope | Blockers | Integrated Stories |
|---|---|---|---|
| [01: Metering Pipeline](01-metering-pipeline.md) | Consume outbox events, deduplicate, record usage_events, detect completeness | None | 1, 2, 3 + infrastructure logs |

### Tier 1: Depends on Tier 0

| Ticket | Scope | Blockers | Integrated Stories |
|---|---|---|---|
| [02: Rate Cards](02-rate-cards.md) | Set institution prices for future months, enforce immutability | 01 | 4, 5 + audit_logs + infrastructure logs |

### Tier 2: Depends on Tier 1

| Ticket | Scope | Blockers | Integrated Stories |
|---|---|---|---|
| [03: Billing Periods](03-billing-periods.md) | Period lifecycle: provisional figures, auto-draft, refresh, finalize | 02 | 6, 7, 8, 9 + audit_logs + infrastructure logs |

### Tier 3: Depends on earlier tiers

| Ticket | Scope | Blockers | Integrated Stories |
|---|---|---|---|
| [04: Statements & Export](04-statements-export.md) | Render statements (PDF/CSV) and raw usage detail export | 03 (story 10), 01 (story 11) | 10, 11 |
| [05: Corrections](05-corrections.md) | Add/reverse adjustments post-approval | 03 | 12 + audit_logs + infrastructure logs |

### Tier 4: Depends on Tier 2 & 3

| Ticket | Scope | Blockers | Integrated Stories |
|---|---|---|---|
| [06: Management Report](06-management-report.md) | Monthly volume, revenue, trends, late usage, pending adjustments | 03, 05 | 13 |
| [07: Operational Alerts](07-operational-alerts.md) | Alert on delayed usage and late approval | 03, 01 | 15 |

---

## Story Mapping

| User Story | Ticket | Status |
|---|---|---|
| 1: Count QR generations | 01 | ready-for-agent |
| 2: Count QR validations | 01 | ready-for-agent |
| 3: Ensure no usage is lost | 01 | ready-for-agent |
| 4: Set prices | 02 | ready-for-agent |
| 5: Withdraw prices | 02 | ready-for-agent |
| 6: See provisional figures | 03 | ready-for-agent |
| 7: Auto-generate drafts | 03 | ready-for-agent |
| 8: Refresh drafts | 03 | ready-for-agent |
| 9: Approve month | 03 | ready-for-agent |
| 10: Render statements | 04 | ready-for-agent |
| 11: Export usage detail | 04 | ready-for-agent |
| 12: Add adjustments | 05 | ready-for-agent |
| 13: Management report | 06 | ready-for-agent |
| 14: Audit logging | 02, 03, 05 (integrated) | ready-for-agent |
| 15: Operational alerts | 07 | ready-for-agent |

---

## Key Notes

- **Database is done:** Migrations `010_outbox.sql`, `011_metering.sql`, `012_billing.sql` are committed in `develop` (SHA `542d69d`). All constraints, triggers, and grants are in place.
- **Logging integrated:** Audit logs (`audit_logs` table, action names like `billing.rate_entry`) and infrastructure logs (structured, per-ticket strategy) are included in every ticket that performs operations.
- **Sequential shipping:** Each ticket can land independently once its blockers are complete, keeping CI green. Start with 01, then 02 and beyond once dependencies clear.
