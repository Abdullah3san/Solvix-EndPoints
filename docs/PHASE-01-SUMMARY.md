# Phase 01 – Platform foundation: technical summary

## Scope delivered

| # | Requirement | Status | Where |
|---|-------------|--------|-------|
| 1 | Next.js + TypeScript frontend | Done | `src/frontend` (Next 16.4, React 19, Tailwind 4) |
| 2 | ASP.NET Core backend | Done | `src/backend` (.NET 10) |
| 3 | PostgreSQL | Done | compose `postgres` service, roles init script |
| 4 | EF Core | Done | `Solvix.Infrastructure/Persistence` |
| 5 | Redis | Done | `IDistributedCache` (StackExchange), resilient wrapper, `ITenantCache` |
| 6 | Docker / Compose | Done | `docker-compose.yml`, `src/backend/Dockerfile`, `src/frontend/Dockerfile` |
| 7 | Modular project structure | Done | SharedKernel + Infrastructure + 3 feature modules + host |
| 8 | Dependency injection | Done | `IModule.ConfigureServices`, `AddSolvixInfrastructure` |
| 9 | Structured logging | Done | Serilog compact JSON, correlation/tenant/user enrichment |
| 10 | Centralized exception handling | Done | `GlobalExceptionHandler` → RFC 9457 problem details |
| 11 | API versioning | Done | Asp.Versioning 10, URL segment `/api/v1`, versioned OpenAPI |
| 12 | Environment variables | Done | options classes + validation, `.env.example`, compose wiring |
| 13 | Migration infrastructure | Done | EF migrations, `Solvix.Migrator` init container, design-time factory |
| 14 | Tenant registration | Done | `POST /api/v1/tenant/register` (tenant + profile + HQ + owner, atomic) |
| 15 | Tenant management | Done | `GET/PUT /api/v1/tenant`, overview, members CRUD + roles |
| 16 | Company profiles | Done | `GET/PUT /api/v1/company-profile` |
| 17 | Branch management | Done | `/api/v1/branches` CRUD, activate/deactivate, single HQ |
| 18 | Department management | Done | `/api/v1/departments` CRUD, tree, nesting with cycle checks |
| UI | Shell: sidebar, header, breadcrumbs, content, profile menu, notifications, theme | Done | `src/frontend/src/components/shell` |

Endpoint management is intentionally not started.

## Multi-tenancy design (summary; details in `docs/ARCHITECTURE.md`)

* **Resolution**: the tenant is taken only from the `tid` claim of the signed JWT.
  `X-Tenant-Id` is accepted purely as a consistency check and rejected on mismatch
  (403 `cross_tenant_access`). The tenant must be active and the caller must hold an
  active membership (Redis-cached, invalidated on change), so revoked members are
  locked out within seconds even with a valid token.
* **Server-side ownership**: `TenantId` is assigned by the persistence layer from the
  ambient context, never bound from request bodies; it is immutable (interceptor +
  alternate keys); every tenant table has a FK to `tenants`.
* **EF Core**: named global query filter on every `ITenantScoped` entity; SaveChanges
  interceptor rejects cross-tenant inserts/updates/deletes.
* **PostgreSQL RLS**: FORCE ROW LEVEL SECURITY + `tenant_isolation` policy on
  `tenant_memberships`, `company_profiles`, `branches`, `departments`, driven by the
  `app.tenant_id` session variable the connection interceptor sets on every open.
  The runtime role is `NOBYPASSRLS`; the schema owner is subject to the policy too.
* **Background jobs**: `ITenantJobRunner` / `ITenantScopeFactory` create a
  tenant-bound DI scope; the root scope sees nothing and cannot insert.
* **Platform scope**: registration, login and tenant switching run inside an explicit
  `BeginPlatformScope()`; this is the single escape hatch and it is never driven by
  request input.

## Database design

Normalised, no duplicated tenant data: account-level fields live on `tenants`,
business details on `company_profiles` (1:1, unique `tenant_id`), people are global
`users` linked through `tenant_memberships` (unique per tenant+user). Branch codes
are unique per tenant, department codes per branch, one headquarters per tenant
(partial unique index). Tenant-safe composite foreign keys guarantee a department's
branch and parent belong to the same tenant at the database level. Indexes exist for
every lookup path (`slug`, `normalized_email`, `(tenant_id, user_id)`, `user_id`,
`(tenant_id, code)`, `(tenant_id, branch_id, code)`, `(tenant_id, parent_department_id)`).

