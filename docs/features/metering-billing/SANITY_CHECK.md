# Sanity Check: 6 Tickets Cover All 15 User Stories

**Purpose:** Verify the 6 tickets deliver the complete metering-billing feature and nothing is missing.

**Date:** 30 September 2026

---

## End-to-End Flow: QR Operation → Billing Statement

```
1. QR Operation (in rvl-qr-generation / rvl-verification)
   ↓
2. Outbox event created (same transaction)
   ↓
3. [T01] Dispatcher consumes event → creates usage_event (Story 1, 2, 3)
   ↓
4. [T02] Billing team sets institution rates for future months (Story 4, 5)
   ↓
5. Month ends
   ↓
6. [T03] System checks: all usage arrived? (Story 3 - completeness check)
   ├─ If incomplete: [T03] Show warning to billing team (Story 15)
   └─ If complete: [T03] Auto-generate draft statements (Story 7)
   ↓
7. [T03] Billing team sees provisional figures during month (Story 6)
   ↓
8. [T03] Billing team refreshes draft before approval (Story 8)
   ↓
9. [T05] Billing team may add adjustments for mistakes (Story 12)
   ↓
10. [T03] Billing team approves month, locks all statements (Story 9)
   ↓
11. [T04a] Render statement as PDF/CSV for sending to FI (Story 10)
    ↓
12. [T04b] FI can audit: view raw usage detail (Story 11)
    ↓
13. [T06] Management sees monthly report: volume, revenue per FI (Story 13)

[Throughout] All actions audited: [T02], [T03], [T05] log to audit_logs (Story 14)
```

---

## Story Mapping to Tickets

### Group A: Counting Usage (P0 critical path)

| Story | Title | Ticket | Status | Acceptance Criteria |
|---|---|---|---|---|
| **1** | Count every QR code created | T01 | ✅ Included | Deduplicate by client_reference, charge per generation, handle resends, correct timezone |
| **2** | Count every QR check | T01 | ✅ Included | Count definite answers only, charge to requesting FI, deduplicate, handle resends |
| **3** | Never lose usage | T01 | ✅ Included | Block drafts/approval if usage incomplete, detect PENDING/DEAD outbox, clear when caught up |

**T01 delivers:** Complete metering pipeline from QR operation → usage_events, with deduplication and completeness checking.

---

### Group B: Prices (P0 foundation)

| Story | Title | Ticket | Status | Acceptance Criteria |
|---|---|---|---|---|
| **4** | Set prices for an institution | T02 | ✅ Included | Per-FI rates, future months only, immutable after start, non-billable FIs recorded |
| **5** | Withdraw prices before they start | T02 | ✅ Included | Can withdraw future prices, cannot change active prices |

**T02 delivers:** Rate card management with time-based constraints and immutability.

---

### Group C: Closing a Month (P0 critical path)

| Story | Title | Ticket | Status | Acceptance Criteria |
|---|---|---|---|---|
| **6** | See provisional figures during month | T03 | ✅ Included | Real-time queries, clearly marked Provisional, <few minutes behind |
| **7** | Auto-draft after month end | T03 | ✅ Included | Draft after completeness check, qty × price rounded once, negative totals as credits |
| **8** | Refresh drafts before approval | T03 | ✅ Included | Refresh any # times, recalculate all quantities, no refresh after approval |
| **9** | Approve month | T03 | ✅ Included | Approval requires name + total reviewed, refuse if total changed, lock after approval |
| **15** | Warn when billing at risk | T03 | ✅ Integrated | Show warning for delayed usage, show warning if not approved by 5th |

**T03 delivers:** Complete period lifecycle (Provisional → Draft → Finalized) with state machine, completeness checks, and operational warnings.

---

### Group D: Statements & Evidence (P0/P1)

| Story | Title | Ticket | Status | Acceptance Criteria |
|---|---|---|---|---|
| **10** | Give institution its statement | T04a | ✅ Included | PDF + CSV, header/lines/daily/outcomes/adjustments/earlier/dispute window, same figures |
| **11** | Show evidence behind statement | T04b | ✅ Included | CSV export, date/type/ref/outcome/charged, match statement totals, no PII |

**T04a delivers:** Billing report rendered for sending to FIs (PDF/CSV).  
**T04b delivers:** Audit-friendly usage detail export for FI verification.

---

### Group E: Corrections (P1)

| Story | Title | Ticket | Status | Acceptance Criteria |
|---|---|---|---|---|
| **12** | Correct mistake with adjustment | T05 | ✅ Included | Add charge/credit with reason, appears on next statement, no edit/delete (reverse only), immutable once approved |

**T05 delivers:** Post-approval corrections via reversible adjustments.

---

### Group F: Oversight

| Story | Title | Ticket | Status | Acceptance Criteria |
|---|---|---|---|---|
| **13** | Monthly platform report | T06 | ✅ Included | Volume/revenue per FI, trends, daily totals, non-billable, pending adjustments, late usage |
| **14** | Audit logging | T02, T03, T05 | ✅ Integrated | Rate entry/withdrawal, refresh/approval, adjustment logged with who/when/before-after totals |

**T06 delivers:** Management visibility into monthly business metrics.  
**T14 (integrated):** Audit trail built into every billing operation.

---

## Ticket Summary & Dependencies

