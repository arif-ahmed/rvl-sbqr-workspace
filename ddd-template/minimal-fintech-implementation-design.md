# Minimal Production-Grade Fintech Implementation Design

Analyse the provided:
1. **Frozen Strategic DDD baseline**
2. **Approved Tactical DDD design**
3. **Existing codebase**

for the Bounded Context: `<BOUNDED_CONTEXT_NAME>`

**Goal:** Produce a minimal, pragmatic, production-grade implementation design and implementation plan that fits the existing architecture. Do not redesign the Strategic or Tactical DDD model unless there is a genuine blocking contradiction.

---

## Core Philosophy

Prefer the simplest implementation that preserves:
*   Business correctness
*   Financial correctness
*   Security
*   Tenant isolation
*   Auditability
*   Reliability
*   Transactional consistency
*   Idempotency
*   Maintainability

Fintech-grade does **NOT** mean adding enterprise patterns everywhere. Prefer explicit, boring, understandable code over unnecessary abstraction. 

Add complexity only when it solves a current and demonstrated problem involving: correctness, security, auditability, concurrency, reliability, transactional consistency, cross-context delivery, or regulatory/operational traceability.

*   **Design only for confirmed current requirements.**
*   **Do not introduce speculative architecture for hypothetical future requirements.**
*   **Do not introduce patterns merely because they are associated with:** DDD, Clean Architecture, CQRS, event-driven architecture, or fintech systems.

---

## Existing Architecture

Each Bounded Context follows this structure:

```text
src/Modules/<BoundedContext>/
├── SBQR.Modules.<BoundedContext>.Api
├── SBQR.Modules.<BoundedContext>.Application
├── SBQR.Modules.<BoundedContext>.Contracts
├── SBQR.Modules.<BoundedContext>.Domain
└── SBQR.Modules.<BoundedContext>.Infrastructure
```

Preserve this structure. Do not introduce additional projects or layers unless there is a concrete requirement that cannot be handled cleanly with the existing structure. Prefer patterns already used consistently by neighbouring modules.

---

## 1. Map the Tactical Model to the Existing Architecture

Determine where each required concept belongs. Do not invent additional domain objects or abstractions beyond the approved Tactical DDD model.

### Domain (`SBQR.Modules.<BC>.Domain`)
Contains only real domain concepts required by the Tactical Design:
*   Aggregate / Aggregate Root (only if explicitly justified)
*   Entity
*   Value Object
*   Business rules / invariants
*   Domain Event (only if genuinely required)
*   Domain Service (only if genuinely required)

**Do not place:** MediatR handlers, integration event consumers, DTOs, API models, EF Core configuration, DbContext, or infrastructure services here. If the Tactical Design concludes that no Aggregate, Repository, Domain Event, or Domain Service is required, preserve that decision.

### Application (`SBQR.Modules.<BC>.Application`)
Contains use-case orchestration:
*   Commands and Queries
*   Handlers
*   Integration event consumers
*   Application-level coordination

Business rules should remain in the domain model when the Tactical Design places them there. For simple append-only or straightforward persistence flows, direct use of the module `DbContext` is acceptable. Do not introduce a repository purely to hide EF Core.

### Contracts (`SBQR.Modules.<BC>.Contracts`)
Contains only contracts intentionally exposed across Bounded Context boundaries:
*   Integration events
*   Shared integration contract DTOs
*   Cross-context query interfaces
*   Published Language enums/types

**Dependency Rule:** Other Bounded Contexts should depend on Contracts, not Domain or Infrastructure.
```text
Preferred:  Consumer.Application -> Producer.Contracts
Avoid:      Consumer -> Producer.Domain
Avoid:      Consumer -> Producer.Infrastructure
```

### Infrastructure (`SBQR.Modules.<BC>.Infrastructure`)
Contains technical implementation details:
*   DbContext, EF Core configuration, persistence, migrations
*   Repository implementation (only if actually required)
*   Query implementation
*   External integrations
*   Module dependency registration

### API (`SBQR.Modules.<BC>.Api`)
Contains delivery concerns only:
*   Controllers / endpoints
*   API request and response models
*   HTTP-specific validation
*   Authentication / authorisation integration
*   API-specific error mapping

---

## 2. Identify Required Code Changes

Inspect the existing repository first. Reuse existing conventions wherever practical. Identify only the files that genuinely need to be created, modified, or removed.

For every proposed change provide: **Project**, **Path**, **Class/Type**, **Responsibility**, and **Reason it is required**. Do not create files merely to satisfy a textbook layer structure or introduce empty abstractions.

---

## 3. Define the Main Runtime Flows

Describe each important use case end-to-end using the simplest flow that represents the implementation. Do not include stages that do not actually exist. 

**Example Flow:**
`Trigger` → `API / Integration Event` → `Application Handler` → `Domain Behaviour` → `Persistence` → `Commit` → `External Integration Event`

**For integration-event consumers:**
`Producer BC` → `Integration Contract` → `Consumer Handler` → `Business Rule` → `Persistence` → `Success / Retry / Dead Letter`

*(Explicitly document idempotency behaviour).*

---

## 4. Persistence Design

