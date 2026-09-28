# Requirements: QR Generation (Signed BanglaQR P2P Payload Issuance)

> **Date:** 2026-09-10
> **Type:** feature (documenting an already-implemented capability)
> **Source:** Feature ask (verbal brief, this session) + `docs/bb-banglaqr-p2p-specification.md` + `AGENTS.md` (constraints C1–C23, A1–A12) + current implementation in `src/Modules/QrGeneration`, `src/Modules/KeyCustody`, `src/Modules/Tenancy`

## Summary

A Recipient Financial Institution (RFI) requests a signed BanglaQR P2P code — static or dynamic — from the platform. The platform validates the request, builds an EMVCo-TLV payload per the BanglaQR P2P spec, signs it with the RFI's Ed25519 private key held in the platform's Key Custody vault, and returns the finished QR string once. The RFI's application then renders it for the sender/payer to scan. The Sender/Payer FI later verifies the signature against the RFI's public key in the Bangladesh Bank Trust Store (a separate, already-scoped Verification capability — out of scope here).

## Problem & Motivation

Every BanglaQR P2P code a sender scans must be provably issued by the institution it claims to represent, or a forged-accept becomes a direct money-loss event (see `AGENTS.md` charter). Recipient institutions do not hold or manage their own signing keys; the platform is the trusted custodian that signs on their behalf so no RFI ever has to handle private key material. Without this endpoint, RFIs have no compliant way to produce a spec-conformant, cryptographically verifiable QR code.

## Users & Consumers

- **Recipient Financial Institution (RFI) mobile application** — the originally specified caller: integrates the platform's SDK, authenticates with tenant-issued credentials, and calls the QR generation endpoint with recipient and transaction details to obtain a QR code to display, download, or share.
- **Sender/Payer Financial Institution** — downstream consumer of the *output* (the QR code), which it later verifies against the Bangladesh Bank Trust Store. Not a direct caller of this feature.
- **Platform operations / audit** — consumes the audit trail this flow produces (success and failure) for compliance and incident response.

> **Implementation note (current state vs. spec intent):** the endpoint today is implemented and reachable only on the **server-to-server plane** — a tenant RFI backend authenticates via OAuth2 client-credentials with the `qr:generate` scope and calls `POST /v1/qr/generate/static` or `/dynamic` directly. Direct mobile-app-to-platform calling (the SDK-in-mobile-app flow described in the feature ask) is tracked as blocked (internal refs B1/B2) and is deferred past the 10-Sep feature freeze. This FR documents the mobile-app-as-caller model as the target; see **Open Questions** for the interim gap.

## Functional Requirements

| ID  | Requirement | Acceptance Criterion |
|-----|-------------|-----------------------|
| R1  | The RFI application, via the SDK and its assigned tenant credentials, can request generation of a **static** QR (recipient identity only, no amount). | `POST /v1/qr/generate/static` with a valid bearer token scoped `qr:generate` and valid recipient fields returns 201 with a QR payload where Tag 01 = "11" and Tag 54 (amount) is absent. |
| R2  | The RFI application, via the SDK, can request generation of a **dynamic** QR (recipient identity + transaction amount). | `POST /v1/qr/generate/dynamic` with a valid bearer token and a required `transactionAmount` returns 201 with a QR payload where Tag 01 = "12" and Tag 54 carries the exact amount string supplied. |
| R3  | The platform validates every request field server-side before building any payload. | Missing/empty required fields (recipient name, city, PAN) or a non-numeric/over-length transaction amount returns 400 with a field-level error; no payload is built or signed. |
| R4  | Sending a transaction amount on the **static** endpoint is rejected — a static QR never carries Tag 54. | `POST /v1/qr/generate/static` with a `transactionAmount` present in the request body returns 400. |
| R5  | The platform constructs the QR payload in EMVCo TLV format per the BanglaQR P2P spec, with deterministic tag order and byte-exact field encoding. | Golden spec vectors (A7, `AGENTS.md`) round-trip byte-for-byte; the same input at the same instant always produces the same pre-signature bytes (A4). |
| R6  | The platform signs the required payload fields using **Ed25519** with the RFI's private key, resolved solely from the platform's Key Custody vault by tenant identity — never from anything the caller supplies. | The produced signature verifies against the public key on record for that RFI's active signing key; no request field can select or override which key signs. |
| R7  | The digital signature is embedded into the QR payload per the BanglaQR spec's signature tag placement, and the CRC is computed last, over the final payload including the signature. | Finalized payload's CRC (Tag 63) validates independently; recomputing CRC over the payload matches the embedded value. |
| R8  | The completed, signed QR data is returned to the calling application in the response — once — for it to render (display, download, or share). | 201 response includes the full QR payload string, a content hash for correlation, the QR type, and the signing key version used. The payload is not retrievable again afterward (it is not persisted). |
| R9  | If the tenant's signing key is unavailable (none provisioned, revoked, rotated out, or not yet activated), the request is rejected — never signed with a fallback or default key. | No ACTIVE key for the tenant → 422 `KEY_NOT_ACTIVE`; no payload is built or returned. |
| R10 | If the signing operation itself fails for any reason, the request is rejected with no partial artifact returned. | A signing exception → the request fails with a reject response; no QR string, partial payload, or unsigned payload is ever returned to the caller. |
| R11 | If the request contains invalid or incomplete recipient/transaction information, the request is rejected with a specific, actionable error — never a generic failure. | Each validation failure mode (missing field, malformed amount, oversized field) maps to a distinct error the caller's SDK can branch on. |
| R12 | A caller may supply an idempotency key so retried requests (e.g., after a network timeout) do not produce two distinct signed QR codes for what was meant to be one request. | Reusing an `Idempotency-Key` already used by that tenant returns 409, not a second successful generation. |
| R13 | Every generation attempt — success or rejection — is recorded in the immutable audit trail with enough detail to reconstruct who requested what and when, without ever including the private key or full QR payload. | An audit entry exists for every call to the endpoint, keyed by a correlation id, tagged with tenant, QR type, and signing key version. |

