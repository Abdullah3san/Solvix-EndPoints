# Architecture

Solvix-EndPoint is a multi-tenant, MSP-capable Unified Endpoint Management platform
(UEM, ITAM, remote administration, monitoring, and later AI). This document describes
the **system architecture**. Sections are marked **[Implemented]** (Phase 01, in the
code today) or **[Target]** (agreed design, not yet built).

| Document | Contents |
|----------|----------|
| [DECISIONS.md](DECISIONS.md) | ADRs, challenges to requirements, assumptions, risks, open decisions |
| [DATABASE.md](DATABASE.md) | Schemas, tenancy/MSP model, device model, partitioning, retention, GDPR |
| [SECURITY.md](SECURITY.md) | Trust boundaries, isolation, authN/Z, device identity, command security, remote desktop |
| [DEVICE_PLATFORM.md](DEVICE_PLATFORM.md) | Windows agent, gateway, heartbeat/presence at 10k devices, telemetry |
| [INFRASTRUCTURE.md](INFRASTRUCTURE.md) | Production deployment on Docker Compose / VPS |
| [API_DOCUMENTATION.md](API_DOCUMENTATION.md) | API surfaces, conventions, current endpoints |
| [DEVELOPMENT_ROADMAP.md](DEVELOPMENT_ROADMAP.md) | Phases and exit criteria |
| [PHASE-01-SUMMARY.md](PHASE-01-SUMMARY.md) | What Phase 01 delivered and how it was verified |

## 1. Principles

1. **Modular monolith.** One repository, one database, one release, with strict module
   boundaries.
2. **Security is structural.** Tenant isolation is enforced in the database. Every
   endpoint action is permission-checked. Every endpoint-side action is a signed,
   allow-listed command.
3. **One request = one tenant.** MSP access is delegation into a single customer tenant
   per request, never a widened view.
4. **Designed for 10,000 devices on one well-sized host,** stateless enough to scale out
   without a rewrite. Not designed for millions.
5. **Provider abstractions** for every external dependency (object storage, signing, KMS,
   e-mail, remote desktop, patch sources, AI), so a future on-prem edition is a
   configuration change.

## 2. Deployable units [Target; `web` and `api` Implemented]

| Host | Project | Responsibility | Public |
|------|---------|----------------|--------|
| `web` | `src/frontend` | Next.js UI + BFF (session cookie, CSRF, proxy to api) | yes (`app.<domain>`, custom domains) |
| `api` | `Solvix.Api` | Management plane: REST `/api/v1`, console SignalR hub | no (only via BFF) |
| `gateway` | `Solvix.Gateway` (new) | Device plane: `/agent/v1`, agent SignalR hub, telemetry ingest, remote tunnel | yes (`agents.<domain>`) |
| `worker` | `Solvix.Worker` (new) | Hangfire, presence detector, flushers, rollups, outbox dispatcher, retention | no |
| `migrator` | `Solvix.Migrator` | Applies migrations, then exits | no |

All .NET hosts reference the same module assemblies. A module contributes controllers,
hub methods or jobs to whichever host it targets through its `IModule` registration.

## 3. Module map

```
Solvix.SharedKernel  ◄── every module (entities, tenant context, permissions, exceptions)
Solvix.Infrastructure  (EF Core, interceptors, Redis, outbox, Hangfire wiring, storage/KMS providers)

Implemented: Tenancy · Identity (users, MFA, sessions, roles & permissions, invitations,
             auth audit; see PHASE-02-SUMMARY.md) · Organization
Target:      Authorization (in Identity) · Audit · Devices · Inventory · Monitoring · Patch ·
             Software · Commands · Approvals · RemoteAccess · Notifications · Files ·
             Reporting · (post-V1) AI · ApplePlatform · AndroidEnterprise
```

Boundary rules:
* Modules expose a `Contracts` namespace (interfaces, DTOs, integration events). No module
  references another module's entities or `DbSet`s. An architecture test enforces this.
* `Devices` owns the device record. Everything that changes an endpoint goes through
  `Commands`, so Software, Patch, RemoteAccess and AI create command requests rather
  than talking to agents.
* Cross-module side effects (audit, notifications, live UI updates) use domain events
  written to the **transactional outbox** and dispatched by the worker.
* Inside a module: `Domain` (invariants), `Application` (use cases, validators, DTOs),
  `Persistence` (EF configurations), `Api` (controllers). No generic repository over EF
  Core; a dedicated query or repository class only where it carries real logic.

## 4. Tenancy and MSP model [Phase 01 tenancy Implemented; MSP Target]

* A **tenant** is the isolation boundary. `kind = customer` (owns devices) or
  `kind = partner` (an MSP: owns staff, roles, settings, audit; **never devices**).
