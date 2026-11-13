# Phase 09 – Enterprise patch and configuration management

## Starting point

The repository contained only Phase 01 (tenants, identity, company profile,
branches, departments). Phases 02–08 are not in this repository, so Phase 09 also
delivers the minimum it depends on: a **device registry** (enrolment, device
groups, placement in branch/department), an **endpoint agent** with its own
authentication, and an **append-only audit trail**. Everything follows the
Phase 01 conventions (modular monolith, tenant isolation in EF Core *and*
PostgreSQL RLS, RFC 9457 errors, versioned API).

## Requirements

### Patch management

| # | Requirement | Implementation |
|---|-------------|----------------|
| 1 | Identify missing Windows updates | Agent searches the Windows Update Agent (`IUpdateSearcher.Search("IsInstalled=0 … or IsInstalled=1 …")`) and uploads the result; per-device `Missing` state; `GET /patches?onlyNeeded=true` |
| 2 | Collect patch inventory | Tenant patch catalogue (update id + revision, KBs, CVEs, classification by WUA category GUID, MSRC severity, reboot behaviour), per-device states, device counts per patch |
| 3 | Detect update status | `device_update_summaries`: last scan/result/HRESULT, `wuauserv` state, AU notification level, WSUS vs Windows Update, last install, restart required + reasons (CBS, WU, pending file renames, `ISystemInformation`) – `GET /patches/devices/{id}` |
| 4 | Approve patches | Approve/decline/reset (bulk), notes, audit; auto-approval rules by classification/severity (optionally applied to existing patches); patches in active deployments cannot lose approval |
| 5 | Schedule deployments | Draft → **explicit authorisation** (`/schedule`) → starts at `scheduledStartUtc`; deadline; maintenance window; targets by all devices/branch/department/group/device with exclusions |
| 6 | Deploy approved patches | Only approved patches; agent installs exactly the approved update revision through `IUpdateDownloader`/`IUpdateInstaller`, one update at a time |
| 7 | Track deployment progress | Per-device jobs (stage, %, attempt, HRESULT), lease renewed by progress, progress roll-up per deployment, `GET /patch-deployments/{id}/devices` |
| 8 | Detect failed updates | WUA history failures and deployment results → `Failed` state with HRESULT and failure count; `GET /patches/failures` flags repeated failures |
| 9 | Compliance reports | Compliant / PendingRestart / NonCompliant / Unknown (stale scan) against **approved** patches; breakdown by branch, department, device group; top missing patches; CSV export |
| 10 | Restart requirements | Restart requests created when an install needs it (respecting `Never` / `WithinMaintenanceWindow` / `ForceAtDeadline`), manual restarts, user warning via `shutdown.exe /r /t <grace> /c …`, completion detected from the new boot time, post-restart rescan **verifies** the update survived the restart |

### Configuration management

| # | Area | Setting type | Windows interface used by the agent |
|---|------|--------------|-------------------------------------|
| 1 | Windows services | `Service` (startup type incl. delayed, running/stopped) | SCM: `ChangeServiceConfig`/`ChangeServiceConfig2`, `ServiceController` |
| 2 | Registry | `Registry` (HKLM, all value types, present/absent) | `Microsoft.Win32.Registry` (64-bit view) |
| 3 | Startup applications | `StartupApp` (HKLM Run/RunOnce: present, disabled, absent) | Registry incl. Explorer `StartupApproved` (what Task Manager uses) |
| 4 | Windows Firewall | `FirewallProfile`, `FirewallRule` (grouped “Solvix”) | `INetFwPolicy2` / `INetFwRule` COM API |
| 5 | Power settings | `Power` (sleep, display, hibernate, lid action; AC/DC) | Power Management API (`PowerRead/WriteAC/DCValueIndex`, `PowerSetActiveScheme`) |
| 6 | Screen lock | `ScreenLock` (machine inactivity limit, sign-in on wake, require CTRL+ALT+DEL) | `Policies\System` registry, `CONSOLELOCK` power setting |
| 7 | USB devices | `UsbStorage` (removable disks, WPD, CD/DVD, all: full/read-only/none) | “Removable Storage Access” ADMX policy keys |
| 8 | Approved security configurations | Built-in reviewed baselines (workstation security, power saving, kiosk) + risk-based approval workflow for every version | – |

