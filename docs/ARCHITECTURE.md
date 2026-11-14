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
API is reachable exclusively through that proxy. The single-VPS production stack uses the Kestrel
endpoint, because the web container also reaches the API directly (`docs/DEPLOYMENT-VPS.md`,
*Windows agents*).

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
`DeviceManagementSecurity`), verified by `MigrationTests` through `RowLevelSecurity.AllTenantTables`.
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

---

# IT asset management (Phase 05)

## Module boundaries

`Solvix.Modules.Assets` owns the physical asset register. It has **no project reference** to
`Solvix.Modules.Devices` (and vice versa); the two meet only through shared-kernel contracts:

```
Devices ──implements──► IEndpointDirectory          ◄──uses── Assets
Devices ──publishes───► IEndpointIdentityObserver   ◄──implements── Assets (AssetEndpointObserver)
            both use ► HardwareIdentity (serial / manufacturer / model normalisation)
```

* `IEndpointDirectory` – tenant-scoped read access to enrolled devices (by id, by normalised serial,
  paged search, active count). Backed by an indexed generated column
  `devices.serial_number_key = upper(btrim(serial_number))`.
* `IEndpointIdentityObserver` – called by `EndpointIdentityPublisher` **after** an enrollment or
  re-enrollment commits, and after an inventory upload changes serial number, manufacturer or model.
  Observer failures are logged and swallowed: asset matching can never make enrollment fail.

## Data model

```
asset_vendors ◄─┐                                 branches / departments (Organization)
                │ (tenant_id, vendor_id)                 ▲ SET NULL (column)
assets ─────────┴── (tenant_id, id) alternate key ───────┤
  │  ux (tenant, asset_tag) · ux (tenant, manufacturer_key, serial_number_key) · xmin version
  ├── asset_assignments          (one open per asset: partial unique index)
  ├── asset_transfers            (from/to branch, department, location)
  ├── asset_maintenance_records  (one InProgress per asset: partial unique index)
  ├── asset_physical_audit_items ──► asset_physical_audits
  └── asset_device_links ──(tenant_id, device_id)──► devices (CASCADE)
        ux (tenant, asset, device) · ux (tenant, asset) WHERE Approved · ux (tenant, device) WHERE Approved
asset_settings (one per tenant) · asset_tag_sequences (upsert counter) · asset_audit_log (append-only)
```

All 11 tables are tenant-owned: global query filter, write interceptor, FK to `tenants`, forced RLS.
Cross-module references are tenant-safe composite foreign keys declared in
`AssetManagementSecurity` (an asset's current holder references `users` with `NO ACTION`: a holder
cannot vanish silently). Check constraints mirror the domain rules (non-negative amounts, salvage ≤
cost, warranty period, depreciation inputs, `InUse` ⇒ assignee, one assignee kind, disposal
details, upper-case tags).

## Lifecycle

```
            assign ┌────────┐ return
 register ─► InStock ◄────► InUse ──┐
     │         ▲  │ maintenance     │ maintenance (status restored afterwards)
     │         │  ▼                 ▼
     │      found  InMaintenance ◄──┘
     │         │
     │       Missing (manual, bulk, or physical audit completion)
     ▼
  Retired ──reinstate──► InStock          Retired ──dispose──► Disposed (read-only, terminal)
```

Branch, department and location change only through transfers (history rows). Assignments may
target a workspace member or an employee without a login (name + employee number). Bulk actions
(transfer, check-in, condition, missing, retire; ≤ 500 assets) are all-or-nothing.

## Asset numbering

`{prefix}-{category code}-{000001}` (e.g. `AST-LAP-000042`), from a per-tenant, per-scope counter
incremented with `INSERT … ON CONFLICT DO UPDATE … RETURNING` (atomic under concurrency; numbers
taken by manually entered tags are skipped). Changing the prefix starts a new sequence.

## Endpoint matching

`MatchEvaluator` (pure, unit-tested) scores an asset/endpoint pair:

| Evidence | Effect |
|----------|--------|
| Serial number (normalised, placeholders such as "To be filled by O.E.M." rejected) | **required** for a suggestion |
| Manufacturer ("Dell Inc." = "DELL", "Hewlett-Packard" = "HP", corporate suffixes ignored) | match / mismatch / unknown |
| Model (letters + digits; containment counts as partial) | supporting only — OEMs often report machine-type codes |
| Category that does not run the agent (monitor, switch …) | warning, never High |
| More than one asset or endpoint with the serial, or either side already linked | ambiguous |

Confidence: **High** = serial + manufacturer match, model not contradicting, agent-capable category;
**Medium** = serial match, manufacturer not contradicting; **Low** otherwise.

Evaluation runs from both sides (endpoint enrolled / hardware changed; asset created / identity
edited; admin "rescan"). It creates `Suggested` links with the evidence (jsonb) and withdraws
suggestions whose serials no longer match. Decisions are remembered per pair (`Rejected`,
`Superseded`, `Unlinked`) and never re-suggested. Approving a link supersedes competing
suggestions; partial unique indexes guarantee one approved endpoint per asset and vice versa.

Nothing is linked without an administrator unless the tenant enables
`AutoApproveHighConfidenceMatches` — and even then only for a High-confidence pair that is
unambiguous on both sides. The asset and device records are never merged or synchronised; the
link exposes discrepancies (serial, manufacturer, model, branch, assigned user) for the reviewer.
Unregistered endpoints can be turned into assets (pre-filled from inventory, then linked).

## Audit log

Every change writes an `asset_audit_log` row in the same transaction (action code, summary,
`{field: {from, to}}` change set, actor user or `System` for agent-triggered matching,
correlation id). A trigger rejects UPDATE and TRUNCATE always and DELETE outside platform scope,
for the application role *and* the schema owner.

## Labels, export, depreciation

* `GET /assets/{id}/barcode.svg` (Code 128 of the tag) and `/qr.svg` (QR, error correction M),
  encoded by ZXing.Net and rendered as compact SVG paths; served with `nosniff` and a sandboxing
  CSP. QR payload: `{LabelBaseUrl}/assets/{id}` when configured, else `SOLVIX-ASSET:{tag}`.
  Scanning accepts the tag, either QR payload or the serial number.
* CSV exports (register with all filters, depreciation) are RFC 4180 with a UTF-8 BOM and
  spreadsheet formula-injection protection.
* Depreciation: straight-line or double-declining balance (switching to straight-line), monthly
  from the purchase date, rounded per month with the final month landing exactly on salvage value;
  disposed assets stop depreciating.

## Access

All register, matching, audit, maintenance, vendor, report and settings endpoints require
`TenantAdmin` (they contain financial data). Members get `GET /my-assets` (their own assets, no
costs). Device certificates satisfy no user policy. Other tenants' ids resolve to 404.
