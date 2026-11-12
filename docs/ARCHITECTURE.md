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

---

# Device management (Phase 03)

## Components

```
Windows device                                         Solvix platform
┌──────────────────────────────┐   outbound HTTPS     ┌──────────────────────────────────────────┐
│ SolvixAgent service          │   mutual TLS         │ Kestrel "Agents" endpoint :8443          │
│ (NT SERVICE\SolvixAgent)     │ ───────────────────► │  (or TLS proxy + forwarded cert header)  │
│  runtime loop · inventory    │                      │ DeviceCertificate auth handler           │
│  key in TPM / CNG (no export)│                      │  → DeviceCredentialValidator (RLS scope) │
│  logs %ProgramData%\…\logs   │                      │ Devices module (Solvix.Modules.Devices)  │
└──────────────────────────────┘                      │ Device CA (ECDSA P-384, PKCS#12)         │
  Solvix Agent Updater task (SYSTEM)                  └──────────────────────────────────────────┘
  installs only re-verified staged MSIs                 Admin UI / API (JWT, unchanged)
```

`Solvix.Modules.Devices` follows the module pattern of Phase 01 and depends only on the
SharedKernel and `Solvix.Agent.Contracts`. It reaches Identity and Organization through two
new read-only SharedKernel contracts, `IMemberDirectory` and `IOrganizationDirectory`.

## Device identity and authentication

1. **Enrollment token** — created by a tenant admin, `svxe_` + base64url(tenantId ‖ tokenId ‖
   256-bit secret). Only SHA-256(secret) is stored. Default lifetime 24 h, maximum 7 days
   (configurable), limited uses, revocable, shown once. The embedded tenant id lets the server bind
   the anonymous request to that tenant *before* reading the token row, so the lookup runs under RLS;
   a tampered tenant or token id simply finds nothing.
2. **Enrollment** (`POST /api/v1/agent/enroll`, anonymous, rate limited per client IP): token
   checks (constant-time hash compare, expiry, revocation, remaining uses) → CSR validation
   (signature = proof of possession; ECDSA P-256/P-384 or RSA 2048–4096; requested subject ignored)
   → atomic token consumption (`UPDATE … WHERE use_count < max_uses`) → device created or re-enrolled
   → device CA signs a client certificate → one transaction; any failure leaves the token unused.
3. **Device certificate** — `CN=<deviceId>, OU=<tenantId>`, SAN `urn:solvix:device:<tenant>:<device>`,
   EKU clientAuth, 90 days. Only the public certificate is stored (`device_credentials`, keyed by
   SHA-256 thumbprint); the private key never leaves the device.
4. **Every agent request** — the `DeviceCertificate` scheme validates the chain against the device
   CA only (`CustomRootTrust`, never the OS store), validity period and EKU. The validator then reads
   the tenant from the SAN, checks the tenant is active, binds the tenant context and verifies, under
   RLS, that the thumbprint belongs to that device, is not revoked and the device is active. Agent
   endpoints require policy `Device` (certificate scheme only); user endpoints keep the JWT default
   scheme, so neither credential type can call the other's API.
5. **Renewal** — `POST /api/v1/agent/certificate` with a new CSR. The previous certificate stays
   valid until the new one is first used, then it is revoked (`Superseded`): an agent that crashes
   mid-renewal is never locked out.
6. **Revocation** — retiring a device revokes all of its credentials immediately; deleting it removes
   them. Re-enrollment of the same hardware revokes the previous credentials (`ReEnrolled`).

TLS can terminate in Kestrel (named endpoint `Agents`, `ClientCertificateMode.AllowCertificate`) or
at a trusted reverse proxy that forwards the client certificate in a configured header
(`Solvix:Devices:ForwardedClientCertificateHeader`). The header mode must only be enabled when the
API is reachable exclusively through that proxy.

## Duplicate devices

The hardware fingerprint is SHA-256(SMBIOS UUID ‖ serial). Placeholder values (all-zero/all-F UUIDs,
"To be filled by O.E.M.", …) produce no fingerprint, so unrelated machines are never merged. A
unique index `(tenant_id, hardware_fingerprint)` keeps one record per machine *per tenant*; the
same machine in two tenants yields two independent records. Re-enrolling known hardware keeps the
record (groups, assignment, history) and revokes its old credentials; a *retired* device cannot
re-enroll until its record is deleted.

## Data model

