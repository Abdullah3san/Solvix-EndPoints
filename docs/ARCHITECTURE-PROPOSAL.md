# Solvix-EndPoint – Target Architecture Proposal

**Status:** Draft for approval · **Date:** 2026-11-12 · **Baseline:** Phase 01 (`0b81db5`)

This document is the product-level architecture for Solvix-EndPoint (UEM / MDM / ITAM /
remote administration / monitoring / AI). It does **not** replace `docs/ARCHITECTURE.md`,
which documents what is built today. Once approved, its sections will be split into
`ARCHITECTURE.md`, `DATABASE.md`, `SECURITY.md`, `API_DOCUMENTATION.md` and
`DEVELOPMENT_ROADMAP.md`.

Sections marked **DECISION** need an explicit answer before the relevant phase starts
(collected in §12).

---

## 0. Baseline – what already exists

| Area | State | Assessment |
|------|-------|-----------|
| Modular monolith (`SharedKernel`, `Infrastructure`, modules Tenancy / Identity / Organization, `Api` host, `Migrator`) | Built | Keep. Sound boundaries, `IModule` registration. |
| Tenant isolation: JWT `tid` → middleware → EF query filter → SaveChanges interceptor → PostgreSQL FORCE RLS, `NOBYPASSRLS` runtime role | Built, tested (31 integration tests) | Keep. Strongest part of the codebase. One weakness, see R-1. |
| Auth: HS256 JWT (60 min), BFF with httpOnly cookie, 3 fixed roles (Owner/Admin/Member) | Built | Must evolve: asymmetric keys, refresh/revocation, permission-based RBAC, MFA. |
| Errors / logging / versioning / health | Built (RFC 9457, Serilog JSON, `/api/v1`, health checks) | Keep. |
| Frontend: Next.js 16, Tailwind 4, hand-written UI primitives, BFF proxy | Built | Primitives are not shadcn/ui – see conflict C-1. |
| Hangfire, SignalR, pgvector, agent, audit log, rate limiting, CI | Not present | Planned below. |

---

## 1. System architecture

### 1.1 Logical view

```
                   ┌──────────────────────── Browser ────────────────────────┐
                   │ Next.js app (RSC + client components)                    │
                   └───────────────┬──────────────────────────────────────────┘
                                   │ HTTPS, cookie session (httpOnly, SameSite=Lax)
                   ┌───────────────▼──────────────┐
                   │ Next.js BFF (route handlers)  │  CSRF check, token never in JS
                   └───────────────┬──────────────┘
                                   │ HTTPS, bearer (user audience)
┌──────────────────────────────────▼───────────────────────────────────────────────┐
│ Solvix.Api  (management plane – modular monolith)                                 │
│  Identity · Tenancy · Organization · Authorization · Audit · Devices · Inventory  │
│  Software · Patch · Policy · Commands · Approvals · Remote · Alerts · Reports · AI │
│  SignalR hub: /hubs/console (browser live updates)                                │
└───────┬───────────────┬───────────────┬───────────────┬───────────────────────────┘
        │               │               │               │
   PostgreSQL 16+     Redis 7       Object storage     Hangfire server(s)
   (RLS, pgvector,    (cache,       (S3-compatible:    (same binary, `--role worker`)
    partitions)        backplane,    packages, files,
                       rate limits)  reports, scripts)
        ▲
┌───────┴──────────────────────────────────────────────────────────────────────────┐
│ Solvix.AgentGateway  (device plane – same solution, separate deployable host)     │
│  mTLS device auth · enrollment · check-in · telemetry ingest · command delivery    │
│  SignalR/WebSocket hub: /agent (push channel) · HTTPS long-poll fallback          │
└───────▲──────────────────────────────────────────────────────────────────────────┘
        │ mTLS (device certificate), TLS 1.2+/1.3, outbound only from endpoint
┌───────┴────────────────────┐        ┌─────────────────────────┐
│ Solvix Agent (Windows svc) │◄──────►│ Remote-session relay     │ (Phase 7, WebRTC/TURN or
└────────────────────────────┘        └─────────────────────────┘  WebSocket relay)
```

### 1.2 Key structural decisions

1. **One codebase, three hosts.** `Solvix.Api` (users), `Solvix.AgentGateway` (devices),
   `Solvix.Worker` (Hangfire). All three reference the same module assemblies. This is
   still a modular monolith – one repository, one schema, one release – but lets the
   device plane scale and be rate-limited independently of the admin UI, and keeps the
   agent attack surface (internet-facing to every endpoint) out of the admin host.
   Start with Api + Worker in one process if desired; the Gateway should be separate from
   day one because its authentication scheme (mTLS) and threat model differ.
2. **Modules communicate through public contracts only** (`Solvix.Modules.X.Contracts`
   assembly: interfaces, DTOs, integration events). No module references another module's
   internals or `DbSet`s. An architecture test (NetArchTest or reflection) enforces this.
3. **In-process integration events + transactional outbox.** Domain events raised inside
   a transaction are written to an `outbox_messages` table in the same transaction and
   dispatched by a Hangfire/background dispatcher. This gives reliable side effects
   (audit, notifications, SignalR fan-out, AI indexing) without a message broker. A broker
   (RabbitMQ/Kafka) is deferred until a measured need exists (see R-6).