| Cross-cutting requirement | Implementation |
|---------------------------|----------------|
| Assignment by device group, department, branch | `configuration_assignments` (+ all devices, single device, exclusions); departments include sub-departments; the same targeting engine drives patch deployments |
| Configuration versioning | Immutable numbered versions (Draft → PendingApproval → Approved/Rejected → Published → Superseded), content hash, change notes, restore-as-draft, fast rollback by re-publishing a superseded version |
| Drift detection | Agent re-evaluates every 15 min and on change; Monitor mode reports `Drifted`, Enforce mode remediates and reports `Remediated`; history in `configuration_drift_events`, compliance per policy/device |
| Windows-supported interfaces | See the table above and the WUA usage in patch management; restarts through `shutdown.exe` |
| Never apply destructive configuration without authorisation | See *Safety model* |
| Pilot deployments | Pilot device group or deterministic percentage; success threshold gate; manual promotion or auto-promotion after a soak; failed pilot halts, only an Owner can override (with a recorded reason) |
| Maintenance windows | Weekly (IANA time zone, DST-correct, may cross midnight) or one-off; gate patch installation, restarts and enforced configuration changes (evaluation still runs) |
| Failure handling | Retries with back-off up to `maxAttempts`; aborted runs (window closing, service stop) re-queued without counting; broad-phase failure threshold halts the rollout; deadline times out undispatched devices |
| Audit history | `audit_entries` written in the same transaction as the change; actor (user / agent / system), correlation id, JSON details; **append-only** enforced by database triggers; `GET /audit` (admins) |
| Conflicts, interrupted deployments, rollback tested | See *Verification* |

## Safety model (“never apply destructive configurations without authorisation”)

1. **Risk is computed, never supplied.** `SettingRiskClassifier` (shared by server
   and agent) classifies each setting as `Low`, `Elevated`, `Destructive` or
   `Blocked`, e.g. disabling Defender/firewall services or turning a firewall
   profile off is Destructive; disabling `RpcSs`, `Winmgmt` or the Solvix agent,
   or writing `SAM`/`SECURITY`/BCD keys, is Blocked and can never be saved.
2. **Approval by risk.** Low: any admin. Elevated: a second admin (four-eyes).
   Destructive: an **Owner** other than the author who ticks an explicit
   acknowledgement; publishing it and enforcing it on more devices are also
   Owner-only. The approved risk is stored as `AuthorizedRisk`.
3. **The agent re-checks.** Before changing anything the agent re-classifies the
   setting, verifies its hash, and refuses (`Refused`) anything Blocked, riskier
   than `AuthorizedRisk`, or above a local ceiling (`Solvix:Agent:MaxAllowedRisk`).
4. **Monitor first.** Assignments can run in Monitor mode; Enforce mode changes
   devices only inside the assignment’s maintenance window.
5. **Reversible by default.** Before the first change to a key the agent stores a
   snapshot of the original state; removing an assignment (RemovalBehavior =
   Revert), archiving a policy or changing a version restores it. Every provider
   supports rollback; a missing snapshot is reported as `RevertUnsupported`.
6. **Patching is equally gated.** Only approved patches, explicit scheduling;
   Owner sign-off for forced restarts, upgrades/feature packs/service packs/drivers
   and all-device rollouts without a pilot.
7. **Conflicts never guess.** Two policies setting the same key differently with
   the same priority and target specificity are reported and **not applied** until
   resolved; otherwise lower priority, then the more specific target wins.

## Architecture additions

```
src/shared/Solvix.Endpoint.Contracts      setting model, risk classifier, agent wire DTOs (server + agent)
src/backend/Modules/
  Solvix.Modules.Audit                    append-only audit trail (IAuditTrail), GET /audit
  Solvix.Modules.Devices                  devices, groups, enrolment tokens, Device auth scheme, heartbeat
  Solvix.Modules.EndpointManagement       targeting, maintenance windows, patches, deployments,
                                          orchestrator + background worker, restarts, configuration
src/agent/
  Solvix.Agent.Core                       API client, configuration evaluator, patch job runner, runtime loop
  Solvix.Agent.Windows                    Windows service + WUA/SCM/registry/firewall/power providers
deploy/windows/                           Install-/Uninstall-SolvixAgent.ps1
```

* **Agent authentication.** Devices enrol with a one-time token
  (`sxe_{id}_{secret}`) and receive a per-device credential
  (`sxd_{id}_{secret}`, 256-bit, SHA-256 stored, DPAPI-protected on the device).
  A policy scheme routes `Authorization: Device …` to the device handler, which
  issues a principal with the device’s `tid` and `act=agent`, so tenant resolution,
  query filters and RLS apply to agents exactly as to people. User policies reject
  agents and the `Agent` policy rejects people. Retiring a device revokes it at once.
* **Agent protocol.** Heartbeat → work (patch jobs, restarts, configuration
  revision, rescan request). Configuration is fetched with `If-None-Match`
  (304 when unchanged). Patch jobs carry an attempt number and a lease; progress
  renews the lease (also periodically during a long install); stale attempts get 409.
