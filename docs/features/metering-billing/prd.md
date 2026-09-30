# PRD: Usage-based Billing for Institutions

| Field | Detail |
|---|---|
| Product | Bangla QR P2P platform |
| Feature | Usage-based billing for QR generation and QR validation |
| Version | 0.1 |
| Date | 30 September 2026 |
| Status | Draft, pending Product and Head of Engineering sign-off |
| Parent document | [BRD: Bangla QR P2P SDK v2.0](../../../rvl-sbqr-api/docs/BRD_BanglaQR_P2P_SDK_v2.md) |
| Owner | Product team (name to be confirmed) |
| Type | Retrospective: written after the first technical design, to record the business intent that the design must serve |

> **How to read this document.** It says *what* the business needs and *why*. It does not say how the platform builds it. Where the technical design already made a choice that is really a business decision, that choice is listed in section 9 with its sign-off status, not stated here as settled.

---

## 1. Summary

Institutions pay for what they use: each QR code the platform creates for them, and each QR code the platform checks for them. At the end of every month the platform prepares one statement per institution. The billing team reviews and approves the month. An approved statement never changes; a mistake is corrected on a later statement. Finance uses the approved statements to invoice.

## 2. Background and link to the BRD

- The BRD (section 1) describes a *commercially licensed* toolkit but does not say how institutions are charged. This PRD covers the usage charges.
- **BRD addendum needed:** one short paragraph in BRD sections 1-2 stating that institutions are charged per use for QR generation and QR validation, so the commercial model is agreed at business level (question Q1).
- Where the charges come from in the BRD journeys:
  - **Generation:** section 5.1 step 3 and section 5.2 step 3. The QR is created for the recipient's institution.
  - **Validation:** section 5.1 step 6 and section 5.2 step 6. The QR is checked for the sender's institution, but only when the platform performs the check (question Q2).

## 3. Goals

| # | Goal |
|---|---|
| G1 | Charge every institution exactly for what it used: nothing missed, nothing counted twice. |
| G2 | Give every institution a clear monthly statement it can check against its own records. |
| G3 | Give platform management a monthly view of volume and revenue. |
| G4 | Make every charge provable and every correction traceable, to fintech audit standard. |

What v1 does not do is listed in section 10.

## 4. Who is involved

| Who | What they need |
|---|---|
| Paying institution (bank, MFS provider, PSP) | Know what it is charged for, check it, raise a dispute |
| Platform billing team | Set prices, review and approve each month, correct mistakes, answer disputes |
| Finance | Approved amounts to invoice. Tax and payment collection stay with Finance. |
| Platform management | Monthly volume, revenue and trends |
| Auditors | Who did what, when and why |

## 5. Key terms

| Term | Meaning |
|---|---|
| Chargeable operation | A QR generation or QR validation that the platform completed for an institution |
| Price | BDT per chargeable operation, set per institution, separately for generation and validation |
| Billing month | A calendar month in Bangladesh time |
| Statement | One institution's charges for one billing month |
| Provisional / Draft / Approved | Provisional: the month is still running. Draft: the month is over and under review. Approved: final, never changes. |
| Adjustment | A charge (+) or credit (-) with a written reason, placed on the next statement to correct a mistake |
| Usage detail | The list of every operation behind a statement; the evidence for any charge |
| Non-billable institution | An institution with no price set (for example a pilot or test institution): usage is recorded but not charged |

---

## 6. User journeys

### 6.1 A new institution starts paying

*Example Bank signs its contract on 20 October. Agreed prices: BDT 0.50 per QR generated and BDT 0.25 per validation, from 1 November.*

1. The billing team enters both prices with November as the start month.
2. Example Bank's October usage is recorded but not charged (see Q6 on the first partial month).
3. From 1 November, every chargeable operation is counted.
4. A price can be withdrawn and re-entered until it takes effect. After that it cannot change; a new price can only start from a later month.

### 6.2 Closing a month

*Closing November.*

1. During November the billing team can view running figures, clearly marked **Provisional**.
2. When November ends and all of November's usage has arrived, the platform prepares **Draft** statements for every paying institution by itself. Nobody has to start it.
3. The billing team reviews the drafts: total per institution and total for the month.
4. A named person approves the month. The platform approves exactly what was reviewed. If any figure changed after the review (for example a new adjustment), approval stops and the team reviews the updated drafts.
5. All statements for the month are approved together. The billing team sends each institution its statement, and Finance invoices from them.

### 6.3 A dispute and its correction

*Example Bank says 5,000 validations on 14 November failed because the platform's copy of the Bangladesh Bank key list was out of date.*

1. The bank raises the dispute within 30 days of approval.
2. The billing team checks the usage detail and confirms the claim.
3. The billing team adds a credit of BDT -1,250.00 (5,000 x 0.25) with the reason.
4. The credit appears as its own line on Example Bank's December statement. The November statement stays unchanged.

---

## 7. Functional requirements

### 7.1 What is charged

