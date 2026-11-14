# Solvix EndPoints

Multi-tenant SaaS platform for endpoint management.

* **Phase 01** – production-oriented foundation: tenant registration and management, company
  profiles, branches and departments, a Next.js application shell, and tenant isolation enforced in
  the application *and* in PostgreSQL (Row-Level Security).
* **Phase 03** – Windows endpoint management: a .NET Worker Service agent for Windows 10/11 and
  Windows Server 2016–2025, secure enrollment with short-lived tokens and X.509 device certificates
  (mutual TLS), hardware/OS/software/network/disk inventory, agent health and version tracking,
  device groups and assignment. See [`docs/PHASE-03-SUMMARY.md`](docs/PHASE-03-SUMMARY.md) and
  [`src/agent/README.md`](src/agent/README.md).

| Layer      | Stack                                                                 |
|------------|-----------------------------------------------------------------------|
| Frontend   | Next.js 16 (App Router, TypeScript, Tailwind CSS 4), Vitest           |
| Backend    | ASP.NET Core 10, EF Core 10, Npgsql, Serilog, FluentValidation, Asp.Versioning |
| Data       | PostgreSQL 16 (RLS), Redis 7                                          |
| Agent      | .NET 10 Worker Service (Windows service), WMI/registry, CNG/TPM keys, Serilog |
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
| Agents    | https://localhost:8443 (mutual TLS; development certificate generated on first start) |

`migrator` applies EF Core migrations as the schema-owner role and exits; `api`
only starts once it succeeded. Register a workspace at http://localhost:3000/register.

To enroll a Windows device: under *Endpoints → Enrollment* create a token (optionally pre-assigning a group,
branch and department, e.g. one token per store) and click **Download agent package**. Extract the zip on the
computer and double-click `Solvix.Agent.exe` (approve the administrator prompt): it installs itself and enrolls.
No command line is needed; for deployment tools use `Solvix.Agent.exe install --silent`. The package works out of
the box with the compose stack (development TLS certificate is pinned automatically); see `src/agent/README.md`.

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
    Solvix.Modules.Devices/      devices, enrollment, device CA, mTLS device auth, inventory, groups
  Solvix.Api/                 host: DI, Serilog, problem details, versioning, auth, OpenAPI, health
  Solvix.Migrator/            one-shot migration runner (Docker init container)
src/shared/Solvix.Agent.Contracts/  wire contracts shared by agent and API
src/agent/                    Windows agent: Core (portable), Windows (WMI, CNG, Authenticode), host, installer
src/frontend/                 Next.js app (BFF proxy, app shell, feature pages)
tests/
  Solvix.UnitTests/           domain + interceptor + naming tests (in-memory)
  Solvix.IntegrationTests/    real PostgreSQL + Redis: migrations, RLS, API isolation, jobs, devices, mTLS, agent end-to-end
  Solvix.Agent.Tests/         agent runtime, enrollment, backoff, renewal, updates, inventory
docker/postgres/init/         creates the migrator and runtime roles
docs/                         architecture and phase summaries
```

## Tests

```bash
dotnet test                                           # unit + integration + agent
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
`Solvix__Jwt__SigningKey` (≥ 32 chars), and for device management a device CA bundle
(`Solvix__Devices__CertificateAuthority__PfxPath`/`PfxBase64` + `PfxPassword`) and a TLS
certificate for the `Kestrel__Endpoints__Agents` endpoint. See `.env.example` and `docs/ARCHITECTURE.md`.
