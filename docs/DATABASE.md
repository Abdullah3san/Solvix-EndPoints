# Database Architecture

**Status:** Phase 01 implemented; Phase 02 and Phase 04 models proposed for review.
Decisions referenced as ADR-x are in [DECISIONS.md](DECISIONS.md).

## 1. Conventions

* PostgreSQL 16 (pgvector-capable image when AI work starts), EF Core 10 migrations,
  one `SolvixDbContext`, migrations applied by `Solvix.Migrator` as the schema owner.
* UUIDv7 primary keys; snake_case names (model convention); `xmin` optimistic concurrency
  on mutable aggregates (from Phase 02).
* **Schema per module** (ADR-016): `tenancy`, `identity`, `org`, `audit`, `platform`,
  `devices`, `inventory`, `monitoring`, `patch`, `software`, `commands`, `approvals`,
  `remote`, `files`, later `ai`. Phase 01 tables move in one metadata-only migration.
* **Tenant-owned table rules** (enforced by `MigrationTests`, extended to all schemas):
  1. `tenant_id uuid NOT NULL` with FK to `tenancy.tenants`.
  2. `ENABLE` + `FORCE ROW LEVEL SECURITY`, policy `tenant_isolation`:
     `tenant_id = app_current_tenant_id()` (no bypass after ADR-004).
  3. Indexes lead with `tenant_id`.
  4. Children reference parents through `(tenant_id, parent_id)` composite FKs to the
     parent's `(tenant_id, id)` alternate key, so cross-tenant references are impossible.
* **Platform tables** (no tenant): `tenancy.tenants`, `identity.users`, user-level
  credentials/sessions, global public catalogs (e.g. Windows Update metadata). Accessed
  only through owning services or `SECURITY DEFINER` functions.

### Database roles

| Role | Used by | Rights |
|------|---------|--------|
| `solvix_migrator` | Migrator | Schema owner. Subject to FORCE RLS. |
| `solvix_app` | api, gateway, worker | DML on parent tables; `NOBYPASSRLS`; **no** UPDATE/DELETE on `audit`; no access to the `partitions` schema |
| `solvix_platform` *(new)* | owns `SECURITY DEFINER` functions only (no login) | Narrow cross-tenant reads for login, tenant switch, partner access resolution |
| `solvix_maintenance` *(new)* | worker maintenance jobs only | Partition create/drop, retention deletes; never wired into HTTP hosts |

## 2. Tenancy and MSP model (Phase 02)

```
                       tenancy.tenants
                  id, kind{customer|partner}, name, slug, status,
                  region, device_quota, deletion_requested_at
                     ▲                         ▲
   partner_tenant_id │                         │ customer_tenant_id
            ┌────────┴─────────────────────────┴────────┐
            │       tenancy.partner_customer_links       │  visible to both sides (policy below)
            │ id, partner_tenant_id, customer_tenant_id, │
            │ status{pending|active|revoked}, origin     │
            │ {partner_provisioned|customer_invited},    │
            │ requires_approval_for_high_risk, created_at,│
            │ activated_at, revoked_at, revoked_by        │
            └────────────────────────────────────────────┘
   UNIQUE (customer_tenant_id) WHERE status IN ('pending','active')   ← one managing partner
   UNIQUE (partner_tenant_id, customer_tenant_id) WHERE status <> 'revoked'

identity.users (platform) ──< identity.tenant_memberships (RLS: tenant_id)
                                  id, tenant_id, user_id, status
                                     │
                     ┌───────────────┴─────────────────┐
     identity.role_assignments (RLS)            identity.partner_access_grants (RLS: tenant_id = partner)
     tenant_id, membership_id, role_key,        tenant_id(=partner), id, membership_id,
     scope_type{tenant|branch|device_group},    customer_tenant_id NULL = all linked customers,
     scope_id NULL                              role_key, scope_type, scope_id, expires_at,
                                                created_by
```

**Constraints and enforcement**

* `kind` is immutable after creation (domain + trigger).
* Link endpoints must be of the correct kind. A FK cannot express this, so it is
  enforced by a trigger plus the domain.
