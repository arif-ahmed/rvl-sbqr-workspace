# Traceability: Usage-based Billing

| Field | Detail |
|---|---|
| Date | 30 September 2026 |
| Purpose | Link every PRD requirement to its user story, the technical design, what the database already enforces, and the acceptance test. Show what is built and what is not. |
| Audience | Product, engineering, reviewers, auditors |
| Chain | [BRD](../../../rvl-sbqr-api/docs/BRD_BanglaQR_P2P_SDK_v2.md) → [PRD](prd.md) → [User stories](user-stories.md) → [Design v1.1](metering-billing-v1-bn.md) → [DDD docs](README.md) → [Database design](database-design.md) → migrations `010`-`012` |

Unlike the PRD, this document uses technical names on purpose: it is the bridge between the two languages.

**Build status (30 Sep 2026):** database migrations `010_outbox.sql`, `011_metering.sql`, `012_billing.sql` are committed in `rvl-secure-bqr-manager` (`develop`, `542d69d`). No application code, no API, no tests of the billing flow yet.

Status keys:
- **DB** = the database already enforces the rule.
- **Code** = the rule needs application code, not written yet.
- **Process** = a people rule; the system does not enforce it.
- **Open** = waits for a PRD question.

---

## 1. Words: PRD vs technical documents

| PRD (product) | Technical documents |
|---|---|
| Institution | FI, tenant (`tenant_id`) |
| Chargeable operation | Billable usage event (`usage_events.billable = true`) |
| QR created / QR check | Meter `GENERATION_STATIC`, `GENERATION_DYNAMIC` / `VALIDATION` |
| Definite answer | Conclusive verdict (ADR 0002) |
| Refused request | Protocol rejection (`REQUEST_STALE`, `REQUEST_REPLAYED`) or HTTP 4xx / 5xx |
| Institution's own reference | `Idempotency-Key` (generation), `requestId` (validation); stored as `client_reference` |
| Price | Rate card (`billing_rate_cards`) |
| Billing month | Billing period (`billing_periods.period`, `BillingMonth`) |
| Provisional / Draft / Approved | `PROVISIONAL` / `DRAFT` / `FINALIZED` |
| Approve a month | Finalize (with `finalizedBy`, `expectedTotal`) |
| Refresh drafts | Recalculate |
| All usage has arrived | Usage completeness: no `PENDING` or `DEAD` outbox message up to the period end |
| Usage detail | Raw usage extract (CSV) |
| Non-billable institution | Tenant without a rate card for the period |
| Billing team | Operator with the `admin` scope |
| Record of who did what | `audit_logs`, actions `billing.*` |

---

## 2. BRD → PRD

| BRD | PRD |
|---|---|
| Section 1: "commercially licensed" | Section 2; open question Q1 (BRD addendum for per-use charging) |
| Sections 5.1 and 5.2, step 3: the platform creates and signs the QR | R1, R7 |
| Sections 5.1 and 5.2, step 6: the QR is checked | R2, R8; open question Q2 (device check vs platform check) |
| Section 5.3: failure outcomes (signature fails, unknown institution, damaged QR) | R2 (charged as definite answers); Q3, Q4 |

---

## 3. PRD requirement → story → design → enforcement → test

"Design §" = section of [metering-billing-v1-bn.md](metering-billing-v1-bn.md). "AC" = its section 12 acceptance criteria.

