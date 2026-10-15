# SBQR DevOps Handoff — Pre-UAT (Dev)

**Ops creates the AWS resources below, in the order listed. The dev team deploys and configures the app.**
Region `ap-southeast-1` · Name `rvl-sbqr-<resource>-dev` · Tags `Project=SBQR`, `Env=dev` on every resource.

```
Internet ─443─▶ EC2 (Docker: sbqr-api, terminates TLS/mTLS) ─5432─▶ RDS PostgreSQL (private)
                  │  ▲
                  │  └── pulls image ◀── ECR ◀── GitHub Actions (OIDC) ──SSM──▶ EC2
                  └── IAM role ─▶ S3 key vault (private)
```

## Build order (each step needs the ones above it)

| # | Resource | Needs | Section |
|---|---|---|---|
| 1 | Network: VPC, 1 public subnet (EC2), ≥ 2 private subnets in different AZs (RDS) | — | §1 |
| 2 | Security groups `ec2-sg`, then `db-sg` | 1 (`db-sg` references `ec2-sg`) | §2 |
| 3 | S3 bucket `key-vault` (no policy yet) | — | §3 |
| 4 | IAM role + instance profile `rvl-sbqr-s3-dev` | 3 (bucket ARN) | §4 |
| 5 | S3 bucket policy | 3, 4 | §3 |
| 6 | EC2 key pair (import) | dev team's public key (**external input**) | §5 |
| 7 | ECR repository `rvl-sbqr-api` | — | §6 |
| 8 | RDS: subnet group, parameter group, instance | 1, 2 | §7 |
| 9 | SSM parameter: DB password | 8 | §7 |
| 10 | EC2 instance + Elastic IP | 1, 2, 4, 6 | §5 |
| 11 | DNS A record | 10 (Elastic IP), zone owner | §8 |
| 12 | GitHub OIDC provider + deploy role | 7, 10 (instance ID) | §9 |
| 13 | SNS topic, CloudWatch log groups + alarms, CloudTrail data events, EBS snapshots, Budget | 3, 8, 10 | §10 |

Steps 3, 7 and 8 have no dependency on each other and can run in parallel.

## 1. Network

Use the existing/default VPC if one exists.
- EC2 goes in a **public** subnet.
- RDS goes in **private** subnets: the DB subnet group needs ≥ 2 AZs.
- **No NAT Gateway** (~$45/month, not needed).

## 2. Security groups

**`rvl-sbqr-ec2-sg-dev`**

| In/Out | Port | Source |
|---|---|---|
| In | 22 | Office/VPN IP only (break-glass; deploys use SSM) |
| In | 443 | `0.0.0.0/0` now; tighten to FI/BB IPs when mTLS goes live (§11) |
| In | 80 | `0.0.0.0/0` (certificate issuance + redirect only) |
| Out | all | `0.0.0.0/0` |

**`rvl-sbqr-db-sg-dev`**: In 5432 from `rvl-sbqr-ec2-sg-dev` only.

## 3. S3 — key vault

`rvl-sbqr-key-vault-dev` — holds tenant QR signing keys (app-encrypted). Most sensitive data in the system.

- Block Public Access: all 4 ON · ACLs off · no static website
- Versioning ON · noncurrent versions expire after 30 days
- Encryption SSE-S3
- **Bucket policy (add after step 4):** deny `aws:SecureTransport=false`; allow only role `rvl-sbqr-s3-dev`, prefix `keycustody/`

## 4. IAM — instance role

`rvl-sbqr-s3-dev` (EC2 instance profile). No access keys anywhere.

| Policy | Purpose |
|---|---|
| Custom `rvl-sbqr-s3-policy-dev`: `s3:Get/Put/DeleteObject` + `ListBucket` on the vault bucket only | Key vault |
| AWS-managed `AmazonSSMManagedInstanceCore` | Deploys and shell access via SSM |
| AWS-managed `CloudWatchAgentServerPolicy` | Logs and memory/disk metrics |
| AWS-managed `AmazonEC2ContainerRegistryReadOnly` | Pull images from ECR |

## 5. EC2 — API host

| Item | Value |
|---|---|
| Name / type | `rvl-sbqr-api-dev` · `t3.medium` · Ubuntu 24.04 LTS |
| Disk | 30 GB gp3, encrypted |
| Public IP | Elastic IP `rvl-sbqr-eip-dev` (associate to the instance) |
| Metadata | **IMDSv2 required** |
| Attached | SG `rvl-sbqr-ec2-sg-dev` · instance profile `rvl-sbqr-s3-dev` · key pair `rvl-sbqr-key-dev` |
| Software | Dev team installs Docker, nginx and the CloudWatch Agent after first login. Ops needs to do nothing here. |

### 5.1 SSH key — who sends what

The private key never travels. Dev team generates the pair; Ops only receives the public half.

