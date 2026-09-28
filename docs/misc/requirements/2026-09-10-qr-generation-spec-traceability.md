# Spec Traceability: QR Generation Request Properties

> **Date:** 2026-09-10
> **Purpose:** For each property on `GenerateStaticQrRequest` / `GenerateDynamicQrRequest` (and each spec field deliberately kept off those DTOs), cite the exact line(s) in `docs/bb-banglaqr-p2p-specification.md` that justify the name, the length cap, and — for excluded fields — why the platform derives or fixes the value instead of accepting it from the caller.
> **Audience:** engineering leadership sign-off / design defense.
> **Companion:** `docs/requirements/2026-09-10-qr-generation.md` (FR), `docs/requirements/2026-09-10-qr-generation-user-stories.md` (backlog).

All line numbers below refer to `docs/bb-banglaqr-p2p-specification.md` as of this writing (664 lines total — confirmed with `wc -l`). Quotes are verbatim from the file. **Correction:** an earlier pass at this review only read the first ~160 lines and mistook a mid-document "# Reference" heading (line 154) for the end of the file; the document continues through Annex A–F (line 664). That did not change any citation below — all of them sit in lines 1–150 — but it's noted here for the record.

---

## How to verify this yourself — exact file:line pairs

Don't read this as prose. For each property, open the two files below side by side and jump straight to the line number. In VS Code / Rider, **Ctrl+G** (Go to Line) then type the number. If you use `code -g` or `rider.sh`, you can jump directly from a terminal, e.g.:

```
code docs/bb-banglaqr-p2p-specification.md:97
code src/Modules/QrGeneration/SBQR.Modules.QrGeneration.Api/Controllers/QrGenerationController.cs:145
```

