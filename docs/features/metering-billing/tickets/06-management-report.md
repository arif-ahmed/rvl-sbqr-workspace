# 06: Management Report — Monthly Volume & Revenue

**What to build:** Platform management can see monthly volume and revenue per institution, trends against previous months, and visibility into late usage and pending corrections. This report helps leadership understand the business and alerts to billing delays.

**Blocked by:** 03 (finalized statements), 05 (adjustments to understand pending corrections).

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Monthly report shows per institution (sorted largest first by revenue): volume (quantity of all operations), revenue (total BDT), percentage share of total
- [ ] Change against previous month: volume change, revenue change
- [ ] Daily totals for the reported month: quantity and revenue per day
- [ ] Status of each month: which are Provisional, Draft, or Finalized
- [ ] Non-billable institutions' usage (institutions with no rate card for the month): shown separately
- [ ] Adjustments waiting for the next statement: per institution, reason summaries
- [ ] Usage that arrived after its month was approved (late usage): tracked per month, helps understand data pipeline delays
- [ ] Infrastructure logs: INFO-level when report is generated; DEBUG-level for query performance on large datasets

## Related Stories

PRD: R29  
User stories: 13