* A partner manages customers through **links** (at most one active managing partner per
  customer) and **grants** (partner membership → role → customer or all linked
  customers).
* An MSP user who switches to customer C receives a token with `tid = C` and
  `ptid = partner`. Every request re-validates membership, link and grant.
* Direct customers register exactly as today. They can later accept a partner link, and
  can revoke it.

Data model in [DATABASE.md §2](DATABASE.md); controls in [SECURITY.md §2.3](SECURITY.md).

## 5. Request pipeline [Implemented]

```
ForwardedHeaders (trusted proxies only) → ExceptionHandler → StatusCodePages → CorrelationId
→ security headers → Serilog request log → CORS → RateLimiter → Authentication (JWT)
→ TenantResolutionMiddleware → Authorization (permission policies) → Controllers
```

`TenantResolutionMiddleware` is the only place a tenant and an access grant are attached
to a request. It reads `tid`, `sub` and `sid` from the validated token, rejects an
`X-Tenant-Id` header that disagrees, checks the tenant is active, and then resolves,
inside the tenant scope, that the session is alive, the membership and account are
active, and which permissions the member's roles grant now (Redis-cached for 30 s,
invalidated on change and again after commit). Authorization then evaluates
`[RequirePermission]` against that grant; the token carries no roles or permissions.
Every controller action must declare `[AllowAnonymous]`, `[RequirePermission]` or
`[AuthenticatedOnly]`, or the host refuses to start. **[Implemented, Phase 02]**

**[Target]** `IAccessResolver` additionally handles the partner path (`ptid`, link and
grant checks, ADR-003). The gateway uses the same middleware shape with device tokens.

## 6. Tenant isolation – four layers [Implemented]

| Layer | Mechanism | Protects against |
|-------|-----------|------------------|
| 1. API | Tenant comes only from the signed token; no endpoint accepts a tenant id; ids from other tenants resolve to 404; role policies. | URL/body/header manipulation |
| 2. EF Core reads | Named global query filter `Tenant` on every `ITenantScoped` entity, bound to the scoped `ITenantContext`. Unresolved context → zero rows. | Forgotten `WHERE tenant_id = …` |
| 3. EF Core writes | `TenantEnforcementInterceptor`: assigns the tenant to new rows, rejects writes for other tenants, rejects any `TenantId` change, stamps audit columns. Tenant-safe composite foreign keys. | Cross-tenant inserts/updates, cross-tenant parent references |
| 4. PostgreSQL | `TenantSessionConnectionInterceptor` sets `app.tenant_id` on every opened connection; FORCE RLS + `tenant_isolation` policy on every tenant table; runtime role is `NOBYPASSRLS`; session state reset on pool return. | Raw SQL, tooling, bugs that bypass EF |

Platform-level tables (`tenants`, `users`, `user_tokens`, `user_password_history`,
`user_recovery_codes`) are not tenant-owned. `auth_audit_events` has an optional tenant:
RLS shows a tenant its own rows only, rows without a tenant are platform-only, and
triggers make the table append-only. Operations that span
tenants (registration, login, tenant switching) currently run inside
`ITenantContextMutator.BeginPlatformScope()`, which sets `app.rls_bypass = on` for those
connections.

**[Target, Phase 02 – ADR-004]** The bypass variable is removed from all policies because
the runtime role can set it itself (risk R-1). Platform operations move to narrow
`SECURITY DEFINER` functions and a worker-only maintenance role. Partitioned tables are
only reachable through their RLS-protected parents (ADR-009).

Background work uses `ITenantJobRunner` / `ITenantScopeFactory`, which create a DI scope
bound to one tenant. A DbContext resolved from the root scope sees nothing and cannot
insert. **Every Hangfire job and every batch writer must use this**, grouping work by
tenant.

## 7. Device plane [Target]

The Windows agent (LocalSystem service + separate updater) authenticates with a
TPM-backed key and short-lived device tokens. It keeps one outbound SignalR WebSocket
for heartbeats and command push and uploads inventory and telemetry over HTTPS.
Presence lives in Redis; PostgreSQL receives only transitions and batched updates.
Commands are typed, allow-listed, per-device signed envelopes with nonce, sequence and
expiry. Remote desktop uses guacd through an agent reverse tunnel. Full design:
[DEVICE_PLATFORM.md](DEVICE_PLATFORM.md), security in [SECURITY.md §5–§9](SECURITY.md).

MDM readiness: devices carry `platform` and `management_channel`. Apple and Android
Enterprise will be separate protocol modules that create devices with their own channel;
the Windows agent never emulates MDM (ADR-014).

## 8. Background processing [Target]

