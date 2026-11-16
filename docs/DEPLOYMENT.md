# Deployment guide (controlled production)

Target: a single-region deployment of the Docker images behind a TLS-terminating reverse proxy, with a managed or
self-hosted PostgreSQL 16 and Redis 7. Everything here is also the checklist for a **staging** deployment, which you should
do first (`docs/PRODUCTION-READINESS.md`).

> Verification status: the application, migrations, tests, backup/restore and recovery drill were executed. The
> Compose overlays (`docker-compose.prod.yml`, `docker-compose.monitoring.yml`) and the container image builds could **not**
> be executed in the authoring environment (no Docker daemon); their YAML was syntax-checked only. Treat the first staging
> run as their test.

## 1. Topology and trust boundaries

```
Internet ──TLS──► reverse proxy ──► web (Next.js, :3000)  ── BFF ──► api (:8080, internal)
                       │                                              ▲
                       ├── /api/v1/billing/webhooks/*  ─────────────────┤  payment provider webhooks (public)
                       ├── /api/v1/helpdesk/inbound-email ──────────────┤  mail relay (public, token)
                       └── :8443 mutual TLS passthrough (agents) ───────┘  Windows agents
                                api ──► PostgreSQL (solvix_app, RLS)   migrator ──► PostgreSQL (solvix_migrator)
                                api ──► Redis (cache only)
Internal only: /api/v1/platform/* (operator API), /health/*, metrics (OTLP out), Prometheus/Grafana.
```

* The browser only ever talks to **web**. The access token is an httpOnly cookie held by the web BFF; it never reaches
  browser JavaScript.
* **Public API routes** that must bypass the web proxy: billing webhooks and inbound e-mail (both authenticated by
  signature/secret, rate limited), and agent mutual TLS on `:8443`.
* **Never expose** `/api/v1/platform/*`, `/health/*`, `/scalar` or `/openapi` publicly (restrict at the proxy).
* Run the API as **two database roles**: `solvix_migrator` (owns the schema, used only by the migrator) and `solvix_app`
  (runtime, `NOSUPERUSER NOBYPASSRLS`). Never give the API the superuser or migrator credentials: row-level security is the
  last line of tenant isolation and only holds for a non-bypass role.

## 2. Prerequisites

* Docker Engine 25+ with Compose v2.24+ (the production overlay uses `!reset`), or an equivalent orchestrator.
* A DNS name + TLS certificate for the web/API origin; a **second certificate** (public CA or private CA that your agents
  pin) for the agent endpoint.
