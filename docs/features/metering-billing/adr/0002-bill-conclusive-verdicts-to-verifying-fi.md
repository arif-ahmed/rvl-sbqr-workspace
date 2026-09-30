---
status: accepted
---

# Bill every conclusive verdict, to the verifying FI

A validation is billable whenever the platform completes the check and returns a conclusive verdict — `VALID`, and also rejections such as `INVALID_SIGNATURE`, `STRUCTURAL_INVALID`, `KEY_NOT_FOUND`, `KEY_SUSPENDED`, `KEY_REVOKED`, `KEY_NOT_ACTIVE` and `NON_P2P`. Only protocol rejections (`REQUEST_STALE`, `REQUEST_REPLAYED`) and HTTP errors are free. The charge goes to the FI whose tenant called validate, never to the institution that issued the QR. We chose this because catching a bad or fraudulent QR is the service the FI is paying for, because charging only for `VALID` would make our cost rise with fraud attempts while revenue does not, and because the caller's tenant is known for certain on every request whereas billing the issuer would charge one FI for another FI's traffic.

## Considered Options

- **Bill only `VALID`.** Looks fairer at first sight, but rejections cost the platform the same work, and free rejections invite abuse (probing with garbage payloads).
- **Free `STRUCTURAL_INVALID` and `NON_P2P`** (the earlier workspace doc `docs/misc/metering-billing-design.md`), on the grounds that no cryptographic check ran. Rejected: parsing, CRC and routing still consume platform resources, and the FI can avoid the charge by pre-checking payloads client-side (open question to HoE, C14 in the workspace doc `docs/features/metering-billing/metering-billing-v1-bn.md`).
- **Bill the issuing institution.** Would charge an FI for validations it neither made nor controls, and invites disputes.

## Consequences

- The rule must be stated plainly in FI contracts and the integration guide ("you pay for every conclusive verdict you request").
- When a rejection is the platform's own fault (e.g. a stale trust-store sync yields `KEY_NOT_FOUND`), the rule does not change; the FI is refunded through an Adjustment after a dispute.
- The Billability policy is owned by Metering, so changing this later touches one policy — but historical usage events keep the billable flag they were recorded with.