## Verification

### Backend
Run against a real PostgreSQL 16 and Redis 7 (fresh database per run, two-role layout).

| Suite | Result | Notes |
|-------|--------|-------|
| `Solvix.UnitTests` | **44 / 44 passed** | slug rules, tenant context, entity invariants, snake_case naming, SaveChanges tenant enforcement (in-memory provider) |
| `Solvix.IntegrationTests` (Redis up) | **31 / 31 passed** (8 s) | see breakdown below |
| `Solvix.IntegrationTests` (Redis unreachable) | 30 / 31 passed (20 s) | only `Health_endpoints_report_postgres_and_redis` fails, i.e. `/health/ready` correctly reports 503; all tenant-isolation and functional tests still pass thanks to the cache circuit breaker |

Integration coverage:

* **Migrations** – all applied, model matches snapshot (`HasPendingModelChanges == false`),
  every `ITenantScoped` table has RLS enabled + forced + `tenant_isolation` policy, every
  tenant table has a FK on `tenant_id`, platform tables are not RLS-protected,
  full rollback to `0` and re-apply succeeds.
* **Row-Level Security (raw SQL, bypassing EF)** – no tenant context ⇒ 0 rows; tenant A
  context ⇒ only A's rows; INSERT/UPDATE moving rows to tenant B rejected with
  `42501`; UPDATE/DELETE on B affect 0 rows; schema owner subject to RLS;
  `IgnoreQueryFilters()` and `SqlQueryRaw` still cannot leak; pooled connections do not
  leak the session variable.
* **API manipulation** – other tenant's branch/department ids ⇒ 404 on GET/PUT/DELETE/
  activate; department cannot reference another tenant's branch or parent; mismatching
  `X-Tenant-Id` ⇒ 403; removed member locked out with a still-valid token; login cannot
  bind to a tenant the user does not belong to; switch-tenant to a foreign tenant ⇒ 403;
  anonymous / forged tokens ⇒ 401; Member role cannot create branches ⇒ 403.
* **Background jobs** – tenant-scoped job sees only its tenant, cannot write into
  another tenant, writes land in the right tenant; root scope sees nothing and cannot insert.
* **Functional** – registration provisions profile + HQ, validation errors map to field
  errors, duplicate slug / e-mail ⇒ 409, branch code uniqueness per tenant, single HQ,
  department hierarchy (same-branch parent, cycle detection, delete guards), tenant
  rename invalidates the cached directory, health, versioned OpenAPI, correlation ids.

### Frontend
| Check | Result |
|-------|--------|
| `tsc --noEmit` | clean |
| `eslint .` (next/core-web-vitals + typescript, React Compiler rules) | clean |
| `vitest run` | **10 / 10 passed** (session claims, breadcrumbs, sidebar active state, theme toggle, notification centre, API client error mapping) |
| `next build` | succeeds; 5 dynamic app routes, 2 static auth routes, 5 API routes, proxy |

### Docker
Verified in this environment with `docker compose` (images built from the committed
Dockerfiles; alternate host ports were used because the sandbox already occupied the defaults).

| Check | Result |
|-------|--------|
| `docker compose config` | valid |
| `postgres` init script | creates `solvix_migrator` (owner of db + schema) and `solvix_app` (NOSUPERUSER, NOBYPASSRLS); default privileges granted |
| Cold start (`docker compose down -v && up -d`) | all five services healthy after ~21 s; `migrator` exits 0 and applies `InitialSchema` + `RowLevelSecurity` on the empty database |
| `api` | healthy (`/health/ready` = Healthy, `/openapi/v1.json` served), starts only after the migrator succeeded |
| `web` | healthy; `/dashboard` → 307 to `/login` when anonymous |
| End-to-end | tenant registered through the containerized API; login through the Next.js BFF sets an `HttpOnly` cookie and returns no token in the body; the dashboard server-renders tenant data through the BFF (sidebar, breadcrumbs, theme and notification controls present); `/api/proxy/v1/branches` returns the tenant's branches with the cookie and 401 without it; mismatching `X-Tenant-Id` → 403 |