| Ticket | Stories | Priority | Blockers | Deliverable |
|---|---|---|---|---|
| **T01** | 1, 2, 3 | P0 | None | Metering pipeline: outbox → usage_events, deduplication, completeness |
| **T02** | 4, 5, 14* | P0 | T01 | Rate cards: set/manage/audit institutional prices |
| **T03** | 6, 7, 8, 9, 15, 14* | P0 | T02 | Billing periods: provisional/draft/approve with warnings |
| **T04a** | 10, 14* | P0 | T03 | FI Billing Report: PDF/CSV for sending to institutions |
| **T04b** | 11 | P1 | T01 | Audit Usage Detail: CSV raw events for FI verification |
| **T05** | 12, 14* | P1 | T03 | Corrections: add/reverse adjustments post-approval |
| **T06** | 13 | P2 | T03, T05 | Management Report: volume, revenue, trends |

\* Story 14 (audit logging) is **split across T02, T03, T05** — each operation logs itself as it ships.

---

## Completeness Verification

### ✅ All 15 Stories Covered

- [x] Story 1: T01
- [x] Story 2: T01
- [x] Story 3: T01
- [x] Story 4: T02
- [x] Story 5: T02
- [x] Story 6: T03
- [x] Story 7: T03
- [x] Story 8: T03
- [x] Story 9: T03
- [x] Story 10: T04a
- [x] Story 11: T04b
- [x] Story 12: T05
- [x] Story 13: T06
- [x] Story 14: T02, T03, T05 (integrated)
- [x] Story 15: T03 (integrated)

### ✅ All PRD Requirements Mapped

- [x] R1-R8: Counting usage (T01)
- [x] R9-R14: Prices (T02)
- [x] R15-R24: Statements & period lifecycle (T03, T04a)
- [x] R25-R28: Corrections & evidence (T05, T04b)
- [x] R29-R32: Reporting & audit (T06, T02/T03/T05)

### ✅ All Priority Levels Respected

- **P0 (needed for first bill):** Stories 1, 2, 4, 7, 9, 10, 14 → T01, T02, T03, T04a, all with audit
- **P1 (before first dispute/audit):** Stories 5, 6, 8, 11, 12, 15 → T03, T04b, T05
- **P2 (can follow):** Story 13 → T06

---

## What Each Ticket Delivers (User Perspective)

### T01: "Usage is recorded accurately"
- Every QR operation counted exactly once
- Resends don't double-charge
- Timezone is correct (Dhaka)
- System won't let a month close if usage is missing

### T02: "Pricing is controlled"
- Billing team sets rates per FI, per month
- Rates lock after they take effect
- Wrong rates can be withdrawn before they start
- Every rate change is audited

### T03: "Months close reliably"
- Team sees provisional figures in real-time
- Drafts auto-generate after month ends
- Team reviews and refreshes before approving
- Month is locked after approval (immutable)
- Warnings alert if usage is delayed or month not approved by 5th

### T04a: "Statements reach FIs"
- Professional PDF/CSV statement with all required details
- Same figures in both formats
- Clear status marking
- Ready to send to institution and Finance

### T04b: "FIs can audit their bill"
- Raw CSV export showing every operation
- Totals match the statement exactly
- No personal data exposed

### T05: "Mistakes are correctable"
- Billing team can add credits/charges post-approval
- Wrong adjustments can be reversed
- Corrections appear on next month's statement
- Every adjustment is audited

### T06: "Management understands the business"
- Monthly volume and revenue per FI
- Trends vs previous month
- Visibility into late usage and pending adjustments
- Non-billable institutions tracked

---

## Potential Gaps or Concerns?

### ✅ Database is complete
Migrations 010-012 are committed; all constraints, triggers, grants ready.

### ✅ Logging strategy is clear
- **Audit logs:** Every operation (price entry, approval, adjustment) logged in `audit_logs` table
- **Infrastructure logs:** Per-ticket DEBUG/INFO events for debugging and monitoring

### ✅ No missing stories
All 15 stories are assigned to exactly one ticket (or split across tickets as needed).

### ✅ Blocking dependencies are correct
- T01 is foundation (no blockers)
- T02 depends on T01 (conceptually; DB is ready)
- T03 depends on T02 (needs rates to calculate statements)
- T04a/b depend on T03 (need finalized data)
- T05 depends on T03 (need approved statements)
- T06 depends on T03, T05 (needs complete billing data)

### ✅ End-to-end testable
Each ticket can be independently tested and deployed once blockers are complete.

### ⚠️ One assumption to confirm
**Story 14 (audit logging) is integrated into T02, T03, T05.** This assumes:
- Each operation logs itself when it runs
- No separate "audit infrastructure" ticket needed

If audit logging needs to be its own foundational ticket before any operations ship, we'd need to add T00. Confirm this assumption is OK.

---

## Recommendation

**All 6 tickets are necessary and sufficient to complete the metering-billing feature.** 

The structure is:
1. **Foundation:** T01 (metering) has no dependencies
2. **Configuration:** T02 (rates) depends on T01 only
3. **Core business logic:** T03 (periods) depends on T02
4. **Outputs:** T04a, T04b, T05, T06 depend on T03 (and T05 for full reports)

Ship in order: **T01 → T02 → T03 → [T04a, T04b, T05 in parallel] → T06**

No stories are missing. No requirements are forgotten. Ready to create Jira tickets. ✅
