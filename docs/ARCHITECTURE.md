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

Implemented: Tenancy · Identity · Organization
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
ExceptionHandler → StatusCodePages → CorrelationId → Serilog request log → CORS
→ Authentication (JWT) → TenantResolutionMiddleware → Authorization → Controllers
```

`TenantResolutionMiddleware` is the only place a tenant is attached to a request. It
reads the `tid` claim of the validated token, rejects an `X-Tenant-Id` header that
disagrees, checks that the tenant exists and is active (Redis-cached), and that the user
still holds an active membership (Redis-cached, invalidated on change).

**[Target, Phase 02]** Membership checking becomes an `IAccessResolver` that handles
both paths (direct membership, or partner grant via `ptid`) and returns the effective
permission set for the authorization handlers. Rate limiting is inserted before
authentication. The gateway uses the same middleware shape with device tokens.

## 6. Tenant isolation – four layers [Implemented]

| Layer | Mechanism | Protects against |
|-------|-----------|------------------|
| 1. API | Tenant comes only from the signed token; no endpoint accepts a tenant id; ids from other tenants resolve to 404; role policies. | URL/body/header manipulation |
| 2. EF Core reads | Named global query filter `Tenant` on every `ITenantScoped` entity, bound to the scoped `ITenantContext`. Unresolved context → zero rows. | Forgotten `WHERE tenant_id = …` |
| 3. EF Core writes | `TenantEnforcementInterceptor`: assigns the tenant to new rows, rejects writes for other tenants, rejects any `TenantId` change, stamps audit columns. Tenant-safe composite foreign keys. | Cross-tenant inserts/updates, cross-tenant parent references |
| 4. PostgreSQL | `TenantSessionConnectionInterceptor` sets `app.tenant_id` on every opened connection; FORCE RLS + `tenant_isolation` policy on every tenant table; runtime role is `NOBYPASSRLS`; session state reset on pool return. | Raw SQL, tooling, bugs that bypass EF |

Platform-level tables (`tenants`, `users`) are not tenant-owned. Operations that span
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
`solvix:tenant:{id}:membership:{user}` (60 s), and `ITenantCache` for tenant-scoped data.

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

Next.js App Router with a backend-for-frontend: the access token is stored in an
httpOnly cookie by `/api/auth/*` route handlers and attached by `/api/proxy/v1/*` to
upstream calls, so it never reaches browser JavaScript. `proxy.ts` gates routes by
cookie validity, and the `(app)` layout validates the session against `/auth/me` on
every navigation. The shell has a sidebar, a header with breadcrumbs, a theme selector
(light/dark/system via next-themes), a notification centre and a user menu.

**[Target]** shadcn/ui (Radix) primitives replace the hand-written ones without
changing their props. TanStack Table provides the data grid and TanStack Query the
interactive server state. Shared patterns: DataTable, FilterBar, EmptyState, ErrorState,
ConfirmDialog, typed "danger" confirmation, DetailDrawer, StatusBadge. Also planned: a
tenant switcher that shows direct and partner-delegated tenants (with the partner context
visibly indicated), a permission-aware UI, SignalR live updates through a short-lived hub
ticket from the BFF, branding by host for custom domains, WCAG 2.2 AA, and i18n-ready
strings.

## 12. Configuration, logging and errors [Implemented]

Options classes are bound from configuration (`Solvix:Database`, `Solvix:Redis`,
`Solvix:Jwt`, `Solvix:Cors`) and validated at start-up. Environment variables use
`Section__Key`. **[Target]** Secrets are read from files (`*_FILE`, Docker secrets), and
new sections are added: `Solvix:Retention`, `Solvix:Signing`, `Solvix:Storage`,
`Solvix:Deployment:Region`.

Serilog writes compact JSON in containers (text in Development), enriched with
`CorrelationId`, `TraceId`, `TenantId`, `TenantSlug`, `UserId`, machine and environment.
**[Target]** It adds `PartnerTenantId` and `DeviceId`. `GlobalExceptionHandler` turns
domain exceptions and PostgreSQL constraint violations into RFC 9457 problem details
with a stable `code` and, for validation, an `errors` map keyed by camelCase property.

## 13. Phase 01 data model [Implemented]

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
`*` = unique index. Ids are UUIDv7. Names are snake_case via a model convention. The
target model is in [DATABASE.md](DATABASE.md).