* **Interrupted deployments.** Server: an expired lease means “interrupted” →
  retry with back-off or fail. Agent: a write-ahead journal is written before
  installing; on the next cycle an existing journal is resolved by asking WUA
  what actually installed (or by delivering a result that could not be sent).
* **Scheduler.** `EndpointManagementWorker` ticks `IDeploymentOrchestrator` per
  active tenant inside a tenant-bound scope (30 s by default); state rows use
  `xmin` optimistic concurrency so several API replicas are safe.
* **Database.** 20 new tenant tables, all with composite `(tenant_id, id)`
  foreign keys and FORCE RLS (`EndpointManagementSecurity` migration);
  `audit_entries` rejects UPDATE/DELETE/TRUNCATE by trigger, also for the owner.
* **UI.** Endpoints section: Devices (enrolment tokens, groups, retire), Patches
  (compliance, approvals, failures, CSV), Deployments (create, authorise, promote,
  pause/resume/cancel, per-device progress), Maintenance windows, Configuration
  (policies, conflicts, drift, baselines; policy page with version workflow, risk
  explanations and assignments), Settings › Audit log.

## Verification

Run in this environment against PostgreSQL 16 and Redis 7.

| Suite | Result |
|-------|--------|
| `Solvix.UnitTests` | **134 / 134 passed** (44 Phase 01 + 90 new) |
| `Solvix.IntegrationTests` (real PostgreSQL, two-role RLS layout) | **60 / 60 passed** (31 Phase 01 + 29 new) |
| Frontend `tsc`, `eslint`, `vitest` | clean, clean, **16 / 16 passed**; `next build` succeeds |
| Docker | API and migrator images build; migrator applies all 4 migrations to an empty database; API container reports Healthy |
| Browser walk-through | Seeded API + production Next.js build, every new page rendered with real data in headless Chromium (no errors from the new pages) |
| Mutation check | Breaking revert delivery, pilot-ring gating or lease expiry each made the corresponding integration test fail |

New coverage includes:

* **Policy conflicts** – unresolved conflicts withheld and reported, resolution by
  priority and by specificity, identical values not a conflict, exclusions,
  sub-department targeting (unit + API).
* **Interrupted deployments** – lease expiry → retry with back-off → late result of
  the abandoned attempt rejected (409) → failure after `maxAttempts`; aborted runs do
  not consume attempts; agent resumes the same attempt; journal recovery after power
  loss and after an undeliverable result; lease renewal during long installs.
* **Configuration rollback** – snapshot on first change, revert on assignment
  removal, “leave” behaviour and archiving, fast version rollback, restore-as-draft,
  rollback unsupported when no snapshot exists.
* **Authorisation** – four-eyes and Owner+acknowledgement approvals, Owner-only
  publishing/enforcing of destructive policies, blocked settings rejected, agent
  refuses settings above the authorised risk or a local ceiling, tampered hashes
  refused; Owner-only forced restarts, upgrades and pilot overrides.
* **Patch lifecycle end to end** – inventory, failures from WUA history, auto
  approval, pilot gate, restart dispatch inside the window, completion on reboot,
  post-restart verification, promotion, completion, compliance and CSV; closed
  maintenance window defers installation and restarts; failure-rate halt and resume;
  deadline time-outs; cancellation.
* **Isolation** – agent vs user credentials, retired devices, cross-device and
  cross-tenant job reports (404), RLS on the new tables with raw SQL, append-only
  audit for the runtime role and the schema owner.

## Known limitations / next steps

* **The Windows providers were compiled here but not executed on Windows.** The
  logic around them (evaluation, risk gate, snapshots, journal, protocol) is covered
  by tests with in-memory providers, and the server is covered end to end; the
  COM/P/Invoke code (WUA, firewall, SCM, PowrProf, DPAPI) still needs a run on
  Windows 10/11 and Server 2016+ test machines before release.
* **No signed agent package.** Scripts install a published folder as a service;
  an MSI, code signing and agent self-update are not implemented.
* The enrolment token is visible in the temporary scheduled task’s arguments for the
  seconds the installer runs (it is single-use and can be capped to one use).
* Desired-state documents are not signed; integrity relies on TLS + device
  authentication, and the agent independently re-checks risk and hashes.
* Only machine-scope (HKLM/system) settings; per-user (HKCU) policies such as
  screen-saver settings are out of scope.
* Solvix approvals gate Solvix deployments; Windows Update’s own automatic
  updates still run on devices unless restricted by Group Policy/Intune.
* Compliance and conflict reports are computed per request; very large fleets
  (tens of thousands of devices) will need materialised summaries.
* No end-user restart deferral UI; users see the `shutdown.exe` countdown.
* Pre-existing (Phase 01): breadcrumb parent links such as `/organization` and
  `/settings` have no page and 404 on prefetch (`/endpoints` now redirects).
