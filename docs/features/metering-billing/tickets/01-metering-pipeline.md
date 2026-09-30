# 01: Metering Pipeline — Record QR Usage Events

**What to build:** Billing foundation: consume QR generation and validation events from the outbox, deduplicate them, and record them as usage_events so billing can count what was actually used. The system detects when a month's usage is incomplete and blocks approval until all events arrive.

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Dispatcher consumes `outbox_messages` (event type `QrGenerated`, `QrValidated`) and creates rows in `usage_events`
- [ ] Idempotency: if the same request (same `client_reference`) is sent twice, it creates only one `usage_event` (via `uq_usage_events_source` unique constraint)
- [ ] All operations recorded with correct Bangladesh time (`occurred_at` in Dhaka zone, not UTC)
- [ ] Non-chargeable operations recorded with `billable = false` (refused requests, protocol rejections like `REQUEST_STALE`)
- [ ] Chargeable operations recorded with `billable = true` based on verdict conclusiveness (per ADR 0002: all definite answers are charged)
- [ ] System can detect when a month's usage is still incomplete: any `PENDING` or `DEAD` outbox message up to the period end blocks draft generation
- [ ] Completeness check is visible to billing team (used by T3 to decide when to auto-generate drafts)
- [ ] Infrastructure logs: DEBUG-level events for event consumption, deduplication detection, timezone conversion, completeness checks

## Related Stories

PRD: R1, R2, R3, R4, R5, R6  
User stories: 1, 2, 3
