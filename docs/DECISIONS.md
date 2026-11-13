# Architecture Decisions, Assumptions, Risks and Open Items

**Status:** For review before Phase 04 · **Last updated:** 2026-11-12

This is the decision log for Solvix-EndPoint. Each ADR records the context, the decision,
the alternatives considered and the trade-offs. Product requirements agreed on 2026-11-12
are recorded as inputs (`PR-x`). Where this log challenges or refines one of them, the
challenge is stated explicitly.

Related documents: [ARCHITECTURE](ARCHITECTURE.md) · [DATABASE](DATABASE.md) ·
[SECURITY](SECURITY.md) · [DEVICE_PLATFORM](DEVICE_PLATFORM.md) ·
[INFRASTRUCTURE](INFRASTRUCTURE.md) · [API_DOCUMENTATION](API_DOCUMENTATION.md) ·
[DEVELOPMENT_ROADMAP](DEVELOPMENT_ROADMAP.md)

---

## 1. Agreed product requirements (inputs)

| ID | Requirement |
|----|-------------|
| PR-1 | SaaS-first; Docker Compose on a VPS; on-prem edition possible later but not built or maintained in V1; custom domains and HTTPS. |
| PR-2 | MSP partners manage multiple independent customer tenants; direct customers register independently; MSP admins switch between authorized customers; strict isolation; no MSP can access another MSP's customers; data model and authorization built now, rich MSP portal later. |
| PR-3 | 1,000 devices initially, 10,000 growth target, horizontal scaling possible; PostgreSQL primary, Redis for cache/real-time; background processing; efficient heartbeats. |
| PR-4 | Retention: operational logs 90 days, inventory history 12 months, audit ≥ 12 months, configurable. |
| PR-5 | GDPR-aware; prepare for SOC 2 Type II / ISO 27001 without claiming them; configurable hosting region. |
| PR-6 | V1 = Windows: inventory, online/offline, remote PowerShell (strictly authorized), restart/shutdown, basic software deployment, Windows Update visibility, basic security monitoring. |
| PR-7 | Third-party patching designed for, not built. |
| PR-8 | Remote desktop via an established solution (evaluate Apache Guacamole), replaceable provider. |
| PR-9 | Android/Apple MDM not in V1; architecture ready; never simulated through the Windows agent. |
| PR-10 | AI after the core platform; backend-only; sanitized data; no arbitrary execution; actions through tools, permissions and approvals. |
| PR-11 | Obtain a code-signing certificate now; signed binaries, installers and updates; keys in secure storage; CA and signing method configurable. |

---

## 2. Challenges to the agreed requirements

These are not refusals. Each one names a technical problem in a requirement as stated and
the refinement this architecture adopts. Please confirm or overrule them.

### CH-1 · Phase ordering: Phase 04 cannot start on the current baseline
The repository contains Phase 01 only. The device plane depends on items scheduled for
Phase 02: the RLS bypass fix (R-1), permission-based authorization, MFA, refresh-token
revocation, rate limiting, audit logging, the MSP model and CI. Building devices first
would mean building the most dangerous feature (code execution on customer machines) on
top of three hard-coded roles and an RLS escape hatch, then reworking it.
**Refinement:** Phase 02 = security, MSP tenancy and audit foundation. Phase 03 = UI
foundation (can overlap with 02). Phase 04 = device plane, as specified. See the roadmap.

### CH-2 · "Remote PowerShell" vs. "never expose unrestricted administrator command execution"
Ad-hoc PowerShell running as SYSTEM is, by definition, unrestricted *on that device*. No
technical control can make arbitrary script content "safe". What can be controlled is
**who** can run it, **where**, **when**, **with whose approval**, and **with what record**.
**Refinement (ADR-010):** ad-hoc PowerShell is a separate, highest-risk capability. It is
off by default per tenant, needs a dedicated permission, a fresh MFA, an immutable script
artifact per execution (not a live keystroke shell), configurable four-eyes approval
(on by default for MSP users and multi-device targets), fan-out limits and full transcript
audit. Day-to-day operations should use the reviewed **script library** instead.