4. **Single PostgreSQL database, schema per module** (`identity`, `tenancy`, `org`,
   `devices`, `inventory`, …). Migrations remain in one EF Core history for now (one
   DbContext) – splitting DbContexts per module is allowed later but not required.
5. **Clean Architecture inside each module, pragmatically:** `Domain` (entities, invariants),
   `Application` (use-case services, validators, DTOs), `Persistence` (EF configurations),
   `Api` (controllers). No generic repository over EF Core; repositories only for
   aggregates with real query/persistence logic (e.g. device inventory snapshots,
   telemetry). This matches Phase 01 conventions.

---

## 2. Module boundaries

| # | Module | Owns (aggregates) | Depends on (contracts) | Notes |
|---|--------|-------------------|------------------------|-------|
| 1 | **Tenancy** *(exists)* | Tenant, subscription/plan, licence counts, tenant settings | – | Add plan limits (device quota), data-residency region, deletion workflow. |
| 2 | **Identity** *(exists)* | User, TenantMembership, credentials, MFA factors, sessions/refresh tokens, API keys, SSO connections | Tenancy | Becomes the authentication authority. |
| 3 | **Authorization** *(new)* | Role, Permission, RoleAssignment, Scope (all / branch / device group) | Identity, Organization | Replaces fixed enum roles; see §4.3. Could live inside Identity – kept separate because every module depends on it. |
| 4 | **Organization** *(exists)* | CompanyProfile, Branch, Department | – | Branch becomes an authorization scope and a device "site". |
| 5 | **Audit** *(new)* | AuditEvent (append-only), SecurityEvent | – (consumes events) | Every module publishes; nobody else writes. |
| 6 | **Devices** *(new)* | Device, DeviceIdentity (certificate), EnrollmentToken, DeviceGroup (static/dynamic), Agent version/health | Organization, Authorization | Single source of truth for "a managed endpoint". |
| 7 | **Inventory / ITAM** *(new)* | Hardware snapshot, InstalledSoftware, Asset (incl. non-agent assets), warranty, contracts, licences | Devices | Hardware/software inventory is *observed state*; Asset is *managed record*. Keep them separate, linked by device id. |
| 8 | **Software** *(new)* | Package, PackageVersion, catalog, Deployment | Devices, Commands, Storage | Package files in object storage, hash + signature verified. |
| 9 | **Patch** *(new)* | Patch catalog, applicability per device, PatchPolicy, deployment rings, maintenance windows | Devices, Commands | Windows Update Agent API first; third-party patch catalog is a licensing decision (D-6). |
| 10 | **Configuration / Policy** *(new)* | Policy, PolicyAssignment, compliance result | Devices | Desired-state settings (registry, firewall, BitLocker, local users, USB). |
| 11 | **Commands** *(new)* | CommandDefinition (allow-list), CommandRequest, CommandExecution, result | Devices, Authorization, Approvals, Audit | The only path by which anything executes on an endpoint. See §4.6. |
| 12 | **Approvals** *(new)* | ApprovalPolicy, ApprovalRequest, decision | Authorization | Generic: used by Commands, Software, Patch, AI actions, tenant deletion. |
| 13 | **Remote Access** *(new, later)* | RemoteSession, recording metadata, consent | Devices, Approvals, Audit | Remote desktop / shell / file transfer. |
| 14 | **Monitoring & Alerts** *(new)* | Metric series, AlertRule, Alert, NotificationChannel | Devices | Telemetry is time-series; see §3.4. |
| 15 | **Notifications** *(new)* | In-app notification, e-mail/webhook delivery | – | Replaces the current client-side-only notification centre. |
| 16 | **Reporting** *(new)* | ReportDefinition, scheduled report, export | read models from other modules | Read-only; uses SQL views / read models, not other modules' entities. |
| 17 | **Files / Storage** *(new, shared infra)* | StoredFile metadata, upload sessions, malware-scan status | – | Secure upload pipeline (§4.10). |
| 18 | **AI Assistant** *(new)* | Conversation, KnowledgeDocument, Embedding, ToolInvocation | Read contracts of other modules, Commands, Approvals | Never executes directly; proposes actions that go through Commands + Approvals. |
| 19 | **MDM** *(new, late)* | Apple/Android/Windows-MDM enrollment, profiles | Devices, Policy | Separate protocol stack (APNs, Android Management API, OMA-DM). |
| 20 | **Platform Admin** *(new)* | Platform operators, tenant lifecycle, support access (time-boxed, audited) | Tenancy, Audit | Separate audience; never a tenant role. |

**Boundary rules**

* `Devices` is the only owner of the device row. Inventory, Patch, Policy, Commands,
  Alerts reference `device_id` with a tenant-safe composite FK `(tenant_id, device_id)`
  (the pattern Phase 01 already uses for branches/departments).
* A capability that writes to an endpoint is always a `Commands` request – Software,
  Patch, Policy and AI build command requests; they never talk to the gateway directly.
* Reporting and AI read through query contracts or database views, never through another
  module's EF entities.