## Non-Functional Requirements

NFRs for this endpoint are governed by the existing platform-wide gates in `AGENTS.md` rather than feature-specific numbers invented here:

- **C7** — per-tenant rate limiting (burst + sustained) applies to this endpoint; over-limit returns 429. Enforced at the gateway, not in this module's code.
- **C18** — request/payload size caps, parse timeout, bounded field lengths; over-limit returns 413/400, never a hang.
- **C2 / C17** — Ed25519 signing key must originate from an approved custody backend; the private key never leaves Key Custody, and every signed artifact carries a key version (`kid`-equivalent).
- **C12** — the endpoint is reachable only over TLS 1.3 (1.2 floor); no plaintext HTTP.
- **A3** — the generation record and its audit entry are written atomically with the signing outcome — no state where a QR was issued but not recorded, or vice versa.

No additional numeric SLA targets (latency, throughput) are set in this document — if the team wants explicit p95/p99 or req/s targets, that is a separate decision to make against real load-test data (soak test is scheduled 22 Oct per `AGENTS.md`).

## Behaviors & Domain Rules

**Static vs. dynamic is a hard fork, not a shared field toggle.**
- Static (Tag 01 = "11"): recipient identity only. No amount ever. Reusable for many transactions; the sender types in the amount at pay time.
- Dynamic (Tag 01 = "12"): recipient identity **and** a required transaction amount (Tag 54). Amount is a string end-to-end — never parsed to a float — so no rounding or precision drift can occur between issuance and verification (A8).

**The signing key is resolved by tenant identity, never by request content.**
The caller cannot name, select, or influence which private key signs its payload. The platform looks up the calling tenant's currently ACTIVE key. This is the trust boundary that makes a forged-accept impossible via this endpoint: an RFI (or an attacker impersonating one) cannot get the platform to sign with someone else's key.

**Fail closed, every time, no exceptions.**
Three independent failure classes all resolve to "no QR issued, reject clearly": (1) invalid/incomplete request, (2) no usable signing key, (3) signing operation itself throws. None of these produce a partially-built or unsigned payload returned to the caller.

