# 04: Statements & Usage Export — Render and Deliver

**What to build:** Billing team can export a finalized or draft statement as a printable page (PDF) and a spreadsheet (CSV), showing all required details. Separately, any institution's usage detail (raw events) can be exported for audit verification.

**Blocked by:** 03 (statements must exist) for story 10; 01 (usage_events must exist) for story 11.

**Status:** ready-for-agent

## Acceptance Criteria

### Statement Rendering (Story 10)

- [ ] Statement available as PDF and CSV; both show identical figures
- [ ] PDF is printable page with: institution name, month, status (Provisional/Draft/Approved), approval date and approver name, rates used
- [ ] Each line shows: operation type (GENERATION_STATIC, GENERATION_DYNAMIC, VALIDATION), quantity, price per unit, line total
- [ ] Daily usage breakdown: for each day in the month, quantity by operation type
- [ ] Validations by outcome: separate line for each definite answer type (VALID, INVALID_SIGNATURE, KEY_EXPIRED, etc.) with count and whether charged
- [ ] Adjustments section: one line per adjustment (date, reason, amount, who made it)
- [ ] Earlier approved statements listed (for reference)
- [ ] Dispute window end date clearly shown
- [ ] "Not a tax invoice" disclaimer
- [ ] Status (Provisional/Draft/Approved) clearly marked on every page

### Usage Detail Export (Story 11)

- [ ] CSV export of raw usage_events: one row per operation
- [ ] Columns: date and time (Bangladesh), operation type, institution's own reference (client_reference), outcome (for validations), charged (yes/no)
- [ ] No personal data: no names, account numbers, phone numbers
- [ ] Charged rows add up exactly to the statement quantities (verifiable by summing CSV)
- [ ] Available for any month, any institution

## Related Stories

PRD: R22, R23, R24, R28, R32  
User stories: 10, 11