---

## 3. Database architecture

### 3.1 Principles (inherited from Phase 01, extended)

* PostgreSQL 16+ (pgvector requires the extension; 16 is fine, 17 recommended).
* UUIDv7 keys, snake_case, schema per module.
* Every tenant-owned table: `tenant_id NOT NULL` + FK to `tenancy.tenants` + FORCE RLS
  `tenant_isolation` policy + covering index starting with `tenant_id`. Enforced by the
  existing migration test – extended to all schemas.
* Child tables use `(tenant_id, parent_id)` composite FKs so cross-tenant references are
  impossible at the database level.
* Soft delete only where a business requirement exists (devices: "retired" status;
  audit: never deleted until retention expiry).
* Optimistic concurrency (`xmin` row version) on all mutable aggregates.

### 3.2 Core entities (new)

```
devices.devices                (tenant_id, id) PK-alt; hostname, os, serial, branch_id,
                               status(pending|active|retired|quarantined), last_seen_at,
                               agent_version, identity_thumbprint
devices.device_identities      device cert serial, public key, issued/expires/revoked_at
devices.enrollment_tokens      hashed token, scope (branch/group), expires_at, max_uses, uses
devices.device_groups          static | dynamic (stored, validated filter expression)
devices.device_group_members
inventory.hardware_snapshots   device_id, collected_at, jsonb payload + promoted columns
inventory.installed_software   device_id, name, publisher, version, install_date (normalized)
inventory.software_titles      normalized title catalog (dedup across devices)
inventory.assets               managed asset record (also non-agent assets)
commands.command_definitions   allow-list: key, version, parameter JSON schema, risk level,
                               required permission, requires_approval
commands.command_requests      requested_by, target (device | group), definition, params,
                               status, approval_request_id, expires_at
commands.command_executions    per device: nonce, sequence, signed envelope hash,
                               dispatched_at, acked_at, completed_at, exit code, output ref
approvals.approval_policies / approval_requests / approval_decisions
audit.audit_events             append-only, partitioned monthly, hash-chained per tenant
monitoring.metric_samples      partitioned (time), see 3.4
ai.knowledge_documents / ai.embeddings (vector(1536|3072)) / ai.conversations
platform.outbox_messages       transactional outbox
files.stored_files             object key, sha256, size, content type, scan status
```

### 3.3 Inventory storage strategy

Hardware inventory is a deep, vendor-variable document. Store the full snapshot as
`jsonb` (last N snapshots kept), **promote** the fields used in filters/reports
(CPU, RAM, disk, OS build, model, serial, TPM, BitLocker status) to typed columns, and
keep installed software in a normalized table because it is the most-queried dimension
(licence compliance, vulnerability matching). Agents send **deltas** after the first
full inventory.

### 3.4 High-volume data (telemetry, audit, command output)

| Data | Volume driver | Strategy |
|------|---------------|----------|
| Heartbeats | every device every 1–5 min | Do **not** write each to PostgreSQL. Update `last_seen_at` in Redis; flush to Postgres in batches (e.g. every 60 s) or only on state change. |
| Metrics (CPU, mem, disk, services) | devices × metrics × interval | Native range partitioning by day (`pg_partman`) + rollups (5-min, hourly); raw retention 7–30 days. TimescaleDB is an option (D-4). |
| Audit events | user + system actions | Monthly partitions, append-only (no UPDATE/DELETE grants to runtime role), per-tenant hash chain for tamper evidence, retention per plan. |
| Command output / logs | variable, can be large | Store in object storage; DB keeps reference + truncated preview. |

RLS on partitioned tables works (policy is declared on the parent) – covered by the
existing migration test once it walks partitions.

### 3.5 pgvector

* `ai.embeddings(tenant_id, document_id, chunk_index, embedding vector, content, metadata)`
  with RLS like every other tenant table – **retrieval can never cross tenants**, even on
  a buggy query.
* HNSW index; because RLS filters after the ANN scan, use per-tenant filtering with
  `ef_search` tuning / iterative scans (pgvector ≥0.8) – validated in a spike (R-8).
* Global (non-tenant) product knowledge base lives in a separate platform table.

### 3.6 Backups, residency, encryption

* Encryption at rest: managed-disk/volume encryption (cloud KMS) + encrypted backups.
* Column-level encryption (envelope, per-tenant data key from KMS) for secrets only:
  SSO client secrets, SMTP passwords, stored credentials for deployments, MFA seeds.
* PITR backups, tested restore, per-tenant export (GDPR) and per-tenant delete.

---

## 4. Security architecture

### 4.1 Threat model summary (STRIDE, top items)

| Asset | Primary threats | Main controls |
|-------|-----------------|---------------|
| Agent command channel | Command injection, spoofed server, replay, admin takeover → code exec on whole fleet | Signed allow-listed commands, mTLS, nonce+sequence+expiry, approvals, per-device auth |
| Admin accounts | Credential stuffing, phishing, session theft | MFA (TOTP/WebAuthn), SSO, rate limiting, short tokens + rotating refresh, step-up auth for sensitive actions |
| Tenant data | Cross-tenant leak | Existing 4-layer isolation + RLS on every new table + tests |
| Update / package channel | Supply-chain compromise of agent or packages | Code-signed agent, signed update manifest, hash-pinned packages |
| AI assistant | Prompt injection via device data, data exfiltration to LLM provider | Tool allow-list, no direct execution, output validation, data minimization, tenant opt-in |
| Audit log | Tampering by insider | Append-only grants, hash chain, export to external sink |

