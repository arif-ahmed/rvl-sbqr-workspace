# SBQR.Api — deployment guide (staging + production)

This document is the single source of truth for shipping `SBQR.Api` outside
the developer laptop. Local development uses `docker/docker-compose.yml`
(see `docker/postgres/README.md`); everything below assumes a Container
Registry + orchestrator.

> **TL;DR**
>
> 1. CI builds the image from `docker/Dockerfile.api` and pushes to ACR.
> 2. Staging runs on Azure Container Apps (single Flexible Server for both DBs).
> 3. Production runs on AKS with **two** PostgreSQL Flexible Server instances
>    for blast-radius isolation between the public-side and the private-key
>    vault. Connection strings come from Azure Key Vault. Database migrations
>    are owned by the external migration tool — the API image does not run
>    migrations itself.

---

## 1. Image build & push

The Dockerfile (`docker/Dockerfile.api`) is the same artefact for every
environment — local, staging, and prod. Build it once per release, tag it
with the git SHA, and push it to Azure Container Registry.

```bash
# From repo root.
ACR=<your-acr-name>.azurecr.io
SHA=$(git rev-parse --short HEAD)

az acr login --name "$ACR"

docker build \
    -f docker/Dockerfile.api \
    -t "$ACR/sbqr-api:$SHA" \
    -t "$ACR/sbqr-api:latest-staging" \
    .

docker push "$ACR/sbqr-api:$SHA"
docker push "$ACR/sbqr-api:latest-staging"
```

**Tagging discipline**

| Tag | Used by | Lifetime |
|---|---|---|
| `<git-sha>` | Every deployable revision | Immutable |
| `latest-staging` | The currently-deployed staging revision | Mutable |
| `latest-prod` | The currently-deployed production revision | Mutable |

`latest-*` tags exist so an operator can deploy "whatever is in staging"
without re-tagging — but every release gets the immutable `<git-sha>` tag
first, and that tag is what the audit trail records.

### What the image bakes in vs what gets injected

**Baked in** (at build time, immutable per SHA):
- The .NET 10 binary tree for `SBQR.Api`.
- Static Razor / wwwroot assets if any are added later.
- The container user `app` (UID 1000), tini, healthcheck wiring, and
  default `ASPNETCORE_URLS=http://+:8080`.

**Injected at runtime** (must come from secrets, never from image):
- `ConnectionStrings__sbqr_app`
- `ConnectionStrings__sbqr_key_vault`
- `Jwt__Authority`, `Jwt__Audience`, `Jwt__SigningKey` (when JWT is wired)
- `KeyCustody__ActiveProvider` (must be `PemVault` or `Hsm` in Production)
- Any Key Vault-backed KEKs for the wrapped private-key store
- Tenant-portal admin password hashes

---

## 2. Staging (Azure Container Apps)

**Why ACA, not AKS, for staging?** ACA scales to zero between deploys, is
cheaper to run 24×7, and exposes the same container contract the API
expects (HTTP/HTTPS, secrets-as-env-vars, liveness/readiness probes). AKS's
overhead only pays off at production scale and tighter network controls.

### 2.1 Resources

```
rg-sbqr-staging
├── acr-sbqrstg                       (Container Registry, Basic tier)
├── psql-sbqr-staging                 (PostgreSQL Flexible Server, General Purpose, 2 vCore)
│   ├── database: sbqr_app            (created by IaC, not the docker init script)
│   └── database: sbqr_key_vault      (same server, same firewall rule)
├── kv-sbqr-staging                   (Key Vault, standard tier)
│   ├── secret: ConnectionStrings--sbqr-app
│   ├── secret: ConnectionStrings--sbqr-key-vault
│   └── secret: Jwt--Authority        (et al.)
├── ca-env-sbqrstg                    (Container Apps Environment)
│   ├── app: sbqr-api                 (revision-managed; min 0, max 4 replicas)
│   └── job: sbqr-migrate-staging     (one-shot manual trigger, hosts the external migration tool — see §2.3)
└── appi-sbqr-staging                 (Application Insights for logs/metrics/traces)
```

**Why one Postgres server for both DBs in staging?** Staging mirrors the
local docker-compose stack for parity. Keeping the topology identical
between dev and staging means we never have a "well, it works in staging"
failure mode that is actually "the prod split-server topology isn't
exercised here." When we get to production we **do** split the servers
(see §3).