* `partner_customer_links` RLS policy: `app_current_tenant_id() IN (partner_tenant_id,
  customer_tenant_id)`. Both parties can see their link; nobody else can.
* Devices and every device-plane table reject `kind = partner` tenants (domain check in
  Devices + enrollment-token creation is refused).
* Partner access resolution at request time uses `identity.resolve_partner_access(user_id,
  partner_tenant_id, customer_tenant_id)`, a `SECURITY DEFINER` function that returns the
  role keys and scopes only if the user's partner membership is active, the link is
  active and a non-expired grant covers the customer. The result is cached in Redis for
  60 s, and the cache is invalidated on any change to membership, link or grant.

**Tenant switch list** = direct memberships ∪ the partner tenant(s) the user belongs to ∪
customers reachable through active grants.

**Lifecycle**

| Flow | Steps |
|------|-------|
| MSP provisions a customer | Partner Owner/Admin creates customer tenant → link `active`, origin `partner_provisioned` → customer gets an Owner invitation (optional; customers may remain MSP-operated) |
| Existing direct customer joins an MSP | Customer Owner issues a one-time link code (MFA) → Partner accepts → link `active` |
| Customer leaves MSP | Customer Owner revokes (MFA) → all partner grants for that customer stop working immediately (cache invalidation) |
| MSP drops customer | Partner revokes → same; customer tenant continues as direct customer |

## 3. Authorization tables (Phase 02)

* `identity.roles`: system roles seeded with `tenant_id NULL`, where the policy permits
  read of system rows. Custom roles later use `tenant_id`.
* `identity.role_permissions(role_id, permission)`; permission strings are code constants
  validated at startup.
* Migration of the Phase 01 `tenant_memberships.role` column: data is copied to
  `role_assignments`; the column is kept read-only until the UI no longer uses it, then
  dropped (backward compatibility for one release).

## 4. Identity and session tables (Phase 02)

| Table | Scope | Notes |
|-------|-------|-------|
| `identity.user_sessions` | platform (user) | refresh-token family, hashed current token, tenant + partner context, IP/UA, created/last used/expires/revoked |
| `identity.mfa_factors` | platform (user) | type{totp|webauthn}, encrypted secret / credential public key, created, last used |
| `identity.recovery_codes` | platform (user) | hashed |
| `identity.invitations` | tenant | hashed token, e-mail, role, expires |

Secrets (TOTP seeds, SSO client secrets, SMTP credentials) are encrypted by the
application using envelope encryption (data key protected by the secret store / KMS).

## 5. Audit and outbox (Phase 02)

```
audit.audit_events  PARTITION BY RANGE (occurred_at) monthly, partitions in `partitions` schema
  tenant_id, id, occurred_at, actor_type{user|partner_user|device|system|platform_operator},
  actor_user_id, actor_partner_tenant_id, action, target_type, target_id,
  outcome{success|denied|failed}, ip, user_agent, correlation_id, details jsonb,
  prev_hash, hash
```

* Append-only for `solvix_app` (INSERT, SELECT only).
* Per-tenant hash chain: `hash = SHA-256(prev_hash || canonical(event))`. The chain is
  verified by a scheduled job and on export.
* An action by a partner user inside customer C is written to **C's** log and mirrored
  as a summary row in the partner tenant's log.
* `platform.outbox_messages(id, occurred_at, type, payload jsonb, tenant_id, processed_at,
  attempts, error)` is written in the same transaction as the business change and
  dispatched by the worker.
* `platform.platform_audit_events`: events without a tenant (failed logins for unknown
  e-mails, registration, platform operator actions).

## 6. Device plane model (Phase 04)

All tables are tenant-owned unless stated otherwise.