### CH-3 · Apache Guacamole does not solve the hard parts of remote desktop
Guacamole is a good, Apache-2.0 HTML5 gateway, but it has three gaps:
1. **Reachability.** `guacd` must open a TCP connection to the device's RDP port. Managed
   endpoints sit behind customer NAT/firewalls, so we need a **reverse tunnel through
   our agent** in any case.
2. **Attended support.** RDP opens a *separate* session or takes over the console. It does
   not let a technician see the screen the user is looking at, which is the main helpdesk
   use case. Windows Home editions have no RDP host at all.
3. **Credentials.** RDP needs Windows credentials. We must not store them.
**Refinement (ADR-011):** V1 delivers **unattended RDP via guacd over an agent reverse
tunnel**, with technician-entered credentials that are never stored. Attended
screen-sharing is an open decision (OD-5) that needs a time-boxed spike: a VNC-style
console capture component (license review needed) or a commercial SDK. The
`IRemoteSessionProvider` abstraction makes either one pluggable.

### CH-4 · "Horizontal scaling" vs. a single Docker Compose VPS
Compose on one host cannot scale horizontally and is a single point of failure, which
matters for SOC 2 availability commitments. That is acceptable for 1,000 devices and, sized
correctly, for 10,000. **Refinement (ADR-002):** the application is built stateless and
multi-instance-safe from day one (Redis backplane, distributed locks, no in-memory
ownership). Scaling out is then an infrastructure change, not a rewrite. The V1
non-negotiables on a single VPS: **off-site encrypted backups with tested restores**,
external monitoring, and object storage that is not on the same disk.

### CH-5 · Hangfire is not a heartbeat processor
Queuing a Hangfire job per heartbeat would mean about 170 writes per second into
Hangfire's PostgreSQL storage at 10,000 devices. That is exactly the excessive database
write load PR-3 asks us to avoid. **Refinement (ADR-008):** heartbeats and telemetry use an
in-memory → Redis → batched-write pipeline. Hangfire is used only for scheduled, retriable
and long-running work: retention, partition maintenance, report generation, flushes and
deletion workflows.

### CH-6 · MSP access needs customer consent and visibility
"An MSP can manage multiple customer tenants" is safe only if the customer side can see
and control it. Otherwise any compromise of an MSP account becomes a silent compromise of
every customer. Attacks on MSP tooling are a well-documented ransomware vector.
**Refinement (ADR-003):** partner access is an explicit, auditable link that the customer
owner can see and revoke. Partner users must use MFA. Partner actions are written to the
customer's audit log, and a customer can require approval for partner high-risk actions.

### CH-7 · Custom domains must never become a tenant-selection mechanism
**Refinement (ADR-002, SECURITY §3):** a custom domain selects **branding and the login
page only**. The tenant is always taken from the signed token. Custom domains must be
verified (DNS TXT) before a TLS certificate is issued, which prevents certificate-issuance
abuse. Agents always talk to a platform-controlled hostname.

### CH-8 · Per-tenant retention vs. time partitioning
Time-partitioned tables are shared by all tenants, so dropping a partition is cheap.
Per-tenant *shorter* retention would require row deletes in every partition.
**Refinement (ADR-013):** V1 retention is configurable per data category at **deployment
level**. Per-tenant overrides may only **extend** retention, and they come after V1.

---

## 3. Architecture Decision Records

### ADR-001 · Modular monolith with four deployable hosts — **Accepted**
* **Decision:** One solution, one database, one release. The hosts are:
  `web` (Next.js + BFF), `api` (management plane), `gateway` (device plane), and `worker`
  (Hangfire + stream consumers). All hosts load the same module assemblies and each
  activates only the endpoints and handlers relevant to it.