### 4.2 Authentication (users)

* **Signing:** move from HS256 shared secret to **asymmetric (ES256/RS256) keys with `kid`
  and rotation**, keys held in a secret store/KMS. The Gateway and Worker only need public
  keys.
* **Tokens:** access token 5–15 min; **refresh token** (opaque, hashed in DB, rotating,
  reuse-detection revokes the family) stored in the BFF httpOnly cookie. Logout and admin
  "revoke sessions" become effective immediately.
* **MFA:** TOTP + WebAuthn/passkeys; recovery codes (hashed). Tenant policy: "MFA required
  for admins / all". **Step-up** (recent MFA, `amr`/`auth_time` claims) for: command
  execution, approvals, remote sessions, role changes, API key creation.
* **SSO:** OIDC (Entra ID, Google, Okta) and later SAML; per-tenant connection; optional
  SCIM provisioning.
* **Password policy:** existing policy + breached-password check (k-anonymity HIBP or
  offline list), Argon2id or the existing ASP.NET Identity hasher with raised iterations.
* **Brute-force:** per-IP and per-account rate limits (Redis), progressive delay,
  lockout notification.

### 4.3 Authorization – RBAC with scopes

```
Permission  = "<module>.<resource>.<action>"   e.g. devices.device.read,
                                               commands.execute.high_risk,
                                               approvals.request.decide
Role        = tenant-defined set of permissions (system templates: Owner, Admin,
              Helpdesk, Patch Manager, Auditor (read-only), Asset Manager)
Assignment  = (membership, role, scope) where scope = tenant | branch | device group
```

* Permissions are code constants; roles are data. Endpoints declare
  `[RequirePermission(Permissions.Devices.Read)]`.
* **Scope enforcement in queries**: a Helpdesk user scoped to Branch A sees only Branch A
  devices. Implemented as an application-level filter from the resolved permission set
  (cached in Redis per membership, invalidated on change) – **not** via RLS (RLS stays
  tenant-only to keep it simple and fast).
* Least privilege: no wildcard "execute anything" permission exists; high-risk commands
  require a dedicated permission *and* an approval policy.
* Separation of duties: requester cannot approve their own request (enforced server-side).
* Platform operators are a separate principal type with a separate audience; tenant
  support access is time-boxed, consented by the tenant, and audited.

### 4.4 Device identity and authentication

1. Admin creates an **enrollment token** (scoped to branch/group, short expiry, max uses,
   stored hashed). The MSI is installed with the token (`msiexec … ENROLLTOKEN=…`).
2. Agent generates a key pair **in the Windows TPM** (CNG Platform Crypto Provider; software
   key in DPAPI-protected store as fallback, flagged in device record).
3. Agent sends a CSR + token + hardware identifiers to `/agent/v1/enroll` over TLS
   (server certificate pinned to the Solvix CA bundle shipped in the MSI).
4. Gateway validates token, creates `Device` (status `pending` or `active` per tenant
   policy), issues a **device certificate** from a per-environment intermediate CA
   (subject = device id, tenant id in extension, 90-day validity, auto-renew at 2/3 life).
5. All subsequent traffic uses **mTLS**. Gateway maps cert → device → tenant and sets the
   tenant context exactly like the user middleware does. A revoked/retired device is
   rejected (revocation list cached in Redis).

Tenant id never comes from the agent payload – it comes from the certificate mapping.

### 4.5 Command signing and replay protection

Every command delivered to an agent is a signed envelope:

```
{ v, commandId, executionId, tenantId, deviceId, definitionKey, definitionVersion,
  parameters, issuedAt, notBefore, expiresAt, nonce, sequence, approvalRef }
signature = Ed25519/ECDSA-P256 over canonical JSON, key id = kid
```

* Signing key lives in KMS/HSM (or a dedicated signing service); the API never holds the
  private key in configuration. Agents ship with the public keys (rotatable via a signed
  key-set update).
* Agent verifies: signature, `deviceId` == self, `tenantId` == enrolled tenant, time window
  (with tolerated clock skew; agent also syncs time from server responses), **nonce not
  seen** (persisted nonce cache for the validity window) and **sequence strictly
  increasing** per device. Any failure → reject + security event.
* Agent executes only `definitionKey`s it implements; parameters are validated against
  the definition's schema **on the agent as well** as on the server.
* Results are returned over mTLS and bound to `executionId`.

### 4.6 Command execution model ("no unrestricted admin execution")

* Commands are **typed, versioned, allow-listed operations** (`software.install`,
  `service.restart`, `process.kill`, `registry.set`, `script.run`, …) – there is no
  "run arbitrary shell string" endpoint.
* `script.run` executes only **scripts from the tenant script library**: stored, versioned,
  hash-pinned, reviewed (approval policy on publish), optionally Authenticode-signed;
  parameters passed as typed arguments, never string-concatenated.