Two container-only defects were found by this run and fixed: the migrator image needs the
ASP.NET shared framework (it links the Infrastructure assemblies), and the Alpine runtime
images ship no IANA time-zone database, so `timeZone` validation rejected valid ids; the
Dockerfile now copies `/usr/share/zoneinfo` from the SDK stage.

## Changed files

All 224 files are new (the repository was empty). `package-lock.json` is included for reproducible installs.

<details><summary><b>Repository tooling</b> (14 files)</summary>

```
.dockerignore
.editorconfig
.env.example
.gitignore
Directory.Build.props
Directory.Packages.props
README.md
Solvix.slnx
docker-compose.yml
docker/postgres/init/01-roles.sh
docs/ARCHITECTURE.md
docs/PHASE-01-SUMMARY.md
global.json
scripts/create-local-db.sh
```
</details>

<details><summary><b>Backend – SharedKernel</b> (22 files)</summary>

```
src/backend/Solvix.SharedKernel/Abstractions/IClock.cs
src/backend/Solvix.SharedKernel/Entities/Entity.cs
src/backend/Solvix.SharedKernel/Entities/IAuditable.cs
src/backend/Solvix.SharedKernel/Entities/ITenantScoped.cs
src/backend/Solvix.SharedKernel/Entities/TenantEntity.cs
src/backend/Solvix.SharedKernel/Exceptions/DomainException.cs
src/backend/Solvix.SharedKernel/Guards/Guard.cs
src/backend/Solvix.SharedKernel/Identity/ICurrentUser.cs
src/backend/Solvix.SharedKernel/Identity/IMembershipLookup.cs
src/backend/Solvix.SharedKernel/Identity/SolvixClaimTypes.cs
src/backend/Solvix.SharedKernel/Modules/IModule.cs
src/backend/Solvix.SharedKernel/Persistence/ISolvixDbContext.cs
src/backend/Solvix.SharedKernel/Results/PagedResult.cs
src/backend/Solvix.SharedKernel/Solvix.SharedKernel.csproj
src/backend/Solvix.SharedKernel/Tenancy/ITenantContext.cs
src/backend/Solvix.SharedKernel/Tenancy/ITenantContextMutator.cs
src/backend/Solvix.SharedKernel/Tenancy/ITenantDirectory.cs
src/backend/Solvix.SharedKernel/Tenancy/ITenantScopeFactory.cs
src/backend/Solvix.SharedKernel/Tenancy/Slug.cs
src/backend/Solvix.SharedKernel/Tenancy/TenantContext.cs
src/backend/Solvix.SharedKernel/Web/ApiControllerBase.cs
src/backend/Solvix.SharedKernel/Web/Policies.cs
```
</details>

<details><summary><b>Backend – Infrastructure</b> (21 files)</summary>

```
src/backend/Solvix.Infrastructure/Caching/ITenantCache.cs
src/backend/Solvix.Infrastructure/Caching/RedisOptions.cs
src/backend/Solvix.Infrastructure/Caching/ResilientDistributedCache.cs
src/backend/Solvix.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs
src/backend/Solvix.Infrastructure/DependencyInjection/SolvixModules.cs
src/backend/Solvix.Infrastructure/Identity/HttpCurrentUser.cs
src/backend/Solvix.Infrastructure/Persistence/Conventions/SnakeCaseNaming.cs
src/backend/Solvix.Infrastructure/Persistence/DatabaseOptions.cs
src/backend/Solvix.Infrastructure/Persistence/Interceptors/TenantEnforcementInterceptor.cs
src/backend/Solvix.Infrastructure/Persistence/Interceptors/TenantSessionConnectionInterceptor.cs
src/backend/Solvix.Infrastructure/Persistence/Migrations/20261111212920_InitialSchema.Designer.cs
src/backend/Solvix.Infrastructure/Persistence/Migrations/20261111212920_InitialSchema.cs
src/backend/Solvix.Infrastructure/Persistence/Migrations/20261111213000_RowLevelSecurity.cs
src/backend/Solvix.Infrastructure/Persistence/Migrations/RowLevelSecurity.cs
src/backend/Solvix.Infrastructure/Persistence/Migrations/SolvixDbContextModelSnapshot.cs
src/backend/Solvix.Infrastructure/Persistence/SolvixDbContext.cs
src/backend/Solvix.Infrastructure/Persistence/SolvixDbContextFactory.cs
src/backend/Solvix.Infrastructure/Solvix.Infrastructure.csproj
src/backend/Solvix.Infrastructure/Tenancy/ITenantJobRunner.cs
src/backend/Solvix.Infrastructure/Tenancy/TenantResolutionMiddleware.cs
src/backend/Solvix.Infrastructure/Tenancy/TenantScopeFactory.cs
```
</details>

