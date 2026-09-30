# 03: Billing Periods — Draft & Approve Statements (+ integrated audit logging)

**What to build:** Billing period lifecycle: team sees provisional figures during month; drafts auto-generate after month ends and usage is complete; drafts can be refreshed before approval; month can be finalized (approved) to lock statements. Every refresh and approval is audited.

**Blocked by:** 02 (rate cards must exist to calculate statement lines).

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Billing team sees **Provisional** figures for the current month, updated within minutes (query aggregates `usage_events` + `rate_cards` in real-time)
- [ ] Draft statements auto-generate once a month ends and no incomplete usage remains (all outbox messages for the period are `PROCESSED` or absent)
- [ ] One draft statement per paying institution per month; each line = quantity × price, rounded once to 2 decimals (half away from zero)
- [ ] Adjustments waiting for that institution appear as their own lines in the draft
- [ ] Negative totals shown as credits (no sign check in DB)
- [ ] Drafts can be refreshed any number of times before approval (refreshing recalculates all quantities from `usage_events` and includes pending adjustments)
- [ ] After approval, no refreshes allowed; the statement is locked
- [ ] Approval requires: name of approver + month total amount reviewed by them; if actual total has changed since review, approval is refused with the new total
- [ ] Approving an already-approved month changes nothing and shows the original result
- [ ] After approval, nothing on any statement of that month can change (DB triggers `trg_billing_periods_finalized`, `trg_billing_statements_guard`, `trg_billing_statement_lines_guard` prevent updates)
- [ ] **Audit log** (`audit_logs` table): Every draft refresh recorded with action `billing.period_refresh`, including who, when, period, total before/after
- [ ] **Audit log** (`audit_logs` table): Every approval recorded with action `billing.period_finalize`, including who, when, period, approved total, actual total (must match)
- [ ] **Infrastructure logs**: INFO-level for draft auto-generation and approvals; DEBUG-level for completeness checks, refresh calculations, state transitions

## Related Stories

PRD: R6, R15, R16, R17, R18, R19, R20, R21, R24  
User stories: 6, 7, 8, 9  
Integrated: Story 14 (audit logging for period operations)