* Risk levels (low / medium / high / critical) drive: required permission, step-up MFA,
  approval policy, maximum fan-out (e.g. high-risk commands to >N devices require a
  second approver), and rate limits.
* Every step is audited: requested → approved/denied → signed → dispatched → acked →
  completed, with actor, IP, device, parameters hash and result.

### 4.7 Approval workflows

Generic `Approvals` module: policy (which actions, which scope, N approvers, which roles,
expiry), request, decision with step-up MFA, notifications, auto-expire. Approval is
re-checked at dispatch time (an approval that expired or a revoked approver invalidates
pending executions).

### 4.8 Session management & web security

* BFF cookie: `HttpOnly; Secure; SameSite=Lax; __Host-` prefix, idle and absolute timeouts.
* **CSRF:** state-changing BFF routes require a same-origin check (`Origin`/`Sec-Fetch-Site`)
  plus a double-submit token header. The backend API itself is bearer-only (not cookie) so
  it is not CSRF-exposed.
* Security headers in Next.js: strict CSP with nonces, HSTS, `frame-ancestors 'none'`,
  `Referrer-Policy`, `Permissions-Policy`.
* Output encoding: React escaping by default; ban `dangerouslySetInnerHTML` via lint rule;
  AI/markdown output rendered through a sanitizing renderer.

### 4.9 Secrets management

No secrets in source or images. Local dev: `.env` / user-secrets. Production: a secret
store (Azure Key Vault / AWS Secrets Manager / Vault – D-3) injected at runtime; signing
keys via KMS APIs, not as env vars. Secret scanning (gitleaks) in CI.

### 4.10 Secure file uploads

Pre-signed upload to object storage quarantine bucket → size/type allow-list and magic-byte
check → SHA-256 → malware scan (ClamAV container or cloud scanner) → move to clean bucket →
`StoredFile.status = clean`. Downloads by short-lived pre-signed URL; agents verify hash
(and Authenticode for executables) before running anything.

### 4.11 Rate limiting

ASP.NET Core rate limiter with Redis-backed partitions: per IP (anonymous), per user, per
tenant, per device (gateway), plus tighter limits on login, MFA, enrollment and command
creation. Returns 429 with `Retry-After`.

### 4.12 Audit and security event monitoring

* Audit: who, what, when, from where (IP, user agent, device), tenant, target, before/after
  (diff for config changes), correlation id. Written via outbox → `audit.audit_events`.
* Security events (failed logins, MFA failures, signature failures on agents, enrollment
  anomalies, cross-tenant access attempts which today return 403/404) feed alert rules and
  are exportable to SIEM (syslog/CEF, webhook, later Sentinel/Splunk).

### 4.13 Encryption in transit

TLS 1.2+ everywhere (1.3 preferred), HSTS, mTLS for agents, TLS to PostgreSQL and Redis in
production (`sslmode=verify-full`, Redis TLS + ACL user).

---

## 5. API architecture

| Surface | Base path | Auth | Consumers |
|---------|-----------|------|-----------|
| Management API | `/api/v1/...` | Bearer (user access token via BFF) | Web UI |
| Public/Integration API | `/api/v1/...` (same controllers) | API keys / OAuth client credentials, scoped permissions | Customer automation, PSA/ITSM |
| Agent API | `/agent/v1/...` (Gateway host) | mTLS device cert | Windows agent |
| Real-time (UI) | `/hubs/console` | Cookie→BFF→bearer | Browser |
| Real-time (agent) | `/agent/v1/channel` | mTLS | Agent push channel |
| Platform admin | `/platform/v1/...` | Platform operator tokens (separate audience) | Internal ops |

Conventions (most already in place): URL-segment versioning, kebab-case, RFC 9457 problem
details with stable `code`, FluentValidation, cursor pagination for large collections
(devices, events) and offset pagination for small admin lists, `?filter=` with a
**whitelisted field/operator grammar** (no dynamic SQL), `?sort=`, `If-Match`/ETag for
concurrency, `Idempotency-Key` header on POSTs that create commands/deployments, bulk
operations as asynchronous jobs (`202 Accepted` + job resource), OpenAPI per version with
generated TypeScript client for the frontend.

Deprecation policy: a version is supported ≥12 months after its successor ships; `Sunset`
and `Deprecation` headers.

---

## 6. Frontend architecture

* **Keep** Next.js App Router + BFF (token never in browser JS) – it is the right design.
* **UI kit:** adopt shadcn/ui (Radix primitives) as the component base (C-1), migrating the
  existing `components/ui/*` primitives one by one while preserving their public props.
  Add **TanStack Table** for the data grid (sorting, column visibility, virtualization,
  server-side pagination) and **TanStack Query** for client-side server state where
  interactivity requires it; React Hook Form + zod for forms (zod already present).
* **Design tokens:** CSS variables in `globals.css` (color, radius, spacing scale, typography,
  elevation, motion) consumed by Tailwind 4 `@theme`; light/dark via `next-themes` (exists).
  Density setting (comfortable / compact) for data-heavy screens.