| PRD | Story | Design § | Database (in `010`-`012`) | Status | AC |
|---|---|---|---|---|---|
| R1 | 1 | 2.1, 4.3 | `usage_events` meters `GENERATION_*`; `uq_usage_events_source` | DB + Code | 1, 4 |
| R2 | 2 | 2.2; ADR 0002 | `usage_events.billable`, `detail` (verdict) | Code (policy), Open (Q2, Q3) | 5 |
| R3 | 1, 2 | 2.1, 2.2 | Not-charged rows kept with `billable = false` | Code | 5 |
| R4 | 1, 2 | 2.4 | `uq_usage_events_source`; existing idempotency and replay unique keys in `003`, `004` | DB + Code (keys still optional in code) | 1, 2, 3 |
| R5 | 1, 2 | 3, 4.3 | `usage_events.occurred_at`; `ix_usage_events_tenant_time` | DB + Code | 7 |
| R6 | 3 | 4.4, 4.7, 5 | `outbox_messages` states; `ix_outbox_messages_open` | Code | 4, 8 |
| R7 | 1 | 2.3 | `usage_events.tenant_id` | Code | - |
| R8 | 2 | 2.3; ADR 0002 | `usage_events.tenant_id` (verifying FI) | Code | 6 |
| R9 | 4 | 2.5 | A statement needs a rate card: `fk_billing_statements_rate_card` | DB + Code | 9 |
| R10 | 4 | 3 | `numeric(18,4)`; `ck_billing_rate_cards_rates` | DB | - |
| R11 | 4 | 3 | `ck_billing_rate_cards_first_day`; `uq_billing_rate_cards_tenant_from`; "future only" in code | DB + Code | 15 |
| R12 | 5 | 3, 4.8 | `trg_billing_rate_cards_guard` (no update; delete only before start, Dhaka date) | DB | - |
| R13 | 4 | 3, 14.3 | Rate copy on the statement; composite FK to the same tenant's rate card | DB | 13 |
| R14 | 4 | 2.1 | Separate meters, one `generation_rate` | DB, Open (Q9) | - |
| R15 | 7 | 14.3 | `uq_billing_statements_period_tenant` | DB | - |
| R16 | 7 | 3 | `ck_billing_statements_total`, `ck_billing_statement_lines_shape`; rounding in code | DB + Code | - |
| R17 | 7 | 3 | Total may be negative (no sign check) | DB | 14 |
| R18 | 7 | 4.7 | Period `DRAFT` state | Code | 8 |
| R19 | 8 | 5 | Triggers allow changes until the month is `FINALIZED` | DB + Code | - |
| R20 | 9 | 5 | `ck_billing_periods_finalized` (who and when required); period read `FOR SHARE` | DB + Code, Open (Q14, Q17) | 10, 11 |
| R21 | 9 | 5 | `trg_billing_periods_finalized`, `trg_billing_statements_guard`, `trg_billing_statement_lines_guard` | DB | 12 |
| R22 | 10 | 7.1 | - | Code | - |
| R23 | 10 | 6, 7.1 | - | Code | 18 |
| R24 | 6, 10 | 6, 7.1 | Status comes from `billing_periods.status` | Code | - |
| R25 | 12 | 5, 14.3 | `ck_billing_adjustments_amount`; `trg_billing_adjustments_guard` (no delete) | DB | - |
| R26 | 7, 12 | 5 | `fk_billing_adjustments_statement` (same tenant); `uq_billing_statement_lines_adjustment`; applied once (trigger) | DB | 14 |
| R27 | 12 | 5 | - | Process, Open (Q7) | - |
| R28 | 11 | 7.3 | `usage_events.client_reference`, `detail`, `billable`; no personal-data columns | DB + Code | 16 |
| R29 | 13 | 7.2 | - | Code (late-usage query missing, gap 2) | - |
| R30 | 14 | 6, 10 | Runtime grants in `010`-`012` | Code | 17 |
| R31 | 14 | 10 | `audit_logs` (existing `007`) | Code | - |
| R32 | 10 | 10 | - | Code | - |
| Section 8 (visibility) | 15 | 8 | - | Code, Open (Q16) | 8 |

---

## 4. PRD open questions → earlier design questions

The design (section 15.3) asked these to HoE before the PRD existed. The PRD now owns the product and commercial ones.

| PRD | Design section 15 | Note |
|---|---|---|
| Q1 BRD addendum | - | New, from the BRD review |
| Q2 Device check vs platform check | - | New, from the BRD review |
| Q3 Charge invalid outcomes | Question 2 | |
| Q4 Pre-check damaged QRs | Question 3, C14, F14 | |
| Q5 One price list, service choice | Questions 4 and 17, F15 | |
| Q6 First partial month | Question 5, C2 | |
| Q7 30-day dispute window | Question 7 | |
| Q8 Credit when the platform is at fault | Question 6, C3 | |
| Q9 Static and dynamic price | Question 16, C15, F2 | |
| Q10 BDT only | Question 8 | |
| Q11 Bangladesh Bank fee rules | Question 9 | |
| Q12 Retention beyond 13 months | Question 10 | |
| Q13 VAT, Finance system | Question 11, F5 | |
| Q14 Who may approve and adjust | Question 12, C5, F9 | |
| Q15 Self-service, billing screen | Question 13, F6, F7 | |
| Q16 Who watches warnings | Question 15, C10 | |
| Q17 Months approved in order | Billing tactical doc, open question 1 | |

Engineering-only, not in the PRD: design question 1 (canonical repo, C6) and question 14 (certificate to tenant binding, C7).

---

## 5. Known gaps

| # | Gap | Affects | Where it is tracked |
|---|---|---|---|
| 1 | `Idempotency-Key` and `requestId` are still optional in code; the design makes them required | R4 | Discovery, observation 4 |
| 2 | No query yet for usage recorded after its month was approved | R29 | Discovery, open question 6 |
| 3 | It is unclear whether `REQUEST_STALE` ever reaches Metering (400 vs verdict) | R3 | Design C8; discovery open question 3 |
| 4 | Approver and adjuster are free-text names, not logins | R20, R31 | Design C5; PRD Q14 |
| 5 | The dispute window is a process rule; the system does not block late adjustments | R27 | Billing tactical doc |
| 6 | The runtime role migration does not exist; grants apply only if the role exists | R30 | Database design, appendix গ (open items), item 5 |