```
devices.devices
  tenant_id, id, display_name, hostname, platform{windows|macos|linux|ios|android},
  management_channel{agent|apple_mdm|android_enterprise|windows_mdm},
  lifecycle_status{pending_approval|active|quarantined|retired},
  presence{online|offline|unknown}, presence_changed_at, last_seen_at (batched),
  branch_id → (tenant_id, id) org.branches, department_id → (tenant_id, id) org.departments,
  os_name, os_version, os_build, architecture, manufacturer, model, serial_number,
  smbios_uuid, agent_version, enrolled_at, enrolled_via_token_id, retired_at, row_version
  idx: (tenant_id, lifecycle_status), (tenant_id, presence), (tenant_id, hostname),
       (tenant_id, serial_number), (tenant_id, last_seen_at)

devices.device_credentials
  tenant_id, id, device_id, algorithm{ES256}, public_key (SPKI DER), thumbprint (UNIQUE),
  key_protection{tpm|software}, created_at, revoked_at, revocation_reason
  UNIQUE (tenant_id, device_id) WHERE revoked_at IS NULL

devices.enrollment_tokens
  tenant_id, id, name, token_prefix, token_hash (UNIQUE, SHA-256), branch_id, device_group_id,
  auto_approve, expires_at, max_uses, use_count, created_by, revoked_at

devices.device_groups            tenant_id, id, name, type{static|dynamic}, rule jsonb (whitelisted grammar)
devices.device_group_members     tenant_id, group_id, device_id (static groups; dynamic groups materialized by worker)

devices.presence_transitions     PARTITIONED daily; tenant_id, device_id, at, from, to, reason   (90 days)
```

* `smbios_uuid` and `serial_number` are **not unique**: they are known to be duplicated or
  blank on some OEM and VM images. They serve as re-enrollment and clone-detection
  hints only.
* A device is never hard-deleted while within retention. "Retire" revokes credentials;
  the deletion job removes it after the retention period.

### Inventory (Phase 04 basic, Phase 05 full)

```
inventory.hardware_current       tenant_id, device_id (PK), collected_at, hash, payload jsonb,
                                 promoted: cpu_model, cpu_cores, ram_mb, system_disk_gb,
                                 system_disk_free_gb, tpm_version, secure_boot, bitlocker_status
inventory.hardware_history       PARTITIONED monthly; one row per *change* (hash differs) (12 months)
inventory.software_titles        tenant_id, id, normalized_name, publisher       ← tenant-scoped
inventory.installed_software     tenant_id, device_id, title_id, version, install_date, scope{machine|user}, source
inventory.software_changes       PARTITIONED monthly; installed / removed / updated events (12 months)
```

The software title dictionary is **tenant-scoped on purpose**. A global dictionary
built from tenant data would leak the names of customers' in-house applications to
other tenants. A curated global catalog, if added later, is maintained by the platform
and never derived from tenant data.

### Monitoring and security (Phase 04–08)

```
monitoring.device_metrics_5m     PARTITIONED daily; tenant_id, device_id, bucket_start,
                                 cpu_avg, cpu_max, mem_used_pct, disk_free_min_pct, net_in, net_out (30 days)
monitoring.device_metrics_1h     PARTITIONED monthly; same columns rolled up (13 months)
monitoring.security_posture      tenant_id, device_id (PK), defender_enabled, av_signature_age_h,
                                 firewall_profiles jsonb, bitlocker, uac, local_admins_count,
                                 pending_reboot, os_supported, collected_at
monitoring.security_events       PARTITIONED daily; tenant_id, device_id, occurred_at, event_id, category,
                                 severity, summary jsonb (minimized) (90 days)
monitoring.alert_rules / alerts  (Phase 08)
```

### Patch (Phase 05, designed for third-party)

```
patch.update_catalog       PLATFORM (public metadata): source{windows_update|third_party},
                           external_id, revision, title, kb, classification, severity, released_at
patch.device_update_states tenant_id, device_id, catalog_id, state{missing|installed|failed|hidden|pending_reboot},
                           first_detected_at, last_detected_at, last_error
patch.scan_results         tenant_id, device_id, scanned_at, update_service{wu|wsus|wufb}, status
```

The `source` column plus server/agent `IPatchSource` providers allow a third-party
catalog later without schema redesign (PR-7).

### Commands, approvals, software, remote (Phase 06–09)