* **Structure:** `app/(app)/<module>/…` routes; `features/<module>/` (components, hooks,
  api calls, schemas); `components/ui` (primitives only); `components/patterns`
  (DataTable, FilterBar, EmptyState, ErrorState, ConfirmDialog, DangerConfirm with typed
  confirmation, PageHeader, DetailDrawer, StatusBadge, KPI tile).
* **Permissions in UI:** session exposes effective permissions; UI hides/disables actions,
  but the server is always authoritative.
* **Real-time:** SignalR client via BFF-issued short-lived hub token; used for device
  online state, command progress, notifications.
* **Accessibility:** WCAG 2.2 AA target; axe checks in component and E2E tests; keyboard
  navigation for tables and dialogs.
* **i18n readiness:** string extraction from the start (next-intl), RTL-safe logical CSS
  properties (relevant if Arabic is a target market – D-9).

---

## 7. Windows agent architecture

```
Solvix.Agent (Windows Service, .NET 10 Worker, self-contained, single-file, x64/ARM64)
├─ Host: service lifetime, crash recovery, config (ProgramData, ACL'd to SYSTEM/Admins)
├─ Identity: TPM-backed key, device cert store, renewal
├─ Transport: HTTPS + mTLS, WebSocket/SignalR push, long-poll fallback, proxy support
│            (WinHTTP/PAC), exponential backoff with jitter, offline queue (SQLite)
├─ Scheduler: heartbeat, inventory (full weekly / delta daily), metrics, policy evaluation
├─ Collectors: WMI/CIM, registry, Win32 APIs, Windows Update Agent (WUA) API, event log
├─ Command engine: envelope verification (sig, nonce, sequence, expiry) → handler registry
│            (allow-listed handlers only) → parameter schema validation → execution with
│            timeout/cancellation → result upload
├─ Script runner: PowerShell in constrained, non-interactive host; hash-pinned scripts;
│            output size caps; runs as SYSTEM only when the definition requires it
├─ Updater: separate "Solvix.Agent.Updater" service; signed update manifest; MSI/Authenticode
│            verification; staged rollout rings; automatic rollback on failed health check
└─ User-session helper (later): per-user process for notifications, user consent prompts,
             remote-desktop capture (session 0 isolation requires it)
```

Design points:

* **Least privilege:** the service runs as LocalSystem only because inventory/patching
  require it; code paths that do not need it (network I/O, parsing) are isolated where
  feasible. No inbound listening ports.
* **Tamper resistance:** service ACLs, protected install directory, uninstall requires a
  server-issued uninstall code (optional per tenant), self-health watchdog.
* **Packaging:** WiX MSI, Authenticode-signed (EV certificate – D-7), silent install
  parameters for Intune/GPO/SCCM deployment.
* **Resource budget:** idle CPU <1%, memory target <80 MB, jittered schedules to avoid
  thundering herd.
* **Testability:** collectors and handlers behind interfaces; contract tests against the
  gateway; a Linux-runnable "simulated agent" for load tests (10k+ virtual devices).

---

## 8. Development roadmap

Each phase ends with: tests green, security review, docs updated, CHANGELOG entry.

| Phase | Theme | Main deliverables | Exit criteria |
|-------|-------|-------------------|---------------|
| **01** ✅ | Platform foundation | Tenancy, Identity, Organization, RLS, BFF shell | Done (see `PHASE-01-SUMMARY.md`) |
| **02** | Security foundation | Asymmetric JWT + key rotation, refresh tokens & revocation, MFA (TOTP + recovery), rate limiting, permission-based RBAC with scopes, audit module + outbox, CSRF hardening, security headers, CI pipeline (build, test, lint, gitleaks, dependency scan), fix R-1 | Pen-test checklist for auth passes; all endpoints permission-annotated (test enforces) |
| **03** | UI system | shadcn/ui migration, design tokens, DataTable/FilterBar/Empty/Error/Confirm patterns, users & roles screens, audit log viewer, Playwright E2E | Accessibility checks pass; visual regression baseline |
| **04** | Device plane | AgentGateway host, internal CA, enrollment tokens, mTLS, Windows agent MVP (service, enrollment, heartbeat, basic inventory), device list/detail UI, Hangfire, SignalR | 1,000 simulated agents sustained; isolation tests for device APIs |
| **05** | Inventory & ITAM | Full hardware/software inventory, deltas, software normalization, asset records, device groups (static + dynamic), reports v1 | Inventory accuracy validated on reference machines |
| **06** | Commands & approvals | Command definitions, signed envelopes, replay protection, approvals, script library, execution UI with live progress, command audit | Red-team tests: replay, tampered envelope, cross-tenant, expired approval all rejected |
| **07** | Software & patch | Package upload pipeline + scanning, deployment, WUA patching, rings, maintenance windows, compliance dashboards | Patch deployment to pilot ring with rollback |
| **08** | Monitoring & alerts | Metrics, partitioning/rollups, alert rules, notification channels, server-side notifications | 10k devices metrics ingest within SLO |
| **09** | Policy / configuration | Desired-state policies, compliance, BitLocker/firewall/USB | – |
| **10** | Remote access | Remote shell/file transfer, then remote desktop (relay), consent, recording | Security review of relay; session audit |
| **11** | AI assistant | RAG over tenant inventory/docs (pgvector), read-only Q&A first, then action proposals via Commands+Approvals | Prompt-injection test suite passes; tenant opt-in |
| **12** | Enterprise | SSO (OIDC/SAML), SCIM, public API keys, SIEM export, platform admin, tenant deletion/export, billing | – |
| **13+** | MDM & cross-platform | macOS/Linux agents, Apple MDM, Android Enterprise, Windows MDM (OMA-DM) | – |

