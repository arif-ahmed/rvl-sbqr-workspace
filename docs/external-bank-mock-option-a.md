# External (Non-Tenant) Mock Bank — Plan Draft (Option A: Zero Migration)

## 1. Goal
Demo a bank that is NOT an SBQR platform tenant, but whose P2P QR codes still verify on the platform.
For demo purposes: mock owns its keypair + QR generator UI; platform stores only its public key and verifies signatures.

## 2. Current state (verified in code)
- `public.institution_keys` (`db/migrations/005_institution_trust.sql:66-78`) is ALREADY the common lookup table:
  `institution_code (6-digit = Tag26-01+Tag26-02)`, `key_version`, `public_key`, `status`, `source IN ('REGISTRY','LOCAL')`.
- `ValidateQrCommandHandler.cs:225-229` resolves issuer key ONLY via `GetInstitutionPublicKeyQuery(institutionCode)`. No tenant join, no KeyCustody branch. Works for any issuer with a row.
- Status vocabulary `ACTIVE | SUSPENDED | RETIRED | REVOKED` + temporal gate (`ValidFrom/ValidTo/RevokedAt`) enforced in `GetInstitutionPublicKeyQueryHandler.cs:60-80`. Historical fallback retries latest `RETIRED`.
- Single writer: `InstitutionUpsertService.UpsertAsync()` — manual admin `UpsertInstitutionCommand` writes `Source='LOCAL'` (`InstitutionUpsertService.cs:158`); `DailyTrustSyncService` overwrites to `REGISTRY` on its branch.
- Spec: `docs/bb-banglaqr-p2p-specification.md:114-122,327-347` — payload = `Tag59(AccountName) + Tag26-03(AccountNumber)`, Ed25519 sign, Base64 into tags 80/81, `Verify(public_key, payload, signature)`.

## 3. Option A decision
Reuse `institution_keys` + existing admin upsert with NO schema/migration change.
External mock bank rows stored as `Source='LOCAL'` (semantically "manually seeded", same as tenant publish for demo). No code change to verification read path.
Trade-off accepted: `LOCAL` conflates tenant keys + external keys in demo. Production hardening (Option B: `EXTERNAL` source) explicitly out of scope.

## 4. New mock: `services/external-bank-mock/` in `rvl-sbqr-mocks`
Follow `fi-idp-mock` pattern (one image, multi-instance via env).

### 4.1 Layout
```
services/external-bank-mock/
  src/server.js        # Node: node:crypto (Ed25519, SPKI PEM) + qrcode, no framework
  src/ui/index.html    # single-page generator UI
  Dockerfile
  README.md
  fly.extbank.toml
```

### 4.2 Config (env)
`PORT` (e.g. 5205), `BANK_ID` (e.g. `shapla`), `BANK_NAME` (default `Shapla Commercial Bank PLC` — fictional, see branding note), `BANK_TAGLINE`, `BANK_PRODUCT` (`Shapla Pay`), `INSTITUTION_ID` (6-digit, e.g. `039999` = type `03` + code `9999`), `PRIVATE_KEY_PEM_PATH` (volume), `CUSTOMERS_FILE` (seeded demo roster for the UI picker).

Branding safety: the bank identity is fictional by design; every response carries `X-Demo-Service` + `X-Robots-Tag: noindex` and the UI shows a persistent simulation banner, so a public deploy can never read as a real bank — and the mock's public key must never be seeded on a production platform.

### 4.3 APIs (minimal)
- `POST /api/keys/generate` — `Ed25519.Generate`, persist private PEM to volume, return + log public PEM + SHA256 fingerprint.
- `GET /api/keys/public` — return `{ institutionId, institutionCode, instituteType, institutionName, publicKeyPem }` for copy-paste onboarding.
- `POST /api/qr/generate` — input `{ accountName, accountNumber, amount? }`:
  1. Build EMVCo P2P string via shared codec rules (Tag 26 sub 01/02/03, Tag 59, Tag 80/81, CRC).
  2. `payload = accountName + accountNumber` (no space/symbol, per spec 2.5).
  3. `sig = Base64(Ed25519.sign(payload, privateKey))` → split into tags 80/81.
  4. Return `{ emvcoString, qrPngDataUrl, signatureBase64, institutionId }`.
