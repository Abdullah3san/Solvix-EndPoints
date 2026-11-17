# Operations runbook

## 1. Monitoring

### What is instrumented

* **Health:** `GET /health/live` (process), `GET /health/ready` (PostgreSQL + Redis). Probe `ready` from outside the API
  network (blackbox exporter in the monitoring overlay) and use `live` for container liveness.
* **Logs:** structured JSON on stdout (Serilog compact), each line carries `CorrelationId`, `TraceId`, `TenantId`, `UserId`.
  Ship them to your log platform and keep ≥ 30 days. Request logging records the path, never headers, bodies or the
  `X-Channel-Token` / `X-Operator-Key` / `Stripe-Signature` values. Webhook payloads and payment provider error bodies are
  never logged.
* **Metrics & traces (OpenTelemetry):** set `OTEL_EXPORTER_OTLP_ENDPOINT` (done by `docker-compose.monitoring.yml`).
  ASP.NET Core request metrics, HTTP-client, .NET runtime, plus business counters (tag values never contain tenant or user data):

| Metric (Prometheus name) | Meaning |
|---|---|
| `solvix_devices_enrollments_denied_total{reason}` | enrollments refused by the subscription (`device_limit_reached`, `subscription_*`) |
| `solvix_billing_invoices_issued_total{purpose}` / `…payments_succeeded_total{provider}` | revenue flow |
| `solvix_billing_payment_mismatches_total{reason}` | **money needs attention** (amount, tenant, duplicate, void_invoice) |
| `solvix_billing_webhooks_rejected_total` | signature failures (secret rotated? probing?) |
| `solvix_billing_job_failures_total` | renewal/expiry job failed for a tenant |
| `solvix_helpdesk_tickets_created_total{source}`, `…tickets_escalated_total`, `…sla_breaches_total{kind}`, `…inbound_emails_total`, `…ai_summaries_total{source}` | helpdesk health |

* **Dashboards/alerts:** `docker/monitoring/` (collector, Prometheus, blackbox, Grafana dashboard `Solvix overview`,
  `alerts.yml`). Deliver alerts with Alertmanager or Grafana contact points to your on-call channel; `severity: page` rules
  should wake someone, `ticket` rules should not.

### Alert → action