* **Why:** The device gateway faces every managed endpoint on the internet, has a different
  authentication scheme and holds 10k long-lived connections. Isolating it in its own
  process limits blast radius and lets it be restarted, rate-limited and scaled separately.
  It costs one more container, not a microservice architecture.
* **Rejected:** a single host (the agent attack surface would share a process with admin
  APIs); microservices (unjustified at 10k devices).

### ADR-002 · Docker Compose on a single VPS for V1 — **Accepted with conditions (CH-4, CH-7)**
* Caddy is the reverse proxy: automatic HTTPS, on-demand TLS for verified custom domains,
  and WebSocket support.
* Only ports 80/443 are public. PostgreSQL, Redis and guacd sit on internal networks only.
* S3-compatible object storage is **external** to the VPS.
* Off-site, encrypted, continuous PostgreSQL backups (WAL archiving) with quarterly
  restore tests.
* All infrastructure dependencies sit behind abstractions (object storage, secrets,
  signing, e-mail). A future on-prem edition then only swaps providers, satisfying PR-1
  without building it.

### ADR-003 · Tenancy model: partners are tenants; two-level delegated access — **Proposed**
* `tenants.kind ∈ {customer, partner}`. A partner tenant holds the MSP's own staff,
  roles, settings and audit log. **It can never own devices.** An MSP's internal IT is a
  normal customer tenant linked to itself.
* `partner_customer_links` connect one partner to a customer. **At most one active
  managing partner per customer** (unique partial index), which makes "no MSP can see
  another MSP's customers" structural.
* `partner_access_grants` give a partner membership a role in one linked customer, or in
  all linked customers (explicit flag, audited).
* **A request always runs in exactly one tenant** (the customer). Partner access never
  widens RLS: an MSP user switching to customer C gets a token with `tid = C` plus
  `ptid` = the partner tenant. The middleware re-validates the link and the grant on
  every request (cached and invalidated on change).
* Only two levels. Resellers of MSPs and nested hierarchies are explicitly out of scope:
  they multiply the authorization paths for little V1 value.
* Cross-customer MSP dashboards (later) are built by fanning out per-tenant queries or
  from a dedicated partner read model. They never weaken RLS.
* **Rejected:** a recursive `parent_tenant_id` with RLS over a tenant set (policy
  complexity, slow, easy to get wrong); a separate `partners` table outside the tenant
  model (it would duplicate memberships, roles and audit).

### ADR-004 · Remove the RLS session bypass (fixes R-1) — **Proposed**
* **Problem:** Today every policy is `USING (app_rls_bypass() OR tenant_id = app_current_tenant_id())`,
  and the runtime role can run `set_config('app.rls_bypass','on')` itself. Any SQL
  injection or careless raw SQL therefore exposes **all** tenants.
* **Decision:**
  1. Policies become `tenant_id = app_current_tenant_id()` only.
  2. Cross-tenant platform operations (login lookup, tenant switch, link/grant resolution)
     use **narrow `SECURITY DEFINER` functions**. They are owned by a dedicated
     `solvix_platform` role, take explicit parameters, return minimal columns and are
     covered by tests.
  3. Registration first creates the tenant row (a platform table), then *sets the tenant
     context to the new tenant* and writes the tenant-owned rows under normal RLS.
  4. Maintenance jobs that truly need every tenant (partition drop, retention) use a
     separate `solvix_maintenance` connection string, are present only in the worker,
     and never handle HTTP input.
* **Residual risk (stated honestly):** `app.tenant_id` is still a session variable.
  Attacker-controlled SQL could set it to *one other* tenant. RLS protects against
  application bugs; SQL injection is prevented by parameterized EF Core queries, review,
  and the absence of dynamic SQL (filters use a whitelist grammar).