### 2.2 Connection-string injection

ACA mounts Key Vault secrets as environment variables on each container.
The names written into the Container App environment must match the
canonical `__`-doubled keys the host reads:

| ACA secret reference | Env var in container | Consumer |
|---|---|---|
| `kv-uri` of `ConnectionStrings--sbqr-app` | `ConnectionStrings__sbqr_app` | `SBQR.Api` via `IConfiguration` |
| `kv-uri` of `ConnectionStrings--sbqr-key-vault` | `ConnectionStrings__sbqr_key_vault` | `SBQR.Api` via `IConfiguration` |
| `kv-uri` of `Jwt--Authority` | `Jwt__Authority` | `IdentityAccessModule` |

> **Naming note.** Key Vault secret names use `--` (double dash) and ACA
> container env vars use `__` (double underscore). The Azure portal
> writes this mapping for you when you toggle "Add as environment variable"
> on a Key Vault secret reference — keep the auto-translated names.

### 2.3 Migrations before rollout

The API container does **not** apply the schema — database migrations are
owned by the external migration tool (see §4). CI invokes that tool as a
separate one-shot step against the staging Flexible Server before bumping
the API revision:

CI pipeline ordering for every staging deploy:

```
1.  docker build → push <sha>
2.  external migration tool runs against psql-sbqr-staging
3.  wait for "Succeeded"
4.  az containerapp update      sbqr-api              --image <sha>
5.  smoke-test /health/live and /health/ready, then redirect 5% traffic
```

If step 2 fails, step 4 is skipped — the previous revision keeps serving.

### 2.4 Health probes

ACA's probe configuration:

| Probe | Path | Initial delay | Period | Failure threshold |
|---|---|---|---|---|
| Liveness | `/health/live` | 15s | 30s | 3 |
| Readiness | `/health/ready` | 10s | 10s | 3 |

Both are wired in `SBQR.Api/Program.cs` and return `200 {"status":"live"}`
and `200 {"status":"ready"}` respectively. In epic-1 the readiness probe
gets a real DB-check; the liveness probe stays trivial (it must not pull
on shared dependencies — a transient DB blip should not cause Kubernetes
to kill the pod).

---

## 3. Production (AKS + two PostgreSQL Flexible Servers)

Production diverges from staging in three ways:

1. **Two PostgreSQL Flexible Server instances**, on different subnets, with
   different firewall rules. The app server can talk to the app DB; the
   app DB is in a subnet that **cannot** reach the vault subnet.
2. **AKS instead of ACA**, because we need HSM integration (Phase 2), pod-
   level network policies, and per-module scaling that ACA can't express as
   cleanly.
3. **Workload Identity (federated)** instead of stored ACR/KV credentials.

### 3.1 Resources

```
rg-sbqr-prod
├── acr-sbqrprod                       (Container Registry, Premium tier)
├── psql-sbqr-app-prod                 (Flexible Server in subnet-app-pg)
│   └── database: sbqr_app
├── psql-sbqr-vault-prod               (Flexible Server in subnet-vault-pg)
│   └── database: sbqr_key_vault       (separate firewall; separate AAD admin)
├── kv-sbqr-prod                       (Key Vault, premium tier — HSM-backed for phase 2)
│   ├── secret: ConnectionStrings--sbqr-app
│   ├── secret: ConnectionStrings--sbqr-key-vault
│   ├── secret: Jwt--Authority
│   ├── key:  sbqr-prod-kek            (RSA 2048 in Key Vault; HSM-Backed in phase 2)
│   └── hsm:  Managed HSM pool (phase 2)
├── aks-sbqr-prod                      (AKS, 3 system + 3 user node pools)
│   ├── node pool: sbqr-app             (DSv3, 3 nodes, runAsNonRoot enforced)
│   └── namespace: sbqr
│       ├── deployment: sbqr-api        (image from ACR via Workload Identity)
│       ├── cronjob:   sbqr-migrate      (hosts the external migration tool — manual + scheduled dry-run)
│       └── networkpolicy: deny-all + explicit allow-list
├── appi-sbqr-prod                     (Application Insights)
└── ddosplan-sbqr-prod                 (DDoS Protection on the hub vnet)
```