Phase 02 deliberately precedes any endpoint work: the agent channel multiplies the impact
of every auth weakness.

---

## 9. Technical risks

| ID | Risk | Impact | Mitigation |
|----|------|--------|-----------|
| **R-1** | **RLS bypass is a session variable the runtime role can set itself** (`set_config('app.rls_bypass','on')`). Any future SQL injection or careless raw SQL could disable tenant isolation. | Critical | Phase 02: replace with a **separate database role** for platform scope (distinct connection string, `BYPASSRLS` or explicit platform policies), used only by the platform-scope code path; runtime role's policy ignores `app.rls_bypass`. Alternatively make `app.tenant_id` settable only through a `SECURITY DEFINER` function that validates a signed value. Add a test that the runtime role cannot read across tenants even after `set_config`. |
| R-2 | Agent channel compromise = fleet-wide code execution | Critical | §4.4–4.7; independent security review before Phase 06 ships. |
| R-3 | Agent scale: long-lived connections (10k–100k) per gateway node | High | Gateway as separate host, Redis backplane, connection limits, load test with simulated agents from Phase 04; consider Azure SignalR Service / sticky LB. |
| R-4 | Telemetry volume overwhelms PostgreSQL | High | §3.4 batching, partitioning, rollups, retention; measure early. |
| R-5 | Hangfire + RLS: jobs must always run tenant-scoped | High | Every job takes `tenantId` and runs through existing `ITenantJobRunner`; analyzer/test forbids jobs resolving `DbContext` outside a tenant scope; Hangfire storage in its own schema without RLS, holding no tenant business data beyond ids. |
| R-6 | In-process events lose messages on crash | Medium | Transactional outbox from Phase 02. |
| R-7 | AI: prompt injection through device data (e.g. hostname, event logs), data leaving tenant boundary to OpenAI | High | Tools are read-only or produce *proposals*; actions go through Commands+Approvals; strict JSON-schema tool outputs; tenant opt-in; data minimization/redaction; zero-retention API settings; per-tenant token budgets. |
| R-8 | pgvector + RLS recall/performance | Medium | Spike in Phase 11; partial indexes or per-tenant partitions for large tenants. |
| R-9 | Remote desktop is a large, specialized subsystem (codec, capture, relay, NAT traversal) | High | Evaluate building on WebRTC with self-hosted TURN vs. licensing an SDK (D-8); time-box. |
| R-10 | Third-party patch catalog and MDM certificates require external contracts | Medium | Product decisions D-6, D-10 early. |
| R-11 | Code-signing and update channel supply chain | Critical | EV cert in HSM, reproducible builds, signed manifests, staged rings. |
| R-12 | Benchmark parity scope creep (Endpoint Central is ~20 years of features) | High | Strict phase gates; MVP feature list per phase; "original UI, comparable function" only. |
| R-13 | Clock skew on endpoints breaks expiry checks | Low | Server time in every response; agent keeps offset; tolerant window. |

---

## 10. Testing strategy

| Level | Scope | Tooling | Gate |
|-------|-------|---------|------|
| Unit | Domain invariants, validators, envelope canonicalization/signing, permission evaluation, filter parser | xUnit, NSubstitute | PR |
| Architecture | Module dependency rules, every controller action has a permission attribute, every `ITenantScoped` table has RLS, no `IgnoreQueryFilters` outside platform scope | NetArchTest / reflection tests | PR |
| Integration | Real PostgreSQL + Redis (existing fixture), RLS, API isolation per new module, outbox, Hangfire jobs | WebApplicationFactory, Testcontainers (optional) | PR |
| Security | Cross-tenant matrix tests for every new endpoint (auto-generated from OpenAPI), replay/tamper tests for commands, auth abuse (brute force, token reuse), dependency & secret scanning, SAST (CodeQL), DAST (OWASP ZAP baseline) on staging | xUnit, CodeQL, ZAP, gitleaks | PR / nightly |
| Agent | Handler unit tests, envelope verification, collectors on Windows CI runners, MSI install/upgrade/uninstall tests on VMs | xUnit, GitHub Windows runners, Hyper-V/Azure VMs | PR / nightly |
| Contract | Agent ↔ Gateway message schemas versioned and tested both directions | Shared schema package + tests | PR |
| Frontend | Components & hooks (Vitest + Testing Library), a11y (axe), E2E critical journeys (Playwright), visual regression for design system | Vitest, Playwright | PR / nightly |
| Performance | Simulated agent fleet (10k → 50k), API p95 latency, ingest throughput | k6 + simulated agent | Per phase |
| AI | Golden Q&A set, prompt-injection corpus, tool-call validation, tenant leakage tests | Custom eval harness | Phase 11+ |