| ID | Requirement | Acceptance criterion |
|---|---|---|
| R1 | Each QR code the platform creates for an institution, static or dynamic, is one chargeable generation. | A created QR appears once in the usage detail and adds exactly 1 to the count. |
| R2 | Each QR check that the platform completes with a definite answer is one chargeable validation, whether the QR is good or not. Definite answers: valid; signature does not match; QR damaged or malformed; issuing institution not in the Bangladesh Bank key list; issuer's key suspended, revoked or not yet active; not a person-to-person QR. | Each of these outcomes is counted. The statement shows the count per outcome. |
| R3 | A request the platform refuses or cannot complete is never charged. This covers incomplete or invalid requests, unauthorised requests, requests too old to accept, and platform errors. | None of these appears as charged. They may appear as "recorded, not charged" for transparency. |
| R4 | A resent request (for example after a network timeout) is charged once. Each request carries the institution's own reference, and a resend with the same reference is recognised. | Two sends with the same reference produce one charge. |
| R5 | An operation belongs to the month in which it happened (Bangladesh time), even if it is recorded later. | A QR created at 23:59:59 on 30 November counts in November. |
| R6 | No usage may be lost. If recording falls behind, the month cannot close until it catches up. | While any usage for the month is outstanding, no draft is prepared and approval is refused. |

### 7.2 Who pays

