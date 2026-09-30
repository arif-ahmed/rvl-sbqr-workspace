# User Stories: Usage-based Billing for Institutions

| Field | Detail |
|---|---|
| Source | [PRD](prd.md) (requirements R1-R32), [BRD v2.0](../../../rvl-sbqr-api/docs/BRD_BanglaQR_P2P_SDK_v2.md) |
| Date | 30 September 2026 |
| Status | Draft, follows the PRD sign-off |
| Format | User story + acceptance criteria, checked against INVEST |
| Stories | 15, in 6 groups |

> Same rule as the PRD: stories describe what people need, not how it is built. Where stories and PRD disagree, the PRD wins. Build status of each story is in [traceability.md](traceability.md).

**Who's who:** *billing team* = the platform team that runs billing; *institution* = a paying bank, MFS provider or PSP; *management* = platform management.

**Priority:** P0 = needed for the first real bill; P1 = needed before the first dispute or audit; P2 = can follow.
**Size:** S / M / L, relative effort.

---

## A. Counting usage

### Story 1: Count every QR code created

**As an** institution, **I want** to be charged once for each QR code the platform creates for me, **so that** my bill matches what I actually asked for.

- [ ] Each QR created, static or dynamic, is counted once, for the institution that asked for it.
- [ ] A resend of the same request (same reference) is counted once.
- [ ] A refused request (incomplete, unauthorised, platform error) is not counted.
- [ ] A QR created at 23:59:59 on the last day of a month (Bangladesh time) counts in that month, even if it is recorded later.

PRD: R1, R3, R4, R5, R7 | Priority: P0 | Size: M | Depends on: none

### Story 2: Count every QR check that gets a definite answer

**As an** institution, **I want** to be charged once for each QR check the platform completes with a definite answer, **so that** I pay for the checking service whether the QR turns out good or bad.

- [ ] Each definite answer is counted once, for the institution that asked for the check (not the one that issued the QR). Definite answers: valid; signature does not match; damaged or malformed; issuer not in the Bangladesh Bank key list; issuer's key suspended, revoked or not yet active; not a person-to-person QR.
- [ ] A refused request (too old, incomplete, unauthorised, platform error) is not charged.
- [ ] A resend of the same check (same reference) is counted once.

PRD: R2, R3, R4, R5, R8 | Priority: P0 | Size: M | Depends on: Q2, Q3 in the PRD

### Story 3: Never lose usage

**As the** billing team, **I want** a month to stay open while any of its usage has not yet arrived, **so that** no statement is ever missing charges.

- [ ] While any usage for the month is outstanding, no draft is prepared.
- [ ] While any usage for the month is outstanding, approval is refused with a clear reason.
- [ ] Once the delay clears, drafts are prepared as normal.
- [ ] The billing team can see that usage is delayed (see Story 15).

PRD: R6 | Priority: P0 | Size: M | Depends on: Stories 1, 2

---

## B. Prices

### Story 4: Set prices for an institution

**As the** billing team, **I want** to set an institution's price per QR generation and per QR check from a future month, **so that** charges follow the signed contract.

- [ ] Two prices in BDT, up to 4 decimals, both zero or more.
- [ ] The start is always the 1st of a future month. Entering the current or a past month is refused.
- [ ] An institution with no price for a month is not charged, but its usage still shows in the platform report.
- [ ] A statement always uses the price in effect for its month, even if a newer price is added later.

PRD: R9, R10, R11, R13, R14 | Priority: P0 | Size: S | Depends on: Q5, Q6, Q9 in the PRD

### Story 5: Withdraw a price entered by mistake

**As the** billing team, **I want** to withdraw a price before it takes effect, **so that** a typing mistake never reaches a bill.

- [ ] A price that has not started can be withdrawn.
- [ ] A price that has started cannot be changed or withdrawn.

PRD: R12 | Priority: P1 | Size: S | Depends on: Story 4

---

## C. Closing a month

### Story 6: See running figures during the month

**As the** billing team, **I want** to see each institution's charges so far in the current month, **so that** I can answer questions before the month ends.

- [ ] Figures are clearly marked **Provisional**.
- [ ] Figures are no more than a few minutes behind.

PRD: R24, section 8 (timeliness) | Priority: P1 | Size: S | Depends on: Stories 1, 2, 4

### Story 7: Drafts prepared automatically after month end

**As the** billing team, **I want** draft statements for every paying institution to appear by themselves after the month ends, **so that** closing does not depend on someone remembering to start it.

- [ ] Drafts appear once the month has ended and all its usage has arrived.
- [ ] One statement per paying institution. Each line = quantity x price, rounded once to 2 decimals.
- [ ] Every adjustment waiting for that institution appears as its own line.
- [ ] A negative total is shown as a credit.

