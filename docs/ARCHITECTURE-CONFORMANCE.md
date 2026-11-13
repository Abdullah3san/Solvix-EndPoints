# Architecture Conformance Checklist

**Purpose:** a self-contained work list that any session or engineer building a phase
can follow, so the implementation matches the agreed architecture
([DECISIONS.md](DECISIONS.md), [SECURITY.md](SECURITY.md), [DATABASE.md](DATABASE.md),
[DEVICE_PLATFORM.md](DEVICE_PLATFORM.md)).

* **Part A** lists concrete gaps found in the in-progress Phase 02 branch
  `claude/funny-wozniak-t660n8` (first reviewed at `2191ab4`; findings re-checked at `74d49f0`, 2026-11-13).
* **Part B** is the gate every later phase must pass before it is called done.

Tick an item only when its **verification** passes in CI.

---

## Part A – Phase 02 alignment (branch `claude/funny-wozniak-t660n8`)

The branch already delivers permission-based endpoints, tenant-owned roles, server-side
sessions checked per request, rotating refresh tokens with reuse detection, TOTP MFA
with tenant policy, rate limiting and lockout, invitations, password reset, encrypted
secrets, an auth audit log and strong security tests. The items below are what remains
to meet the agreed architecture. They are ordered by priority.

### A1 · Remove the RLS bypass (R-1, ADR-004) — **Critical, do first**

**Finding:** `RowLevelSecurity.EnableTenantIsolation` still emits
`USING (app_rls_bypass() OR tenant_id = app_current_tenant_id())`. The runtime role
`solvix_app` can run `SELECT set_config('app.rls_bypass','on',false)` itself, so any SQL
injection or raw SQL mistake reads **every tenant**. Phase 02 added 9 more tables under
this policy (`roles`, `role_permissions`, `membership_roles`, `user_sessions`,
`refresh_tokens`, `user_invitations`, `user_invitation_roles`,
`tenant_security_policies`, `auth_audit_events`). `BeginPlatformScope()` is now called in
27 places (re-counted 2026-11-13 at `74d49f0`): `AuthService` (6), `AccountService` (6), `MfaService` (4),
`InvitationService` (2), `SessionService` (2), plus Tenancy registration.

**Tasks**
- [ ] Inventory every `BeginPlatformScope()` call and classify it:
  - **(a) Tenant becomes known early.** Examples: refresh (session → tenant), tenant
    switch after the membership check, MFA for a session, accepting an invitation by
    token. Replace the bypass by looking up the tenant id first through a narrow function,
    then calling `ITenantContextMutator.SetTenant(...)` and continuing under normal RLS.
  - **(b) Lookup by a user-level key across tenants.** Examples: login by e-mail →
    memberships; password-reset/verification token → user; "my tenants" list. Implement
    as a `SECURITY DEFINER` SQL function owned by a non-login role `solvix_platform`,
    with explicit parameters and minimal return columns.
  - **(c) Platform tables only** (`users`, `tenants`). These need no bypass because they
    are not RLS-protected. Remove the scope.
- [ ] Create role `solvix_platform` (NOLOGIN) in `docker/postgres/init/01-roles.sh` and
  `scripts/create-local-db.sh`; grant `EXECUTE` on the functions to `solvix_app` only.
- [ ] Each function must `SET search_path = pg_catalog, public`, use no dynamic SQL, and
  `REVOKE ALL … FROM PUBLIC`.
- [ ] New migration: recreate every `tenant_isolation` policy without `app_rls_bypass()`,
  drop `app_rls_bypass()`, and remove `app.rls_bypass` from
  `TenantSessionConnectionInterceptor`.
- [ ] Migrations that need to see all rows (e.g. the backfill in
  `20261112102112_IdentityAccessManagement` that runs `set_config('app.rls_bypass','on',true)`)
  must instead run as the migrator under a temporary, migration-only policy, or with
  `SET LOCAL row_security = off` while connected as a role with `BYPASSRLS`. Decide one
  approach and document it.
- [ ] `ITenantContext.IsPlatformScope` either disappears or is renamed and documented as
  "no tenant; platform tables only".
- [ ] Future maintenance jobs (retention, partitions) get a separate
  `solvix_maintenance` connection string that only the worker host loads.

**Verification**
- [ ] Integration test: as `solvix_app`, run `set_config('app.rls_bypass','on',false)`
  and then `SELECT` from every tenant table with no tenant set ⇒ **0 rows**.
- [ ] Migration test: no policy definition contains `rls_bypass`
  (query `pg_policies.qual` / `with_check`).
- [ ] Migration test: every `SECURITY DEFINER` function in the database is on an
  allow-list, has a fixed `search_path` and is not executable by `PUBLIC`.