| Alert | Likely cause | First steps |
|-------|--------------|-------------|
| `SolvixApiNotReady` (page) {#api-not-ready} | PostgreSQL or Redis down, API crashed, bad deploy | `docker compose ps`; `curl /health/ready` (the failing check is named); API logs; DB disk full? Redis only degrades caching, so a Redis-only failure is *not* fatal to requests but fails the probe: fix it, do not ignore it. |
| `SolvixHigh5xxRate` (page) | bug in a release, DB saturation, upstream outage | find `Unhandled exception` lines by `traceId`; roll back the last release if it started with a deploy |
| `SolvixPaymentMismatch` (page) | provider payment ≠ invoice | follow `docs/BILLING.md` *Reconciling payments*; refund duplicates in the provider dashboard |
| `SolvixWebhookSignatureFailures` | rotated/wrong `Stripe:WebhookSecret`, or probing | compare the secret in the provider dashboard with config; if unchanged and sustained, check source IPs and rate-limit at the proxy |
| `SolvixBillingJobFailing` | a tenant's renewal threw | grep `Billing job failed for tenant`; the job continues for others and retries hourly; renewals are idempotent |
| `SolvixEnrollmentsDeniedBurst` | a customer at its limit / lapsed | contact the customer; `GET usage`; raise limits via the operator API if intended |
| `SolvixAuthThrottling` | credential stuffing, signup abuse, a runaway client | identify the source address in access logs; block at the proxy/WAF |
| `SolvixSlaBreachSpike` | queue backlog, technicians offline | check *Escalated/Overdue* queues; staffing |
| `SolvixInboundEmailSilent` | mail relay stopped | check the forwarding flow and the channel's *last received* time |

### Routine checks

Daily: alerts quiet, last backup < 26 h old (`ls -l` backups, alert on the `BACKUP FAILED` log line). Weekly: disk growth
(attachments live in PostgreSQL), `dotnet list package --vulnerable` / `npm audit` (CI does it on every change), review
*Overdue* tickets and failed payments. Monthly: **restore a backup into scratch and run `verify-restore.sh`**.

## 2. Backup and recovery

### Strategy

| | |
|---|---|
| What | Logical dump of the whole `solvix` database (all tenants, **including ticket attachments and billing data**) |
| Tool | `scripts/ops/backup-postgres.sh` (`pg_dump -Fc`, verified with `pg_restore --list`, size floor, SHA-256, optional AES-256 encryption, retention pruning) |
| Schedule | `docker-compose.prod.yml` runs it daily (`BACKUP_INTERVAL_SECONDS=86400`); the script exits non-zero and logs `BACKUP FAILED` on any problem |
| **RPO** | **Up to the backup interval (24 h by default).** For lower RPO enable continuous WAL archiving with pgBackRest/WAL-G (outside this repo) and rehearse point-in-time recovery. |
| **RTO** | Dominated by data size. The drill measured seconds for a tiny dataset (`DR-TEST-REPORT.md`); **measure yours** by restoring a real backup. Budget: provision DB + restore time + verification + DNS/app switch. |
| Where | The backup volume is on the same host: **copy backups off-host** (object storage with versioning/immutability, another region/account) every run. A backup that dies with the server is not a backup. |
| Encryption | `BACKUP_PASSPHRASE_FILE`. Store the passphrase in your secret manager and in a **separate** sealed location from the backups; without it encrypted backups are unrecoverable. |
| Not in the DB | the device CA key, TLS certificates, `.env`/secrets. Back them up separately (section 4). Redis is a cache and needs no backup. |

### Backup

```bash
PGHOST=… PGUSER=solvix_migrator PGPASSWORD=… BACKUP_DIR=/var/backups/solvix \
BACKUP_PASSPHRASE_FILE=/run/secrets/backup_passphrase ./scripts/ops/backup-postgres.sh solvix
```

### Restore {#restore}

Always restore into a **new** database name, verify, then switch the application over; never restore over the live database.

```bash
export PGHOST=… PGUSER=<superuser> PGPASSWORD=… SOLVIX_DB_MIGRATOR_USER=solvix_migrator
export SOLVIX_DB_APP_USER=solvix_app SOLVIX_DB_APP_PASSWORD=…        # enables the runtime-role RLS check
BACKUP_PASSPHRASE_FILE=… ./scripts/ops/restore-postgres.sh /var/backups/solvix/solvix-solvix-<ts>.dump.enc solvix_restored
# runs verify-restore.sh automatically; re-run any time:  ./scripts/ops/verify-restore.sh solvix_restored
```

Then point `ConnectionStrings__Postgres` (runtime) at `solvix_restored`, restart the API, run the smoke test
(`DEPLOYMENT.md`). `verify-restore.sh` checks migration history, **forced RLS + policy on every tenant table**, helper
functions, the plan catalog, constraints, and that the runtime role sees no rows without a tenant.

### Recovery scenarios

| Scenario | Action |
|----------|--------|
| Bad deploy, schema unchanged | redeploy previous image tag |
| Bad deploy with migration | restore the pre-upgrade backup (above), accept loss since |
| Accidental data deletion in one tenant | restore into a scratch DB, extract that tenant's rows with `pg_dump --table … --where` / SQL (use the schema owner with `app.rls_bypass`), reinsert; do **not** restore over production |
| Corruption / lost database server | provision PostgreSQL + roles (`docker/postgres/init/01-roles.sh`), restore latest backup, switch over |
| Lost device CA key | agents must re-enroll with new tokens (certificates cannot be reissued); this is why the CA is backed up |
| Lost backup passphrase | encrypted backups are unrecoverable; rotate the passphrase *and* take a fresh backup immediately |

### Rehearsal

`scripts/ops/dr-drill.sh` is a complete rehearsal (CI runs it). Do a **production-data restore rehearsal at least
quarterly** and after any major schema change, record the measured time and update the RTO.

## 3. Day-2 tasks

* **Create/adjust an Enterprise customer:** `BILLING.md` operator API. **Confirm a bank transfer:** `mark-paid`.
* **Suspend a workspace's subscription:** `POST …/suspend` (blocks new devices only; existing devices keep reporting).
  There is **no API to block a tenant's sign-in**; if you must, set it in SQL as the schema owner:
  `UPDATE tenants SET status = 'Suspended', suspended_at_utc = now() WHERE id = '<tenant>';` (the tenant directory is cached
  for up to 5 minutes; users get `tenant_inactive`). Restore with `status = 'Active'`.
* **Change plan prices:** SQL as the schema owner (`BILLING.md`).
* **Rotate an inbound-mail secret:** UI → Helpdesk settings → E-mail intake → Rotate; update the relay.
* **Offboard a customer:** export their data (reports/CSV, tickets) on request, then follow your retention policy;
  tenant deletion is an explicit workflow (foreign keys are `RESTRICT` by design).

## 4. Secrets and keys {#secrets-and-keys}

| Secret | Where it lives | Rotation |
|--------|----------------|----------|
| DB passwords (`solvix_app`, `solvix_migrator`) | secret manager → env | `ALTER ROLE … PASSWORD`, update env, restart (rolling) |
| `Solvix__Jwt__SigningKey` | secret manager | replace + restart: all users re-login |
| Stripe secret / webhook secret | secret manager | roll in the Stripe dashboard (it supports overlapping secrets), update env, watch `webhooks_rejected` |
| `OperatorApiKey` | secret manager, held by few people | replace + restart |
| Inbound-mail channel secrets | shown once in UI, stored hashed | rotate in UI |
| AI provider key | secret manager | provider dashboard + env |
| Device CA PKCS#12 + password | **offline/HSM-backed store, two custodians** | **No rotation procedure is implemented** (single trusted CA): replacing it means re-enrolling every agent. Treat the key as long-lived and protect it accordingly; see the open items in `PRODUCTION-READINESS.md`. |
| Agent TLS certificate | proxy / API mount | before expiry; agents pin its SHA-256 if self-signed |
| Backup passphrase | separate from backups | rotate and re-encrypt on a schedule |

Never put secrets in images, compose files in git, or CI logs. The repository has no real secrets (development defaults are
clearly named `dev-only-…` and `docker-compose.prod.yml` refuses to start without real ones).

## 5. Capacity notes

* PostgreSQL holds attachments; plan disk for `attachments × 10 MiB` worst case and monitor growth. For heavy use, move blobs
  to object storage behind `AddAttachmentAsync`.
* The three background workers use one advisory lock each, so scaling the API horizontally is safe; work is done by one
  instance per tick.
* Reports are bounded (20,000 rows) and aggregate in SQL; very large fleets (≳ 50k devices per tenant) should move
  software/availability reporting to pre-aggregated tables.
