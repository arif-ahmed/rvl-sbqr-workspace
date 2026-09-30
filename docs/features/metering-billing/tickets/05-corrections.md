# 05: Corrections — Adjustments & Reversals (+ integrated audit logging)

**What to build:** Billing team can correct mistakes by adding charges or credits (adjustments) that appear on the next statement. Adjustments cannot be edited or deleted; errors are reversed by adding an opposite adjustment. Every adjustment is audited.

**Blocked by:** 03 (billing periods must exist; adjustments apply to the next finalized statement).

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Billing team can add an adjustment: amount (positive or negative, never 0), reason, and their name
- [ ] Adjustment appears as its own line on the institution's next approved statement
- [ ] Adjustment cannot be edited or deleted; a wrong adjustment is reversed by adding an opposite adjustment
- [ ] Once an adjustment appears on an approved statement, it never moves (immutable)
- [ ] Each adjustment appears only once, on exactly one statement (via unique constraint `uq_billing_statement_lines_adjustment`)
- [ ] Multiple adjustments on the same statement each appear as their own line
- [ ] **Audit log** (`audit_logs` table): Every adjustment recorded with action `billing.adjustment_add`, including who, when, institution, amount, reason
- [ ] **Infrastructure logs**: INFO-level for adjustment additions; DEBUG-level for next-statement resolution logic

## Related Stories

PRD: R25, R26, R27  
User stories: 12  
Integrated: Story 14 (audit logging for adjustment operations)

## Note

Disputes are a process rule (30-day window after approval), not system-enforced. The acceptance window is tracked in the statement display (Story 04).
