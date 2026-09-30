---
status: accepted
---

# Outbox between modules, plain transactions inside a module

Metering needs to know about every successful QR generation and every validation verdict, but those facts are owned by the QrGeneration and Verification modules. We decided that producers write an integration event to a shared `outbox_messages` table **in the same transaction** as their business row, a host `BackgroundService` dispatches it at-least-once, and Metering records it idempotently into its own `usage_events` (unique on `(source_type, source_id)`). Inside a module (e.g. Billing's draft/finalize) we use ordinary DB transactions, and Billing pulls quantities from Metering through `IMeteringQueries` instead of subscribing to events. This keeps the modular-monolith rule (no module reads another's tables), never loses usage (a Metering outage only delays it), and maps directly onto a message broker later without changing handlers.

## Considered Options

- **Billing/Metering reads `qr_generations` / `qr_validations` directly** (the first v1 draft). Simplest, but couples commercial rules to another module's schema; an upstream schema change silently breaks billing, and it blocks extracting either side into a service.
- **QR handlers insert into `usage_events` directly.** Couples the payment-critical QR path to Metering's schema and rules; a Metering bug would fail QR issuance.
- **Publish an in-memory event after commit.** A dual write: a crash between commit and publish issues a QR that is never billed, and the loss cannot be reconciled.
- **A message broker (RabbitMQ/Kafka) now.** Operational cost with no benefit inside one process; the outbox and versioned event names already make this a later swap.

## Consequences

- Every QR transaction does one extra INSERT; if the outbox insert fails, the QR operation fails with it (same transaction, by design).
- Usage reaches Metering a few seconds late, so current-month figures are provisional.
- Dead-lettered messages must be requeued by an operator, and they block finalization of their billing period (usage completeness gate).
- Every event handler must be idempotent.
