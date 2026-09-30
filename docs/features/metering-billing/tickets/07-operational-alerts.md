# 07: Operational Alerts — Warn on Delays & Late Approval

**What to build:** Billing team is alerted when usage is delayed (incomplete outbox messages) or when a previous month has not been approved by the 5th. This keeps the billing workflow on track.

**Blocked by:** 03 (billing period state machine) to detect late approval; 01 (completeness checks) to detect delayed usage.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Alert shown in billing team interface when any usage for a month is still `PENDING` or `DEAD` in outbox_messages
- [ ] Alert shown if the previous calendar month is not yet Finalized by the 5th of the current month
- [ ] Alerts are persistent and visible until the condition clears (usage completes or month is approved)
- [ ] Infrastructure logs: INFO-level alert triggers; DEBUG-level for alert evaluation logic

## Related Stories

PRD: Section 8 (visibility), Q16  
User stories: 15