| ID | Requirement | Acceptance criterion |
|---|---|---|
| R7 | A generation is charged to the institution that asked for the QR (the recipient's institution). | |
| R8 | A validation is charged to the institution that asked for the check (normally the sender's), not to the institution that issued the QR. | Bank A's QR checked for Bank B is charged to Bank B. |
| R9 | An institution with no price for a month is not charged. Its usage is still recorded and shown in the platform report as non-billable. | No statement exists for it. It appears in the platform report. |

### 7.3 Prices

| ID | Requirement | Acceptance criterion |
|---|---|---|
| R10 | Each paying institution has two prices, per generation and per validation, in BDT, with up to 4 decimal places. | |
| R11 | A price takes effect on the 1st of a month, and only for a future month. The current and past months cannot be re-priced. | Entering a price for the current month is refused. |
| R12 | A price that has not taken effect can be withdrawn. A price that has taken effect cannot be changed or removed. | |
| R13 | A statement always uses the price in effect for its month. Later price changes never affect it. | Changing next year's price leaves this year's statements unchanged. |
| R14 | Static and dynamic QR generation cost the same but appear as separate lines. *(Needs sign-off: Q9.)* | |

### 7.4 Monthly statement

| ID | Requirement | Acceptance criterion |
|---|---|---|
| R15 | One statement per paying institution per month. | |
| R16 | For each line, amount = quantity x price, rounded once to 2 decimals (half away from zero). Total = sum of lines + adjustments. | 3 validations x 0.125 = 0.375, shown as 0.38. |
| R17 | A negative total is shown as a credit, as it is. v1 does not carry it forward. | |
| R18 | Drafts are prepared automatically once the month has ended and all its usage has arrived. | |
| R19 | Drafts can be refreshed any number of times before approval (for example after a new adjustment). | |
| R20 | A named person approves a whole month for all institutions at once, confirming the total they reviewed. If the total has changed since, approval is refused. Approving again has no further effect. | |
| R21 | After approval nothing on the statement can change. | |
| R22 | Statement contents: institution, month, status, approval date and approver, prices used; one line per operation type; daily usage; validations by outcome (charged / not charged); adjustments; earlier approved statements; the end date of the dispute window; "not a tax invoice". | |
| R23 | Available as a printable page (save as PDF) and as a spreadsheet (CSV). All formats show the same figures. | |
| R24 | The status (Provisional, Draft, Approved) is clearly marked on every statement. | |

### 7.5 Corrections and disputes

| ID | Requirement | Acceptance criterion |
|---|---|---|
| R25 | Corrections are made only by adjustment: amount (+ or -, never 0), reason, and who made it. An adjustment cannot be edited or deleted; a wrong one is reversed by an opposite adjustment. | |
| R26 | An adjustment goes on the next statement prepared for that institution. Once that statement is approved, the adjustment is settled for good. | |
| R27 | Disputes are accepted for 30 days after approval. The evidence is the usage detail. *(Needs contract check: Q7.)* | |
| R28 | Usage detail has one row per operation: date and time (Bangladesh), operation type, the institution's own reference, outcome (for validations), charged yes / no. It holds no personal data (names, account numbers, phone numbers). | The charged rows add up exactly to the statement quantities. |

### 7.6 Platform report

| ID | Requirement |
|---|---|
| R29 | A monthly internal report: volume and revenue per institution (largest first, with share); change against the previous month; daily totals; status of each month; usage of non-billable institutions; adjustments waiting for the next statement; usage that arrived after its month was approved. |

### 7.7 Access and accountability

| ID | Requirement |
|---|---|
| R30 | Only the platform billing team can set prices, add adjustments, approve months and view reports. In v1 institutions have no direct access; the billing team sends them their statements. |
| R31 | Every price entry, adjustment, draft refresh and approval is recorded: who, when, what, and the totals before and after. |
| R32 | An institution only ever receives its own statement. Statements are confidential. |

---

## 8. Quality requirements

| Area | Requirement |
|---|---|
| Accuracy | Every charged quantity can be traced to individual operations. The statement and the usage detail always agree. |
| Completeness | No operation is lost or counted twice, including during retries and outages. |
| Finality | Approved statements cannot be changed by anyone. |
| Timeliness | Usage is visible to the billing team within minutes. Each month is approved by the 5th of the next month *(target, needs sign-off)*. |
| Visibility | The billing team can see when usage is delayed, and when a month is still not approved by the 5th. |
| Retention | Statements are kept permanently. Usage detail is kept at least 13 months, or longer if regulation requires (Q12). |
| Privacy | Billing records hold no personal data. |

## 9. Business decisions and their status

| # | Decision | Status |
|---|---|---|
| D1 | Pay per use only: no monthly fee, tiers or included units | Agreed in design review |
| D2 | Invalid outcomes are charged too (R2) | Agreed internally; commercial confirmation needed (Q3) |
| D3 | The institution that asks for a validation pays for it (R8) | Agreed in design review |
| D4 | Billing month = calendar month, Bangladesh time | Agreed in design review |
| D5 | Price per institution, set for future months only | Agreed; may become one price list for all (Q5) |
| D6 | Static and dynamic generation cost the same, shown separately | Needs sign-off (Q9) |
| D7 | Rounding once per line, 2 decimals, half away from zero | Agreed in design review |
| D8 | All institutions' statements for a month are approved together | Agreed in design review |
| D9 | Corrections only by adjustment on the next statement | Agreed in design review |
| D10 | 30-day dispute window | Needs contract check (Q7) |
| D11 | No price = not charged | Agreed in design review |
| D12 | Negative total shown; no carry-forward | Agreed in design review |
| D13 | The platform produces statements, not invoices; tax and collection stay with Finance | Agreed in design review |
| D14 | Month approved by the 5th | Needs sign-off |

## 10. Out of scope for v1

- Invoices, VAT, payment tracking
- Monthly fees, tiers, included units, subscriptions
- Self-service access for institutions; a billing screen for the platform team
- Automatic emailing of statements; notifications
- Price changes in the middle of a month; carrying credit forward
- Charts
- Two-person approval and individual logins (v1 records the approver's name)
- Automatic handling of late usage (the billing team corrects it with an adjustment)

## 11. Success measures

- 100% of approved statements agree exactly with their usage detail.
- Zero changes to approved statements.
- Every month approved by the 5th of the following month.
- Every dispute answered within its window; the number of adjustments caused by platform faults is tracked and falls over time.

## 12. Open questions

| # | Question | Owner | Affects |
|---|---|---|---|
| Q1 | BRD addendum: confirm per-use charging as the commercial model. | Business sponsor | Whole PRD |
| Q2 | In the BRD, the sender's app checks the QR on the device (section 5.1 step 6, offline case in 5.3). The platform can only charge for checks it performs itself. Which checks are chargeable, and must institutions use the platform's check? | Product + Head of Engineering | R2, R8, revenue |
| Q3 | Will institutions accept being charged for invalid outcomes, and will contracts say so? | Commercial | R2 |
| Q4 | Should institution apps pre-check a QR for damage before sending it, so they don't pay for damaged QRs? | Product + Head of Engineering | R2 |
| Q5 | Replace per-institution prices with one price list for all, plus a choice of generation only, validation only, or both? | Product | R10-R13 |
| Q6 | Is the first partial month after onboarding charged? | Commercial | R11 |
| Q7 | Does the 30-day dispute window match the contracts? | Legal / Commercial | R27 |
| Q8 | What credit policy applies when the platform is at fault (for example an out-of-date key list)? | Commercial | R25 |
| Q9 | Do static and dynamic generation keep the same price, and stay as separate lines? | Product | R14 |
| Q10 | Are all institutions billed in BDT? | Finance | R10 |
| Q11 | Does Bangladesh Bank set rules or caps on these fees? | Compliance | Whole PRD |
| Q12 | Must usage detail be kept longer than 13 months? | Compliance | Section 8 |
| Q13 | Are these fees subject to VAT? When will Finance's system connect? | Finance | Section 10 |
| Q14 | Who may approve months and add adjustments? Is a recorded name enough for v1? | Operations | R20, R31 |
| Q15 | Is there a roadmap for self-service access and a billing screen? | Product | Section 10 |
| Q16 | Who watches for delayed usage and late approval, and who do they tell? | Operations | Section 8 |
| Q17 | Must months be approved in order? If October is not yet approved, do November's drafts wait? | Product + Operations | R18, R20 |

## 13. Related documents

- [BRD: Bangla QR P2P SDK v2.0](../../../rvl-sbqr-api/docs/BRD_BanglaQR_P2P_SDK_v2.md): the parent document
- [User stories](user-stories.md): this PRD split into buildable stories
- [Traceability](traceability.md): each requirement linked to story, design, database and test, with build status
- Technical documents in this folder ([README](README.md)). They must satisfy this PRD; where they disagree, raise it against this PRD.
