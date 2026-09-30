# 02: Rate Cards — Set & Manage Prices (+ integrated audit logging)

**What to build:** Billing team can set institution prices per QR generation and per QR validation, effective from the 1st of a future month. Prices are immutable once they take effect (DB enforces this via trigger `trg_billing_rate_cards_guard`). Every price entry and withdrawal is audited.

**Blocked by:** 01 (metering pipeline — conceptual foundation).

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Billing team can enter two prices (generation_rate, validation_rate) in BDT with up to 4 decimal places, both ≥ 0
- [ ] Price start date must be the 1st of a future month (attempting current or past month is rejected)
- [ ] A price that has not yet taken effect can be withdrawn; once it takes effect, it cannot be changed or withdrawn
- [ ] A statement always uses the rate card that was active for that month, even if newer rates are added later
- [ ] An institution with no rate card for a month is not charged but its usage still appears in the platform report (non-billable)
- [ ] **Audit log** (`audit_logs` table): Every rate entry recorded with action `billing.rate_entry`, including who, when, institution, start month, generation_rate, validation_rate
- [ ] **Audit log** (`audit_logs` table): Every rate withdrawal recorded with action `billing.rate_withdrawal`, including who, when, institution, withdrawn rates
- [ ] **Infrastructure logs**: INFO-level events for rate entry/withdrawal; DEBUG-level for validation checks (future-month constraint, immutability check)

## Related Stories

PRD: R9, R10, R11, R12, R13, R14  
User stories: 4, 5  
Integrated: Story 14 (audit logging for rate operations)
