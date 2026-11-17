# Production readiness: go / no-go

Legend: ✅ done and verified in this repository · 🟡 implemented, **must be verified in staging** (could not be executed here) ·
🔶 **owner action** (a business/ops decision or secret only you can provide) · ❌ not implemented (stated plainly).

## A. Decisions only you can make 🔶

| Item | Where | Default shipped |
|------|-------|-----------------|
| **Plan prices** (device limits are as specified) | `BillingSecurity` migration / `docs/BILLING.md` | **Placeholders**: $49 / $129 / $299 per month, annual = 10× |
| Trial length and the plan trials start on | `Billing:TrialDays` / `TrialPlanCode` | 14 days, Starter |
| Grace period after a missed payment | `Billing:GracePeriodDays` | 7 days |
| Payment provider and account | `Billing:Provider` | `manual` (invoices + operator confirms) |
| Tax rate(s), seller legal entity/address/tax id | `Billing:DefaultTaxRateBasisPoints`, `Billing:Seller:*` | 0 %, "Solvix" |
| Whether to enable the external AI provider (data processing) | `Helpdesk:Ai:Provider` | `none` |
| Backup RPO (daily vs continuous WAL) and off-host destination | `OPERATIONS.md` | daily, local volume |
| Data retention / deletion policy and privacy terms | — | none |
| On-call, alert routing, support contact | — | none |

## B. Security 

| Check | Status |
|-------|--------|
| Tenant isolation: RLS forced on all 33 tenant tables, runtime role `NOBYPASSRLS`, automated guard for new tables | ✅ |
| Server-side device limit, fail-closed entitlement, no frontend trust | ✅ (tests) |
| Billing data owner-only; no card data stored; webhook signature/replay/idempotency/tenant checks | ✅ (tests) |
| Role changes effective immediately | ✅ (found and fixed, F1) |
| Rate limiting on auth, registration, enrollment, webhooks, inbound mail, operator API | ✅ |
| Security headers; hostile upload/e-mail handling; ReDoS bounds | ✅ |
| Dependency vulnerabilities (NuGet, npm prod) | ✅ 0 findings today; CI re-checks every change |
| Secrets: none in repo; prod compose refuses dev defaults | ✅ / 🟡 compose |
| TLS termination, HSTS, proxy header trust, `/platform` and `/health` not public | 🔶 your proxy |
| External penetration test, SAST/DAST, container image scan | ❌ see `SECURITY-REVIEW.md` O1–O3 |
| Attachment malware scanning | ❌ O1 |

## C. Reliability and recovery

| Check | Status |
|-------|--------|
| Migrations apply on an empty DB, roll back and re-apply (tested), backfill existing tenants | ✅ |
| Backup script: verified, checksummed, encrypted, pruned; non-zero exit on failure | ✅ |
| **Recovery drill**: destroy DB, restore, integrity checks, app serves identical data with RLS intact | ✅ (`DR-TEST-REPORT.md`, also in CI) |
| Restore timing on production-sized data (RTO) | 🔶 measure on a real backup |
| Continuous WAL archiving / PITR (if 24 h RPO is too long) | ❌ not included |
| Off-host copy of backups + passphrase custody | 🔶 |
| Device CA and TLS key backup/custody; CA rotation procedure | 🔶 / ❌ (O8) |
| Workers safe on multiple instances (advisory locks, idempotent writes) | ✅ |

## D. Observability

| Check | Status |
|-------|--------|
| JSON logs with correlation/tenant ids, secrets not logged | ✅ |
| Health endpoints (live/ready) | ✅ |
| Business + runtime metrics exported over OTLP (verified by test that counters fire) | ✅ |
| Collector, Prometheus, blackbox, Grafana dashboard, 12 alert rules (YAML/JSON validated) | 🟡 not executed; ensure alert delivery (Alertmanager/Grafana) to a human |
| Runbook for each alert | ✅ `OPERATIONS.md` |

## E. Quality gates

| Gate | Result |
|------|--------|
| .NET tests | **412 passing**: 85 agent + 175 unit + 152 integration against real PostgreSQL 16 + Redis (70 new integration tests this phase) |
| Frontend | typecheck, lint, 29 tests, `next build` ✅ |
| CI pipeline (build, tests, audits, recovery drill, image build) | 🟡 authored (`.github/workflows/ci.yml`), first run will tell |
| Load / soak test | ❌ not done; run before onboarding more than a pilot |

## F. Controlled rollout plan

1. **Staging** with production-like config: `docker-compose.prod.yml` + monitoring, real TLS, real device CA, Stripe **test mode**
   (or `manual`). Run the smoke test in `DEPLOYMENT.md`; trigger every alert once (stop Redis, send a bad webhook signature,
   fill a plan) and confirm a human is notified.
2. **Restore rehearsal on staging data** and record RTO.
3. **Pilot (3–5 friendly tenants)**: provider `manual`, AI `none`, operator API on an internal network only. Watch dashboards
   daily for two weeks. Do the first invoices/payments end-to-end by hand with finance.
4. **Widen** to Stripe live mode after a test-mode soak and a reconciliation dry run; then consider AI summaries per tenant.
5. **General availability** only after: external penetration test, attachment scanning, trial-abuse controls, load test,
   retention/erasure policy.

**Rollback triggers (pilot):** any cross-tenant data exposure; a payment applied to the wrong tenant/invoice; device limits
failing open; `SolvixHigh5xxRate` > 30 min after a release; a failed restore. Action: redeploy the previous release, suspend
the affected feature (set provider `manual`, disable workers/e-mail channel) and restore from backup if data was affected.

## G. Go / no-go

**GO for a controlled pilot** when: A is decided, the 🔶 items in B and C for pilot scope are closed, staging verified every 🟡
row, and a restore rehearsal met your RTO. **NO-GO** if any ❌ you consider mandatory for your customers (malware scanning,
PITR, pen test) is not accepted as a documented risk.