| # | Property | **Spec** — open `docs/bb-banglaqr-p2p-specification.md`, go to line: | **Code** — open this file, go to line: |
|---|---|---|---|
| 1 | `RecipientPan` | **97** (Table 3B, row "03") | `.../QrGeneration.Api/Controllers/QrGenerationController.cs:145` (DTO field); `.../SharedKernel/QrCodec/P2pQrBuilder.cs:18` (`MaxRecipientPanBytes = 19`) |
| 2 | `RecipientName` | **78** (Table 3A, row "59") | `QrGenerationController.cs:143`; `P2pQrBuilder.cs:21` (`MaxRecipientNameBytes = 25`) |
| 3 | `RecipientCity` | **79** (Table 3A, row "60") | `QrGenerationController.cs:144`; `P2pQrBuilder.cs:24` (`MaxRecipientCityBytes = 15`) |
| 4 | `TransactionAmount` | **71** (Table 3A, row "54"); **50** (static narrative); **58** (dynamic narrative) | Present: `QrGenerationController.cs:152` (`GenerateDynamicQrRequest`). Absent: `QrGenerationController.cs:142-148` (`GenerateStaticQrRequest` — no such field declared at all); `P2pQrBuilder.cs:30` (`MaxAmountBytes = 13`) |
| 5 | `PostalCode` | **80** (Table 3A, row "61") | `QrGenerationController.cs:146`; `P2pQrBuilder.cs:27` (`MaxPostalCodeBytes = 10`) |
| 6 | `CustomerLabel` | **107** (Table 4A, row 62/"06") | `QrGenerationController.cs:147`; `P2pQrBuilder.cs:33` (`MaxAdditionalDataBytes = 25`) |
| 7 | `PurposeOfTransaction` | **108** (Table 4A, row 62/"08") | `QrGenerationController.cs:148`; `P2pQrBuilder.cs:33` (same cap, shared with Customer Label) |
| 8 | Institution Type / ID (excluded) | **95–96** (Table 3B, rows "01"/"02"); **150** (why it matters at verify time) | `.../QrGeneration.Application/Commands/GenerateQrCommandHandler.cs:151-152` (`institutionCode[..2]` / `institutionCode[2..]` — derived from tenant, not from any request DTO) |
| 9 | Payload Format Indicator (excluded) | **66** (Table 3A, row "00") | `.../SharedKernel/QrCodec/P2pQrRequest.cs:17` (`BqrConstants.PayloadFormatIndicator = "01"`) |
| 10 | Point of Initiation Method (excluded) | **67** (Table 3A, row "01") | `P2pQrRequest.cs:20-23` (`StaticInitiation = "11"`, `DynamicInitiation = "12"`); selected by `GenerateQrCommandHandler.cs:150` (`IsDynamic: request.IsDynamic`) |
| 11 | Merchant Category Code (excluded) | **69** (Table 3A, row "52"); **146** (why it's load-bearing for sender classification) | `P2pQrRequest.cs:14` (`P2pMerchantCategoryCode = "4829"`) |
| 12 | Transaction Currency (excluded) | **70** (Table 3A, row "53"); **156** (spec scope = Bangladesh only) | `P2pQrRequest.cs:52` (`Currency = "050"` default) |
| 13 | Country Code (excluded) | **72** (Table 3A, row "58") | `P2pQrRequest.cs:51` (`CountryCode = "BD"` default) |
| 14 | CRC (excluded) | **86** (Table 3A, row "63") | `.../SharedKernel/QrCodec/QrPayloadFinalizer.cs` (computed last, over the signed payload) |
| 15 | Signature Tags 80/81 (excluded) | **84** (Table 3A); **120** (§2.5, Ed25519 + private key); **124-136** (Tables 5A/5B) | `.../KeyCustody.Infrastructure/Custody/PemVaultSigningProvider.cs:60-99` (`SignAsync`) |
| 16 | Recipient Info-Language Template (excluded) | **82** (Table 3A, row "64") | not implemented anywhere — confirm by searching the repo for `Tag64`/`"64"` in `QrCodec` and finding nothing |

If any spec-side line number above doesn't show the quoted text when you jump to it, the spec file has been edited since this doc was written — re-run `wc -l docs/bb-banglaqr-p2p-specification.md` to confirm it's still 664 lines, and if not, treat every citation in this document as stale and ask for it to be regenerated.

---

## Part A — Properties the caller supplies

### 1. `RecipientPan`

- **Spec reference:** line 97, Table 3B, ID "03"

  > `| "03" | Recipient PAN | ans | Var. up to "19" | M | An unique Identification for the recipient assigned by the acquirer/recipient's institution; such as: account or wallet number. |`

- **Name justification:** the property name is a direct copy of the spec's own field name in the "Name" column — "Recipient PAN" — not a paraphrase.
- **Length justification:** spec says `Var. up to "19"`. Code: `P2pQrBuilder.cs` — `MaxRecipientPanBytes = 19`, enforced again independently in `GenerateStaticQrValidator`/`GenerateDynamicQrValidator` (`.MaximumLength(19)`).
- **Cross-reference:** the same field is referenced again at line 114 (§2.5) as "Account Number" — see Part C, note 1, for why the code follows the Table 3B name rather than the §2.5 prose name.

### 2. `RecipientName`

- **Spec reference:** line 78, Table 3A

  > `| Recipient Name | "59" | ans | var. up to "25" | M | |`

- **Name justification:** literal match to the spec's "Name" column.
- **Length justification:** `var. up to "25"` → `MaxRecipientNameBytes = 25` in `P2pQrBuilder.cs`, mirrored in both FluentValidation validators.

### 3. `RecipientCity`

- **Spec reference:** line 79, Table 3A

  > `| Recipient City | "60" | ans | var. up to "15" | M | |`

- **Name justification:** literal match.
- **Length justification:** `var. up to "15"` → `MaxRecipientCityBytes = 15`.

### 4. `TransactionAmount` (present only on `GenerateDynamicQrCommand`)

- **Spec reference:** line 71, Table 3A

  > `| Transaction Amount | "54" | ans | var. up to"13" | C | Follow section 1.4.4 of chapter 1 of these guidelines. |`

- **Name justification:** literal match.
- **Length justification:** `var. up to "13"` → `MaxAmountBytes = 13` in `P2pQrBuilder.cs`; also enforced in `GenerateDynamicQrValidator` (`.MaximumLength(13)`).
- **Why it exists only on the dynamic command, not the static one:** the Presence column for Tag 54 is **"C" (Conditional)**, not "M". The condition is made explicit in the two QR-type narratives:
  - line 50 (§2.2.1, Static): *"...the sender...verifies the recipient's information, enters the transfer amount..."* — the amount is entered by the **sender**, at scan time, not carried in the code.
  - line 58 (§2.2.2, Dynamic): *"A dynamic P2P Bangla QR Code...contains both the recipient's account or wallet information and transaction-specific details, such as the requested amount."* — the amount is part of the code itself.

  This is the direct textual basis for splitting the request into two separate models rather than one shared model with an optional amount: the spec describes two structurally different payloads, not one payload with an optional field.

### 5. `PostalCode`

- **Spec reference:** line 80, Table 3A

  > `| Postal Code | "61" | ans | var. up to "10" | O | |`

- **Name/length justification:** literal match; `MaxPostalCodeBytes = 10`.

### 6. `CustomerLabel`

- **Spec reference:** line 107, Table 4A

  > `| 62 | "06" | Customer Label | ans | Var. up to"25" | O | "FT" may be used as values to identify the transactions as P2P fund transfer |`

- **Name/length justification:** literal match; `MaxAdditionalDataBytes = 25`.

### 7. `PurposeOfTransaction`

- **Spec reference:** line 108, Table 4A

  > `| 62 | "08" | Purpose of Transaction | ans | Var. up to "25" | C | If value is populated as *** , the sender/payer application shall prompt the user to input a payment purpose. |`

- **Name/length justification:** literal match; `MaxAdditionalDataBytes = 25`.
- **Presence note:** Presence is "C" here too, but the spec's own comment only describes behavior *when a value is supplied* (the `***` sentinel case) — it never states a condition that would make the field mandatory. The validator therefore treats it as optional, which is the most literal reading available from the text at line 108; there is no other clause in the document that narrows this further.

---

## Part B — Properties deliberately excluded from the request DTOs

Each of these is a **Mandatory (M)** field in the payload, but the value is derived or fixed server-side rather than accepted from the caller. The reasoning is a trust-boundary argument (R6/C16 in `AGENTS.md`), grounded in what the spec itself says these fields identify.

### 8. Recipient's Institution Type / Institution ID (Tag 26 sub 01 / sub 02)

- **Spec reference:** lines 95–96, Table 3B

  > `| "01" | Recipient's Institution Type | N | "02" | M | 00= Banks, 01= NBFIs 02= MFS providers, 03= PSP/e-wallet service providers, 04= Payment System Operator (PSO), 05= White Label ATM and/or Recipient Acquirer (WLAMA), 06-99 = RFU |`
  > `| "02" | Recipient's institution ID | N | "04" | M | Please see Annex-A for bank, PSP, PSO and MFS provider details. |`

- **Why excluded from the request:** these two fields **identify which institution is issuing the QR** — exactly the identity a forged request would want to spoof. Line 150 confirms how load-bearing this identity is at verify time: *"...the name of the recipient's institution derived from the relevant sub-tag 02 of Tag 26. This verification step will help to ensure transaction accuracy and reduce the risk of erroneous fund transfers."* If a caller could set these fields, any tenant with a valid `qr:generate` token could mint a QR claiming to be a *different* institution. The platform instead derives both values from the authenticated tenant's registered `institutionCode` (`GenerateQrCommandHandler.cs:151`, `institutionCode[..2]` / `institutionCode[2..]`) — the caller's identity, not the caller's claim, decides these fields.

### 9. Payload Format Indicator (Tag 00)

- **Spec reference:** line 66, Table 3A

  > `| Payload Format Indicator | "00" | N | "02" | M | A fixed value of "01" |`

- **Why excluded:** the spec states it is *"A fixed value"* — there is no variability for a request field to express. Hardcoded as `BqrConstants.PayloadFormatIndicator = "01"`.

### 10. Point of Initiation Method (Tag 01)

- **Spec reference:** line 67, Table 3A

  > `| Point of Initiation Method | "01" | N | "02" | O | Value 11 for Static QR codes and 12 for Dynamic QR codes. |`

- **Why excluded as a request field (but still present in the output):** the spec ties the value directly to which *kind* of QR is being produced. Rather than ask the caller to state "11" or "12" as a field (which could disagree with which endpoint they called), the platform derives it from the endpoint itself: `/generate/static` always emits `"11"` (`BqrConstants.StaticInitiation`), `/generate/dynamic` always emits `"12"` (`BqrConstants.DynamicInitiation`). This removes a class of "field says static, endpoint says dynamic" contradiction the spec doesn't otherwise resolve.

### 11. Merchant Category Code (Tag 52)

- **Spec reference:** line 69, Table 3A

  > `| Merchant Category Code | "52" | N | "04" | M | A fixed value of "4829"to indicate P2P QR. |`

  Reinforced at line 146: *"Where Tag 52 contains the value "4829", the transaction shall be identified and processed as a Person-to-Person (P2P) transaction."*

- **Why excluded:** fixed value, and load-bearing for how the *sender's* app classifies the transaction (P2P vs P2M) — a caller-writable field here would let a request silently mis-tag its own transaction type. Hardcoded as `BqrConstants.P2pMerchantCategoryCode = "4829"`.

### 12. Transaction Currency (Tag 53)

- **Spec reference:** line 70, Table 3A

  > `| Transaction Currency | "53" | N | "03" | M | As defined by ISO 4217. For example, "050" for Bangladeshi Taka. |`

- **Why excluded:** this specification is scoped to Bangladesh's domestic NPSB P2P scheme (see line 156, *"National QR Code Specification for Retail Payments in Bangladesh"*) — nothing in the document describes a multi-currency P2P flow. Defaulted to `"050"` (BDT) in `P2pQrRequest`, not exposed on any public DTO. Exposing it as a request field would only create a way to submit an unsupported/invalid currency the platform has no defined handling for.

### 13. Country Code (Tag 58)

- **Spec reference:** line 72, Table 3A

  > `| Country Code | "58" | ans | "02" | M | As defined by ISO 3166.Example "BD" for Bangladesh. |`

- **Why excluded:** same reasoning as Currency — single-country scheme by definition of the spec's own title and scope. Defaulted to `"BD"`.

### 14. CRC (Tag 63)

- **Spec reference:** line 86, Table 3A

  > `| CRC | "63" | ans | "04" | M | |`

  Ordering requirement implied by line 84 (Storage of Signature Value must exist before the checksum is meaningful) and made explicit in code: `QrPayloadFinalizer` computes CRC last, over the payload *including* the embedded signature.

- **Why excluded:** a CRC is by definition a checksum *over* the rest of the payload — it cannot be a caller input without breaking its own purpose as a tamper-evidence mechanism.

### 15. Storage of Signature Value (Tags 80/81)

- **Spec reference:** line 84, Table 3A, and lines 124–136, Tables 5A/5B

  > `| Storage of Signature Value | "80" - "81" | S | Each var. up to "99" | C | This will be used for to store value after signing the data. Refer to section 2.5 of this chapter. |`
  > (line 129) `| "01" | Signature 1st part | S | var. | M | First 44 Characters of Signature |`
  > (line 136) `| "01" | Signature 2nd part | S | var. | M | Last 44 Characters of Signature |`

- **Why excluded:** this is the platform's own signature over the payload (§2.5, line 120: *"The signature will be generated using the Ed25519 algorithm with the QR generating Institution's private key."*). A caller-suppliable signature field would defeat the entire security model — the platform must be the sole producer of this value, using a key the caller never has access to (C17: private key never leaves Key Custody).

### 16. Recipient Information-Language Template (Tag 64)

- **Spec reference:** line 82, Table 3A

  > `| Recipient Information-Language Template | "64" | S | var. up to "99" | O | ...includes Recipient information in an alternate language... |`

- **Why excluded:** Presence is **"O" (Optional)** — the spec itself does not require it. Not implemented today; this is an unbuilt optional feature, not a naming or completeness defect. If multilingual recipient display becomes a requirement, this is the tag to build against — call it out separately as new scope, not a fix to the current request models.

---

## Summary table (for quick reference in review)

| # | Property / field | Tag | Spec line(s) | Presence | Disposition |
|---|---|---|---|---|---|
| 1 | `RecipientPan` | 26.03 | 97 | M | On request — name/length match spec |
| 2 | `RecipientName` | 59 | 78 | M | On request — name/length match spec |
| 3 | `RecipientCity` | 60 | 79 | M | On request — name/length match spec |
| 4 | `TransactionAmount` | 54 | 71, 50, 58 | C | On dynamic request only — spec ties presence to QR type |
| 5 | `PostalCode` | 61 | 80 | O | On request — name/length match spec |
| 6 | `CustomerLabel` | 62.06 | 107 | O | On request — name/length match spec |
| 7 | `PurposeOfTransaction` | 62.08 | 108 | C | On request — treated as optional per literal reading |
| 8 | Institution Type / ID | 26.01/02 | 95–96, 150 | M | Excluded — derived from authenticated tenant (trust boundary) |
| 9 | Payload Format Indicator | 00 | 66 | M | Excluded — fixed value |
| 10 | Point of Initiation Method | 01 | 67 | O | Excluded — derived from endpoint called |
| 11 | Merchant Category Code | 52 | 69, 146 | M | Excluded — fixed value |
| 12 | Transaction Currency | 53 | 70, 156 | M | Excluded — single-currency scheme, defaulted |
| 13 | Country Code | 58 | 72, 156 | M | Excluded — single-country scheme, defaulted |
| 14 | CRC | 63 | 86 | M | Excluded — computed, checksum over final payload |
| 15 | Signature (80/81) | 80–81 | 84, 120, 124–136 | C | Excluded — platform-only, Ed25519 via Key Custody |
| 16 | Recipient Info-Language Template | 64 | 82 | O | Excluded — optional, unbuilt, not a defect |
