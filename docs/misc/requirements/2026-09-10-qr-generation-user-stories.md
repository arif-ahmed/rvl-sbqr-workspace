# Backlog: QR Generation (Signed BanglaQR P2P Payload Issuance)

**Format**: User Stories (3 C's — Card / Conversation / Confirmation; validated against INVEST)
**Total stories**: 7
**Source**: `docs/requirements/2026-09-10-qr-generation.md` (FR), `docs/bb-banglaqr-p2p-specification.md`, `AGENTS.md`
**Estimated total effort**: implemented and in the codebase already — sizes below reflect story complexity, not remaining work, for backlog/traceability purposes.

### Stories

#### Story 1: Generate a static QR code
**As a Recipient Financial Institution (RFI) application integrating the platform SDK, I want to request a static BanglaQR P2P code for a recipient, so that I can display or share a reusable code the sender can scan and pay against without a pre-fixed amount.**

Acceptance Criteria:
- [ ] A request with valid recipient name, city, and PAN, authenticated with the tenant's assigned credentials, returns a signed QR payload with Point of Initiation Tag 01 = "11".
- [ ] The returned payload has no amount field (Tag 54 absent).
- [ ] Optional fields (postal code, customer label, purpose of transaction) are included in the payload when supplied.
- [ ] The response includes the QR payload string, a content hash, the QR type, and the signing key version used — returned once, not retrievable again afterward.
- [ ] The payload passes EMVCo TLV round-trip and CRC validation against the BanglaQR spec's golden vectors.

Priority: P0 | Effort: M | Dependencies: none

---

#### Story 2: Generate a dynamic QR code with a fixed amount
**As an RFI application, I want to request a dynamic BanglaQR P2P code that carries a specific transaction amount, so that the sender scans a code already bound to the exact amount owed.**

Acceptance Criteria:
- [ ] A request with valid recipient fields and a `transactionAmount`, authenticated with the tenant's assigned credentials, returns a signed QR payload with Tag 01 = "12" and Tag 54 set to the exact amount string supplied.
- [ ] Omitting `transactionAmount` on this endpoint is rejected before any payload is built (see Story 4).
- [ ] The amount is carried as a string end-to-end — never parsed to a numeric type, never reformatted between request and signed payload.
- [ ] The response includes the QR payload string, a content hash, the QR type, and the signing key version used.

Priority: P0 | Effort: M | Dependencies: none

---

#### Story 3: Signing uses the platform's Ed25519 vault, never a caller-supplied key
**As the platform (on behalf of both the RFI and the Sender/Payer FI who will later verify), I want every QR signed only with the calling tenant's own custodied private key, resolved server-side, so that no caller can get a QR signed with the wrong institution's identity.**

Acceptance Criteria:
- [ ] The signing key is looked up solely by the authenticated tenant's identity; no request field can name, select, or influence which key signs.
- [ ] The signature is produced with Ed25519 and embedded into the payload per the BanglaQR spec's signature tag placement.
- [ ] The CRC (Tag 63) is computed last, over the final payload including the embedded signature.
- [ ] The private key material never appears in the response, logs, or any persisted record.

Priority: P0 | Effort: M | Dependencies: Key Custody module (already implemented)

---

#### Story 4: Reject invalid or incomplete requests before building any payload
**As an RFI application developer, I want clear, specific validation errors when my request is invalid or incomplete, so that I can fix the call without guessing what went wrong.**

Acceptance Criteria:
- [ ] Missing or empty required fields (recipient name, city, PAN) return 400 with a field-level error; no payload is built or signed.
- [ ] A non-numeric or over-length `transactionAmount` on the dynamic endpoint returns 400.
- [ ] A `transactionAmount` supplied on the **static** endpoint returns 400 — a static QR must never carry Tag 54. Enforced via `JsonSerializerOptions.UnmappedMemberHandling = Disallow` (`Program.cs`, 2026-09-10) rejecting the unrecognized property globally.
- [ ] Recipient fields that exceed their EMVCo TLV tag's byte-length limit are rejected, not silently truncated.

Priority: P0 | Effort: S | Dependencies: none

---

#### Story 5: Fail closed when no usable signing key or the signing operation fails
**As the platform, I want QR generation to be rejected outright — never issued with a fallback, default, or partial signature — whenever the tenant's signing key is unavailable or the signing operation itself fails, so that a forged or unsigned QR can never reach a sender.**

Acceptance Criteria:
- [ ] A tenant with no ACTIVE signing key gets a 422 `KEY_NOT_ACTIVE` response; no payload is built or returned.
- [ ] A tenant whose key was rotated or revoked is treated identically to "no key" — never signed with a stale key.
- [ ] If the signing call itself throws (library, vault, or custody failure), the request fails with a reject response; no partial, unsigned, or placeholder payload is ever returned.
- [ ] Every rejection in this story is captured by Story 7's audit requirement.

Priority: P0 | Effort: S | Dependencies: Story 3

---

#### Story 6: Prevent duplicate signed QR codes on retry
**As an RFI application developer, I want to pass an idempotency key on my request, so that a retried call (e.g., after a network timeout) doesn't produce two distinct signed QR codes for what I meant as one request.**

Acceptance Criteria:
- [ ] A request that includes an `Idempotency-Key` header succeeds normally on first use.
- [ ] Reusing the same `Idempotency-Key` for the same tenant on a subsequent request returns 409 `DUPLICATE_IDEMPOTENCY_KEY`, not a second successful generation and not a silently replayed payload (the payload isn't stored, so no prior result exists to replay).
- [ ] The idempotency key is scoped per tenant — two different tenants may each use the same key value independently.
- [ ] Omitting the idempotency key is allowed; each such request is treated as independent.

Priority: P1 | Effort: S | Dependencies: none

---

#### Story 7: Every generation attempt is audited, success or failure
**As a compliance/security reviewer, I want every QR generation attempt logged with enough detail to reconstruct who requested what and when — without exposing the private key or the full QR payload — so that issuance activity is fully accountable.**

Acceptance Criteria:
- [ ] Every successful generation writes an audit entry with a correlation id, tenant identity, QR type, and signing key version.
- [ ] Every rejected generation (validation failure, no key, signing failure, duplicate idempotency key) also writes an audit entry with the same correlation-id traceability.
- [ ] The audit entry never contains the private key, the raw QR payload, or other content the "no raw payloads/keys in logs" rule (C19) prohibits.
- [ ] The generation record and its audit entry are written atomically with the signing outcome — no state where a QR was issued but not recorded, or vice versa.

Priority: P0 | Effort: S | Dependencies: Stories 1, 2, 5

---

### Story Map

**Must-have (P0) — core issuance + fail-closed guarantees:**
Story 1 (static) → Story 2 (dynamic) → Story 3 (Ed25519 signing via vault) → Story 4 (input validation) → Story 5 (fail-closed on key/signing failure) → Story 7 (audit trail)

**Should-have (P1) — operational safety net:**
Story 6 (idempotency)

**Nice-to-have / follow-on (not in this backlog):**
- Direct mobile-app-to-platform calling without an RFI backend intermediary (blocked, refs B1/B2 — separate FR once unblocked)
- Numeric SLA targets for latency/throughput (post 22-Oct load/soak test)

### Technical Notes

- All seven stories are implemented in `src/Modules/QrGeneration`, `src/Modules/KeyCustody`, and `src/Modules/Tenancy` — this backlog exists for traceability against the FR, not as a build plan.
- Story 4's static-amount acceptance criterion is enforced globally, not per-endpoint: `Program.cs` sets `JsonSerializerOptions.UnmappedMemberHandling = Disallow` on the controllers' JSON options, so *any* endpoint receiving a JSON property its DTO doesn't declare now returns 400 — not just `/qr/generate/static`. `GenerateStaticQrRequest` itself was left unchanged (it already had no `TransactionAmount` field); the fix is in strict deserialization, not the request model shape.
- Cross-cutting: rate limiting (C7) and payload/request size limits (C18) apply to this endpoint but are gateway/infrastructure concerns, not covered by these module-level stories.