```
devices (RLS)                  (tenant_id, id) AK; unique (tenant_id, hardware_fingerprint)
  ├── device_credentials       FK (tenant_id, device_id) CASCADE; unique thumbprint
  ├── device_inventories       FK (tenant_id, device_id) CASCADE; 1:1; jsonb report
  └── device_group_memberships FK (tenant_id, device_id) CASCADE, FK (tenant_id, device_group_id) CASCADE
device_groups (RLS)            unique (tenant_id, normalized_name)
enrollment_tokens (RLS)        secret_hash bytea; FK (tenant_id, device_group_id) RESTRICT
devices.assigned_user_id → users(id) ON DELETE SET NULL
devices.(tenant_id, branch_id) → branches ON DELETE SET NULL (branch_id)
devices.(tenant_id, department_id) → departments ON DELETE SET NULL (department_id)
```

All six tables are covered by the `tenant_isolation` policy (migration
`DeviceManagementSecurity`), verified by `MigrationTests` through `TenantIsolatedTables.All`.
Inventory-derived columns on `devices` (OS, model, IPs, MACs, agent version, last seen) are a
denormalised summary for listing/filtering; the full validated report lives in
`device_inventories.report_json`. Connectivity is derived: a device is *online* when it has
been seen within `Agent:OfflineAfterSeconds` (default 300 s, i.e. 5 missed heartbeats).

## API

| Route | Auth | |
|-------|------|--|
| `POST /api/v1/agent/enroll` | anonymous + token | rate limited, 64 KB |
| `POST /api/v1/agent/heartbeat` | device cert | health report → schedules + boolean hints |
| `PUT /api/v1/agent/inventory` | device cert | ≤ 8 MB, bounded collections |
| `POST /api/v1/agent/certificate` | device cert | renewal |
| `GET /api/v1/agent/updates` | device cert | release offer from the platform catalog |
| `GET /api/v1/devices` (+`stats`, `{id}`, `{id}/inventory`) | member | filters: search, status, connectivity, group, agent version, user |
| `PUT /api/v1/devices/{id}` `…/assignment` `…/groups`, `POST …/inventory-request` `…/retire`, `DELETE` | admin | |
| `/api/v1/device-groups` (+`{id}/devices`) | member read / admin write | |
| `/api/v1/enrollment-tokens` (+`{id}/revoke`) | admin | token value returned once, `Cache-Control: no-store` |

## Configuration (`Solvix:Devices`)

| Key | Default | |
|-----|---------|--|
| `CertificateAuthority:PfxPath` / `PfxBase64` / `PfxPassword` | – | Device CA bundle (required in production) |
| `CertificateAuthority:AutoGenerate` | `false` | Development/evaluation: create a CA (persisted to `PfxPath` if set) |
| `CertificateAuthority:DeviceCertificateLifetimeDays` / `RenewalWindowDays` | 90 / 30 | |
| `Enrollment:DefaultTokenLifetimeHours` / `MaxTokenLifetimeHours` / `MaxTokenUses` | 24 / 168 / 10000 | |
| `Enrollment:RateLimitPerMinute` | 30 | per client IP |
| `Agent:HeartbeatIntervalSeconds` / `InventoryIntervalMinutes` / `UpdateCheckIntervalMinutes` | 60 / 240 / 360 | delivered to agents |
| `Agent:OfflineAfterSeconds` | 300 | |
| `ForwardedClientCertificateHeader` | – | only behind a trusted TLS proxy |
| `Releases[]` | – | `Version`, `Channel`, `DownloadUrl` (https), `Sha256`, `SizeBytes`, `Mandatory` |
| `GenerateDevelopmentTlsCertificate` / `DevelopmentTlsHostNames` | `false` | self-signed cert for `Kestrel:Endpoints:Agents` (refused in Production) |

The options are validated at start-up (ranges, release URLs/hashes, CA presence) and the CA is
loaded eagerly, so misconfiguration fails fast.


# Real-time monitoring (Phase 04)

## Three independent signals

| Signal | Source | Statuses |
|--------|--------|----------|
| **A. Agent connectivity** | authenticated agent contact only (heartbeat or any other agent call) | Online · Warning · Offline |
| **B. Network reachability** | the agent's built-in probes on the device: operational adapter, default gateway (ICMP), DNS, TCP to *locally configured* targets | Reachable · Degraded · Unreachable · Unknown |
| **C. Operating-system health** | measured on the device: available memory, system-drive free space, pending restart; boot time | Healthy · Warning · Critical · Unknown |