1. **Dev team** runs `ssh-keygen -t ed25519 -f rvl-sbqr-key-dev` and sends the `.pub` file to Ops (safe to attach to a ticket).
2. **Ops** imports it (EC2 → Key pairs → *Import key pair*) as `rvl-sbqr-key-dev`. Do **not** use "Create key pair": that makes AWS issue a private key that would then have to be shared.
3. **Dev team connects:** `ssh -i rvl-sbqr-key-dev ubuntu@<elastic-ip-or-dns>` (login user `ubuntu`). More people add their own public key to `~ubuntu/.ssh/authorized_keys`, with no Ops action.

## 6. ECR — image registry

`rvl-sbqr-api`: scan-on-push ON · lifecycle rule keeps the last 10 images. CI pushes images tagged with the git SHA.

## 7. RDS — PostgreSQL

| Item | Value |
|---|---|
| Name / engine | `rvl-sbqr-db-dev` · PostgreSQL 16 · `db.t4g.small` · Single-AZ |
| Storage | 20 GB gp3, **autoscaling ON, max 50 GB** (audit log only grows) |
| Initial DB / user | `sbqr_app` / `sbqr_admin` |
| Network | Private subnets, not publicly accessible · SG `rvl-sbqr-db-sg-dev` |
| Encryption | At rest ON · parameter group sets `rds.force_ssl=1` |
| Backup | Automated, 7 days |
| Logs | Export the `postgresql` log to CloudWatch |
| Password | After creation, store in SSM Parameter Store (SecureString) `/sbqr/dev/db-master-password`. Tell the dev team the path; never send the password in chat, git or tickets. |

## 8. DNS

| Item | Value |
|---|---|
| Record | `A  api.dev.sbqr.<company-domain>` → Elastic IP, TTL 300 |
| Who creates it | Whoever owns the zone: Route 53 (~$0.50/month per zone) or the existing registrar/DNS provider (free). Ops to confirm which. |
| Deadline | Must resolve **before** the dev team requests the TLS certificate (Let's Encrypt validates over port 80), and before mTLS (§11). |

Domain not decided yet? SSH and plain testing work by IP, but no certificate can be issued.

## 9. CI/CD — GitHub OIDC deploy role

Lets GitHub Actions push images and deploy without stored AWS keys.

- Create the GitHub OIDC identity provider (`token.actions.githubusercontent.com`).
- Create role `rvl-sbqr-gha-deploy-dev`, trusted only for repo `Relief-Validation/rvl-secure-bqr-manager`, branch `develop`.
- Permissions: ECR push to `rvl-sbqr-api`; `ssm:SendCommand` and `ssm:GetCommandInvocation` on the instance `rvl-sbqr-api-dev` only.

## 10. Monitoring & safety nets

- **SNS topic** `rvl-sbqr-alerts-dev` (subscribe the team's email).
- **Log groups**, retention **30 days** (the default is "never expire"): `/rvl-sbqr/dev/api`, `/rvl-sbqr/dev/nginx`. Dev team ships logs with the CloudWatch Agent.
- **Alarms** → the SNS topic:

| Alarm | Threshold |
|---|---|
| EC2 status check failed | any, 2 consecutive minutes |
| EC2 CPU | > 80% for 15 min |
| EC2 memory / disk used (agent metrics) | > 85% for 10 min |
| RDS free storage | < 5 GB |
| RDS CPU | > 80% for 15 min |
| RDS connections | > 80% of max |

- **CloudTrail data events** on the key-vault bucket only (object-level audit of the most sensitive data).
- **EBS snapshot** daily via Data Lifecycle Manager, keep 7.
- **Budget** $100/month, email alerts at 50/80/100%.

## 11. mTLS readiness (next deployment)

The API will terminate mTLS itself (`feature/mtls-server`). **No AWS change is needed now**, but do not design against it:

- **No ALB, CloudFront or WAF in front of the instance.** They end the TLS session and the client certificate never reaches the app. Clients go straight to the Elastic IP on 443. If a load balancer is ever needed, use an NLB in TCP pass-through mode.
- When mTLS goes live, Ops will be asked to **restrict 443 to the FI / Bangladesh Bank source IPs** (list supplied by the dev team).
- The server certificate is public (Let's Encrypt); the client-CA bundle is private and managed by the dev team on the host. Ops issues nothing.
- The API runs in a container, so the container port is mapped from the host. Dev team owns the code change this needs.

## 12. Cost (indicative, on-demand, 24×7)

| Resource | USD/month |
|---|---|
| EC2 `t3.medium` + 30 GB EBS | ~41 |
| RDS `db.t4g.small` + 20 GB | ~33 |
| Public IPv4 / Elastic IP (AWS charges $0.005/hr) | ~4 |
| S3, ECR, snapshots, CloudWatch logs/alarms | ~4–6 |
| **Total** | **~80–90** |

Confirm with the [AWS Pricing Calculator](https://calculator.aws). Stopping EC2 + RDS outside working hours saves ~$35/month, but only if no external tester needs the environment at night (RDS auto-restarts after 7 days stopped; the IPv4 charge continues while stopped).

## 13. Hand back to dev team

Instance ID + Elastic IP · SSH user (`ubuntu`) and confirmation the imported key is attached · DNS record name (or "pending") · RDS endpoint · bucket name · SSM path of the DB password · ECR repo URI · OIDC role ARN · SNS topic confirmation.