- [ ] All existing auth, MFA, invitation and session tests still pass.

### A2 · MSP tenancy (ADR-003, DATABASE §2) — **High; changes tokens, so do it in Phase 02**

**Finding:** there is no partner concept anywhere (`partner`, `ptid`, tenant kind).

**Tasks**
- [ ] `tenants.kind` (`customer` | `partner`), immutable; registration creates
  `customer` by default; a separate partner sign-up or platform action creates `partner`.
- [ ] `partner_customer_links` with the unique partial indexes from DATABASE §2 and an
  RLS policy `app_current_tenant_id() IN (partner_tenant_id, customer_tenant_id)`.
  Kind checks are enforced by a trigger.
- [ ] `partner_access_grants` (owned by the partner tenant): membership, customer or
  "all linked customers", role(s), optional expiry.
- [ ] `SECURITY DEFINER` function `resolve_partner_access(user_id, partner_tenant_id,
  customer_tenant_id)` returning role ids only when the membership, link and grant are
  all active.
- [ ] Token claims: `tid` = customer, `ptid` = partner, and the session row records the
  partner context. Switching to a customer requires MFA verified in the session.
- [ ] `AccessResolver` handles both paths and invalidates its cache on link, grant or
  membership changes (`AccessCacheInvalidations`).
- [ ] Revoking a link revokes the sessions bound to that customer through that partner.
- [ ] Endpoints:
  - partner side: list/create customers, revoke a link, manage grants;
  - customer side: view the managing partner, issue a one-time link code (MFA), revoke.
- [ ] `GET /auth/tenants` returns direct memberships, partner tenants and delegated
  customers, each with an `accessPath` field.
- [ ] Audit: an action by a partner user in a customer tenant is written to the
  customer's audit log with `actor_partner_tenant_id`, and mirrored in the partner's log.
- [ ] Guard: no device-plane entity may be created in a `partner` tenant. Add the check
  now as a reusable domain rule so Phase 04 uses it.

**Verification (each an integration test)**
- [ ] A partner P1 user cannot switch to a customer of P2 (403) and cannot read its data
  by id (404).
- [ ] Without a grant, a partner user cannot switch to a linked customer.
- [ ] After a link is revoked, the next request with a still-valid access token fails.
- [ ] A second active managing partner for one customer is rejected (unique index).
- [ ] A customer Owner sees partner actions in the customer audit log.
- [ ] A partner user without MFA cannot switch into a customer.

### A3 · Asymmetric JWT signing (SECURITY §3) — Medium
**Finding:** `JwtTokenService` and `Program.cs` use `HmacSha256` with a shared
`SigningKey`.
- [ ] ES256 signing keys with `kid`, loaded from file/secret store. Keep the current
  key plus the previous one so rotation needs no downtime. Publish a JWKS (internal) or
  provide the public keys to the future gateway and worker.
- [ ] Validation pins `ValidAlgorithms = [EcdsaSha256]`.
- [ ] Test: a token signed with HS256 using the old secret is rejected; a token with an
  unknown `kid` is rejected.

### A4 · Scoped role assignments (ADR-005) — Medium
**Finding:** `membership_roles` has no scope.
- [ ] Add `scope_type` (`tenant` | `branch` | `device_group`) and `scope_id` to
  `membership_roles` (default `tenant` for existing rows) and to partner grants.
- [ ] `AccessResolution` exposes permissions per scope, and a helper builds the query
  filter. Branch scope can be tested now with branches and departments.
- [ ] Test: a user with Viewer scoped to branch A cannot read branch B (404).

### A5 · General audit module (DATABASE §5) — Medium
**Finding:** auditing is the Identity-only `auth_audit_events`, which is mutable for the
runtime role and has no tamper evidence or reliable delivery.
- [ ] `audit.audit_events` partitioned monthly, append-only grants (INSERT/SELECT) for
  `solvix_app`, per-tenant hash chain, and `actor_partner_tenant_id`.
- [ ] Transactional outbox (`platform.outbox_messages`) plus a dispatcher. Modules raise
  audit events through a SharedKernel contract instead of writing tables directly.
- [ ] Migrate or keep `auth_audit_events` as a view; do not duplicate business logic.
- [ ] Tests: UPDATE/DELETE on `audit_events` as `solvix_app` fails; a tampered row is
  detected by chain verification.

### A6 · Command permission granularity (SECURITY §6) — Low now, required by Phase 06
**Finding:** `Permissions.Devices.ExecuteCommand` and `RemoteAccess` are single,
coarse keys.
- [ ] Replace them (before any role data depends on them) with `commands.read`,
  `commands.operate`, `scripts.execute`, `scripts.manage`, `remote.powershell.adhoc`,
  `remote.desktop`, `remote.recordings.read`, `approvals.decide`, `software.deploy`,
  `devices.enroll`, `devices.retire`.