### ADR-005 · Permission-based RBAC with scopes — **Proposed**
* Permissions are code constants (`devices.read`, `commands.restart`,
  `remote.powershell.adhoc`, …). V1 roles are **system-defined** (Owner, Administrator,
  Technician, Helpdesk, Auditor, Viewer). Custom roles come later; the data model
  supports them now.
* An assignment = (membership or partner grant) × role × scope (`tenant | branch |
  device_group`).
* The existing `TenantRole` values migrate as Owner → Owner, Admin → Administrator,
  Member → Viewer, keeping today's behaviour backward compatible.

### ADR-006 · Device authentication: TPM-backed device key + short-lived device tokens — **Proposed (supersedes the mTLS design in the earlier proposal)**
* **Decision:** At enrollment the agent creates a non-exportable P-256 key (TPM via CNG,
  with a software CNG key as recorded fallback) and registers the public key. It then
  authenticates with challenge → signature → a **15-minute device access token**
  (ES256, `aud=agent`, bound to the key thumbprint).
* **Why not mTLS (the earlier proposal):** On a Compose/Caddy deployment, mTLS needs
  either TLS passthrough or proxy-terminated client certificates with header forwarding.
  It also needs a private CA, certificate renewal and CRL distribution, and many customer
  networks break it through TLS inspection. Key + token gives the same proof-of-possession
  identity, revocation within seconds (token issuance is refused and sockets are
  dropped), and works through any proxy.
* **Trade-off:** A stolen token is usable for up to 15 minutes. Mitigations: the key never
  leaves the TPM; the token lives in memory only; commands carry an independent server
  signature; device-originated data is treated as untrusted.
* mTLS can be added later as an optional hardening mode without changing the device model.

### ADR-007 · Agent transport: SignalR over WebSocket + HTTPS fallback; no certificate pinning — **Proposed**
* There is one outbound WebSocket per device (SignalR, MessagePack protocol) for
  heartbeats and command push. Bulk uploads (inventory, telemetry, command output) use
  HTTPS POST. Where WebSockets are blocked, agents fall back to long polling.
* No TLS pinning: pinning breaks on Let's Encrypt rotation and corporate TLS inspection.
  Integrity of what matters (commands, updates, key sets) is guaranteed by **application-
  level signatures** verified by the agent.

### ADR-008 · Heartbeat and telemetry pipeline — **Proposed** (details in DEVICE_PLATFORM §4)
* Presence lives in Redis. PostgreSQL receives only **state transitions** plus a batched
  `last_seen_at` flush every 5 minutes. Telemetry is written in batches through a bounded
  in-process channel. There are no per-heartbeat Hangfire jobs (CH-5).

### ADR-009 · Time-partitioned tables, application-managed — **Proposed**
* Native PostgreSQL range partitioning for telemetry, security events, presence
  transitions, inventory history and audit. A worker job creates partitions ahead of time
  and drops expired ones. This avoids a custom Postgres image just for `pg_partman`.
* Partitions live in a dedicated `partitions` schema **with no privileges for the runtime
  role**. Direct access to a partition bypasses the parent's RLS policy, so this closes
  that path. A migration test enforces it.

### ADR-010 · Remote command model — **Proposed** (details in SECURITY §6)
* Commands are typed, versioned and allow-listed. Every execution is a per-device signed
  envelope with nonce, sequence number and expiry.
* Risk tiers drive permission, MFA, approval and fan-out limits. Ad-hoc PowerShell is
  tier 3 and is handled as in CH-2.
* Signing goes through `ICommandSigner`. Production uses a **cloud KMS asymmetric key**
  (the key is non-exportable and every signature is logged independently by the KMS);
  development and a future on-prem edition use a file-backed key.

### ADR-011 · Remote desktop via guacd behind `IRemoteSessionProvider` — **Proposed (CH-3)**
* Components: guacd (internal network only) plus a **Guacamole-protocol tunnel
  implemented in our gateway** and `guacamole-common-js` in the UI. We deliberately do
  not run the Guacamole Java web application, so we avoid a second authentication system
  and its attack surface.