### 3.2 Subnet isolation — why two PostgreSQL FSVs

> **Invariant.** The `sbqr_key_vault` private-key material must NEVER be
> reachable from a process that can also reach `sbqr_app`. The defence-in-
> depth guarantee comes from network isolation, not just credentials.

| Subnet | Allowed inbound | Allowed outbound | Lives in |
|---|---|---|---|
| `subnet-app-pg` | AKS node CIDR only | AKS node CIDR + Key Vault | spoke `app-pg` |
| `subnet-vault-pg` | AKS node CIDR only | AKS node CIDR only | spoke `vault-pg` |

A Network Policy on each subnet enforces egress allow-lists. Even if
credentials for the vault DB leak into the application subnet (they
shouldn't), the vault subnet refuses the connection. Staging keeps both
DBs on one server for cost; production does not.

### 3.3 Workload Identity for ACR + Key Vault

AKS clusters get a managed identity (`aks-sbqr-prod-identity`). Federated
credentials bind that identity to a Kubernetes ServiceAccount named
`sbqr-api` in the `sbqr` namespace. The pod uses this identity to pull
from ACR and to read Key Vault — **no `imagePullSecrets`, no service-
principal passwords stored as K8s secrets**.

```yaml
# See Helm chart values; excerpt of the ServiceAccount + binding.
apiVersion: v1
kind: ServiceAccount
metadata:
  name: sbqr-api
  namespace: sbqr
  annotations:
    azure.workload.identity/client-id: <mi-client-id>
    azure.workload.identity/tenant-id: <tenant-id>
```

Key Vault access uses the [AKS Secrets Store CSI Driver][csi] to project
`ConnectionStrings--sbqr-app` and `ConnectionStrings--sbqr-key-vault`
secrets into the pod as files; the entrypoint reads them into env vars at
startup.

[csi]: https://learn.microsoft.com/azure/aks/csi-secrets-store-driver

### 3.4 Migrations as a Kubernetes Job (gated rollout)

A `CronJob` named `sbqr-migrate` runs **on demand** from CI plus a weekly
scheduled dry-run. Each invocation creates a one-shot `Job` from the
template; the job runs the external migration tool image and **must reach
Succeeded before the API `Deployment` rolls out**.

CI gate for production (sketch — concrete commands depend on the external
migration tool's CLI):

```bash
kubectl create job \
    --from=cronjob/sbqr-migrate \
    sbqr-migrate-$SHA \
    -n sbqr

kubectl wait --for=condition=complete \
    --timeout=10m job/sbqr-migrate-$SHA -n sbqr \
    || { echo "migration failed, aborting rollout"; exit 1; }

# Only now bump the Deployment.
kubectl set image deployment/sbqr-api sbqr-api=$ACR/sbqr-api:$SHA -n sbqr
```

Only the env vars (and therefore the DB the migration tool points at)
change between environments — the migration tool's own invocation is
constant.

### 3.5 Pod security — required to match the image

The image is built with the following invariants. Helm values must match.

| Image invariant | Helm value |
|---|---|
| Runs as UID 1000 (`app`) | `securityContext.runAsNonRoot: true` and `securityContext.runAsUser: 1000` |
| Listens on :8080 | `containerPort: 8080` |
| `/tmp`, `/home/app` are writable | `volumes` of type `emptyDir` mounted there, `securityContext.readOnlyRootFilesystem: true` |
| No new privileges | `securityContext.allowPrivilegeEscalation: false` |
| Drops all Linux capabilities | `securityContext.capabilities.drop: [ALL]` |
| SIGTERM is forwarded to dotnet | Container CMD runs under tini (baked into image) — Kubernetes sends SIGTERM, the pod gets a 30s graceful shutdown |

A Kyverno or Gatekeeper policy in the `sbqr` namespace rejects any pod
that does not set every field above.

### 3.6 Egress NetworkPolicy

The pod may only egress to:

- `<acr-name>.azurecr.io` (image pulls already-cached layers; AC pulls via Workload Identity)
- `*.postgres.database.azure.com` on :5432 (the two PG FSVs)
- `*.vault.azure.net` on :443 (Key Vault)
- `*.in.applicationinsights.azure.com` on :443 (telemetry)

A `NetworkPolicy` with `policyTypes: [Egress]` plus a single allow-rule +
default-deny enforces this. Ingress is handled by the Azure Load Balancer
fronting the AKS ingress controller.

### 3.7 Production cryptographic-boundary guard

The host (`Program.cs`) refuses to start in Production if the resolved
`ISigningProvider.ProviderId` starts with `plain-file`. The CI/CD pipeline
**rejects** any Helm release that sets `KeyCustody__ActiveProvider=PlainFile`
in the production values file (pre-commit hook + admission webhook).

```yaml
# helm/sbqr-api/values-prod.yaml (excerpt — DO NOT MERGE PlainFile here)
env:
  - name: ASPNETCORE_ENVIRONMENT
    value: Production
  - name: KeyCustody__ActiveProvider
    value: PemVault    # or "Hsm" in phase 2
```

A pre-deployment check in CI:

```bash
if grep -qx 'KeyCustody__ActiveProvider' helm/sbqr-api/values-prod.yaml; then
  PROVIDER=$(yq '.env[] | select(.name=="KeyCustody__ActiveProvider").value' \
                helm/sbqr-api/values-prod.yaml)
  case "$PROVIDER" in
    PemVault|Hsm) ;;                  # OK
    PlainFile|*) echo "Prod signing provider refused: $PROVIDER"; exit 1 ;;
  esac
fi
```

---

## 4. Migration runbook

Database migrations are owned by an external tool — the SBQR.Api image does
**not** apply the schema. Each environment exposes its own way of invoking
that tool:

| Env | Invocation |
|---|---|
| Local | Run the external migration tool against `sbqr.postgres:5432` (databases `sbqr_app` and `sbqr_key_vault`) — the API does not need to be running for the migration to succeed. |
| Staging | `az containerapp job start --name sbqr-migrate-staging --image <migrator-image>:<sha> --env-vars ConnectionStrings__sbqr_app=secretref:...` |
| Production | `kubectl create job --from=cronjob/sbqr-migrate sbqr-migrate-$SHA -n sbqr` (gated by the rollout pipeline in §3.4) |

### Operational notes

- **The external migration tool owns its own bookkeeping table.** Do NOT
  drop it manually; doing so will cause the tool to re-run every
  migration from scratch on the next run. Refer to the migration tool's
  own documentation for the table name it creates.
- **File-naming convention** for new migrations:
  `YYYYMMDDHHMMSS_short_description.sql`. The prefix is lexicographically
  sortable so the tool picks them up in chronological order.
- **No EF Core `dotnet ef database update`** anywhere, ever. EF Core
  migrations are forbidden — the SQL scripts are the source of truth.
- **No DDL outside the migration tool.** If you find yourself wanting to
  "just run this ALTER one time", don't — write it as
  `YYYYMMDDHHMMSS_*.sql` instead.

---

## 5. Rollback

| Env | Action |
|---|---|
| Local | `docker compose -f docker/docker-compose.yml down -v` (wipes Postgres) and `docker compose up --build` after pinning the previous image tag. |
| Staging | `az containerapp revision list -n sbqr-api -g rg-sbqr-staging`, then `az containerapp revision activate -n sbqr-api -g rg-sbqr-staging --revision <previous-revision>`. Connection strings unchanged. |
| Production | `kubectl rollout undo deployment/sbqr-api -n sbqr`. Connection strings unchanged. |

**DB rollback policy:** migrations are **additive only**. No release may
contain a destructive SQL statement (DROP, TRUNCATE of an active table,
ALTER COLUMN that narrows a type) without a *matching forward-fix*
migration. This guarantees that `rollout undo` plus the existing
migration-tool bookkeeping state is always a valid prior release.

If you discover a destructive migration after merge, write the forward-fix
migration **before** you write the rollback ticket.

---

## 6. Observability — where logs land

The SBQR.Api host writes ONLY to stdout (and stderr). Retention, rotation,
and shipping are the wrapper's job. This section pins the wrapper per
environment. The app image is identical across all four rows below; only
the wrapper changes.

### 6.1 Local — bare `dotnet run`

Stdout goes to the terminal. There is **no `logs/` directory** in this repo
by design. See `docs/logging-plan.md` §3.6.5 for the rationale. Default
mode for a developer running a smoke test or stepping through a request
with the debugger.

### 6.2 Local — Docker Compose

`docker/docker-compose.yml` pins `sbqr.api` to the `json-file` driver with
`max-size: 50m`, `max-file: 10` (~500 MB cap per container). Access:

```bash
docker compose -f docker/docker-compose.yml logs -f sbqr.api
docker compose -f docker/docker-compose.yml logs --tail=200 sbqr.api
```

To switch the dev stack to a remote sink (so logs survive
`docker compose down`), edit the `logging:` block — see
`docs/logging-plan.md` §3.5.3 for the `awslogs` / `gcplogs` / `loki`
driver shapes.

### 6.3 Staging — Azure Container Apps

ACA captures container stdout automatically. Two shipping options:

**Option A (default):** Container Apps environment diagnostic settings
→ Log Analytics workspace. Turn on `ContainerAppConsoleLogs` in the ACA
env's "Diagnostic settings" blade. Query in Log Analytics:

```
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "sbqr-api"
| where Log_s contains "correlation_id"
| project TimeGenerated, Log_s, ContainerAppName_s
```

Retention: set on the Log Analytics workspace (30 days default; bump to
90 if the regulator audit window warrants it).

**Option B:** Application Insights connection string in env
(`APPLICATIONINSIGHTS_CONNECTION_STRING=...`) + the
`Microsoft.ApplicationInsights.AspNetCore` SDK already wired in
`Program.cs`. Lets you correlate stdout with HTTP requests + dependency
calls in one tool. Used in staging to validate the trace story end-to-end
before prod. Not used in prod because Azure Monitor's per-GB cost is
significant at the FI's request volume; AKS uses Loki + Fluent Bit
instead (cheaper, owned by us).

Access the staging log:

```bash
az containerapp logs show -n sbqr-api -g rg-sbqr-staging --tail 200
```

### 6.4 Production — AKS + Fluent Bit + Loki

The AKS node's kubelet tails every container's stdout to
`/var/log/pods/<ns>_<pod>/<container>/0.log` on the node. A **Fluent Bit
DaemonSet** (one pod per node) tails those files, parses the JSON,
**applies a PII scrubber** (strips any `recipient_pan` / `recipient_name`
keys that slipped through, plus the raw `qr_payload` if it ever appears),
and pushes to Loki. Grafana queries Loki.

The app stays unchanged — same stdout-only contract. Fluent Bit config
(in the Helm chart) is the single PII defense-in-depth point.

```bash
# Operator runbook:
kubectl -n sbqr logs -l app=fluent-bit --tail=100          # is the shipper healthy?
kubectl -n sbqr port-forward svc/loki 3100:3100            # browse in Grafana via http://localhost:3000
logcli -addr=http://localhost:3100 query '{app="sbqr-api"}' | head
```

Retention:

- Loki hot tier: 30 days on the cluster's local PVCs
- Loki cold tier: 1 year on S3 (the regional bucket; cross-region
  replication per the bank's DR plan)

> **Note:** regulator `audit_logs` (PostgreSQL hash-chain) is a SEPARATE
> store, on its own retention tier (7-year tamper-evident per Bangladesh
> Bank ICT guidelines). Application logs are operational, not
> regulatory — never confuse the two.

### 6.5 Quick reference table

| Env | Wrapper | Sink | Retention |
|---|---|---|---|
| Dev (`dotnet run`) | The terminal emulator | Terminal scroll buffer | None |
| Dev (Compose) | Docker `json-file` driver | Container json-file under `/var/lib/docker/containers/...` | 50 MB × 10 files (~500 MB) |
| Staging (ACA) | ACA log stream → Log Analytics workspace | Application Insights / Log Analytics | 30 days hot, configurable |
| Prod (AKS) | kubelet tails container stdout → Fluent Bit DaemonSet → Loki | Loki + Grafana | 30 days hot in Loki; 1 year cold in S3 |

---

## 7. References

- `docs/design/database-design.md` — two-database schema source-of-truth.
- `docs/design/tactical-design.md` — module / host topology.
- `db/migrations/README.md` — migration naming convention and the external
  migration tool's expectations of the script set.
- `docker/postgres/README.md` — local Postgres init script semantics.
- `docker/.env.example` — tracked local-dev override template.