- [ ] Ensure `remote.powershell.adhoc` is in **no** system role.

### A7 · Schema per module (ADR-016) — Low
- [ ] One metadata-only migration moving tables to `tenancy`, `identity`, `org`
  (`ALTER TABLE … SET SCHEMA`), with EF `ToTable(name, schema)` updated. Keep it in its
  own commit; do it before Phase 04 adds many tables.

---

## Part B – Gates for every later phase

Each phase must satisfy **all general gates** plus its phase-specific gates.

### B0 · General gates (every phase)
- [ ] Every new tenant-owned table: `tenant_id` + FK, FORCE RLS policy **without
  bypass**, indexes leading with `tenant_id`, composite tenant-safe FKs for children
  (`MigrationTests` extended).
- [ ] Every new controller action has a permission or explicit `[AllowAnonymous]`
  (architecture test).
- [ ] Cross-tenant **and cross-partner** tests for every new endpoint (foreign ids ⇒
  404).
- [ ] No module references another module's internals (architecture test).
- [ ] Background work runs through `ITenantJobRunner`, grouped by tenant.
- [ ] Sensitive actions are audited through the audit module; secrets and tokens are
  never logged.
- [ ] No secrets in the repository; gitleaks clean.
- [ ] Docs updated (relevant `docs/*.md`, API_DOCUMENTATION, CHANGELOG).

### B1 · Phase 03 – UI
- [ ] shadcn/ui primitives keep their existing props; no `dangerouslySetInnerHTML`
  (lint rule).
- [ ] The tenant switcher visibly indicates partner-delegated context.
- [ ] Destructive actions use the typed-confirmation dialog; MFA step-up prompts are
  handled.
- [ ] Playwright + axe pass for login, MFA, switching tenant, and partner grant flows.

### B2 · Phase 04 – Device plane (DEVICE_PLATFORM §8 is the full list)
- [ ] Separate `gateway` and `worker` hosts; the gateway accepts no user tokens and the
  api accepts no device tokens (`aud` checks + tests).
- [ ] Enrollment tokens are hashed, expiring and scoped, and refused in partner tenants.
- [ ] Device key is in the TPM (software fallback flagged); 15-minute device tokens;
  retirement disconnects within 30 s.
- [ ] Presence in Redis; DB writes only for transitions plus a 5-minute batch; no
  per-heartbeat Hangfire jobs.
- [ ] Partitions in the `partitions` schema with no `solvix_app` grants (test).
- [ ] 10k simulated-agent load test meets the targets.

### B3 · Phase 05 – Inventory and updates
- [ ] Software titles are tenant-scoped (no global dictionary built from tenant data).
- [ ] Inventory history only on change; retention jobs drop partitions after 12 months.
- [ ] The agent updater rejects an unsigned or tampered manifest or binary (test).

### B4 · Phase 06 – Commands
- [ ] No endpoint accepts free-form command strings; definitions are allow-listed and
  their parameters are schema-validated on server **and** agent.
- [ ] Envelope signed through `ICommandSigner`; no private key in configuration for
  production.
- [ ] Agent rejects replayed, expired, tampered or wrong-device envelopes, and
  out-of-order sequences (tests).
- [ ] Re-authorization at dispatch; requester ≠ approver; step-up MFA for tier 2/3.
- [ ] Tier-3 PowerShell: feature off by default, explicit permission, single device,
  approval default for partner users, immutable script stored, output capped, AMSI not
  bypassed.

### B5 · Phase 07 – Software deployment
- [ ] Upload pipeline: type allow-list, size limit, SHA-256, malware scan; only `clean`
  files are deployable.
- [ ] Install arguments are fixed per package version; the agent verifies the hash from
  the signed envelope.

### B6 · Phase 08 – Monitoring and alerts
- [ ] Batched telemetry writes; rollups; retention per `Solvix:Retention`.
- [ ] Alerts are deduplicated with a cooldown; notifications go through the outbox.

### B7 · Phase 09 – Remote desktop
- [ ] guacd only on an internal network; the agent reverse tunnel targets loopback only.
- [ ] Session tickets are single-use, short-lived and bound to user and session;
  Windows credentials are never stored or logged.
- [ ] Session audit, max duration, admin termination, encrypted recordings with
  retention.

### B8 · Phase 10 – GDPR and production readiness
- [ ] Tenant export, deletion workflow (30-day grace), user pseudonymization (audit chain
  stays valid).
- [ ] Backup restore drill recorded; INFRASTRUCTURE §11 checklist complete.
- [ ] Documentation and marketing make no certification claims.
