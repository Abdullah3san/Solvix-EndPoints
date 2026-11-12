# Architecture (Phases 01–02)

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
* **Identity** – platform-level `User` (credentials, MFA), tenant-owned memberships, roles, sessions, refresh
  tokens, invitations and security policies; sign-in, MFA, refresh rotation, password flows, RBAC administration,
  audit trail, cached `IAccessResolver`. See `docs/PHASE-02-SUMMARY.md`.
* **Organization** – `CompanyProfile` (1:1 with tenant), `Branch`, `Department`
  (nested, per branch).
* **Infrastructure** – `SolvixDbContext`, interceptors, migrations, Redis cache,
  tenant resolution middleware, tenant-scoped background job runner.

## Request pipeline

```
ForwardedHeaders (trusted proxies only) → ExceptionHandler → StatusCodePages → CorrelationId
→ security headers → Serilog request log → CORS → RateLimiter → Authentication (JWT)
→ TenantResolutionMiddleware → Authorization (permission policies) → Controllers
```

`TenantResolutionMiddleware` is the only place a tenant and an access grant are attached
to a request. It reads `tid`, `sub` and `sid` from the validated token, rejects an
`X-Tenant-Id` header that disagrees, checks the tenant is active, and then resolves –
inside the tenant scope – that the session is alive, the membership and account are
active, and which permissions the member's roles grant now (Redis-cached for 30 s,
invalidated on change and again after commit). Authorization then evaluates
`[RequirePermission]` against that grant; the token carries no roles or permissions.
Every controller action must declare `[AllowAnonymous]`, `[RequirePermission]` or
`[AuthenticatedOnly]` or the host refuses to start.

## Tenant isolation – four layers

| Layer | Mechanism | Protects against |
|-------|-----------|------------------|
| 1. API | Tenant comes only from the signed token; no endpoint accepts a tenant id; ids from other tenants resolve to 404; role policies (`TenantMember`, `TenantAdmin`, `TenantOwner`). | URL/body/header manipulation |
| 2. EF Core reads | Named global query filter `Tenant` on every `ITenantScoped` entity, bound to the scoped `ITenantContext`. Unresolved context → zero rows. | Forgotten `WHERE tenant_id = …` |
| 3. EF Core writes | `TenantEnforcementInterceptor`: assigns the tenant to new rows, rejects writes for other tenants, rejects any `TenantId` change, stamps audit columns. Branch/Department additionally carry `(tenant_id, id)` alternate keys and tenant-safe composite foreign keys. | Cross-tenant inserts/updates, moving rows, cross-tenant parent references |
| 4. PostgreSQL | `TenantSessionConnectionInterceptor` sets `app.tenant_id` on every opened connection; FORCE ROW LEVEL SECURITY + `tenant_isolation` policy on every tenant table (`USING`/`WITH CHECK tenant_id = app_current_tenant_id()`); runtime role is `NOBYPASSRLS`; Npgsql resets session state on pool return. | Raw SQL, tooling, bugs that bypass EF, the schema owner itself |

Platform-level tables (`tenants`, `users`, `user_tokens`, `user_password_history`,
`user_recovery_codes`) are not tenant-owned. `auth_audit_events` has an optional tenant:
RLS shows a tenant its own rows only, rows without a tenant are platform-only, and
triggers make the table append-only. Operations that
legitimately span tenants – registration, login, tenant switching – run inside an
explicit `ITenantContextMutator.BeginPlatformScope()`; the connection interceptor then
sets `app.rls_bypass = on` for those connections only, and the code must still call
`IgnoreQueryFilters()` explicitly. This is the single, auditable escape hatch.

Background work uses `ITenantJobRunner` / `ITenantScopeFactory`, which create a DI
scope bound to one tenant; the job sees exactly what an HTTP request for that tenant
sees. A DbContext resolved from the root scope sees nothing and cannot insert.

## Data model

```
tenants (platform)            users (platform: credentials, MFA, lockout)
  id, name, slug*, status       id, normalized_email*, password_hash, mfa_secret (encrypted), lockout…
        │                            │ user_tokens*, user_password_history, user_recovery_codes
        ├── tenant_memberships (RLS) ─┘   (tenant_id, user_id)*  is_active
        │        ▲ (tenant_id, id)
        │        └── membership_roles (RLS) ──► roles (RLS) (tenant_id, normalized_name)*, system_key
        │                                          └── role_permissions (RLS) (tenant_id, role_id, permission)*
        ├── user_sessions (RLS) ◄── refresh_tokens (RLS) token_hash*
        ├── user_invitations (RLS) token_hash* ◄── user_invitation_roles (RLS) ──► roles
        ├── tenant_security_policies (RLS) tenant_id*
        ├── auth_audit_events (RLS, nullable tenant, append-only)
        ├── company_profiles   (RLS)      tenant_id*  legal data + address
        ├── branches           (RLS)      (tenant_id, code)*  one HQ per tenant (partial unique)
        └── departments        (RLS)      (tenant_id, branch_id, code)*
```
`*` = unique index. Ids are UUIDv7, assigned by the application. Arrows between
tenant-owned tables are composite `(tenant_id, …)` foreign keys, so references can
never cross tenants. Names are snake_case via a model convention.

## Caching

`IDistributedCache` (Redis) is wrapped by `ResilientDistributedCache`: a failure
degrades to a cache miss and opens a 15 s circuit, so a Redis outage never fails
requests. Keys: `solvix:platform:tenant:*` (tenant directory, 5 min),
`solvix:tenant:{id}:access:{user}` and `solvix:tenant:{id}:session:{sid}` (30 s),
`solvix:tenant:{id}:security-policy` (5 min) – all removed on change and again after the
transaction commits – and `ITenantCache` for tenant-scoped application data.

## Frontend

Next.js App Router with a backend-for-frontend. Access and refresh tokens are kept in
httpOnly, SameSite=Lax cookies (`__Host-`, Secure in production) by the `/api/auth/*`
route handlers and never reach browser JavaScript. `/api/proxy/v1/*` attaches the
access token, renews it with the refresh cookie when expired, and enforces CSRF
(Origin, Fetch-Metadata, double-submit token). `proxy.ts` renews sessions for page
navigations, gates routes and issues the CSRF cookie; the `(app)` layout loads
`/auth/me` (user, tenant, roles, permissions) on every navigation and the UI shows
only what those permissions allow. Shell: sidebar, header with breadcrumbs, theme
selector, notification centre, user menu with workspace switching.

## Configuration & environments

Options classes are bound from configuration (`Solvix:Database`, `Solvix:Redis`,
`Solvix:Jwt`, `Solvix:Identity`, `Solvix:Email`, `Solvix:RateLimiting`, `Solvix:Network`,
`Solvix:Cors`) and validated at start-up. Environment variables use
`Section__Key`. Docker Compose wires: `postgres` (init script creates
`solvix_migrator` owner + `solvix_app` NOBYPASSRLS roles), `redis`, `mailpit` (SMTP
sink), `migrator` (runs then exits), `api`, `web`.

## Logging & errors

Serilog with compact JSON in containers (text in Development), enriched with
`CorrelationId`, `TraceId`, `TenantId`, `TenantSlug`, `UserId`, machine and
environment. `GlobalExceptionHandler` turns domain exceptions and PostgreSQL
constraint violations into RFC 9457 problem details with a stable `code` and, for
validation, an `errors` map keyed by camelCase property.
