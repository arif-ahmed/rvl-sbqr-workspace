# Trust-Store Public-Key Upload Payloads — All Annex A Institutions
Generated payload samples for:

```
PUT {{baseUrl}}/trust-store/institutions/{{institutionId}}/public-key
Content-Type: application/json
```

One entry per Annex A institution. `institutionId` (route) = Tag 26 sub-tag 01
(institution type, 2 digits) + sub-tag 02 (Annex A Acquirer ID, 4 digits) = 6
digits, per `docs/bb-banglaqr-p2p-specification.md` Annex B (example:
Sub-tag 01=`03`, Sub-tag 02=`1008` → Institution_ID `031008`).

Institution type codes (Tag 26 sub-tag 01): `00`=Banks, `01`=NBFIs,
`02`=MFS providers, `03`=PSP/e-wallet providers, `04`=PSO, `05`=WLAMA.
NBFIs (`01`) have no entries published in Annex A, so none are listed below.

`publicKeyPem` below is a **placeholder Ed25519 SPKI public key** shared
across every sample purely to keep the payload shape valid for the mock's
Ed25519-only gate (`PublishPublicKeyRequestValidator`). Each real
institution must generate and upload its **own** key pair per spec Annex C:

```bash
openssl genpkey -algorithm ed25519 -out {institutionId}-private.pem
openssl pkey -in {institutionId}-private.pem -pubout -out {institutionId}-public.pem
```

---
## Contents

