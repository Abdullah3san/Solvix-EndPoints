# Phase 12: Helpdesk, Reporting and SaaS commercialization (+ production readiness)

Status: **implemented and tested**; ready for a **staging deployment and controlled pilot**, not for general availability
(see `PRODUCTION-READINESS.md` for the exact gates).

## What was built

| Area | Delivered | Docs |
|------|-----------|------|
| **Helpdesk** | Ticket creation (web, API, **e-mail**), categories, priorities, assignment + auto-assignment, technician management (capacity, skills, on-duty), SLA policies (24×7 / business hours, pause on waiting), escalation workflows, comments + internal notes, attachments, resolution history + audit trail, AI/extractive ticket summaries, **triage queue for uncategorised e-mail tickets** | `HELPDESK.md` |
| **Reporting** | Device inventory, availability (with hourly trend), asset lifecycle, software inventory, patch compliance, security compliance, deployment, technician activity; JSON + CSV (formula-injection safe) | `REPORTING.md` |
| **SaaS** | Plans Starter 25 / Business 75 / Professional 200 / Enterprise custom, monthly and annual subscriptions, device licensing, usage + history, payment integration (Stripe hosted checkout + signed webhooks, or manual), invoice generation (HTML/print), billing history, renewal worker, grace period, upgrade proration, scheduled downgrades, usage limits, operator API for Enterprise | `BILLING.md` |
| **Security** | Server-enforced device limits (atomic, fail-closed), owner-only billing data, tenant isolation everywhere (RLS on 33 tables), webhook authenticity, 9 defects found and fixed during the phase | `SECURITY-REVIEW.md` |
| **Production** | Security review, automated tests, deployment guide, monitoring stack + alerts, backup/restore tooling, **executed recovery drill**, production compose overlay, CI pipeline, go/no-go checklist | `DEPLOYMENT.md`, `OPERATIONS.md`, `DR-TEST-REPORT.md`, `PRODUCTION-READINESS.md` |

## Decisions and assumptions you should know about

1. **Prices are placeholders** ($49/$129/$299 per month, annual = 10×). Device limits are exactly as specified. Change prices
   with one SQL statement (`BILLING.md`).
2. **The repository contains only Phases 01 and 03**; there is no patch-management or software-deployment module, so the
   *patch compliance*, *software inventory* and *deployment* reports are built from what the platform actually collects
   (OS build/revision, installed-software inventory, agent versions). Each report states its limits in its `notes`.
   If Phases 04–11 exist elsewhere, these reports should be extended to read their data.
3. **Billing model:** invoice-based renewals; payment provider abstraction with Stripe and manual implementations. Stored-card
   auto-renewal and dunning e-mails are not built (there is no outbound e-mail in the platform).
4. **E-mail intake** is a webhook (`POST /helpdesk/inbound-email` with a per-channel secret), not an IMAP/Graph poller;
   relay mail to it with Power Automate, an Azure Function or a provider webhook. Tickets arrive **uncategorised** by design and
   are routed from the Triage queue, with a keyword-based *suggestion*.
5. **AI summaries** default to a built-in extractive digest (no data leaves the platform). Claude is opt-in and redacted.
6. **New workspaces get a 14-day Starter trial;** existing workspaces were backfilled with a 30-day Starter trial by the
   migration. Entitlement is **fail-closed**: no subscription, no new devices (existing devices are never disabled).
7. Attachments are stored in PostgreSQL (backed up with the database); move to object storage for heavy use.

## Architecture changes

* New modules `Billing`, `Helpdesk`, `Reporting` (modular-monolith pattern of Phases 01/03). `Reporting` is a deliberate
  read-only consumer of Devices/Helpdesk entities.
* New SharedKernel contracts: `IDeviceEntitlementProvider` (Billing → Devices), `IDeviceUsageReader` (Devices → Billing),
  `ISubscriptionProvisioner` and `ITenantProvisioningStep` (registration hooks), `IDeviceDirectory`, `PaymentRequiredException`
  (HTTP 402), `ExternalServiceException` (502), business metrics (`SolvixMetrics`).
* **Database:** 6 migrations (`BillingAndSubscriptions`, `BillingSecurity`, `Helpdesk`, `HelpdeskSecurity`, `Reporting`,
  `ReportingSecurity`; each tenant table has forced RLS, composite tenant-safe foreign keys, check constraints; plan catalog is
  trigger-protected; 33 tenant tables in total). Devices gained `purchased_on`, `warranty_expires_on`.
* **Host:** security-headers middleware, shared auth rate limit, OpenTelemetry (OTLP) metrics/traces, `402/502/409(concurrency)`
  problem mappings, role-claim re-sync in tenant resolution.
* **Workers (all advisory-locked, idempotent):** billing renewal/expiry (hourly), helpdesk SLA/escalation (5 min),
  availability sampler (hourly).
* **Frontend:** Tickets (queues incl. Triage, create, detail with replies/notes/status/assignment/attachments/history/summary),
  Helpdesk settings (categories, technicians, SLA, escalation, e-mail channels), Reports (list + viewer + CSV), Billing
  (plan, usage meter, plan picker, invoices, profile, history). Navigation and role gating updated.

## Test results (run in this environment)

| Suite | Result |
|-------|--------|
| Agent tests | 85 passed |
| Unit tests | 175 passed (SLA calendar, ticket lifecycle, escalation, entitlement, proration, invoices, Stripe signature + parser, CSV, attachments, redaction, summariser, e-mail parsing, tokens, ReDoS bounds) |
| Integration tests (real PostgreSQL 16 + Redis, in-process API) | 152 passed (70 new: billing 32, helpdesk 16, e-mail intake 9, reporting 10, hardening 3) |
| Frontend | typecheck, lint, 29 tests, production build: pass |
| Recovery drill | PASSED (`DR-TEST-REPORT.md`) |
| Dependency scans | 0 vulnerabilities (NuGet incl. transitive, npm prod) |

Not run here: Docker image builds and the compose overlays/monitoring stack (no Docker daemon), a load test, and an external
penetration test.

## How to run

```bash
dotnet test                                   # needs PostgreSQL superuser + Redis (see README)
cd src/frontend && npm run check && npm run build
PGHOST=… PGUSER=postgres PGPASSWORD=… ./scripts/ops/dr-drill.sh      # recovery rehearsal
docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.monitoring.yml up -d   # staging
```

## Known limitations (also listed where relevant)

No outbound e-mail/notifications; no stored-card auto-renewal; no attachment malware scan; no PITR; no CA rotation procedure;
patch/deployment reports are inventory-derived; trial abuse controls are minimal; no data export/erasure workflows; no
load test. See `SECURITY-REVIEW.md` §4 and `PRODUCTION-READINESS.md`.
