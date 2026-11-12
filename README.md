# Solvix EndPoints

Multi-tenant SaaS platform.

* **Phase 01** – foundation: tenant registration and management, company profiles,
  branches and departments, a Next.js application shell, and tenant isolation enforced in
  the application *and* in PostgreSQL (Row-Level Security). See `docs/PHASE-01-SUMMARY.md`.
* **Phase 02** – identity and access management: sign-in with server-side sessions and
  rotating refresh tokens, e-mail verification, password reset, TOTP multi-factor
  authentication, account lockout and rate limiting, configurable password and session
  policy, tenant-scoped roles with permission-based authorization, invitations, user
  activation/deactivation and an append-only authentication audit log.
  See `docs/PHASE-02-SUMMARY.md`.

| Layer      | Stack                                                                 |
|------------|-----------------------------------------------------------------------|
| Frontend   | Next.js 16 (App Router, TypeScript, Tailwind CSS 4), Vitest           |
| Backend    | ASP.NET Core 10, EF Core 10, Npgsql, Serilog, FluentValidation, Asp.Versioning |
| Data       | PostgreSQL 16 (RLS), Redis 7                                          |
| Local dev  | Docker Compose                                                        |

## Quick start (Docker)

```bash
cp .env.example .env            # adjust secrets if you like
docker compose up --build
```

| Service   | URL                                   |
|-----------|---------------------------------------|
| Web       | http://localhost:3000                 |
| API       | http://localhost:8080 (Scalar UI at `/scalar/v1`, OpenAPI at `/openapi/v1.json`) |
| Health    | http://localhost:8080/health/ready    |
| Mail      | http://localhost:8025 (Mailpit: verification, reset and invitation e-mails) |

`migrator` applies EF Core migrations as the schema-owner role and exits; `api`
only starts once it succeeded. Register a workspace at http://localhost:3000/register,
then open the confirmation e-mail in Mailpit before signing in.

## Running without Docker

Prerequisites: .NET 10 SDK, Node 22, PostgreSQL 16, Redis 7.

```bash
# 1. database + roles (needs a superuser connection)
PGHOST=localhost PGUSER=postgres PGPASSWORD=postgres ./scripts/create-local-db.sh solvix

# 2. API (applies migrations on startup in Development)
dotnet run --project src/backend/Solvix.Api

# 3. Web
cd src/frontend && npm install && npm run dev
```

Development settings live in `src/backend/Solvix.Api/appsettings.Development.json`
(Redis password `solvix_redis_dev`; start Redis with `redis-server --requirepass solvix_redis_dev`
or change the connection string). Without an SMTP server, e-mails (including their
one-time links) are written to the API log in Development only.

## Repository layout

```
src/backend/
  Solvix.SharedKernel/        base entities, tenant context, exceptions, module contract
  Solvix.Infrastructure/      EF Core context, interceptors, migrations, RLS, Redis, tenant resolution
  Modules/
    Solvix.Modules.Tenancy/      tenant entity, registration, management, tenant directory
    Solvix.Modules.Identity/     users, sessions, MFA, roles & permissions, invitations, audit
    Solvix.Modules.Organization/ company profile, branches, departments
  Solvix.Api/                 host: DI, Serilog, problem details, versioning, auth, OpenAPI, health
  Solvix.Migrator/            one-shot migration runner (Docker init container)
src/frontend/                 Next.js app (BFF proxy, app shell, feature pages)
tests/
  Solvix.UnitTests/           domain + interceptor + naming tests (in-memory)
  Solvix.IntegrationTests/    real PostgreSQL + Redis: migrations, RLS, API isolation, jobs
docker/postgres/init/         creates the migrator and runtime roles
docs/                         architecture and phase summaries
```

## Tests

```bash
dotnet test                                           # unit + integration
cd src/frontend && npm run check                      # typecheck + lint + vitest
```

Integration tests need a PostgreSQL superuser connection and a Redis instance:

| Variable               | Default                                                                 |
|------------------------|-------------------------------------------------------------------------|
| `SOLVIX_TEST_PG_ADMIN` | `Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres` |
| `SOLVIX_TEST_REDIS`    | `localhost:6379`                                                        |

Each run creates and drops its own database (`solvix_test_*`), using the same
two-role layout as production so Row-Level Security is really exercised.

## Database migrations

```bash
dotnet tool install -g dotnet-ef
dotnet ef migrations add <Name> \
  --project src/backend/Solvix.Infrastructure \
  --startup-project src/backend/Solvix.Infrastructure \
  --output-dir Persistence/Migrations
```

Every new tenant-owned table (any entity implementing `ITenantScoped`) must be protected
in a migration with `RowLevelSecurity.EnableTenantIsolation` and listed in
`RowLevelSecurity.AllTenantTables`. The integration test
`MigrationTests.Every_tenant_scoped_table_has_forced_rls_policy` fails otherwise.

## Adding a protected endpoint

Add a permission to `SharedKernel/Authorization/Permissions.cs` and `PermissionCatalog`
(and to the built-in roles that should hold it, with a migration updating existing
tenants), then annotate the action with `[RequirePermission(Permissions.X.Y)]`. Every
action must carry `[AllowAnonymous]`, `[RequirePermission]` or `[AuthenticatedOnly]`;
the API refuses to start otherwise, and `PrivilegeEscalationTests` probes every route.

## Configuration

All settings can be supplied as environment variables (`Section__Key`). Required in
production: `ConnectionStrings__Postgres`, `ConnectionStrings__Redis`,
`Solvix__Jwt__SigningKey` (≥ 32 chars), `Solvix__Identity__DataProtectionKey` (≥ 32 chars,
stable, different from the JWT key), `Solvix__Identity__WebBaseUrl`, SMTP settings
(`Solvix__Email__Smtp__*`) and `Solvix__Network__TrustedProxies__*`; on the web container
`SOLVIX_WEB_ORIGIN` and `SOLVIX_COOKIE_SECURE=true` behind HTTPS. See `.env.example`,
`docs/ARCHITECTURE.md` and `docs/PHASE-02-SUMMARY.md`.
