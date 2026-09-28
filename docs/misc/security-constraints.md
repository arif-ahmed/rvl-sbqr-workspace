# Constraint Detail — Rationale, Enforcement, VAPT Intent

Companion to the constraint tables in `AGENTS.md`. `AGENTS.md` states **what** the
rule is; this file states **why** it exists, **how** it is enforced, and **what the
VAPT team will try** against it. Every id here has a row there, and vice versa.

Status legend: **Enforced** (a check runs today) · **Manual** (review/checklist) ·
**Gap** (claimed gate does not exist yet — see the Known-gap section of `AGENTS.md`).

## Security

### C1 — VAPT pass
Why: GA sign-off is contractual for a financial-institution integration.
How: external VAPT 15 Sep – 15 Oct; critical/high tracked to closure with re-test
evidence; medium either remediated or formally waived by the Security Committee.
Status: Manual.

### C2 — Approved crypto only
Why: weak or homegrown crypto is the cheapest forgery path to money loss.
How: RSA-3072+ or ECDSA-P256+ for signing, AES-256-GCM for encryption. No MD5/SHA-1,
no ECB, no custom padding, no "temporary" plaintext mode. Code-review checklist now;
a CI policy check when the pipeline lands.
VAPT will: downgrade the algorithm, strip the signature, and replay a signature over
a re-ordered payload. Canonicalize before signing so re-ordering cannot verify.
Status: Manual (CI half is a Gap).

### C3 / C9 — No key material, no secrets in source
Why: a leaked signing key means an attacker mints valid QR codes at will.
How: keys in HSM or vault; configuration through the config provider only. No key
bytes in `appsettings*.json`, `.env`, test fixtures, or commit history.
VAPT will: grep the repo and container images, and read build logs.
Status: Manual today; secret scanning is a **Gap**.

### C4 — Key rotation <= 90 days
Why: bounds the blast radius of an undetected key compromise.
How: rotation driven by infrastructure policy; each rotation leaves an audit record.
Rotation must be possible with **zero downtime** — the old `kid` verifies until its
retirement window closes, the new `kid` signs immediately. See C17.
Status: Manual.

### C5 — Hard tenant isolation
Why: one institute reading another institute's transactions is a reportable breach,
not a bug.
How: `TenantId` filtering at the EF Core level (global query filter), never left to a
caller-supplied parameter. Tenant context comes from the authenticated principal,
never from a request body or an unauthenticated header.
VAPT will: swap the tenant header, replay another tenant id, and probe IDOR on every
resource id.
Status: integration tests per module (Enforced); CI leak-detection query is a Gap.

### C6 — Replay protection
Why: a replayed accept can double-settle a transaction.
How: verification requests carry a timestamp and a request id; window <= 5 minutes,
request id single-use, backed by the same DB-uniqueness mechanism as A9. Clock skew
or an unavailable store means **reject**, never accept (see C14, A12).
Note: the QR payload itself is merchant-presented and may be static — replay defence
lives at the request layer, not in the payload.
Status: Manual until the single-use store lands.

### C7 — Per-tenant rate limiting
Why: protects against brute-force verification probing and noisy-neighbour outage.
How: burst + sustained limits per tenant on every endpoint; exceed -> 429 with
`Retry-After`. Limits validated under the 22 Oct load test.
Status: Manual.

### C8 — Immutable audit log
Why: the audit trail is the evidence base for any disputed transaction.
How: append-only, tamper-evident hash chain over issuance, verification, key use and
config change. No UPDATE or DELETE grant on the audit tables for the runtime user (C20).
VAPT will: attempt to edit or delete a log row and expect it to go unnoticed. It must
be detected and alerted.
Status: Manual.

### C10 / C11 — SCA and SAST
Why: a critical CVE in a transitive dependency is an unowned backdoor.
How: dependency scan and static analysis on every PR, build fails on critical.
Status: **Gap** — no pipeline exists. Blocker before 10 Sep.

### C12 — TLS
Why: plaintext transport exposes tokens and payloads end to end.
How: TLS 1.3 preferred, 1.2 hard floor, strong cipher suites only, HSTS, and no
plaintext listener in any environment reachable from outside the host.
Status: Manual.

### C13 — Error hygiene
Why: stack traces and internal messages hand an attacker the system map for free.
How: one generic problem-detail shape to the client with a stable reason code and a
correlation id; full detail only to structured logs. No exception message pass-through.
VAPT will: force malformed input on every endpoint and read what comes back.
Status: Manual.

### C14 — Time sync
Why: the C6 window and A10 timestamps are meaningless on a drifting clock.
How: NTP on all nodes, max drift 1 s, alert past that. Drift beyond tolerance fails
closed on the verification path.
Status: Manual.

### C15 — Deny by default
Why: an endpoint that forgets its attribute is an open door to a money decision.
How: authorization required globally; anonymous access is an explicit, reviewed
exception (liveness only). Every endpoint declares its required scope and its tenant
binding. Internal and admin surfaces are network-isolated, never internet-exposed.
VAPT will: enumerate routes and call each one unauthenticated.
Status: Manual — audit the route table before freeze.