| Kind | Mechanism | Examples |
|------|-----------|----------|
| High-frequency streams | gateway in-process channels + Redis; batched DB writes | heartbeats, metrics, presence |
| Periodic singleton loops | `BackgroundService` in worker + Redis lock | offline detector, last-seen flush, rollups, dynamic groups |
| Scheduled / retriable / long-running | **Hangfire** (PostgreSQL storage in its own `hangfire` schema, explicitly excluded from the RLS rule) | retention, partition maintenance, exports, deletions, report generation, alert digests |
| Reliable side effects | transactional outbox → dispatcher in worker | audit, notifications, SignalR fan-out |

Hangfire jobs receive a `tenantId` argument and execute through `ITenantJobRunner`.
Job arguments never contain secrets or personal data.

## 9. Caching and real-time [Implemented: cache; Target: rest]

`IDistributedCache` (Redis) is wrapped by `ResilientDistributedCache`: a failure degrades
to a cache miss and opens a 15 s circuit, so a Redis outage never fails requests. Keys:
`solvix:platform:tenant:*` (tenant directory, 5 min),
`solvix:tenant:{id}:access:{user}` and `solvix:tenant:{id}:session:{sid}` (30 s),
`solvix:tenant:{id}:security-policy` (5 min), all removed on change and again after the
transaction commits, and `ITenantCache` for tenant-scoped data.

**[Target]** Redis additionally provides presence, distributed locks, rate limiting
(**fail closed** for authentication endpoints, fail open elsewhere), and the SignalR
backplane when more than one node runs. Redis runs with AOF and a password/ACL user.
Console SignalR hub groups are `tenant:{id}`, joined only after server-side tenant
resolution.

## 10. Storage [Target]

`IObjectStorage` (S3-compatible) for packages, scripts, exports, command output and
session recordings. Tenant data is under `tenants/{tenantId}/…` prefixes; access is only
through short-lived pre-signed URLs issued after authorization.

## 11. Frontend [Implemented; Target changes noted]

Next.js App Router with a backend-for-frontend. Access and refresh tokens are kept in
httpOnly, SameSite=Lax cookies (`__Host-`, Secure in production) by the `/api/auth/*`
route handlers and never reach browser JavaScript. `/api/proxy/v1/*` attaches the access
token, renews it with the refresh cookie when expired, and enforces CSRF (Origin,
Fetch-Metadata, double-submit token). `proxy.ts` renews sessions for page navigations,
gates routes and issues the CSRF cookie; the `(app)` layout loads `/auth/me` (user,
tenant, roles, permissions) on every navigation and the UI shows only what those
permissions allow. Shell: sidebar, header with breadcrumbs, theme selector (light/dark/
system via next-themes), notification centre, user menu with workspace switching.

**[Target]** shadcn/ui (Radix) primitives replace the hand-written ones without changing
their props. TanStack Table provides the data grid and TanStack Query the interactive
server state. Shared patterns: DataTable, FilterBar, EmptyState, ErrorState,
ConfirmDialog, typed "danger" confirmation, DetailDrawer, StatusBadge. Also planned: a
tenant switcher that shows direct and partner-delegated tenants (with the partner
context visibly indicated), SignalR live updates through a short-lived hub ticket from
the BFF, branding by host for custom domains, WCAG 2.2 AA, and i18n-ready strings.

## 12. Configuration, logging and errors [Implemented]

Options classes are bound from configuration (`Solvix:Database`, `Solvix:Redis`,
`Solvix:Jwt`, `Solvix:Identity`, `Solvix:Email`, `Solvix:RateLimiting`, `Solvix:Network`,
`Solvix:Cors`) and validated at start-up. Environment variables use
`Section__Key`. **[Target]** Secrets are read from files (`*_FILE`, Docker secrets), and
new sections are added: `Solvix:Retention`, `Solvix:Signing`, `Solvix:Storage`,
`Solvix:Deployment:Region`.

Serilog writes compact JSON in containers (text in Development), enriched with
`CorrelationId`, `TraceId`, `TenantId`, `TenantSlug`, `UserId`, machine and environment.
**[Target]** It adds `PartnerTenantId` and `DeviceId`. `GlobalExceptionHandler` turns
domain exceptions and PostgreSQL constraint violations into RFC 9457 problem details
with a stable `code` and, for validation, an `errors` map keyed by camelCase property.

## 13. Implemented data model (Phases 01–02)

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
tenant-owned tables are composite `(tenant_id, ...)` foreign keys, so references can
never cross tenants. Names are snake_case via a model convention. The target model
(MSP tenancy, devices, audit module) is in [DATABASE.md](DATABASE.md); the gaps between
the two are tracked in [ARCHITECTURE-CONFORMANCE.md](ARCHITECTURE-CONFORMANCE.md).