PRD: R15, R16, R17, R18, R26 | Priority: P0 | Size: L | Depends on: Stories 3, 4

### Story 8: Refresh drafts before approval

**As the** billing team, **I want** to refresh the drafts after adding an adjustment or clearing delayed usage, **so that** I review current figures.

- [ ] Drafts can be refreshed any number of times before approval.
- [ ] After approval, refresh is not possible.

PRD: R19 | Priority: P1 | Size: S | Depends on: Story 7

### Story 9: Approve the month

**As a** named member of the billing team, **I want** to approve all statements of a month at once, confirming the total I reviewed, **so that** exactly what I checked becomes final.

- [ ] Approval needs my name and the month total I reviewed.
- [ ] If the total has changed since my review, approval is refused and I review the new figures.
- [ ] Approving an already approved month changes nothing and shows the original result.
- [ ] After approval, nothing on any statement of that month can change.

PRD: R20, R21 | Priority: P0 | Size: M | Depends on: Stories 7, 8, and Q14 and Q17 in the PRD

---

## D. Statements and evidence

### Story 10: Give an institution its statement

**As the** billing team, **I want** each institution's statement as a printable page and a spreadsheet, **so that** I can send it and Finance can invoice from it.

- [ ] Contents follow PRD R22: header, monthly lines, daily usage, checks by outcome, adjustments, earlier statements, dispute window end, "not a tax invoice".
- [ ] The printable page and the spreadsheet show the same figures.
- [ ] The status (Provisional, Draft, Approved) is clearly marked.
- [ ] A statement shows only one institution's data.

PRD: R22, R23, R24, R32 | Priority: P0 | Size: M | Depends on: Story 7

### Story 11: Show the evidence behind a statement

**As an** institution, **I want** the list of every operation behind my statement, with my own reference on each row, **so that** I can match it against my records.

- [ ] One row per operation: date and time (Bangladesh), type, my reference, outcome for checks, charged yes / no.
- [ ] The charged rows add up exactly to the statement quantities.
- [ ] No names, account numbers or phone numbers.

PRD: R28 | Priority: P1 | Size: S | Depends on: Stories 1, 2

---

## E. Corrections

### Story 12: Correct a mistake with an adjustment

**As the** billing team, **I want** to add a charge or credit with a reason after a month is approved, **so that** mistakes are fixed without changing an approved statement.

- [ ] An adjustment has an amount (+ or -, never 0), a reason and my name.
- [ ] It appears on the institution's next statement as its own line.
- [ ] It cannot be edited or deleted; a wrong one is reversed by an opposite adjustment.
- [ ] Once on an approved statement, it never moves.
- [ ] Disputes are accepted for 30 days after approval (process rule, not a system block).

PRD: R25, R26, R27 | Priority: P1 | Size: M | Depends on: Story 9, and Q7 and Q8 in the PRD

---

## F. Oversight

### Story 13: Monthly platform report

**As** management, **I want** a monthly report of volume and revenue per institution, **so that** I can follow the business.

- [ ] Per institution, largest first, with share of total.
- [ ] Change against the previous month.
- [ ] Daily totals; status of each month.
- [ ] Non-billable institutions' usage; adjustments waiting; usage that arrived after its month was approved.

PRD: R29 | Priority: P2 | Size: M | Depends on: Stories 7, 12

### Story 14: Only the billing team, and every action on record

**As an** auditor, **I want** billing actions restricted to the billing team and each one recorded, **so that** I can see who changed money figures, when and why.

- [ ] Only the billing team can set prices, add adjustments, refresh, approve and see reports. Institutions have no direct access in v1.
- [ ] Every price entry, adjustment, refresh and approval is recorded: who, when, what, totals before and after.

PRD: R30, R31 | Priority: P0 | Size: S | Depends on: Q14 in the PRD

### Story 15: Warn when billing is at risk

**As the** billing team, **I want** to be warned when usage is delayed or a month is still not approved by the 5th, **so that** bills go out on time.

- [ ] A warning shows while any usage is delayed.
- [ ] A warning shows if the previous month is not approved by the 5th.

PRD: section 8 (visibility) | Priority: P1 | Size: S | Depends on: Q16 in the PRD

---

## INVEST check

| Check | Result |
|---|---|
| Independent | Stories inside a group depend on each other in order (A, then B, then C). Across groups, only through the dependencies listed. |
| Negotiable | Prices, windows and deadlines stay open where the PRD marks them "needs sign-off". |
| Valuable | Each story names who gains and why. |
| Estimable / Small | Largest is Story 7 (L). No story needs splitting yet. |
| Testable | Every story has checkable acceptance criteria. |
