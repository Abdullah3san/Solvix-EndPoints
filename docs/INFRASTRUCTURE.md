# Production Infrastructure (V1: Docker Compose on a VPS)

**Status:** Requirements and target layout for review. Decisions: ADR-001, ADR-002
in [DECISIONS.md](DECISIONS.md).

## 1. Topology

```
                         Internet (443/80 only)
                                  │
                         ┌────────▼────────┐  automatic HTTPS (ACME), on-demand TLS for
                         │      Caddy      │  verified custom domains, WebSockets, HSTS
                         └─┬──────┬──────┬─┘
             app.<domain>  │      │      │ agents.<domain>
     custom domains (UI)   │      │      │
                    ┌──────▼─┐    │   ┌──▼──────┐
                    │  web   │    │   │ gateway │──── guacd (Phase 09, internal only)
                    └───┬────┘    │   └──┬──────┘
                        │ internal│network│
                    ┌───▼────┐    │   ┌──▼─────┐
                    │  api   │◄───┘   │ worker │
                    └───┬────┘        └──┬─────┘
            ┌───────────┴────────────────┴────────────┐
            │ postgres (pgdata volume)   redis (AOF)  │  "data" network, no published ports
            └─────────────────────────────────────────┘
                              │ WAL archive + base backups (encrypted)
                              ▼
          off-site S3-compatible storage (different provider or region)
          app object storage (packages, exports, recordings, command output)
```

* The `api` is **not** published publicly in V1. The browser reaches it only through the
  BFF. A public integration API hostname is added post-V1 together with API keys.
* Networks: `edge` (caddy, web, gateway), `app` (web, api, gateway, worker, guacd) and
  `data` (api, gateway, worker, postgres, redis). Only Caddy publishes ports.
* All application images are built in CI, scanned (Trivy) and pushed to a registry with
  immutable tags. The VPS pulls by digest; nothing is built on the server.

## 2. Sizing

| Stage | Devices | VPS | Disk |
|-------|---------|-----|------|
| Staging | ≤ 10k simulated | 4 vCPU / 16 GB | 160 GB NVMe |
| Production launch | ≤ 1,000 | 4 vCPU / 16 GB | 160 GB NVMe |
| Production growth | ≤ 10,000 | **8 vCPU / 32 GB** | **400 GB NVMe** |

Indicative memory budget at 10k devices: PostgreSQL 8–10 GB (`shared_buffers` 8 GB),
gateway 1–2 GB, api 1 GB, worker 1 GB, Redis 1 GB, web 0.5 GB, Caddy 0.2 GB, OS and
cache the rest. Required OS tuning: `nofile` ≥ 65,536 for Caddy and the gateway, and TCP
keepalive settings.

## 3. Domains and TLS
* Platform hostnames: `app.<domain>` (UI) and `agents.<domain>` (agents). They are
  separate so the agent endpoint can get its own rate limits and WAF rules, and so its
  certificate stays stable (OD-8).
* **Custom domains (UI only):** the tenant or MSP adds a domain → we issue a DNS TXT
  challenge → after verification it is marked `verified` → Caddy's on-demand TLS `ask`
  endpoint (served by the api) allows issuance only for verified domains. CNAME to
  `app.<domain>`. Branding is looked up by host; **authorization never depends on host**
  (CH-7).
* TLS 1.2+/1.3, HSTS. Let's Encrypt via Caddy, with a ZeroSSL fallback.

## 4. Data protection and recovery

| Item | Requirement |
|------|-------------|
| PostgreSQL backups | pgBackRest or WAL-G: continuous WAL archiving + nightly full/differential to off-site S3-compatible storage, client-side encrypted |
| Targets | **RPO ≤ 5 min, RTO ≤ 4 h** (single-VPS V1) |
| Backup retention | 35 days |
| Restore tests | quarterly full restore into staging, recorded (SOC 2 evidence) |
| Redis | AOF everysec; contents are reconstructable, so no backup needed |
| Object storage | provider-managed durability, versioning, lifecycle rules for retention |
| Configuration and secrets | SOPS-encrypted in a private infra repository; restoring a server = provision VPS + pull secrets + `docker compose up` + restore DB |
| Disk encryption | use provider volume encryption if offered; otherwise LUKS on the data volume (document the unlock procedure) |

## 5. Secrets
Docker Compose `secrets:` mounted as files (`/run/secrets/*`). The application reads
`*_FILE` variables, so no secret appears in `docker inspect` or the environment. KMS
credentials for signing are scoped to `sign` on specific keys only. See SECURITY §12.

## 6. Observability
* Logs: Serilog JSON to stdout → a collector (Grafana Alloy / Vector) → Loki or a hosted
  log service, retained 90 days. Logs never contain tokens, passwords or envelope
  signatures (destructuring policies + tests).
* Metrics and traces: OpenTelemetry → Prometheus/Grafana (self-hosted on staging, hosted
  for production is acceptable). Key SLIs: API p95, gateway connections, heartbeat lag,
  telemetry queue depth, offline-detector run time, DB connections and replication/backup
  lag, disk usage.
* Alerting: external uptime checks (outside the VPS), backup failure, certificate
  expiry < 14 days, disk > 70/85%, queue saturation, error rate.

## 7. Host hardening
SSH key-only, no root login, restricted by source IP or through a VPN; automatic
security updates; host firewall (nftables/ufw) allowing only 80/443 plus restricted SSH;
Docker daemon not exposed; containers run as non-root with read-only root filesystems
where possible (the existing images already run as non-root); fail2ban for SSH; time
sync (chrony), which command expiry depends on.

## 8. Deployment process
1. CI builds, tests, scans and signs images (cosign) → tags `vX.Y.Z`.
2. The migrator runs first (`docker compose run --rm migrator`). Migrations must be
   backward compatible with the previous app version (expand → migrate → contract
   across two releases).
3. `docker compose up -d` service by service: worker → api → gateway → web. Agents
   reconnect with jitter; the BFF shows a maintenance banner on brief API unavailability.
4. Rollback = previous image digests. Schema changes are designed so the previous
   version still works.

Zero-downtime deploys (blue/green behind Caddy) are a post-V1 improvement.

## 9. Region and residency (PR-5)
The region is chosen when the VPS, backup and object storage are provisioned and is
recorded as `Solvix:Deployment:Region`. New tenants store the region they were created
in. All sub-processors (VPS, storage, e-mail, later OpenAI) are listed with their
regions in the DPA. Multi-region is out of scope for V1.

## 10. Future on-premises edition (designed for, not built)
The same Compose file with different providers: S3-compatible storage (customer-provided),
file-based signing key or customer HSM, SMTP for e-mail, and an optional or
customer-supplied AI endpoint. No code path may assume a specific cloud vendor; every
external dependency sits behind an interface registered from configuration.

## 11. Pre-production checklist (exit of Phase 10)
External penetration test of web, gateway and agent; restore drill; load test at 10k
simulated devices; secrets rotation drill; incident response runbook; DPA and
sub-processor list; status page; the security contact (`security.txt`).