- `GET /healthz`

### 4.4 UI (demo interface for "their users")
Single page, bank-branded (fictional bank + QR product, customer roster picker, receipt-style output with transaction reference, dark/light mode with system-aware persisted toggle): fields `Account Name | Account Number | Amount (opt)` → `[Generate]` → shows EMVCo string (copy button) + QR PNG (download action) + `institutionId`. No auth, no payer flow, no QR verification — an external bank cannot fetch the trust store's FI public keys, so verification is platform-side only. Persistent simulation banner + demo markers keep any deploy unmistakably a mock.

### 4.5 Docker
Add to `rvl-sbqr-mocks/docker-compose.yml`:
```yaml
ext-bank-mock:
  build: ./services/external-bank-mock
  environment: { PORT: 5205, BANK_ID: ext-demo, INSTITUTION_ID: 039999 }
  ports: ["5205:5205"]
```

## 5. Platform steps (no code change)
1. Generate keypair in mock.
2. Register via existing admin path: `POST /v1/admin/institutions` (MasterAdmin auth; body `{ institutionCode: "039999", instituteType: "03", institutionName: "Ext Demo Bank", publicKeyPem }`) → creates `ACTIVE v1, Source=LOCAL`.
3. Verify path unchanged: payer tenant scans → `ValidateQr` → `ResolvedInstitutionId` → `GetInstitutionPublicKeyQuery` hit → `Ed25519SignatureVerifier.VerifyAsync` → `VALID` / `INVALID_SIGNATURE` / `KEY_NOT_FOUND`.
4. Rotation demo (optional): re-`POST /api/qr/generate` after `upsert` with new PEM → old version auto-`RETIRED`, historical QRs still `VALID (VERIFIED_HISTORICAL_KEY)`.

## 6. Test plan
- [ ] Upsert external key → `ListInstitutions` shows it.
- [ ] Generate QR in mock UI → `ValidateQr` as tenant payer → `VALID, TrustSource=TRUST_DIRECTORY`.
- [ ] Tamper name/pan → `INVALID_SIGNATURE`.
- [ ] Unknown institution QR → `KEY_NOT_FOUND`.
- [ ] Rotate key → old QR still `VALID` via historical fallback; new QR `VALID` on v2.

## 7. Out of scope
- No `EXTERNAL` source value / migration, no `TrustStore` sync change, no `KeyCustody` / tenant provisioning for external bank, no billing/metering change, no mTLS for mock.
- No payer-side verify in the mock: an external bank has no way to fetch the trust store's FI public keys, so verification is platform-side only.

## 7a. Implementation status
Implemented in `rvl-sbqr-mocks` `services/external-bank-mock/` (compose service `ext-bank-mock` on :5205, volume `ext-bank-keys`; also deployed at `https://extbank-mock.fly.dev`, region `sin`, volume `extbank_keys` — key fingerprint `660f6dc6…f29f9d2`). Verified: TLV/CRC/mandatory-tag conformance against the platform parser rules, Ed25519 SPKI PEM signature round-trip, tamper → signature failure, rotation → old QR fails on active key (historical fallback pending live-platform demo), key persistence across container/machine restarts. Evidence: `docs/external-bank-mock-option-a-signoff-report.html`. Platform-side test plan (§6) requires a running manager + MasterAdmin token.

## 8. Risks / notes
- `Source='LOCAL'` ambiguity: document in README that demo external rows share `LOCAL`; filter by `CreatedBy=system` + audit `institution.trust.key.published` if needed.
- Ensure demo `INSTITUTION_ID` does not collide with real tenant codes or BB `REGISTRY` codes (use reserved `99xx` range).
- Private key never leaves mock volume; only PEM public key pasted to platform.