<details><summary><b>Backend – Tenancy module</b> (12 files)</summary>

```
src/backend/Modules/Solvix.Modules.Tenancy/Api/TenantsController.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Directory/CachedTenantDirectory.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Management/Contracts.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Management/TenantService.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Management/Validators.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Registration/Contracts.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Registration/TenantRegistrationService.cs
src/backend/Modules/Solvix.Modules.Tenancy/Application/Registration/Validators.cs
src/backend/Modules/Solvix.Modules.Tenancy/Domain/Tenant.cs
src/backend/Modules/Solvix.Modules.Tenancy/Persistence/TenantConfiguration.cs
src/backend/Modules/Solvix.Modules.Tenancy/Solvix.Modules.Tenancy.csproj
src/backend/Modules/Solvix.Modules.Tenancy/TenancyModule.cs
```
</details>

<details><summary><b>Backend – Identity module</b> (21 files)</summary>

```
src/backend/Modules/Solvix.Modules.Identity/Api/AuthController.cs
src/backend/Modules/Solvix.Modules.Identity/Api/MembersController.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Auth/AuthService.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Auth/Contracts.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Auth/ITokenService.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Auth/JwtTokenService.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Auth/PasswordPolicy.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Auth/Validators.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Members/Contracts.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Members/MemberService.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Members/MembershipLookup.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Members/Validators.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Provisioning/IUserProvisioningService.cs
src/backend/Modules/Solvix.Modules.Identity/Application/Provisioning/UserProvisioningService.cs
src/backend/Modules/Solvix.Modules.Identity/Configuration/JwtOptions.cs
src/backend/Modules/Solvix.Modules.Identity/Domain/TenantMembership.cs
src/backend/Modules/Solvix.Modules.Identity/Domain/User.cs
src/backend/Modules/Solvix.Modules.Identity/IdentityModule.cs
src/backend/Modules/Solvix.Modules.Identity/Persistence/TenantMembershipConfiguration.cs
src/backend/Modules/Solvix.Modules.Identity/Persistence/UserConfiguration.cs
src/backend/Modules/Solvix.Modules.Identity/Solvix.Modules.Identity.csproj
```
</details>

<details><summary><b>Backend – Organization module</b> (25 files)</summary>

```
src/backend/Modules/Solvix.Modules.Organization/Api/BranchesController.cs
src/backend/Modules/Solvix.Modules.Organization/Api/CompanyProfileController.cs
src/backend/Modules/Solvix.Modules.Organization/Api/DepartmentsController.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Branches/BranchService.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Branches/Contracts.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Branches/Validators.cs
src/backend/Modules/Solvix.Modules.Organization/Application/CompanyProfiles/CompanyProfileService.cs
src/backend/Modules/Solvix.Modules.Organization/Application/CompanyProfiles/Contracts.cs
src/backend/Modules/Solvix.Modules.Organization/Application/CompanyProfiles/Validators.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Contracts.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Departments/Contracts.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Departments/DepartmentService.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Departments/Validators.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Provisioning/IOrganizationProvisioningService.cs
src/backend/Modules/Solvix.Modules.Organization/Application/Provisioning/OrganizationProvisioningService.cs
src/backend/Modules/Solvix.Modules.Organization/Domain/Address.cs
src/backend/Modules/Solvix.Modules.Organization/Domain/Branch.cs
src/backend/Modules/Solvix.Modules.Organization/Domain/CompanyProfile.cs
src/backend/Modules/Solvix.Modules.Organization/Domain/Department.cs
src/backend/Modules/Solvix.Modules.Organization/OrganizationModule.cs
src/backend/Modules/Solvix.Modules.Organization/Persistence/AddressConfiguration.cs
src/backend/Modules/Solvix.Modules.Organization/Persistence/BranchConfiguration.cs
src/backend/Modules/Solvix.Modules.Organization/Persistence/CompanyProfileConfiguration.cs
src/backend/Modules/Solvix.Modules.Organization/Persistence/DepartmentConfiguration.cs
src/backend/Modules/Solvix.Modules.Organization/Solvix.Modules.Organization.csproj
```
</details>

