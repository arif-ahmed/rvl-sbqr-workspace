# S3 — Local Development Setup & Usage Guide

This guide covers **local development against the real AWS S3 bucket**
provisioned for SBQR: how the app is configured, how to authenticate, and
how to work with the bucket from the CLI and GUI tools.

> **History note** — local dev used to run against a LocalStack emulator
> (`docker compose up sbqr.localstack`). That container is gone; every
> environment (local dev, staging, production) now talks to the same kind
> of endpoint — real AWS S3 — through the same `IObjectStorage` adapter
> (`SBQR.SharedKernel.Storage.S3.S3ObjectStorage`, in
> `src/SharedKernel/SBQR.SharedKernel.Storage/`). Only the `Storage:*`
> config values differ per environment.

> **Reuse across modules** — `SBQR.Api`'s `Program.cs` registers one
> `IObjectStorageFactory` singleton for the whole host (one shared S3
> client). Any module resolves `IObjectStorageFactory` from DI and calls
> `Create("<own-folder-name>")` to get its own folder-scoped
> `IObjectStorage` — see `KeyCustodyModule`/`S3KeyVaultProvider` for the
> pattern. A module never needs to know the backend is S3, parse
> `Storage:*` config, or manage a client's lifetime itself.

---

## Table of contents

1. [Mental model](#1-mental-model)
2. [Prerequisites](#2-prerequisites)
3. [Getting bucket access](#3-getting-bucket-access)
4. [Application configuration](#4-application-configuration)
5. [Day-to-day CLI usage](#5-day-to-day-cli-usage)
6. [Automated tests](#6-automated-tests)
7. [Troubleshooting](#7-troubleshooting)

---

## 1. Mental model

```
┌────────────────────────────┐         ┌──────────────────────────────────┐
│  Developer workstation     │         │  AWS                             │
│                            │         │                                  │
│  aws CLI / mc / rclone ────┼─────────►  s3://<real-bucket>              │
│  S3 Browser / Cyberduck    │         │   └─ Storage:VaultFolder/keys/…  │
│                            │         │      (per-developer namespace)   │
│  SBQR.Api (dotnet run, or  │         │                                  │
│  docker compose) ──────────┼─────────►                                  │
└────────────────────────────┘         └──────────────────────────────────┘
```

Key facts:

| Fact | Value / rule |
|---|---|
| Endpoint | Real AWS S3 (region-resolved; `Storage:ServiceUrl` stays blank) |
| Credentials | Real IAM access key/secret, scoped to this one bucket. Never `test`/`test`. |
| Region | Whatever region the real bucket lives in — set `Storage:Region` to match, it must be correct for real AWS (unlike an emulator). |
| Bucket | One bucket, **shared by every developer**. Isolation between developers/modules comes from `Storage:VaultFolder` (see §4), not separate buckets. |
| Vault folder | `Storage:VaultFolder` is prepended to every object key by `S3ObjectStorage`. Set yours to something like `keycustody/<your-name>` so your dev signing-key objects never collide with a teammate's. |
| Prod fidelity | Same `AWSSDK.S3` client code as production — moving from local dev to production is a config change only (different bucket, different credential source, `Storage:ForcePathStyle=false`). |

---

## 2. Prerequisites

- An AWS account / IAM user with access to the shared dev bucket (ask
  whoever provisioned it for an access key, or an SSO/role you can assume).
- **AWS CLI v2** — optional but recommended for §5.
  Windows: `winget install Amazon.AWSCLI`. Check with `aws --version`.
- **Git Bash or PowerShell** — all commands below work in both (paths
  shown Git-Bash style).

---

## 3. Getting bucket access

Ask for (or provision, if you own the bucket):

- The **bucket name** and **region**.
- An **IAM access key + secret**, scoped to a least-privilege policy on
  just this bucket:

  ```json
  {
    "Version": "2012-10-17",
    "Statement": [
      {
        "Effect": "Allow",
        "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:ListBucket"],
        "Resource": [
          "arn:aws:s3:::<bucket-name>",
          "arn:aws:s3:::<bucket-name>/*"
        ]
      }
    ]
  }
  ```

  Prefer scoping `s3:ListBucket`/`s3:GetObject`/etc. further with an
  `s3:prefix` condition to your own `Storage:VaultFolder`, if the bucket
  owner supports per-developer policies.

- A **vault folder** name for yourself, e.g. `keycustody/<your-name>` —
  pick something that won't collide with teammates.

**Never** commit the access key/secret anywhere in the repo. They go in
one of two gitignored places (pick based on how you run the app — see §4):

- `dotnet user-secrets` (per-developer store outside the repo) — for `dotnet run`.
- `docker/.env` (already gitignored, copied from `docker/.env.example`) — for `docker compose`.

---

## 4. Application configuration

The `Storage:*` config keys are declared (with blank/safe defaults) in the
tracked `src/Host/SBQR.Api/appsettings.json`:

```json
"Storage": {
  "Provider": "S3",
  "ServiceUrl": "",
  "AccessKeyId": "",
  "SecretAccessKey": "",
  "Region": "us-east-1",
  "BucketName": "",
  "VaultFolder": "keycustody",
  "ForcePathStyle": false
}
```

- `ServiceUrl` stays **blank** for real AWS — the SDK resolves the
  region's default endpoint. It's only ever set to point at an
  S3-compatible emulator (LocalStack-style), in which case
  `ForcePathStyle` should be `true` too.
- `AccessKeyId` / `SecretAccessKey` blank means the AWS SDK's default
  credential chain (env vars → shared profile → SSO → instance role)
  is used instead of static keys — leave them blank there if you'd rather
  not put a key in a file at all, and export
  `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (or use an AWS CLI
  profile) in your shell instead.
- `VaultFolder` namespaces every object this instance writes/reads under
  that folder in the bucket — set it to your own folder locally.

### 4.1 Running with `dotnet run` (host-side)

Put the real values into user-secrets (the non-secret bucket coordinates
are already seeded by `scripts/dev-seed-user-secrets.ps1`/`.sh`; add your
personal credentials):

```bash
dotnet user-secrets set "Storage:AccessKeyId"  "AKIA..." \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
dotnet user-secrets set "Storage:SecretAccessKey" "..." \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
# Optional personal namespace (defaults to the seeded keycustody folder):
dotnet user-secrets set "Storage:VaultFolder" "keycustody/<your-name>" \
  --project src/Host/SBQR.Api/SBQR.Api.csproj
```

Then launch with the `S3-Vault` profile (flips
`Crypto:VaultProvider`/`KeyCustody:ActiveProvider` to the S3-backed vault):

```bash
dotnet run --project src/Host/SBQR.Api --launch-profile S3-Vault
```

> Note: the seed script already sets `Crypto:VaultProvider=S3` in
> user-secrets, so a plain `dotnet run` (default profile) uses the S3
> vault too; the `S3-Vault` profile additionally points
> `TrustStore:BaseUrl` at the local mock.

### 4.2 Running via `docker compose`

The container never sees your user-secrets store (it lives outside the
repo and the image), so real values go in `docker/.env` (gitignored,
copied from `docker/.env.example`) instead — same `Storage__*` env-var
keys the compose file already injects into `sbqr.api`:

```ini
Storage__AccessKeyId=AKIA...
Storage__SecretAccessKey=...
Storage__Region=ap-southeast-1
Storage__BucketName=sbqr-dev-shared
Storage__VaultFolder=keycustody/arif
```

```bash
docker compose -f docker/docker-compose.yml up -d
```

**Production guard (already in place):** `Program.cs` refuses to boot in
Production unless `Crypto:VaultProvider` is `Local` — the S3-backed vault
provider is a local-dev convenience only, never a Production path, so
there's nothing further to guard here for this change.

---

## 5. Day-to-day CLI usage

### 5.1 AWS CLI

```bash
aws s3 ls s3://<bucket-name>/<your-vault-folder>/                  # your objects only
aws s3 ls s3://<bucket-name>/<your-vault-folder>/keys/ --recursive
aws s3 cp s3://<bucket-name>/<your-vault-folder>/keys/100101_v1_private.pem ./
aws s3 rm s3://<bucket-name>/<your-vault-folder>/keys/100101_v1_private.pem
```

Configure a named profile (`~/.aws/config` + `~/.aws/credentials`) instead
of exporting env vars if you prefer — the SDK and CLI both honour it.

### 5.2 MinIO Client (`mc`) / GUI tools

`mc`, S3 Browser, Cyberduck, WinSCP, rclone all work unchanged against a
real bucket — point them at the real endpoint (no custom `ServiceUrl`,
HTTPS **on**, your real access key/secret) instead of `localhost:4566`.

---

## 6. Automated tests

`S3SigningKeyStoreTests` and `PemVaultSigningProviderNsecInteropTests`
(`tests/SBQR.Modules.KeyCustody.Tests/`) do **not** touch the real bucket.
They spin up an ephemeral LocalStack container per test run via
Testcontainers (`Infrastructure/LocalStackS3Fixture.cs`) — hermetic, free,
and safe to run offline/in CI. Only requirement: Docker available to the
test runner, same as the Postgres integration test fixtures.

---

## 7. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Unable to locate credentials` | No static keys configured and no AWS default-chain credentials available | Set `Storage:AccessKeyId`/`SecretAccessKey`, or export `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, or configure an AWS CLI profile |
| `AccessDenied` | IAM policy doesn't cover the bucket/prefix, or wrong region | Check the policy in §3 and that `Storage:Region` matches the bucket's actual region |
| Two developers' key objects seem to overwrite each other | Same `Storage:VaultFolder` (or none) | Give each developer their own `Storage:VaultFolder` |
| `PermanentRedirect` / signature errors | Wrong region, or `ForcePathStyle` left `true` against real AWS | Set `Storage:Region` to the bucket's real region; leave `ForcePathStyle` `false` for real AWS |
| Works with `aws` CLI, SDK fails | CLI profile/region differs from the app's `Storage:*` config | Make sure `Storage:Region`/credentials match what you tested with the CLI |
| Tests fail with a Docker-related error | Docker not running / not available to the test runner | Start Docker Desktop; Testcontainers needs it exactly like the Postgres integration tests do |

---

## Security notes

- Real credentials never belong in `appsettings.json`, `docker-compose.yml`,
  or `docker/.env.example` — only in the per-developer user-secrets store
  or `docker/.env` (gitignored).
- The bucket is shared across developers — nothing written there should be
  treated as production data, and it should never contain real tenant
  data carried over from staging/production.
- Rotate the shared dev IAM access key on the same cadence the team
  applies to other local-dev-only credentials (see AGENTS.md C4/C9).