Define only persistence details required for correctness and implementation:
*   Records/entities persisted and DbContext changes
*   EF Core configuration and required database constraints
*   Indexes required for correctness or known important queries
*   Insert/update/delete behaviour and concurrency requirements
*   Immutable records and transaction boundaries

Use the database to enforce invariants that cannot be safely protected in application memory. 
*Prefer DB unique constraints over `SELECT → check → INSERT`.* 
Do not introduce distributed locking unless demonstrated necessary. Do not create a generic repository layer.

---

## 5. Cross-Context Integration

For each interaction identify:
*   Producer BC & Consumer BC
*   Contract owner & Contract location
*   Integration event or query name
*   Minimum required fields & Versioning
*   Idempotency key / business identity
*   Unknown-value, failure, and retry behaviour

Keep Integration Events separate from Domain Events. Do not turn every internal event into an Integration Event.

---

## 6. Dependency Validation

Validate the existing Clean Architecture boundaries:
*   `Api` → `Application` → `Domain`
*   `Infrastructure` → `Application / Domain`
*   `Cross-context dependency` → `Contracts only`

Flag dependency leakage, especially: one BC referencing another's Domain or Infrastructure, direct cross-context database/table access, or business rules in API/Infrastructure.

---

## 7. Fintech Production Correctness Review

For each applicable concern describe: **Risk → Protection → Implementation location → Required test**

Check for: idempotency, duplicate delivery, transaction atomicity, concurrency, unique constraints, retry safety, dead-letter behaviour, fail-closed handling, immutable financial/business facts, audit trail, tenant isolation, auth boundary, deterministic financial calculations, decimal precision/rounding, time/timezone semantics, unknown enum/event values, contract versioning, late/out-of-order events, observability, and migration safety.

---

## 8. Simplicity Review

For every proposed abstraction or pattern ask: *"What concrete current problem does this solve?"* If there is no clear answer, remove it.

Challenge unnecessary use of: Repositories, Unit of Work, Domain Services, Domain Events, generic base classes, single-implementation interfaces, wrapper types without behaviour, specification patterns, generic result frameworks, extra CQRS models, distributed locks, caching, or microservice-style infrastructure.

---

## 9. Testing Plan

Define the minimum production-grade tests:
*   **Unit Tests:** Business rules, billability/pricing rules, state transitions, deterministic mapping, domain behaviour. (Do not test trivial properties).
*   **Integration Tests:** EF Core mappings, DB constraints, duplicate handling, transactions, integration-event handlers, idempotency, cross-context contracts. (Prefer real database integration tests over mocked persistence for critical paths).
*   **Architecture Tests:** Boundary protections (e.g., cross-context dependencies use Contracts).

---

## 10. Implementation Plan

Produce a small, ordered implementation plan. Each task must be independently understandable, reviewable, and small enough for a focused commit. 

**Example Order:**
1. Cross-context contracts
2. Domain types
3. Persistence model/configuration
4. Database constraints/migration
5. Application handler
6. Integration-event consumer
7. Query path
8. Unit tests
9. Database/integration tests
10. Architecture tests
11. Final production-correctness review

---

## Expected Output Format

### 1. Current Architecture Assessment
Briefly describe how this BC fits the existing solution.

### 2. Implementation Mapping
| Concern | Project | Location | Reason |
| :--- | :--- | :--- | :--- |
| | | | |

### 3. Files to Create
| Project | Path / Type | Responsibility |
| :--- | :--- | :--- |
| | | |

### 4. Files to Modify
| File | Change | Reason |
| :--- | :--- | :--- |
| | | |

### 5. Runtime Flows
*(Show only important flows)*

### 6. Persistence Requirements
*(Document required persistence behaviour, constraints, and transactions)*

### 7. Integration Contracts
| Contract | Owner | Consumer | Location | Purpose |
| :--- | :--- | :--- | :--- | :--- |
| | | | | |

### 8. Dependency Review
*(Show allowed dependencies and any detected violations)*

### 9. Production Correctness
| Risk | Protection | Location | Test |
| :--- | :--- | :--- | :--- |
| | | | |

### 10. Simplicity Review
*(List unnecessary abstractions to remove. Keep if they protect a real business concern.)*

### 11. Testing Plan
*(List only meaningful production-grade tests)*

### 12. Ordered Implementation Plan
*(Provide implementation tasks in dependency order)*

### 13. Blocking Decisions
*(List decisions preventing implementation. If none: "Ready for implementation.")*

---

## Strict Constraints

**Do NOT:**
*   Redesign the approved Strategic DDD or Tactical DDD without a blocking contradiction.
*   Introduce new Bounded Contexts.
*   Force Aggregates, Aggregate Roots, Repositories, Domain Events, Domain Services, or Unit of Work abstractions.
*   Create wrappers around every primitive or add interfaces merely for test mocking.
*   Add CQRS complexity without a real read/write need, or EDA where a direct interaction suffices.
*   Introduce infrastructure for hypothetical scale, create generic frameworks, or optimise prematurely.

**Prioritise:**
`Correctness > Security > Auditability > Reliability > Simplicity > Architectural purity.`

The final implementation should be easy for another engineer to understand, review, operate, and safely change. Use the existing codebase as the primary reference. When multiple approaches are valid, choose the simplest production-safe solution with the fewest moving parts.