* **Device CA** (ECDSA P-384 PKCS#12) created offline and kept in your secret manager (see OPERATIONS → secrets).
* Secrets generated (examples): `openssl rand -base64 48` for the JWT signing key, operator key, passwords.

## 3. Configuration reference

All settings are environment variables (`Section__Key`). `.env.example` lists the compose variables. Required in production
are enforced by `docker-compose.prod.yml` (`${VAR:?…}`) and by startup validation (the API refuses to start with invalid
billing/helpdesk options).

| Setting | Default | Notes |
|---------|---------|-------|
| `ConnectionStrings__Postgres` / `__PostgresMigrator` / `__Redis` | – | runtime role / owner role / Redis with password |
| `Solvix__Jwt__SigningKey` | – | ≥ 32 chars; rotating it signs everyone out |
| `Solvix__Cors__AllowedOrigins__0` | – | the public web origin only |
| `Solvix__RateLimits__AuthPerMinute` | 20 | sign-in/registration/operator attempts per client IP per minute |
| `Solvix__Billing__Provider` | `manual` | `manual` \| `stripe` |
| `Solvix__Billing__TrialDays` / `TrialPlanCode` | 14 / `starter` | |
| `Solvix__Billing__GracePeriodDays` | 7 | enrollment still allowed after a period ends unpaid |
| `Solvix__Billing__RenewalLeadDays` / `InvoiceDueDays` | 14 / 14 | |
| `Solvix__Billing__DefaultTaxRateBasisPoints` | 0 | per-tenant override via the operator API |
| `Solvix__Billing__PublicAppUrl` | `http://localhost:3000` | checkout redirects; **set to the real origin** |
| `Solvix__Billing__OperatorApiKey` | empty (API disabled) | ≥ 32 chars |
| `Solvix__Billing__Stripe__SecretKey` / `__WebhookSecret` | – | required when provider is `stripe` |
| `Solvix__Billing__Seller__*` | – | issuer name/address/tax id/support e-mail on invoices |
| `Solvix__Billing__Worker__Enabled` / `IntervalMinutes` | true / 60 | renewal & expiry job |
| `Solvix__Helpdesk__Ai__Provider` / `ApiKey` / `Model` | `none` / – / `claude-haiku-5-5` | `anthropic` sends redacted ticket text externally |
| `Solvix__Helpdesk__Attachments__MaxBytes` | 10 MiB | per file |
| `Solvix__Helpdesk__Email__MaxTicketsPerSenderPerHour` | 20 | |
| `Solvix__Helpdesk__Worker__Enabled` / `IntervalMinutes` | true / 5 | SLA breach + escalation |
| `Solvix__Reporting__AvailabilityWorkerEnabled` / `AvailabilityIntervalMinutes` | true / 60 | availability trend sampler |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | enables OTLP metrics/traces export |
| `Solvix__Logging__Format` | json | JSON logs to stdout |

Phase 03 device settings (agent TLS, device CA, releases) are unchanged; see `PHASE-03-SUMMARY.md`.

## 4. First deployment

1. Create the stack secrets/`.env` (never commit it). Copy `.env.example`, then at minimum set strong
   `POSTGRES_PASSWORD`, `SOLVIX_DB_*_PASSWORD`, `REDIS_PASSWORD`, `SOLVIX_JWT_SIGNING_KEY`,
   `SOLVIX_CORS_ALLOWED_ORIGINS`, the agent/device-CA settings, `SOLVIX_PUBLIC_APP_URL`, and `BACKUP_PASSPHRASE_FILE`.
2. `docker compose -f docker-compose.yml -f docker-compose.prod.yml [-f docker-compose.monitoring.yml] up -d --build`.
   The `migrator` applies migrations as the schema owner and exits; the API starts only after it succeeded.
3. Put the reverse proxy in front (TLS 1.2+, HSTS, forward `X-Forwarded-*`; mutual-TLS **passthrough** for `:8443` or
   or terminate and forward the client certificate in `Solvix:Devices:ForwardedClientCertificateHeader` as described in `ARCHITECTURE.md` (only if the API is reachable exclusively through that proxy)).
4. Smoke test (below). Then take and **restore-test a backup** before onboarding customers.

### Smoke test

```bash
curl -fsS https://app.example.com/api/proxy/v1/billing/plans -o /dev/null -w '%{http_code}\n'   # 401 without a session: proxy + API up
curl -fsS http://api:8080/health/ready                                                           # from inside the network: DB + Redis
# Register a workspace in the UI: it must show a 14-day Starter trial under Settings → Billing.
# Create an enrollment token, enroll one agent, confirm it appears in Devices and usage shows 1 / 25.
# Helpdesk: create a ticket; create an e-mail channel and POST a test message (docs/HELPDESK.md); it must appear in Triage.
# Billing: buy a plan; with `manual` confirm via the operator API, with `stripe` pay with a test card and confirm Active.
```

## 5. Upgrades and rollback

* **Upgrade:** take a backup → build/pull new images → `docker compose up -d` (migrator runs first). Migrations are
  forward-only in production; each includes a `Down` that was exercised by the test-suite (`Migrations_can_be_rolled_back_and_reapplied`)
  but you should prefer **restore-from-backup** over running `Down` on live data.
* **Rollback of a bad release** (no schema change): redeploy the previous image tag. With a schema change: restore the
  pre-upgrade backup into a new database and repoint the API (`docs/OPERATIONS.md#restore`), accepting the data written since.
* New tenant tables must be added to a `*Security` migration (forced RLS); the test `Every_tenant_scoped_table_has_forced_rls_policy`
  fails the build otherwise.

## 6. Pre-go-live gates

Run the checklist in `PRODUCTION-READINESS.md`; do not onboard paying customers until every "owner action" row is closed.