**The QR payload itself is never stored.**
Only generation *metadata* (tenant, QR type, key version used, idempotency key) is persisted — not the payload bytes or a hash of them. The signature and CRC embedded in the returned string are the integrity guarantee; nothing server-side needs to "remember" the QR to prove it was legitimately issued (that's what the audit log + Trust Store verification path is for).

**Why these rules matter:**
- Never trusting a request-supplied key (R6) is what stops a forged-accept — without it, a compromised or malicious caller could get the platform to sign fraudulent QR content with an arbitrary key.
- Amount-as-string (R2/A8) prevents a whole class of floating-point/formatting bugs where the signed amount and the displayed amount silently diverge.
- CRC-computed-last (R7) matters because if CRC were computed before the signature is embedded, a payload could be truncated/modified between signing and finalization without detection.

**Common mistakes a developer would make on a first attempt:**
- Trying to let the request specify which key or key version signs — must always come from tenant-scoped custody lookup only.
- Computing the CRC before inserting the signature tag, instead of after — breaks the tamper-evidence chain.
- Persisting the QR payload "just in case" — violates the never-persisted rule and increases the leak surface for something that's supposed to be single-use output.
- Treating the transaction amount as a number anywhere in the pipeline instead of a string — introduces reformatting risk between sign and verify (A8).

## Edge Cases & Failure Modes

| Scenario | Decision | Rationale |
|---|---|---|
| Tenant has no signing key provisioned yet | Reject with a clear "key not active" error; no QR issued | Fail closed (A1, A12) — never default to an unsigned or platform-shared key |
| Tenant's signing key was rotated/revoked mid-flow | Reject the same way as "no key" | The platform must never sign with a stale or revoked key even momentarily |
| Signing library/HSM throws during the sign call | Reject; no partial payload returned | Never leak an unsigned or half-built artifact (R10) |
| Same Idempotency-Key sent twice by the same tenant | Second attempt rejected (409), not silently replayed and not silently re-signed | Prevents duplicate signed artifacts for a single logical request; the payload isn't stored so a "replay the same result" response isn't possible anyway |
| Dynamic request missing transaction amount | Reject at validation (400), before any payload is built | Amount is the load-bearing field of a dynamic QR — must be caught before touching the signer |
| Static request includes a transaction amount anyway | Reject (400) per this FR's requirement (R4) | A static QR must never carry Tag 54, by spec | 
| Recipient fields exceed EMVCo TLV byte-length limits for their tag | Reject at validation (400), not truncated | Silent truncation would corrupt recipient identity data inside a signed artifact |
| Caller retries after a timeout with a *new* Idempotency-Key | Treated as a fresh request; a second QR is legitimately issued | The idempotency guarantee is scoped to the key the caller chooses to reuse, not to request content |
| Two requests for the same tenant arrive concurrently with different Idempotency-Keys | Both processed independently; each gets its own signing key lookup and audit entry | No shared mutable state between concurrent generations for the same tenant |

## Decisions Log

| # | Decision | Alternatives Considered | Chosen Because |
|---|---|---|---|
| 1 | FR describes the RFI **mobile application** as the caller (per the original feature ask), even though the currently deployed endpoint is server-to-server only | Describe only the as-built server-to-server caller | User explicitly chose to keep the originally specified mobile-app-via-SDK model as the target behavior for this FR |
| 2 | Static-endpoint rejection of a supplied `transactionAmount` (R4) is a firm requirement. The gap this exposed — unmapped JSON properties were silently dropped rather than rejected — was closed by setting `JsonSerializerOptions.UnmappedMemberHandling = Disallow` globally in `Program.cs` (2026-09-10) | Scope the fix to the static endpoint only (custom validator/model binder) | User chose the global option: consistent strict input handling across every endpoint, not a one-off carve-out, in line with A6 (client is untrusted) |
| 3 | No invented NFR numbers (latency/throughput) — NFRs point at existing AGENTS.md gates C7/C18 instead | Add placeholder target numbers marked TBD | User chose to avoid fabricating figures not backed by code or docs; real targets should come from the 22-Oct load/soak test |
| 4 | QR payload bytes and their hash are not persisted — only generation metadata | Persist payload or hash for traceability | Matches current implementation decision (2026-09-08) recorded in code comments: signature + CRC are the integrity guarantee, reducing what a DB compromise could expose |

## Scope Boundaries

### In Scope
- Static QR generation (recipient identity only)
- Dynamic QR generation (recipient identity + transaction amount)
- Server-side validation of all request fields
- Ed25519 signing via the platform's Key Custody vault, tenant-scoped key resolution
- EMVCo TLV payload construction and CRC finalization per the BanglaQR P2P spec
- Idempotency-key based duplicate-request protection
- Audit logging of every generation attempt, success and failure

### Out of Scope
- Signature verification by the Sender/Payer FI (separate capability — `Verification` module)
- Trust Store publication/lookup of the RFI's public key (separate capability — `InstitutionTrust` module)
- Direct mobile-app-to-platform calling without an RFI backend intermediary (blocked — B1/B2; reason: deferred past 10-Sep feature freeze)
- Key issuance, rotation, and revocation workflows (separate capability — `KeyCustody` module's own FR)
- Numeric SLA targets for latency/throughput (deferred to post-load-test decision, 22 Oct)
- Gateway-level rate limiting configuration (C7 — infrastructure concern, not this module)
- Tag 64 (Recipient Information-Language Template / alternate-language recipient display, e.g. Bangla-script recipient name or city) — optional per spec (Table 3A, presence O), no current RFI requirement for it, and its sub-structure is not fully specified in this document (spec line 82 points to "Table 4.3B of chapter 1," which is not present in `docs/bb-banglaqr-p2p-specification.md`). Deliberately deferred, not missed — revisit if an RFI requests multilingual recipient display.

## Open Questions

- **Mobile-app-direct calling (SDK embedded in the RFI's mobile app hitting the platform without an RFI backend) is blocked (refs B1/B2) and not implemented.**
  - **Impact if unresolved:** this FR's "user" (the mobile app) cannot actually call the endpoint directly today; only an RFI backend service can, using client-credentials auth.
  - **Suggested default:** treat the current server-to-server plane as the interim implementation satisfying this FR's intent (an RFI-controlled caller, properly authenticated, gets a signed QR); track direct mobile calling as a follow-on FR once B1/B2 are resolved, post-freeze.