* If the spike shows the tunnel costs more than estimated, the fallback is
  guacamole-client with the encrypted-JSON authentication extension, still behind our
  authorization.
* An agent reverse tunnel carries RDP to `127.0.0.1:3389` on the endpoint.
* Sessions are approved, time-boxed and audited. Recording is optional per tenant and
  stored encrypted with its own retention.

### ADR-012 · Code signing and release integrity — **Proposed**
* The publicly trusted code-signing key is held by a **cloud HSM signing service or a
  hardware token**. CA/Browser Forum rules have required hardware-protected keys since
  2023, so a `.pfx` in CI is not an option.
* CI signs through short-lived federated credentials, gated by a protected release
  environment.
* The update manifest (version, SHA-256, size, minimum version, ring) is **separately
  signed with a Solvix release key in KMS**. The agent requires both: a valid manifest
  signature *and* a valid Authenticode signature from the expected publisher.
* The CA, signing service and key identifiers are configuration (PR-11).

### ADR-013 · Retention — **Proposed (CH-8)**
* Defaults: operational logs, security events and presence transitions 90 days; inventory
  history 12 months; audit 13 months (12 months minimum plus a buffer for the annual audit
  window); 5-minute metrics 30 days; hourly metrics 13 months; command output 90 days;
  remote-session recordings 30 days.
* Configured per deployment through `Solvix:Retention:*`, validated at startup.

### ADR-014 · MDM readiness without simulation — **Accepted (PR-9)**
* `devices.platform` and `devices.management_channel` (`agent | apple_mdm |
  android_enterprise | windows_mdm`) are modelled now.
* Capabilities are resolved per channel. The Windows agent never claims MDM semantics.
* Apple/Android protocol stacks will be separate modules later.

### ADR-015 · AI integration constraints — **Accepted (PR-10), implementation post-V1**
* A backend `AI` module calls OpenAI from the server only. The API key is in the secret
  store.
* Data passes a tenant-authorized tool layer, then a **field allow-list**, then a
  redaction pass for secrets and personal data before leaving the platform.
* The model can only call backend tools, which run with the *invoking user's*
  permissions. Mutating tools create **proposals** that go through the Commands and
  Approvals modules. There is no free-form script generation for execution.
* AI processing is opt-in per tenant.

### ADR-016 · Database schemas per module — **Proposed**
* New modules get their own PostgreSQL schemas (`devices`, `inventory`, `monitoring`,
  `patch`, `commands`, `audit`, `platform`, …).
* Phase 01 tables move to `tenancy`/`identity`/`org` in a single metadata-only migration
  (`ALTER TABLE … SET SCHEMA`) in Phase 02.
* Benefit: per-schema grants, for example making `audit` append-only for the runtime role.

---

## 4. Assumptions

| ID | Assumption | If wrong |
|----|-----------|----------|
| A-1 | Average tenant is small (10–500 devices); no single tenant exceeds ~5,000 devices in V1. | Revisit per-tenant query patterns and partitioning. |
| A-2 | Managed Windows versions: Windows 10 22H2, Windows 11, Server 2016+; x64 first, ARM64 after. | Agent test matrix changes. |
| A-3 | Endpoints have outbound HTTPS (443) to the platform; a proxy may be present. | Need a relay/distribution server (post-V1). |
| A-4 | One production region per deployment; the region is chosen at provisioning time and recorded in configuration and on each tenant. | Multi-region needs a data-residency routing layer. |
| A-5 | The VPS for production is ≥ 8 vCPU / 32 GB RAM / 400 GB NVMe at 10k devices (see INFRASTRUCTURE). | Capacity plan changes. |
| A-6 | MSP-provisioned customers are billed to the MSP; direct customers pay themselves. | Billing model only; no isolation impact. |
| A-7 | V1 UI is English; the code is i18n-ready. | – |

---