Coverage target is meaningful-path coverage on Domain/Application (≥80%), not a global
number.

---

## 11. Missing requirements and architectural conflicts

### 11.1 Conflicts

| ID | Conflict | Recommendation |
|----|----------|----------------|
| **C-1** | Spec requires **shadcn/ui**; Phase 01 uses hand-written primitives without Radix. | Adopt shadcn/ui in Phase 03, migrate primitives behind the same props. Documented reason: accessibility (focus management, ARIA) of Radix primitives is hard to match by hand. |
| **C-2** | Spec: "modular monolith" vs. the agent plane's very different scaling/threat profile. | Same solution and schema, but a separate **AgentGateway host** (§1.2). Still a monolith in code/release terms. |
| **C-3** | Spec: "MFA readiness" vs. "Secure authentication" for a product that can execute code on every customer device. | Treat MFA as **required for privileged roles** (tenant-configurable for others), not merely "ready". |
| **C-4** | "Signed and authorized commands" + "AI-powered management" – AI-generated commands cannot be signed as trusted. | AI may only select allow-listed command definitions with schema-validated parameters, always routed through Approvals. No free-form AI scripts are executable. |
| **C-5** | HS256 shared secret (Phase 01) vs. multiple hosts (Api, Gateway, Worker) needing to validate tokens. | Asymmetric keys with JWKS (Phase 02). |
| **C-6** | Redis listed for caching, but it will also be SignalR backplane, rate limiter and presence store – a single point of failure for availability. | Redis HA (Sentinel/managed) in production; keep the existing fail-open cache wrapper for caching only, but **rate limiting must fail closed for auth endpoints**. |
| **C-7** | Hangfire uses PostgreSQL storage – its tables are not tenant-scoped and would trip the "every table is RLS" rule. | Dedicated `hangfire` schema, explicitly whitelisted in the migration test, holding job metadata only. |
| **C-8** | R-1 (RLS bypass via session variable) conflicts with "never allow cross-tenant access". | Fix in Phase 02. |

### 11.2 Missing requirements (need product input)

1. **Deployment model:** SaaS only, or also **on-premises / private cloud**? (Endpoint
   Central's market expects on-prem.) Affects KMS, object storage, AI and licensing.
2. **Scale targets:** max devices per tenant, total devices, tenants at launch and at 3
   years; heartbeat interval; data retention per data type.
3. **SLA / RPO / RTO** and target cloud(s) and regions; data residency (EU, GCC/KSA?).
4. **Compliance targets:** SOC 2, ISO 27001, GDPR, HIPAA, local regulations – drive audit
   retention, encryption and logging requirements.
5. **Supported OS matrix:** Windows 10/11, Server 2016+? ARM64? When macOS/Linux?
6. **Licensing & billing model:** per device / per technician / per module; trial limits.
7. **MSP / multi-level tenancy:** do partners manage many customer tenants? This changes the
   tenancy model (parent/child tenants, delegated access) and must be decided before Phase 04.
8. **Remote desktop expectations:** unattended access, multi-monitor, file transfer, session
   recording, end-user consent rules.
9. **Patch scope:** Windows only, or third-party applications (requires a catalog source).
10. **AI:** which data may be sent to OpenAI; per-tenant opt-in/opt-out; Azure OpenAI for
    residency; whether customers bring their own key; cost allocation.
11. **Notifications:** e-mail provider, SMS, Teams/Slack webhooks.
12. **Offline/air-gapped networks:** distribution servers / relay agents for branches with
    poor connectivity (Endpoint Central has distribution servers).
13. **Data export & deletion SLAs** for tenants leaving.
14. **Localization:** languages, RTL.
15. **Support access:** may Solvix staff access tenant data, under which approval?

---

## 12. Decisions requested before implementation

| ID | Decision | Recommendation |
|----|----------|----------------|
| D-1 | Approve overall architecture (§1–§7) | – |
| D-2 | Phase 02 = Security foundation before any agent work | Yes |
| D-3 | Secret store / KMS provider (Azure Key Vault, AWS KMS, HashiCorp Vault) | Depends on target cloud (11.2 #3) |
| D-4 | Time-series storage: native partitioning vs. TimescaleDB | Native partitioning first |
| D-5 | MSP multi-level tenancy needed? | Decide before Phase 04 |
| D-6 | Third-party patch catalog source | Defer to Phase 07, decide by Phase 05 |
| D-7 | Procure EV code-signing certificate (lead time weeks) | Start in Phase 03 |
| D-8 | Remote desktop: build (WebRTC) vs. license SDK | Spike in Phase 08 |
| D-9 | Languages / RTL | Build i18n-ready from Phase 03 |
| D-10 | MDM (Apple/Android) in scope for v1? | Recommend post-v1 |
| D-11 | Adopt shadcn/ui + TanStack Table/Query (C-1) | Yes |

**Recommended next step:** approve D-1, D-2, D-11 and start **Phase 02 – Security
foundation**, beginning with R-1 (RLS bypass hardening) and permission-based RBAC.
