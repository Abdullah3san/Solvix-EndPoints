# Architecture (Phase 01)

## Modular monolith

The backend is one deployable ASP.NET Core process composed of feature modules.
Each module owns its entities, EF Core configurations, application services,
validators and controllers, and registers them through `IModule.ConfigureServices`.
The host discovers controllers and entity configurations from the module assemblies
listed in `SolvixModules.All`.

```
Solvix.Api ──► Solvix.Infrastructure ──► Modules.Tenancy ──► Modules.Identity
                                     │                   └──► Modules.Organization
                                     └──► Solvix.SharedKernel ◄── every module
```

* **SharedKernel** – `Entity`, `TenantEntity`/`ITenantScoped`, `ITenantContext`,
  domain exceptions, `IModule`, `ISolvixDbContext`, authorization policy names.
* **Tenancy** – `Tenant` (platform table), self-service registration (orchestrates
  Identity + Organization provisioning in one transaction), tenant management,
  cached `ITenantDirectory`.
* **Identity** – platform-level `User`, tenant-owned `TenantMembership`, JWT issuance,
  login / tenant switching, member management, cached `IMembershipLookup`.
* **Organization** – `CompanyProfile` (1:1 with tenant), `Branch`, `Department`
  (nested, per branch).
* **Infrastructure** – `SolvixDbContext`, interceptors, migrations, Redis cache,
  tenant resolution middleware, tenant-scoped background job runner.

## Request pipeline

```
ExceptionHandler → StatusCodePages → CorrelationId → Serilog request log → CORS
→ Authentication (JWT) → TenantResolutionMiddleware → Authorization → Controllers
```

`TenantResolutionMiddleware` is the only place a tenant is attached to a request.
It reads the `tid` claim of the validated token, rejects an `X-Tenant-Id` header
that disagrees, checks that the tenant exists and is active (Redis-cached), and that
the user still holds an active membership (Redis-cached, invalidated on change).

## Tenant isolation – four layers

| Layer | Mechanism | Protects against |
|-------|-----------|------------------|
| 1. API | Tenant comes only from the signed token; no endpoint accepts a tenant id; ids from other tenants resolve to 404; role policies (`TenantMember`, `TenantAdmin`, `TenantOwner`). | URL/body/header manipulation |
| 2. EF Core reads | Named global query filter `Tenant` on every `ITenantScoped` entity, bound to the scoped `ITenantContext`. Unresolved context → zero rows. | Forgotten `WHERE tenant_id = …` |
| 3. EF Core writes | `TenantEnforcementInterceptor`: assigns the tenant to new rows, rejects writes for other tenants, rejects any `TenantId` change, stamps audit columns. Branch/Department additionally carry `(tenant_id, id)` alternate keys and tenant-safe composite foreign keys. | Cross-tenant inserts/updates, moving rows, cross-tenant parent references |
| 4. PostgreSQL | `TenantSessionConnectionInterceptor` sets `app.tenant_id` on every opened connection; FORCE ROW LEVEL SECURITY + `tenant_isolation` policy on every tenant table (`USING`/`WITH CHECK tenant_id = app_current_tenant_id()`); runtime role is `NOBYPASSRLS`; Npgsql resets session state on pool return. | Raw SQL, tooling, bugs that bypass EF, the schema owner itself |

Platform-level tables (`tenants`, `users`) are not tenant-owned. Operations that
legitimately span tenants – registration, login, tenant switching – run inside an
explicit `ITenantContextMutator.BeginPlatformScope()`; the connection interceptor then
sets `app.rls_bypass = on` for those connections only, and the code must still call
`IgnoreQueryFilters()` explicitly. This is the single, auditable escape hatch.

Background work uses `ITenantJobRunner` / `ITenantScopeFactory`, which create a DI
scope bound to one tenant; the job sees exactly what an HTTP request for that tenant
sees. A DbContext resolved from the root scope sees nothing and cannot insert.

## Data model

```
tenants (platform)            users (platform)
  id, name, slug*, status       id, email, normalized_email*, display_name, password_hash, is_active
        │                            │
        ├── tenant_memberships (RLS) ─┘   (tenant_id, user_id)*  role
        ├── company_profiles   (RLS)      tenant_id*  legal data + address
        ├── branches           (RLS)      (tenant_id, code)*  one HQ per tenant (partial unique)
        │        ▲ (tenant_id, id) alternate key
        └── departments        (RLS)      (tenant_id, branch_id, code)*  parent within same branch
                 FK (tenant_id, branch_id) → branches(tenant_id, id)
                 FK (tenant_id, parent_department_id) → departments(tenant_id, id)
```
`*` = unique index. Ids are UUIDv7. Names are snake_case via a model convention.

## Caching

`IDistributedCache` (Redis) is wrapped by `ResilientDistributedCache`: a failure
degrades to a cache miss and opens a 15 s circuit, so a Redis outage never fails
requests. Keys: `solvix:platform:tenant:*` (tenant directory, 5 min),
`solvix:tenant:{id}:membership:{user}` (60 s), and `ITenantCache` for tenant-scoped
application data.

## Frontend

Next.js App Router with a backend-for-frontend: the access token is stored in an
httpOnly cookie by `/api/auth/*` route handlers and attached by `/api/proxy/v1/*` to
upstream calls, so it never reaches browser JavaScript. `proxy.ts` gates routes by
cookie validity; the `(app)` layout validates the session against `/auth/me` on
every navigation. Shell: sidebar, header with breadcrumbs, theme selector
(light/dark/system via next-themes), notification centre, user menu.

## Configuration & environments

Options classes are bound from configuration (`Solvix:Database`, `Solvix:Redis`,
`Solvix:Jwt`, `Solvix:Cors`) and validated at start-up. Environment variables use
`Section__Key`. Docker Compose wires: `postgres` (init script creates
`solvix_migrator` owner + `solvix_app` NOBYPASSRLS roles), `redis`, `migrator`
(runs then exits), `api`, `web`.

## Logging & errors

Serilog with compact JSON in containers (text in Development), enriched with
`CorrelationId`, `TraceId`, `TenantId`, `TenantSlug`, `UserId`, machine and
environment. `GlobalExceptionHandler` turns domain exceptions and PostgreSQL
constraint violations into RFC 9457 problem details with a stable `code` and, for
validation, an `errors` map keyed by camelCase property.


## Phase 10 addendum

The `Security` module adds nine tenant-owned tables (all under forced RLS; `security_audit` is append-only) and an anonymous, device-key-authenticated ingest surface that runs inside a tenant-bound scope created by `ITenantScopeFactory`. See [PHASE-10-SECURITY.md](PHASE-10-SECURITY.md).