## 5. Risk register

| ID | Risk | Likelihood / Impact | Mitigation | Owner phase |
|----|------|---------------------|-----------|-------------|
| R-1 | RLS bypass session variable settable by runtime role | Exists / Critical | ADR-004 | 02 |
| R-2 | Command channel compromise ⇒ code execution on fleet | Low / Critical | ADR-006/010, KMS, approvals, independent review before GA | 06 |
| R-3 | MSP account compromise ⇒ many customers | Medium / Critical | MFA mandatory for partner users, customer visibility & revocation, approval for partner tier-2/3 actions, anomaly alerts | 02, 06 |
| R-4 | Single VPS outage / disk loss | Medium / High | Off-site WAL backups, restore runbook (RTO 4 h, RPO 5 min), external uptime monitoring, staging host | 04 |
| R-5 | Reconnect storm after gateway restart (10k sockets) | High / Medium | Jittered backoff, token reuse, connection-rate limiter, SignalR keep-alive tuning | 04 |
| R-6 | Partition access bypassing RLS | Medium / High | ADR-009 schema without grants + test | 04 |
| R-7 | Agent update channel compromise | Low / Critical | ADR-012 dual signatures, staged rings, rollback | 04–05 |
| R-8 | Remote desktop scope (attended support) under-delivers | High / Medium | CH-3 spike before committing UI promises | 09 |
| R-9 | Code-signing certificate lead time (organization validation) | High / Medium | Start now; dev builds signed with an internal test certificate | now |
| R-10 | Cloned VM images share agent identity | Medium / Medium | Hardware fingerprint change + concurrent-session detection → quarantine; golden-image guidance | 04 |
| R-11 | Telemetry growth fills disk | Medium / Medium | Retention jobs, rollups, disk alerts at 70/85% | 08 |
| R-12 | Personal data in endpoint data (usernames, event logs, recordings) | Certain / Medium | Data minimization switches, retention, DPA, export/erasure workflows | 02–10 |
| R-13 | Feature-parity scope creep against the benchmark | High / High | Phase gates; V1 scope = PR-6 only | all |

---

## 6. Open decisions (need an answer; recommended default in brackets)

| ID | Decision | Needed by | Recommendation |
|----|----------|-----------|----------------|
| OD-1 | KMS provider for command-signing and release keys (AWS KMS, Azure Key Vault, GCP KMS) | Phase 06 (dev uses file key) | Whichever cloud also hosts backups/object storage, to limit vendors |
| OD-2 | Code-signing route: CA cloud-signing service vs. hardware token; OV vs. EV | Now (lead time) | Cloud signing service from the CA (CI-compatible). Microsoft has said EV no longer gives immediate SmartScreen reputation, so verify current guidance before paying the EV premium |
| OD-3 | VPS provider and region; object-storage and backup provider | Phase 04 infra | EU region if EU customers are expected (simplifies GDPR) |
| OD-4 | Transactional e-mail provider (invitations, password reset, alerts) | Phase 02 | Any provider with an EU region and DPA |
| OD-5 | Attended remote support technology (CH-3) | Phase 09 spike | Time-boxed spike: VNC-style capture vs. commercial SDK |
| OD-6 | Customer control over MSP access: may a customer revoke unilaterally? Require approval of partner high-risk actions by default? | Phase 02 | Yes / yes for tier 3 |
| OD-7 | On tenant deletion, are its audit logs deleted or retained for a legal period? | Phase 10 | Retain 12 months in restricted platform archive, then delete; disclose in DPA |
| OD-8 | Product domain names (`app.`, `agents.`) | Phase 04 | – |
| OD-9 | Pricing/licensing enforcement (device quotas per plan) | Phase 04 (quota field only) | Soft quota with warning in V1 |
| OD-10 | Whether "all linked customers" partner grants are allowed | Phase 02 | Allow, require Partner Owner + MFA, audit, show to customers |