### C16 — Trust-store authority
Why: trusting a key that arrives with the payload turns signature verification into
theatre.
How: the signer must resolve to a key held in `InstitutionTrust`. Expired, revoked or
unknown -> reject. The payload may carry a key **identifier**, never a key.
Status: integration test (Enforced) + Manual review.

### C17 — Custody boundary
Why: signing keys are the crown jewels; file-based custody in production is a breach
waiting for a filesystem bug.
How: private keys never leave `KeyCustody`; callers get a signature, not a key. Every
signed artifact carries a `kid` so rotation and revocation stay possible (C4).
`LocalKeyVaultProvider` and `EncryptedFileSigningKeyStore` are development-only and
must **throw at startup** when the environment is Production.
Status: Manual — the startup guard is required work.

### C18 — Resource limits
Why: an unbounded parser is a one-request outage on a transaction path.
How: capped request and QR payload size, parse and decode timeouts, bounded TLV
length and element count, and no unbounded or backtracking-prone regex (ReDoS).
Over limit -> 413/400 immediately; never a hang, never an OOM.
VAPT will: send oversized, deeply nested and malformed TLV, plus pathological regex input.
Status: Manual.

### C19 — Log hygiene
Why: logs are widely replicated and long-lived; PII there is PII everywhere.
How: never log raw payloads, account identifiers, keys or tokens. Mask at the sink so
a new call site cannot leak by omission.
Status: Manual.

### C20 — Least-privilege data plane
Why: limits what a compromised app process can do — including to the audit log.
How: the runtime DB user holds DML only, no DDL; migrations run out-of-band through
the external tool (see commit `abb4286`). Separate credentials per environment.
Production data is never copied into a non-production environment; test data is synthetic.
Status: Manual.

### C21 — Supply chain
Why: a floating version turns a third-party compromise into our compromise.
How: central version pinning in `Directory.Packages.props`; no preview or floating
version in a release build; SBOM produced at release.
Status: Manual (pinning Enforced by central package management).

### C22 — Change control
Why: after the 10 Sep freeze, an unreviewed change is the largest remaining risk.
How: branch protection on `develop`, `stage` and `release`; PR only; two approvals for
any diff touching crypto, auth or tenancy. Post-freeze changes are security-critical
fixes only, each linked to a VAPT finding or an incident.
Status: Manual — configure branch protection before freeze.

### C23 — Recoverable
Why: availability of a decision point is part of its security posture.
How: encrypted backups, a restore drill completed before 22 Oct, and a written
RTO/RPO. An untested backup is not a backup.
Status: Manual.

## Accuracy

### A1 — Never fails open
Signature failure = reject. No bypass flag, no fallback path, no debug mode compiled
into a production build. A forged accept is direct financial loss; a false reject is a
retry. The asymmetry is deliberate.

### A2 — Data integrity
Persisted domain state carries an integrity hash so silent modification is detectable,
logged and alerted. VAPT will attempt a quiet row edit.

### A3 — Atomic issuance
Issuance commits in a single transaction. No intermediate state is ever observable — a
partially issued QR is an unreconcilable ledger entry.

### A4 — Deterministic payload
Identical inputs at the same timestamp must produce identical signed bytes. Sources of
non-determinism (dictionary ordering, culture-sensitive formatting, an ambient clock
read twice) are defects, not quirks. Determinism is what makes forensic replay possible.

### A5 — Audit completeness
Every verification is logged, success and failure alike, with a correlation id. A
dropped log line is both a compliance failure and a blind spot for abuse detection.

### A6 — Server-side validation
FluentValidation on every request DTO. Client-side validation is a convenience, never a
control. Validate shape, length, character set and business rule — reject, do not coerce.

### A7 — Spec conformance vectors
Golden vectors taken from `bb-banglaqr-p2p-specification.md`, asserted byte-for-byte
across TLV round-trip and CRC. Interoperability with the scheme is pass/fail, not
best-effort: a payload we accept but the network rejects is a production incident.

### A8 — Amount fidelity
EMVCo Tag 54 is a string and stays a string from request to signature to verification.
Parsing it into `double` or `float`, or reformatting it (`100.50` -> `100.5`), changes
the signed bytes and so silently changes the amount. See `GenerateQrCommand.cs` — the
validator keeps it as text; keep it that way downstream.

### A9 — Idempotency
The issuance idempotency key is tenant-scoped and enforced by a **database unique
constraint**. Application-level checks race under concurrency; the database does not.
A retried request returns the original result, never a second QR.

### A10 — UTC only
Persist, compare and sign in UTC. `Asia/Dhaka` appears at presentation only. Mixed
zones break the C6 replay window and A5 forensic ordering.

### A11 — No silent catch
No swallowed exception anywhere on the verify path. Every rejection returns a stable
machine-readable reason code and writes an audit line. An empty catch block on a money
decision is a release-blocker, not a style issue.

### A12 — Fail closed on dependencies
If the trust store, key custody or audit sink is unavailable, **reject**. There is no
degraded mode that approves. Unavailability is a 503 to the caller, never an accept.
