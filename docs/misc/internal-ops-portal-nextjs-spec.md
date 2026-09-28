# SBQR Manager — Internal Operations & Tenant Management Portal
## Next.js Frontend Architecture & BanglaQR Theme Specification

This specification reviews the current **SBQR Secure BQR Manager** backend architecture, aligns with the **Bangladesh Bank (BB) BanglaQR P2P Specification (`docs/bb-banglaqr-p2p-specification.md`)**, and defines the architecture, design tokens, and API integration mapping for the **Next.js internal management portal**.

An interactive preview prototype is available at:  
👉 **`file:///D:/Workspace/Sources/RVL/rvl-secure-bqr-manager/demo-preview/index.html`**

---

## 1. Backend Architecture Review

The backend is built as a **Modular Monolith in .NET 10 (C#)** following Clean Architecture principles, partitioned into isolated bounded modules that communicate strictly via Contracts and in-process MediatR commands:

| Module | Core Responsibility | API Seam / Routes |
|---|---|---|
| **Tenancy** | Financial Institution (tenant) registration, directory, and lifecycle state machine (`Pending`, `Active`, `Suspended`, `Terminated`). | `POST /v1/admin/tenants`<br>`GET /v1/admin/tenants`<br>`GET /v1/admin/tenants/{id}`<br>`POST /v1/admin/tenants/{id}/activate`<br>`POST /v1/admin/tenants/{id}/suspend`<br>`POST /v1/admin/tenants/{id}/reactivate`<br>`POST /v1/admin/tenants/{id}/terminate` |
| **IdentityAccess** | OAuth2 client credentials provisioning, Argon2id secret hashing, JWT issuance with scopes (`admin`, `qr:generate`, `qr:validate`). | `POST /v1/admin/tenants/{id}/tenant-configuration`<br>`POST /v1/oauth/token` |
| **KeyCustody** | Asymmetric Ed25519 signing key minting (`Generate` or `Adopt`), active version resolution, zero-downtime key rotation (`v1 -> v2`). | `POST /v1/crypto-keys`<br>`PUT /v1/crypto-keys/{tenantId}`<br>`GET /v1/crypto-keys/{tenantId}/active`<br>`GET /v1/crypto-keys/{tenantId}` (Version History) |
| **InstitutionTrust** | Publishes active public keys to the central NPSB Trust Directory, supports daily trust-store sync. | `POST /v1/admin/institutions` |
| **QrGeneration** | EMVCo-compliant TLV payload synthesis, Tag 59 + Tag 26.03 concatenation, Ed25519 private key signing, Tag 80/81 splitting. | `POST /v1/qr/generate/static`<br>`POST /v1/qr/generate/dynamic` |
| **Verification** | Replay-protected QR payload parser, CCITT-16 CRC validator, Ed25519 public key lookup against trust store. | `POST /v1/qr/validate` |

---

## 2. Bangladesh Bank (BB) BanglaQR P2P Specification Compliance

The frontend needs to be aware of the following domain standards defined in `docs/bb-banglaqr-p2p-specification.md`:

1. **Digital Signature Mechanics (BB Spec § 2.5)**:
   - **Signature Payload**: Strict concatenation of `Tag 59 (Recipient Name)` and `Tag 26 Sub-tag 03 (Recipient PAN/Account)` without spaces or symbols.
   - **Algorithm**: **Ed25519** with the generating institution's private key.
   - **Storage**: 64-byte signature is Base64 encoded (88 characters) and split:
     - First 44 chars &rarr; `Tag 80 Sub-tag 01`
     - Last 44 chars &rarr; `Tag 81 Sub-tag 01`
     - Both under GUID `bd.org.bb.npsb` (Sub-tag 00).
2. **Merchant Category Code (MCC)**: `Tag 52 = "4829"` explicitly designates P2P transactions.
3. **Point of Initiation**: `Tag 01 = "11"` (Static QR) vs `Tag 01 = "12"` (Dynamic QR with `Tag 54` amount).
4. **Institution Types (Tag 26 Sub-tag 01)**:
   - `00`: Commercial Banks (Agrani, City Bank, BRAC Bank, etc.)
   - `01`: NBFIs
   - `02`: Mobile Financial Services (bKash, Nagad, Rocket, Upay)
   - `03`: Payment Service Providers (PSPs / e-Wallets)
   - `04`: Payment System Operators (PSO)

---

## 3. BanglaQR Visual Theme & Design Tokens

The internal portal uses the official national payment identity:

| Token Name | Hex Code | Semantic Role |
|---|---|---|
| **Primary (Bangladesh Green)** | `#006A4E` | Dominant institutional green across top navigation bar, primary buttons, active badges, and brand emblems. |
| **Primary Deep / Hover** | `#004C38` / `#003325` | Header contrast accents, active tab container, and button hover states. |
| **Primary Subtle / Surface** | `#F0F7F4` / `#E6F0EC` | Background for active table rows, alert boxes, and contextual cards. |
| **Secondary (BanglaQR Crimson)** | `#D90429` | BanglaQR brand dot, suspension and freeze warnings, terminal decommission actions. |
| **Secondary Surface** | `#FDF2F2` / `#F8B4B4` | Light red surface for suspension alerts and audit freeze indicators. |
| **Accent (Taka Gold / Amber)** | `#D97706` | Top bar gold border, pending activation status chips, 1-time secret modal borders. |
| **Paper Canvas** | `#F4F6F8` | Clean, high-legibility enterprise canvas (matches Bangladesh Bank & NPSB portal aesthetics). |
| **Paper Card Surface** | `#FFFFFF` | Crisp white elevation cards with clean subtle borders (`#E2E8F0`). |
| **Typography Slate** | `#0F172A` / `#334155` | High-contrast banking tabular typography with `Inter` and `JetBrains Mono`. |


---

## 4. Next.js Web Application Architecture

### Recommended Stack
- **Framework**: Next.js 15 (App Router)
- **Language**: TypeScript 5.x
- **Styling**: Tailwind CSS with custom `bqr` palette
- **Component Primitives**: Radix UI + shadcn/ui
- **Data Fetching / Cache**: `@tanstack/react-query` v5
- **Form Management**: `react-hook-form` + `zod`
- **Icons**: `lucide-react`

### Proposed Directory Layout
```
sbqr-admin-portal/
├── src/
│   ├── app/
│   │   ├── (auth)/
│   │   │   └── login/page.tsx               # Operator login (Platform Bootstrap credentials)
│   │   ├── (dashboard)/
│   │   │   ├── layout.tsx                   # Top navigation with BanglaQR theme
│   │   │   ├── page.tsx                     # Executive KPI dashboard & switch health
│   │   │   ├── tenants/
│   │   │   │   ├── page.tsx                 # Tenant directory (filter by Status, Bank/MFS)
│   │   │   │   ├── [id]/page.tsx            # Tenant 360° detail & configuration view
│   │   │   │   └── new/page.tsx             # 4-step FI Onboarding Wizard
│   │   │   ├── crypto-keys/
│   │   │   │   ├── page.tsx                 # Key Custody hub (Active keys, version history)
│   │   │   │   └── rotate/page.tsx          # Key rotation modal/flow
│   │   │   ├── trust-store/page.tsx         # Central NPSB Trust Directory sync status
│   │   │   └── inspector/page.tsx           # Live BanglaQR EMVCo P2P tag parser & validator
│   ├── components/
│   │   ├── ui/                              # Button, Modal, Badge, Input, Table, Tooltip
│   │   ├── tenants/
│   │   │   ├── TenantTable.tsx              # Paged tenant list with lifecycle actions
│   │   │   ├── SuspendDialog.tsx            # Suspend confirmation with reason input & warning
│   │   │   ├── ReactivateDialog.tsx         # Reinstatement confirmation
│   │   │   └── OneTimeSecretModal.tsx       # Secure dialog displaying client_secret once
│   │   ├── crypto/
│   │   │   ├── PemViewer.tsx                # Monospace syntax-highlighted PEM card
│   │   │   └── KeyRotationModal.tsx         # KeyVersion increment & retire confirmation
│   │   └── qr/
│   │       ├── QrTlvTable.tsx               # Decoded EMVCo tags 00 through 81
│   │       └── QrVerificationResult.tsx     # Ed25519 signature validity banner
│   ├── lib/
│   │   ├── api-client.ts                    # Typed Axios/Fetch client against SBQR.Api Host
│   │   ├── banglaqr-codec.ts                # Client-side TLV parsing & CRC validation
│   │   └── types/
│   │       ├── tenant.ts                    # TenantResponse, RegisterTenantRequest
│   │       ├── crypto.ts                    # CryptoKeySummary, CreateCryptoKeyRequest
│   │       └── qr.ts                        # GenerateQrResponse, ValidateQrResponse
```

---

## 5. Screen Breakdown & Workflows

### Screen 1: Tenant Directory & Overview
- **Metrics Bar**: Total FIs, Active Keys, Pending Activation, Suspended.
- **Search & Filter**: Search by FI Name, 6-digit Code, or BIN; filter by status (`Active`, `Pending`, `Suspended`, `Terminated`).
- **Table**: Institution Name, Code, Entity Type (`00` Bank, `02` MFS), Client ID, Key Version, Trust Store sync badge, and Action triggers.

### Screen 2: FI Onboarding Wizard
1. **Step 1 - Profile**: Input Institution Name, 6-digit Institution Code, Type (Bank/MFS/PSP), and BIN.
2. **Step 2 - Auto-Activation**: Checkbox to transition immediately from `Pending` to `Active` via `POST /v1/admin/tenants/{id}/activate`.
3. **Step 3 - Provision Credentials**: Invokes `POST /v1/admin/tenants/{id}/tenant-configuration`, opens **One-Time Secret Modal** displaying `client_id` and `client_secret` (Argon2id hashed).
4. **Step 4 - Key Minting**: Invokes `POST /v1/crypto-keys` with `mode: "Generate"` to mint Ed25519 keypair and auto-publish public key to NPSB Trust Directory.

### Screen 3: Key Custody & HSM Hub
- **Active Key View**: Shows active Key ID, Key Version, SHA-256 fingerprint, and PEM public key.
- **Zero-Downtime Rotation**: Click "Rotate Key" &rarr; calls `PUT /v1/crypto-keys/{tenantId}` &rarr; retires current active key, mints `Version + 1`, updates Trust Store.
- **Adopt External Key**: Allows FIs with external HSMs to submit their own Ed25519 Public Key PEM.

### Screen 4: Tenant Lifecycle & Suspension
- **Suspend Action**: Calls `POST /v1/admin/tenants/{id}/suspend` with mandatory audit reason. Displays warning that all API tokens will return `401 Unauthorized` and signing keys will be suspended.
- **Reactivate Action**: Calls `POST /v1/admin/tenants/{id}/reactivate`, restoring credentials and key status.
- **Terminate Action**: Terminal one-way action calling `POST /v1/admin/tenants/{id}/terminate`, soft-deleting the tenant.

### Screen 5: BanglaQR P2P Tag Inspector
- Loads raw static (`Tag 01 = 11`) and dynamic (`Tag 01 = 12`) BanglaQR strings.
- Decodes all TLV tags and verifies CCITT-16 CRC (Tag 63).
- Validates the Ed25519 digital signature in Tag 80 & 81 against the central trust store.

---

## 6. How to Open the Demo Preview

Open the generated HTML prototype directly in any modern browser:
```powershell
# In PowerShell:
Start-Process "D:\Workspace\Sources\RVL\rvl-secure-bqr-manager\demo-preview\index.html"
```
Or serve via any static web server:
```powershell
npx serve D:\Workspace\Sources\RVL\rvl-secure-bqr-manager\demo-preview
```