<details><summary><b>Backend – API host & Migrator</b> (14 files)</summary>

```
src/backend/Dockerfile
src/backend/Solvix.Api/Infrastructure/BearerSecuritySchemeTransformer.cs
src/backend/Solvix.Api/Infrastructure/CorsOptions.cs
src/backend/Solvix.Api/Infrastructure/GlobalExceptionHandler.cs
src/backend/Solvix.Api/Infrastructure/KebabCaseParameterTransformer.cs
src/backend/Solvix.Api/Infrastructure/ValidationActionFilter.cs
src/backend/Solvix.Api/Middleware/CorrelationIdMiddleware.cs
src/backend/Solvix.Api/Program.cs
src/backend/Solvix.Api/Properties/launchSettings.json
src/backend/Solvix.Api/Solvix.Api.csproj
src/backend/Solvix.Api/appsettings.Development.json
src/backend/Solvix.Api/appsettings.json
src/backend/Solvix.Migrator/Program.cs
src/backend/Solvix.Migrator/Solvix.Migrator.csproj
```
</details>

<details><summary><b>Tests</b> (18 files)</summary>

```
tests/Solvix.IntegrationTests/Fixtures/ApiFixture.cs
tests/Solvix.IntegrationTests/Fixtures/HttpAssert.cs
tests/Solvix.IntegrationTests/Fixtures/PostgresTestDatabase.cs
tests/Solvix.IntegrationTests/Fixtures/SolvixApiFactory.cs
tests/Solvix.IntegrationTests/Solvix.IntegrationTests.csproj
tests/Solvix.IntegrationTests/Tests/BackgroundJobIsolationTests.cs
tests/Solvix.IntegrationTests/Tests/MigrationTests.cs
tests/Solvix.IntegrationTests/Tests/OrganizationApiTests.cs
tests/Solvix.IntegrationTests/Tests/PlatformTests.cs
tests/Solvix.IntegrationTests/Tests/RowLevelSecurityTests.cs
tests/Solvix.IntegrationTests/Tests/TenantIsolationApiTests.cs
tests/Solvix.IntegrationTests/xunit.runner.json
tests/Solvix.UnitTests/Domain/EntityTests.cs
tests/Solvix.UnitTests/Infrastructure/SnakeCaseNamingTests.cs
tests/Solvix.UnitTests/Infrastructure/TenantEnforcementInterceptorTests.cs
tests/Solvix.UnitTests/SharedKernel/SlugTests.cs
tests/Solvix.UnitTests/SharedKernel/TenantContextTests.cs
tests/Solvix.UnitTests/Solvix.UnitTests.csproj
```
</details>

<details><summary><b>Frontend</b> (77 files)</summary>

