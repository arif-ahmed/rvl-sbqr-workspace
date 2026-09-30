# Jira Facts & Guidelines

**Last Updated:** 2026-10-01  
**Project:** OA (One Authenticator) - SBQR Metering-Billing Implementation

---

## Story Points Definition

Story points are abstract units representing the complexity and effort required to complete a task. They are **not direct time estimates** but rather relative measures of work.

### Story Point Scale

| Points | Effort | Time Estimate | Complexity | Examples |
|--------|--------|---------------|-----------|----------|
| 1 | Very Small | 2-4 hours | Simple, well-defined | Data export, simple UI change |
| 2 | Small | 4-8 hours | Straightforward logic | Single feature, minor integration |
| 3 | Medium | 0.5-1 day | Moderate complexity | Component with state management |
| 6 | Large | 1 day | Significant logic & testing | Data pipeline, complex validation |
| 8 | Extra Large | 1.5 days | High complexity & risk | Multi-step workflow, state machine |

### Project Velocity

**Metering-Billing Implementation (OA-50 to OA-56):**
- Total story points: **21 pts** (main implementation)
- Total timeline: **5 working days**
- **Velocity: ~4 story points per day**
- **1 story point ≈ 2-4 hours**

### Current Implementation Story Points

| Ticket | Task | Points | Days | Velocity |
|--------|------|--------|------|----------|
| OA-50 | T01: Metering Pipeline | 6 | 1.0 | 6 pts/day |
| OA-51 | T02: Rate Cards | 3 | 1.0 | 3 pts/day |
| OA-52 | T03: Billing Periods | 8 | 1.5 | 5.3 pts/day |
| OA-53 | T04a: Statements | 1 | 0.5 | 2 pts/day |
| OA-54 | T04b: Audit Export | 1 | 0.5 | 2 pts/day |
| OA-55 | T05: Corrections | 1 | 0.5 | 2 pts/day |
| OA-56 | T06: Reports | 1 | 0.5 | 2 pts/day |
| **Total** | | **21** | **5.0** | **4.2 pts/day avg** |

---

## Traceability Sub-tasks

**15 Traceability/Documentation Sub-tasks (OA-57 to OA-71):**
- Story points: **23 pts** (documentation & requirement mapping)
- Linked to main tickets via descriptions
- Provide complete PRD → User Story → Ticket traceability

**Total Project Scope: 44 story points**
- Implementation: 21 pts (5 days)
- Traceability: 23 pts (2 days)

---

## Access & Update Permissions

### Authorized Users for Task Updates

Only the following user is authorized to modify or update tasks in the OA project for metering-billing implementation:

| Name | Email | Account ID | Role | Permissions |
|------|-------|------------|------|-------------|
| **Arif Ahmed** | arif.ahmed@reliefvalidation.com.bd | `712020:c0c94608-50f2-446e-932c-ac90b0c6a607` | Assignee & Owner | Full update rights on OA-50 to OA-71 |

### Update Restrictions

- ⚠️ **Do not modify** tickets OA-50 to OA-71 without explicit approval from Arif Ahmed
- ⚠️ **Do not add/remove** sub-tasks without documented justification
- ⚠️ **Do not change** start/due dates or story point estimates without team consensus
- ⚠️ **Do not** change status without corresponding progress update

### Workflow Status Progression

| Status | Meaning | Transition Rules |
|--------|---------|------------------|
| **To Do** | Not started | Initial status for all tickets |
| **In Progress** | Currently being worked on | Update when development starts |
| **In Review** | Code/spec review pending | After development, before testing |
| **Testing** | QA/testing in progress | After code review approval |
| **Done** | Completed & verified | After testing + approval |

---

## Ticket Structure

### Main Implementation Tickets (7 tickets: OA-50 to OA-56)

Each contains:
- **Title**: T0X: Feature Name
- **Description**: What to build (user-centric language)
- **Story Points**: Complexity & effort estimate
- **Timeline**: Start date → Due date
- **Status**: To Do (initially)
- **Assignee**: Arif Ahmed
- **Labels**: billing, sbqr, [domain]
- **Priority**: Highest (P0) or High (P1)
- **Blocking relationships**: Dependencies on other tickets

### Traceability Sub-tasks (15 tasks: OA-57 to OA-71)

Each contains:
- **Title**: T0X-SYY: Story Title | Feature Name
- **Story**: User story reference (S1-S15)
- **PRD**: Requirement mappings (R1-R32)
- **Description**: Structured layout
  - Requirement Mapping
  - What to implement
  - Acceptance Criteria (checkbox format)
- **Story Points**: 1-2 pts per sub-task
- **Parent Task**: Link in description (e.g., "Parent Task: OA-50")
- **Labels**: Traceability, [domain]

---

## Dependency Chain

```
START (Oct 1)
  ↓
OA-50 (T01: Metering)
  ↓ Depends on T01
OA-51 (T02: Rates)
  ↓ Depends on T02
OA-52 (T03: Billing Periods)
  ↓ Depends on T03
  ├─→ OA-53 (T04a: Statements)
  ├─→ OA-54 (T04b: Audit Export)  [Parallel]
  ├─→ OA-55 (T05: Corrections)
  ↓ All depend on T03/T05
OA-56 (T06: Reports)
  ↓
END (Oct 6)
```

---

## Integrated Stories

Some user stories span multiple tickets with integrated implementations:

| Story | Type | Tickets | Implementation |
|-------|------|---------|-----------------|
| **S14** | Audit Logging | T02, T03, T05 | Integrated across OA-51, OA-52, OA-55 |
| **S15** | Warnings/Alerts | T03 | Integrated into OA-52 |

These are documented as sub-task OA-71 (Story 14) and OA-70 (Story 15).

---

## Timeline Overview

**5 Working Days: October 1-6, 2026**

| Day | Ticket | Task | Duration |
|-----|--------|------|----------|
| **Oct 1-2** | OA-50 | T01: Metering Pipeline | 1 day |
| **Oct 2-3** | OA-51 | T02: Rate Cards | 1 day |
| **Oct 3-5** | OA-52 | T03: Billing Periods | 1.5 days |
| **Oct 5-6** | OA-53, 54, 55 | T04a/b + T05 (parallel) | 0.5 day each |
| **Oct 6** | OA-56 | T06: Reports | 0.5 day |

---

## Key Metrics

- **Total User Stories**: 15 (S1-S15)
- **Total PRD Requirements**: 32 (R1-R32)
- **Main Tickets**: 6 (OA-50 to OA-56)
- **Sub-tasks**: 15 (OA-57 to OA-71)
- **Story Points (Implementation)**: 21
- **Story Points (Traceability)**: 23
- **Total Story Points**: 44
- **Timeline**: 5 working days (Oct 1-6, 2026)
- **Average Velocity**: 4.2 story points/day
- **Team**: 1 developer (Arif Ahmed)

---

## Notes

- All tasks are assigned to Arif Ahmed
- All tasks are in "To Do" status until development begins
- Sub-tasks are linked via descriptions (not Jira sub-task hierarchy due to project configuration)
- Blocking relationships must be respected; parallel work can only start once dependencies are complete
- Story point estimates are based on complexity and include testing time
- Dates are calendar dates in Bangladesh timezone (Asia/Dhaka)

---

## Contact & Questions

For questions about task assignments, story points, or timeline, contact:
- **Arif Ahmed** | arif.ahmed@reliefvalidation.com.bd | Account ID: 712020:c0c94608-50f2-446e-932c-ac90b0c6a607