```
commands.command_definitions   PLATFORM: key, version, risk_tier, required_permission, parameter_schema jsonb,
                               requires_approval_default, max_targets_without_approval
commands.scripts / script_versions  tenant: name, version, content_hash, content (object storage ref), status{draft|approved|retired}
commands.command_requests      tenant: id, requested_by, via_partner_tenant_id, definition_key/version,
                               parameters jsonb, target{device|group}, target_count, risk_tier,
                               approval_request_id, status, idempotency_key, created_at, expires_at
commands.command_executions    tenant: id, request_id, device_id, sequence, nonce, envelope_hash, signing_key_id,
                               status{queued|signed|dispatched|acknowledged|running|succeeded|failed|expired|rejected},
                               timestamps, exit_code, output_ref, output_preview
approvals.approval_policies / approval_requests / approval_decisions
software.packages / package_versions (file ref, sha256, silent args, detection rule)
remote.sessions                tenant: id, device_id, requested_by, approved_by, provider, started/ended,
                               recording_ref, end_reason
files.stored_files             tenant: object key, sha256, size, content_type, scan_status, uploaded_by
```

`devices.device_command_sequence(tenant_id, device_id, last_sequence)` holds the
per-device monotonic sequence and is incremented with `UPDATE … RETURNING` in the same
transaction that creates the execution.

## 7. Partitioning and RLS

* Partitioned parents live in the module schema and carry the RLS policy. Partitions are
  created in the `partitions` schema, where `solvix_app` has **no** privileges, so a
  partition can only be reached through its parent and therefore through RLS (ADR-009).
* The worker's maintenance job (role `solvix_maintenance`) creates partitions 7 days or 2
  months ahead and drops partitions older than the configured retention.
* `MigrationTests` assert: every partitioned tenant table has a policy on the parent, and
  `solvix_app` cannot `SELECT` from any object in `partitions`.

## 8. Retention

| Category | Default | Mechanism |
|----------|---------|-----------|
| Operational logs (application logs, outside DB) | 90 days | log store retention |
| Presence transitions, security events, command output | 90 days | partition drop / object lifecycle |
| 5-minute metrics | 30 days | partition drop |
| Hourly metrics | 13 months | partition drop |
| Inventory history (hardware, software changes) | 12 months | partition drop |
| Audit events | 13 months (min 12, enforced) | partition drop |
| Remote-session recordings | 30 days | object lifecycle |
| Retired devices | 90 days after retirement | worker delete |
| Backups | 35 days | backup tool retention |

Configured under `Solvix:Retention:<Category>`; the options validator refuses audit < 12
months.

## 9. GDPR data operations

* **Tenant export:** asynchronous job that produces a JSON/CSV archive per module
  (registered `ITenantDataExporter`), stored encrypted in object storage and downloaded
  through a short-lived link. The request is audited.
* **Tenant deletion:** Owner request with MFA → status `pending_deletion`, tenant
  suspended (reversible for 30 days) → worker runs each module's `ITenantDataEraser` in
  dependency order → objects are deleted → backups age out within 35 days → the platform
  audit records completion. Audit handling follows OD-7.
* **User erasure:** `identity.users` personal data is replaced with a pseudonym;
  `actor_user_id` in audit is kept (integrity of the hash chain), while the display
  name is resolved from the pseudonymized user.
* **Minimization switches** per tenant: collect logged-on user names (on/off), collect
  user-scope software (on/off).

## 10. Capacity estimate (10,000 devices)

| Data | Rows | Approx. size |
|------|------|--------------|
| devices + credentials + current inventory | ~10k each | < 1 GB |
| installed_software | ~150 titles × 10k = 1.5M | ~0.5 GB |
| metrics_5m (30 days) | 2.9M/day → 87M | ~12 GB incl. indexes |
| metrics_1h (13 months) | 95M | ~13 GB |
| security_events (90 days, filtered set) | ~1–5M | ~2 GB |
| audit (13 months) | workload-dependent, ~10–30M | ~5–10 GB |

That comes to roughly 40–60 GB of database at full growth, which fits the sized VPS (see
INFRASTRUCTURE.md).