A heartbeat is never treated as evidence for B or C. When the agent is not online, B and C are
shown as *last known* (stale) and counted as Unknown in fleet totals. Probe targets come from
local agent configuration; the server cannot choose them or trigger probes.

## Connectivity rules

`silence = now − last contact`: **Online** below the warning threshold, **Warning** from the
warning threshold up to and including the offline threshold, **Offline** only once the offline
threshold is exceeded (`PresencePolicy.Evaluate`). Defaults 120 s / 300 s, heartbeat 60 s;
tenants change them in `monitoring_settings` (warning ≥ 2 × heartbeat, offline > warning).

Displayed state is the **persisted** state (`device_presence.state`), the same one that drives
events and alerts, so the UI never shows a device offline before the monitor has established it.

## Heartbeat pipeline (write-avoiding)

```
agent ─heartbeat─► AgentService
   ├─ devices row: written only if version / health status / OS build / issues changed,
   │               or the stored health report is older than SignalPersistIntervalSeconds
   └─ PresenceTracker.ProcessHeartbeatAsync
        ├─ state ≠ Online, agent restart, reboot, signal change, snapshot refresh due
        │     → one SaveChanges: presence + connection event + availability days + alert
        └─ otherwise → PresenceBuffer (memory, no DB write)
PresenceBuffer ─every FlushIntervalSeconds─► one UPDATE … FROM unnest(…) RETURNING per tenant
   (tenant scope ⇒ RLS); rows returned in a non-Online state are recovered via the tracker
```

* `device_presence` is a narrow table with `fillfactor = 70` and **no index on
  `last_seen_at_utc`**, so contact updates are HOT updates.
* Transitions use optimistic concurrency (`revision` and `last_seen_at_utc` are concurrency
  tokens); a conflicting decision is retried with fresh data. A partial unique index allows at
  most one open alert per device.
* Every agent request is recorded (`RecordAgentContact` filter): *last seen* for any
  authenticated request, *last successful communication* for 2xx/3xx.

## Monitor sweep (`PresenceMonitor`, every `SweepIntervalSeconds`)

1. Flush the local buffer; take a PostgreSQL advisory lock (one sweeping node at a time).
2. **Outage detection:** if the previous sweep is older than `OutageDetectionSeconds`, nobody was
   observing; silence then counts only from `now + min(gap, MaxRecoveryGraceSeconds)` (the
   *evidence floor*), so agents still in retry backoff are not declared offline.
3. One platform-scoped query pre-selects candidates (thresholds joined per tenant); each tenant's
   devices are evaluated in a tenant scope. A tolerance of flush interval + 2 s is added, so
   buffered-but-unflushed contact can never cause an early transition.
4. Day rollover closes the availability ledger at UTC midnight; hourly retention purge.

The agent caps its retry delay at the heartbeat interval when the server is unreachable (no HTTP
response), so a network interruption is not followed by minutes of self-imposed silence; server
errors (5xx/429) keep the full exponential backoff.

## History, availability, alerts

* `device_connection_events`: Enrolled, HeartbeatDelayed, HeartbeatRestored, WentOffline,
  Recovered, Reconnected (failed attempts bridged without a warning), AgentRestarted,
  DeviceRebooted, NetworkStatusChanged, OsHealthChanged.
* `device_availability_days`: online / warning / offline seconds and offline count per device and
  UTC day, written on transitions and at rollover; reports add each device's open interval.
  Availability = (online + warning) / monitored time; downtime is counted from the start of the
  Warning that preceded an outage.
* `monitoring_alerts`: opened on Offline (if enabled), resolved on recovery (recovery
  notification if enabled), acknowledgeable by admins.

## Live updates

SignalR hub `/hubs/monitoring` (authenticated, tenant group from the token, server → client
only, `CloseOnAuthenticationExpiration`). One `monitoringUpdate` message per unit of work and
tenant carries status changes, events and alerts; routine heartbeats are never pushed. The
browser connects through the BFF route `/api/realtime/hubs/monitoring` (Server-Sent Events, long
polling fallback) so the session token stays in its httpOnly cookie. Multiple API nodes: enable
`RedisBackplane`, and use sticky sessions (SSE/long polling negotiate per node).