```
src/frontend/.dockerignore
src/frontend/.gitignore
src/frontend/Dockerfile
src/frontend/eslint.config.mjs
src/frontend/next.config.ts
src/frontend/package-lock.json
src/frontend/package.json
src/frontend/public/icon.svg
src/frontend/src/__tests__/api-client.test.ts
src/frontend/src/__tests__/breadcrumbs.test.tsx
src/frontend/src/__tests__/notifications.test.tsx
src/frontend/src/__tests__/session.test.ts
src/frontend/src/__tests__/sidebar.test.tsx
src/frontend/src/__tests__/theme-toggle.test.tsx
src/frontend/src/app/(app)/dashboard/page.tsx
src/frontend/src/app/(app)/error.tsx
src/frontend/src/app/(app)/layout.tsx
src/frontend/src/app/(app)/organization/branches/page.tsx
src/frontend/src/app/(app)/organization/company/page.tsx
src/frontend/src/app/(app)/organization/departments/page.tsx
src/frontend/src/app/(app)/settings/workspace/page.tsx
src/frontend/src/app/(auth)/layout.tsx
src/frontend/src/app/(auth)/login/page.tsx
src/frontend/src/app/(auth)/register/page.tsx
src/frontend/src/app/api/auth/_shared.ts
src/frontend/src/app/api/auth/login/route.ts
src/frontend/src/app/api/auth/logout/route.ts
src/frontend/src/app/api/auth/register/route.ts
src/frontend/src/app/api/auth/switch-tenant/route.ts
src/frontend/src/app/api/proxy/[...path]/route.ts
src/frontend/src/app/favicon.ico
src/frontend/src/app/globals.css
src/frontend/src/app/layout.tsx
src/frontend/src/app/not-found.tsx
src/frontend/src/components/features/address-fields.tsx
src/frontend/src/components/features/auth-forms.tsx
src/frontend/src/components/features/branches-view.tsx
src/frontend/src/components/features/company-profile-form.tsx
src/frontend/src/components/features/departments-view.tsx
src/frontend/src/components/features/workspace-settings.tsx
src/frontend/src/components/providers/notifications-provider.tsx
src/frontend/src/components/providers/notifications-store.ts
src/frontend/src/components/providers/session-provider.tsx
src/frontend/src/components/providers/theme-provider.tsx
src/frontend/src/components/providers/toast-provider.tsx
src/frontend/src/components/shell/app-shell.tsx
src/frontend/src/components/shell/breadcrumbs.tsx
src/frontend/src/components/shell/header.tsx
src/frontend/src/components/shell/logo.tsx
src/frontend/src/components/shell/nav-config.ts
src/frontend/src/components/shell/notifications-menu.tsx
src/frontend/src/components/shell/sidebar.tsx
src/frontend/src/components/shell/theme-toggle.tsx
src/frontend/src/components/shell/user-menu.tsx
src/frontend/src/components/ui/alert.tsx
src/frontend/src/components/ui/badge.tsx
src/frontend/src/components/ui/button.tsx
src/frontend/src/components/ui/card.tsx
src/frontend/src/components/ui/dialog.tsx
src/frontend/src/components/ui/field.tsx
src/frontend/src/components/ui/input.tsx
src/frontend/src/components/ui/menu.tsx
src/frontend/src/components/ui/page-header.tsx
src/frontend/src/components/ui/skeleton.tsx
src/frontend/src/components/ui/table.tsx
src/frontend/src/lib/api/client.ts
src/frontend/src/lib/api/errors.ts
src/frontend/src/lib/api/server.ts
src/frontend/src/lib/api/types.ts
src/frontend/src/lib/auth/session.ts
src/frontend/src/lib/config.ts
src/frontend/src/lib/forms.ts
src/frontend/src/lib/utils.ts
src/frontend/src/proxy.ts
src/frontend/tsconfig.json
src/frontend/vitest.config.ts
src/frontend/vitest.setup.ts
```
</details>


## Known limitations / next steps

> Refresh tokens, sign-out revocation, password reset, e-mail verification, invitations
> and brute-force protection were delivered in Phase 02 (`docs/PHASE-02-SUMMARY.md`).

* **No refresh tokens or sign-out revocation**: access tokens live 60 min (configurable);
  logout only clears the cookie. Membership revocation and tenant suspension are
  enforced per request, but a stolen token stays valid until expiry.
* **No platform-admin surface**: tenant suspension/reactivation exists in the domain
  (`Tenant.Suspend/Reactivate`, enforced by the middleware) but there is no endpoint
  or role to drive it yet.
* **Notifications are client-side only**: the notification centre is fed by UI
  actions and stored per browser tab; there is no server-side feed or persistence.
* **Password reset, e-mail verification, invitations by e-mail** are not implemented;
  admins add members by setting an initial password.
* **Rate limiting / brute-force protection** on login and registration is not in place.
* **No CI pipeline yet** (the test/lint/build commands are documented in the README).
* **Playwright/E2E tests** for the frontend are not included; component tests cover
  the shell, session parsing, API client and notification centre.
* Tenant deletion is not implemented (`PendingDeletion` status reserved; FKs are
  `Restrict` on purpose).
* **Alpine runtime images log one Npgsql probe error at start-up** (`Cannot load library
  libgssapi_krb5.so.2`): GSSAPI/Kerberos authentication is not used, password auth works.
  Adding `krb5-libs` to the runtime stages removes the message; it was left out because
  this sandbox cannot reach the Alpine package mirror during image builds.
* **Docker images could only be built here with host networking through the sandbox
  proxy**; the committed Dockerfiles and compose file contain no proxy settings and are
  expected to build unchanged in a normal environment.
