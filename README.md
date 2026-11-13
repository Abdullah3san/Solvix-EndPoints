# Solvix EndPoints

Multi-tenant SaaS platform. **Phase 01** delivers the production-oriented foundation:
tenant registration and management, company profiles, branches and departments, a
Next.js application shell, and tenant isolation enforced in the application *and*
in PostgreSQL (Row-Level Security).

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

`migrator` applies EF Core migrations as the schema-owner role and exits; `api`
only starts once it succeeded. Register a workspace at http://localhost:3000/register.

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
or change the connection string).

## Repository layout

```
src/backend/
  Solvix.SharedKernel/        base entities, tenant context, exceptions, module contract
  Solvix.Infrastructure/      EF Core context, interceptors, migrations, RLS, Redis, tenant resolution
  Modules/
    Solvix.Modules.Tenancy/      tenant entity, registration, management, tenant directory
    Solvix.Modules.Identity/     users, memberships, JWT auth, member management
    Solvix.Modules.Organization/ company profile, branches, departments
    Solvix.Modules.Mdm/          mobile device management: Android Enterprise + Apple MDM (Phase 08)
  Solvix.Api/                 host: DI, Serilog, problem details, versioning, auth, OpenAPI, health
  Solvix.Migrator/            one-shot migration runner (Docker init container)
src/frontend/                 Next.js app (BFF proxy, app shell, feature pages)
tests/
  Solvix.UnitTests/           domain + interceptor + naming tests (in-memory)
  Solvix.IntegrationTests/    real PostgreSQL + Redis: migrations, RLS, API isolation, jobs
docker/postgres/init/         creates the migrator and runtime roles
docs/                         architecture and phase summaries (Phase 08: docs/PHASE-08-MOBILE-DEVICE-MANAGEMENT.md)
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

Every new tenant-owned table (any entity implementing `ITenantScoped`) must be added
to `RowLevelSecurityMigration.TenantTables` (or protected in its own migration with
`RowLevelSecurity.EnableTenantIsolation`). The integration test
`MigrationTests.Every_tenant_scoped_table_has_forced_rls_policy` fails otherwise.

## Configuration

All settings can be supplied as environment variables (`Section__Key`). Required in
production: `ConnectionStrings__Postgres`, `ConnectionStrings__Redis`,
`Solvix__Jwt__SigningKey` (≥ 32 chars). See `.env.example` and `docs/ARCHITECTURE.md`.