- [Bank (type `00`)](#bank-type-00)
- [PSP (type `03`)](#psp-type-03)
- [MFS (type `02`)](#mfs-type-02)
- [PSO (type `04`)](#pso-type-04)
- [WLAMA (type `05`)](#wlama-type-05)

---

## Bank (type `00`)

### Agrani Bank PLC — Acquirer ID `0010` → Institution_ID `000010`
```
PUT {{baseUrl}}/trust-store/institutions/000010/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Agrani Bank PLC",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Al-Arafah Islami Bank PLC. — Acquirer ID `0015` → Institution_ID `000015`
```
PUT {{baseUrl}}/trust-store/institutions/000015/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Al-Arafah Islami Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Ab Bank PLC. — Acquirer ID `0020` → Institution_ID `000020`
```
PUT {{baseUrl}}/trust-store/institutions/000020/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Ab Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bangladesh Commerce Bank PLC. — Acquirer ID `0030` → Institution_ID `000030`
```
PUT {{baseUrl}}/trust-store/institutions/000030/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bangladesh Commerce Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bangladesh Krishi Bank — Acquirer ID `0035` → Institution_ID `000035`
```
PUT {{baseUrl}}/trust-store/institutions/000035/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bangladesh Krishi Bank",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bangladesh Samabaya Bank PLC. — Acquirer ID `0040` → Institution_ID `000040`
```
PUT {{baseUrl}}/trust-store/institutions/000040/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bangladesh Samabaya Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bangladesh Dev. Bank PLC. — Acquirer ID `0047` → Institution_ID `000047`
```
PUT {{baseUrl}}/trust-store/institutions/000047/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bangladesh Dev. Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Basic Bank PLC. — Acquirer ID `0055` → Institution_ID `000055`
```
PUT {{baseUrl}}/trust-store/institutions/000055/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Basic Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Brac Bank PLC. — Acquirer ID `0060` → Institution_ID `000060`
```
PUT {{baseUrl}}/trust-store/institutions/000060/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Brac Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bank Al-Falah PLC — Acquirer ID `0065` → Institution_ID `000065`
```
PUT {{baseUrl}}/trust-store/institutions/000065/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bank Al-Falah PLC",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bank Asia PLC. — Acquirer ID `0070` → Institution_ID `000070`
```
PUT {{baseUrl}}/trust-store/institutions/000070/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bank Asia PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Citi Bank N. A. — Acquirer ID `0075` → Institution_ID `000075`
```
PUT {{baseUrl}}/trust-store/institutions/000075/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Citi Bank N. A.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Commercial Bank Of Ceylon — Acquirer ID `0080` → Institution_ID `000080`
```
PUT {{baseUrl}}/trust-store/institutions/000080/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Commercial Bank Of Ceylon",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Dhaka Bank PLC. — Acquirer ID `0085` → Institution_ID `000085`
```
PUT {{baseUrl}}/trust-store/institutions/000085/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Dhaka Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Dutch-Bangla Bank PLC — Acquirer ID `0090` → Institution_ID `000090`
```
PUT {{baseUrl}}/trust-store/institutions/000090/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Dutch-Bangla Bank PLC",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Eastern Bank PLC. — Acquirer ID `0095` → Institution_ID `000095`
```
PUT {{baseUrl}}/trust-store/institutions/000095/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Eastern Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Exim Bank PLC. — Acquirer ID `0100` → Institution_ID `000100`
```
PUT {{baseUrl}}/trust-store/institutions/000100/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Exim Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### First Security Islami Bank PLC — Acquirer ID `0105` → Institution_ID `000105`
```
PUT {{baseUrl}}/trust-store/institutions/000105/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "First Security Islami Bank PLC",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Habib Bank PLC. — Acquirer ID `0110` → Institution_ID `000110`
```
PUT {{baseUrl}}/trust-store/institutions/000110/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Habib Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### The Hong Kong And Shanghai Banking Corporation Ltd. — Acquirer ID `0115` → Institution_ID `000115`
```
PUT {{baseUrl}}/trust-store/institutions/000115/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "The Hong Kong And Shanghai Banking Corporation Ltd.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Ific Bank PLC. — Acquirer ID `0120` → Institution_ID `000120`
```
PUT {{baseUrl}}/trust-store/institutions/000120/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Ific Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Islami Bank Bangladesh PLC. — Acquirer ID `0125` → Institution_ID `000125`
```
PUT {{baseUrl}}/trust-store/institutions/000125/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Islami Bank Bangladesh PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Jamuna Bank PLC. — Acquirer ID `0130` → Institution_ID `000130`
```
PUT {{baseUrl}}/trust-store/institutions/000130/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Jamuna Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Janata Bank PLC. — Acquirer ID `0135` → Institution_ID `000135`
```
PUT {{baseUrl}}/trust-store/institutions/000135/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Janata Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Mercantile Bank PLC. — Acquirer ID `0140` → Institution_ID `000140`
```
PUT {{baseUrl}}/trust-store/institutions/000140/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Mercantile Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Mutual Trust Bank PLC. — Acquirer ID `0145` → Institution_ID `000145`
```
PUT {{baseUrl}}/trust-store/institutions/000145/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Mutual Trust Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### National Bank PLC. — Acquirer ID `0150` → Institution_ID `000150`
```
PUT {{baseUrl}}/trust-store/institutions/000150/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "National Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### National Bank Of Pakistan — Acquirer ID `0155` → Institution_ID `000155`
```
PUT {{baseUrl}}/trust-store/institutions/000155/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "National Bank Of Pakistan",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### National Credit & Commerce Bank PLC. — Acquirer ID `0160` → Institution_ID `000160`
```
PUT {{baseUrl}}/trust-store/institutions/000160/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "National Credit & Commerce Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### One Bank PLC. — Acquirer ID `0165` → Institution_ID `000165`
```
PUT {{baseUrl}}/trust-store/institutions/000165/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "One Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Prime Bank PLC. — Acquirer ID `0170` → Institution_ID `000170`
```
PUT {{baseUrl}}/trust-store/institutions/000170/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Prime Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Pubali Bank PLC. — Acquirer ID `0175` → Institution_ID `000175`
```
PUT {{baseUrl}}/trust-store/institutions/000175/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Pubali Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Rajshahi Krishi Unnayan Bank — Acquirer ID `0180` → Institution_ID `000180`
```
PUT {{baseUrl}}/trust-store/institutions/000180/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Rajshahi Krishi Unnayan Bank",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Rupali Bank PLC. — Acquirer ID `0185` → Institution_ID `000185`
```
PUT {{baseUrl}}/trust-store/institutions/000185/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Rupali Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Shahjalal Islami Bank PLC. — Acquirer ID `0190` → Institution_ID `000190`
```
PUT {{baseUrl}}/trust-store/institutions/000190/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Shahjalal Islami Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Social Islami Bank PLC — Acquirer ID `0195` → Institution_ID `000195`
```
PUT {{baseUrl}}/trust-store/institutions/000195/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Social Islami Bank PLC",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Sonali Bank PLC. — Acquirer ID `0200` → Institution_ID `000200`
```
PUT {{baseUrl}}/trust-store/institutions/000200/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Sonali Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Southeast Bank PLC. — Acquirer ID `0205` → Institution_ID `000205`
```
PUT {{baseUrl}}/trust-store/institutions/000205/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Southeast Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Standard Bank PLC. — Acquirer ID `0210` → Institution_ID `000210`
```
PUT {{baseUrl}}/trust-store/institutions/000210/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Standard Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Standard Chartered Bank — Acquirer ID `0215` → Institution_ID `000215`
```
PUT {{baseUrl}}/trust-store/institutions/000215/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Standard Chartered Bank",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### State Bank Of India — Acquirer ID `0220` → Institution_ID `000220`
```
PUT {{baseUrl}}/trust-store/institutions/000220/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "State Bank Of India",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### The City Bank PLC. — Acquirer ID `0225` → Institution_ID `000225`
```
PUT {{baseUrl}}/trust-store/institutions/000225/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "The City Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Icb Islamic Bank Ltd. — Acquirer ID `0230` → Institution_ID `000230`
```
PUT {{baseUrl}}/trust-store/institutions/000230/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Icb Islamic Bank Ltd.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### The Premier Bank PLC. — Acquirer ID `0235` → Institution_ID `000235`
```
PUT {{baseUrl}}/trust-store/institutions/000235/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "The Premier Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Trust Bank PLC. — Acquirer ID `0240` → Institution_ID `000240`
```
PUT {{baseUrl}}/trust-store/institutions/000240/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Trust Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### United Commercial Bank Limited — Acquirer ID `0245` → Institution_ID `000245`
```
PUT {{baseUrl}}/trust-store/institutions/000245/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "United Commercial Bank Limited",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Uttara Bank PLC. — Acquirer ID `0250` → Institution_ID `000250`
```
PUT {{baseUrl}}/trust-store/institutions/000250/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Uttara Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Woori Bank — Acquirer ID `0255` → Institution_ID `000255`
```
PUT {{baseUrl}}/trust-store/institutions/000255/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Woori Bank",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Nrb Commercial Bank PLC. — Acquirer ID `0260` → Institution_ID `000260`
```
PUT {{baseUrl}}/trust-store/institutions/000260/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Nrb Commercial Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Union Bank PLC. — Acquirer ID `0265` → Institution_ID `000265`
```
PUT {{baseUrl}}/trust-store/institutions/000265/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Union Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Sbac Bank PLC. — Acquirer ID `0270` → Institution_ID `000270`
```
PUT {{baseUrl}}/trust-store/institutions/000270/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Sbac Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Meghna Bank PLC. — Acquirer ID `0275` → Institution_ID `000275`
```
PUT {{baseUrl}}/trust-store/institutions/000275/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Meghna Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Padma Bank PLC. — Acquirer ID `0280` → Institution_ID `000280`
```
PUT {{baseUrl}}/trust-store/institutions/000280/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Padma Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Midland Bank PLC. — Acquirer ID `0285` → Institution_ID `000285`
```
PUT {{baseUrl}}/trust-store/institutions/000285/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Midland Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Nrb Bank PLC. — Acquirer ID `0290` → Institution_ID `000290`
```
PUT {{baseUrl}}/trust-store/institutions/000290/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Nrb Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Modhumoti Bank PLC. — Acquirer ID `0295` → Institution_ID `000295`
```
PUT {{baseUrl}}/trust-store/institutions/000295/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Modhumoti Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Global Islami Bank PLC. — Acquirer ID `0300` → Institution_ID `000300`
```
PUT {{baseUrl}}/trust-store/institutions/000300/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Global Islami Bank PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Shimanto Bank Limited — Acquirer ID `0305` → Institution_ID `000305`
```
PUT {{baseUrl}}/trust-store/institutions/000305/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Shimanto Bank Limited",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Community Bank Bangladesh PLC. — Acquirer ID `0310` → Institution_ID `000310`
```
PUT {{baseUrl}}/trust-store/institutions/000310/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Community Bank Bangladesh PLC.",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Bengal Commercial Bank — Acquirer ID `0315` → Institution_ID `000315`
```
PUT {{baseUrl}}/trust-store/institutions/000315/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Bengal Commercial Bank",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Citizens Bank Plc — Acquirer ID `0320` → Institution_ID `000320`
```
PUT {{baseUrl}}/trust-store/institutions/000320/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Citizens Bank Plc",
  "instituteType": "00",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

## PSP (type `03`)

### iPay Systems Limited — Acquirer ID `1001` → Institution_ID `031001`
```
PUT {{baseUrl}}/trust-store/institutions/031001/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "iPay Systems Limited",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Dmoney Bangladesh Limited — Acquirer ID `1002` → Institution_ID `031002`
```
PUT {{baseUrl}}/trust-store/institutions/031002/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Dmoney Bangladesh Limited",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Recursion FinTech Limited (Cash Baba) — Acquirer ID `1003` → Institution_ID `031003`
```
PUT {{baseUrl}}/trust-store/institutions/031003/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Recursion FinTech Limited (Cash Baba)",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Green & Red Technologies Ltd. — Acquirer ID `1004` → Institution_ID `031004`
```
PUT {{baseUrl}}/trust-store/institutions/031004/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Green & Red Technologies Ltd.",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Progoti Systems Ltd. — Acquirer ID `1005` → Institution_ID `031005`
```
PUT {{baseUrl}}/trust-store/institutions/031005/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Progoti Systems Ltd.",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Sheba Fintech Limited. — Acquirer ID `1006` → Institution_ID `031006`
```
PUT {{baseUrl}}/trust-store/institutions/031006/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Sheba Fintech Limited.",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### ABG Technologies Limited (Pocket) — Acquirer ID `1007` → Institution_ID `031007`
```
PUT {{baseUrl}}/trust-store/institutions/031007/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "ABG Technologies Limited (Pocket)",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Digital Payments Limited (Pathao Pay) — Acquirer ID `1008` → Institution_ID `031008`
```
PUT {{baseUrl}}/trust-store/institutions/031008/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Digital Payments Limited (Pathao Pay)",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Shamadhan Services Limited — Acquirer ID `1009` → Institution_ID `031009`
```
PUT {{baseUrl}}/trust-store/institutions/031009/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Shamadhan Services Limited",
  "instituteType": "03",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

## MFS (type `02`)

### ROCKET — Acquirer ID `2001` → Institution_ID `022001`
```
PUT {{baseUrl}}/trust-store/institutions/022001/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "ROCKET",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### bKash — Acquirer ID `2002` → Institution_ID `022002`
```
PUT {{baseUrl}}/trust-store/institutions/022002/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "bKash",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Islami Bank mCash — Acquirer ID `2003` → Institution_ID `022003`
```
PUT {{baseUrl}}/trust-store/institutions/022003/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Islami Bank mCash",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Trust Axiata Pay (TAP) — Acquirer ID `2004` → Institution_ID `022004`
```
PUT {{baseUrl}}/trust-store/institutions/022004/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Trust Axiata Pay (TAP)",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### UPAY — Acquirer ID `2005` → Institution_ID `022005`
```
PUT {{baseUrl}}/trust-store/institutions/022005/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "UPAY",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Rupali Bank SureCash — Acquirer ID `2006` → Institution_ID `022006`
```
PUT {{baseUrl}}/trust-store/institutions/022006/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Rupali Bank SureCash",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Islamic Wallet — Acquirer ID `2007` → Institution_ID `022007`
```
PUT {{baseUrl}}/trust-store/institutions/022007/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Islamic Wallet",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### OK Wallet — Acquirer ID `2008` → Institution_ID `022008`
```
PUT {{baseUrl}}/trust-store/institutions/022008/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "OK Wallet",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### MYCash — Acquirer ID `2009` → Institution_ID `022009`
```
PUT {{baseUrl}}/trust-store/institutions/022009/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "MYCash",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### FSIBL FirstPay SureCash — Acquirer ID `2010` → Institution_ID `022010`
```
PUT {{baseUrl}}/trust-store/institutions/022010/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "FSIBL FirstPay SureCash",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### TeleCash — Acquirer ID `2011` → Institution_ID `022011`
```
PUT {{baseUrl}}/trust-store/institutions/022011/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "TeleCash",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### MeghnaPay — Acquirer ID `2012` → Institution_ID `022012`
```
PUT {{baseUrl}}/trust-store/institutions/022012/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "MeghnaPay",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Nagad — Acquirer ID `2013` → Institution_ID `022013`
```
PUT {{baseUrl}}/trust-store/institutions/022013/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Nagad",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Prime Bank FinTech Limited — Acquirer ID `2014` → Institution_ID `022014`
```
PUT {{baseUrl}}/trust-store/institutions/022014/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Prime Bank FinTech Limited",
  "instituteType": "02",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

## PSO (type `04`)

### IT Consultants Ltd. — Acquirer ID `3001` → Institution_ID `043001`
```
PUT {{baseUrl}}/trust-store/institutions/043001/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "IT Consultants Ltd.",
  "instituteType": "04",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Software Shop Limited — Acquirer ID `3002` → Institution_ID `043002`
```
PUT {{baseUrl}}/trust-store/institutions/043002/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Software Shop Limited",
  "instituteType": "04",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### ShurjoMukhi Ltd. — Acquirer ID `3003` → Institution_ID `043003`
```
PUT {{baseUrl}}/trust-store/institutions/043003/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "ShurjoMukhi Ltd.",
  "instituteType": "04",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Portonics Limited — Acquirer ID `3004` → Institution_ID `043004`
```
PUT {{baseUrl}}/trust-store/institutions/043004/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Portonics Limited",
  "instituteType": "04",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Walletmix Limited — Acquirer ID `3005` → Institution_ID `043005`
```
PUT {{baseUrl}}/trust-store/institutions/043005/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Walletmix Limited",
  "instituteType": "04",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Soft Tech Innovation Limited — Acquirer ID `3006` → Institution_ID `043006`
```
PUT {{baseUrl}}/trust-store/institutions/043006/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Soft Tech Innovation Limited",
  "instituteType": "04",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

## WLAMA (type `05`)

### DGePay Services Limited — Acquirer ID `4001` → Institution_ID `054001`
```
PUT {{baseUrl}}/trust-store/institutions/054001/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "DGePay Services Limited",
  "instituteType": "05",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### SSLCOMMERZ Limited — Acquirer ID `4002` → Institution_ID `054002`
```
PUT {{baseUrl}}/trust-store/institutions/054002/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "SSLCOMMERZ Limited",
  "instituteType": "05",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```

### Zaytoon FinTech Limited — Acquirer ID `4003` → Institution_ID `054003`
```
PUT {{baseUrl}}/trust-store/institutions/054003/public-key
Content-Type: application/json

{
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEApq9nl1oQyOh1BObM2PzVw/Wfeo0w2T0WT1yVVoKnWoc=\n-----END PUBLIC KEY-----",
  "institutionName": "Zaytoon FinTech Limited",
  "instituteType": "05",
  "validFrom": "2026-09-13T00:00:00Z",
  "validTo": null
}
```
