# Phase 07 – Enterprise software management: technical summary

IT administrators deploy, update and uninstall software on managed Windows devices. Built on
Phase 03 (agent and device identity), Phase 02 (permissions) and Phase 06 (the signed command
broker): **software deployments are executed as signed commands through the Phase 06 broker**;
nothing new is trusted on the device.

Module: `src/backend/Modules/Solvix.Modules.Software`. Agent: `Solvix.Agent.Core/Software`,
`Solvix.Agent.Windows/Software`. Wire contracts: `Solvix.Agent.Contracts/Software`. UI:
`/software/*`. Migrations `20261113071320_SoftwareManagement` and
`20261113071400_SoftwareManagementSecurity`.

## Scope delivered

| # | Feature | Implementation |
|---|---------|----------------|
| 1 | Software inventory | `software_inventory_items`, projected from every agent inventory upload (`ISoftwareInventoryObserver`); catalog, per-device and "who has X / version Y" queries |
| 2 | Package repository | `software_packages`; streamed upload, validation, SHA-256, tenant-partitioned private storage |
| 3–5 | MSI / EXE / silent install | Fixed command lines built by the privileged executor: `msiexec /i … /qn /norestart /l*v`; EXE with preset (Inno, NSIS, WiX Burn, InstallShield) or custom switches, validated |
| 6 | Uninstallation | MSI by product code (no package); EXE by its configured uninstall switches |
| 7 | Version detection | `DetectionRule` (MSI product code, or Add/Remove Programs name/publisher, version equal / at least); same evaluator on server and device; on-demand `software.detect` command; every install is confirmed by detection afterwards |
| 8 | Deployment groups | device groups + branches + explicit devices − exclusions, resolved at request time |
| 9 | Scheduled deployment | start time (≤ 90 days ahead), deadline window, retry delay and attempts |
| 10 | Deployment approval | one approval (policy) or two for sensitive deployments; requester can never approve (code **and** CHECK constraint); `software.approve` permission |
| 11 | Deployment status | deployment and per-device status, live-refreshing UI |
| 12 | Failure reporting | error codes, exit codes with plain-language meaning, installer log excerpt, failure summary |
| 13 | Deployment history | per deployment, per device (`/software/devices/{id}/deployments`), hash-chained audit trail |
| 14 | Software restriction policies | Blocked / Required rules (wildcard name, publisher, minimum version, device-group scope); compliance report; blocked packages cannot be deployed |

## Deployment workflow

Upload → validate → checksum → configure → select devices → **review** (`POST …/preview`, nothing
stored) → request → approve → execute → collect results → report.

1. **Upload** (`POST /software/packages?fileName=`, `application/octet-stream`): streamed to a private
   temporary file while the SHA-256 is computed and the size limit enforced.
2. **Validate**: type from *content*, not name. MSI = OLE compound file with the Windows Installer
   class id (patches/transforms refused); EXE = PE executable, not a DLL, x86/x64/ARM64. Damaged
   structures, loops in sector chains and oversized streams are refused. Authenticode (PKCS#7) is
   extracted and its CMS signature verified; for PE files the signed image digest is **recomputed**,
   so an executable modified after signing is rejected. Verified against real Microsoft-signed binaries
   in the unit tests.
3. **Configure**: silent switches, success / reboot exit codes, timeout, detection, "sensitive" flag.
   An EXE needs silent switches; arguments are validated (see Security).
4. **Review → request**: targets resolved, duplicates and blocked devices identified, sensitivity and
   required approvals computed. The package configuration is **frozen** into the deployment.
5. **Approve**: `software.approve`, different person(s) than the requester.
6. **Execute** (`DeploymentEngine`, every 15 s per tenant with work): start at the scheduled time; one
   signed command per device through `IRemoteCommandDispatcher`; results applied immediately when the
   agent reports (`IRemoteCommandObserver`) and reconciled by the engine; transient failures
   (`another_install_in_progress`, `package_download_failed`, executor unavailable, installer timeout)
   and interrupted attempts (device silent past the deadline) are retried; devices not reached by the
   deadline are marked expired; the final **report** is stored as JSON when the deployment finishes
   (`GET …/report`, `…/report.csv`).

### Targets and idempotency

A target records its command id **before** the command is dispatched, and dispatch is idempotent on
that id, so a crash between the two writes, a restart or two API nodes never send an attempt twice.
A partial unique index (`ux_software_deployment_targets_active`) makes a second unfinished deployment of
the same package on the same device impossible in the database.

## Security

| Requirement | How |
|-------------|-----|
| Never execute uploaded installers automatically | The server only reads bytes. Nothing runs on upload; an installer runs only inside a deployment that was approved, on the target device, after verification. |
| Restrict package types | `.msi` / `.exe` only; type verified from content; tenant policy can restrict to MSI; agent can restrict locally (`AllowedInstallerTypes`). |
| Validate integrity | SHA-256 at upload; re-hashed before serving when older than 60 min; mismatch → package `Corrupted`, never served, deployments refused. The device re-checks size and SHA-256 from the **signed** command; the executor checks the checksum again on its own copy. |
| Signed packages | Signer and thumbprint stored at upload. A package signed at upload must carry a valid signature **from that signer** on the device (WinVerifyTrust incl. revocation + thumbprint pin), checked by agent and executor. Tenant can require signed packages, and keep a list of trusted publishers; unsigned or untrusted-publisher packages need the additional approval. Devices can pin publishers locally. |
| Store files securely | Outside any web root, `<root>/<tenant>/<random key>.pkg` (never the uploaded name), owner-only permissions, atomic move, dedicated volume (`solvix-packages`). Download requires a device certificate, an accepted command of that device naming that package and checksum, and a valid stored file; Range supported for resume. |
| Least-privilege execution | The agent (`NT SERVICE\SolvixAgent`) downloads and verifies but cannot install. A separate `SolvixSoftwareExecutor` service (LocalSystem, no network function) listens on a named pipe only SYSTEM and the agent service account may open (network denied). It **re-verifies the signed command itself** (own pinned keys and identity, own replay store), accepts only software install/uninstall, copies the package into its own directory, verifies checksum and signature of that copy, and builds the command line itself: fixed executable (`msiexec` from the system directory, or the verified EXE), arguments as an argument list — no shell. |
| Additional approval for sensitive deployments | Unsigned package, untrusted publisher, package marked sensitive, uninstall, large deployment (> threshold), server targets → two approvers, both different from the requester and from each other. |
| Cancellation and timeout | Cancel: queued/undelivered commands never run (acknowledgement is refused); running ones get a signed `command.cancel`, the executor stops the installer process tree. Installer timeout enforced by the executor; device silence enforced by the broker's execution deadline; deployment deadline. |
| Argument injection | MSI: only public `NAME=value` properties (reserved ones such as `REBOOT`, `TRANSFORMS`, `PATCH` refused). EXE: no quotes, `| & < > ^ % \`` or control characters. Enforced when configured, by the broker-side parser, by the agent and by the executor (one shared `SoftwareOperation.Parse`). |
| Tenant isolation / audit | RLS forced on all nine tables; append-only, hash-chained `software_audit_events` (verify endpoint); every command also appears in the Phase 06 remote audit. The requester is re-authorized (permission, device state, technician scope) at dispatch and again at delivery. |

## Permissions

`software.view`, `software.packages.manage`, `software.deploy`, `software.approve`,
`software.policies.manage` (all imply `software.view` → `devices.view`). IT Manager: all; Tenant/Super
administrator: all; IT Technician: view + deploy (cannot upload or approve); other built-in roles: view.
Existing tenants are updated by the security migration. `software.deploy` is also what the broker checks
for the requester of every software command.

## API

`/api/v1/software/` — `packages` (upload, list, get, `PUT` configure, `verify`, `retire`, `detect/{deviceId}`),
`detections/{executionId}`, `deployments` (`preview`, create, list, get, `approve`, `reject`, `cancel`,
`redeploy-failed`, `report`, `report.csv`), `deployment-groups`, `policy`, `restriction-rules`,
`inventory/catalog|installations`, `devices/{id}/inventory|deployments`, `compliance`, `audit`, `audit/verify`.
Agent: `GET api/v1/agent/software/executions/{executionId}/package` (device certificate).

## Configuration

Server: `Solvix:Software:Storage:RootPath`, `MaxPackageBytes` (2 GiB), `ProcessorIntervalSeconds`,
`DispatchBatchSize`, `ReverifyAfterMinutes`. Agent (`SolvixAgent:Software`): `Enabled`,
`AllowedInstallerTypes`, `AllowUnsignedPackages`, `TrustedPublisherThumbprints`, `MaxPackageBytes`,
`ExecutorPipeName`, `DownloadAttempts`. The installer script creates the executor service
(`-DisableSoftwareDeployment` skips it).

## Tests

Unit (57 new): package validation (PE/MSI structure, Authenticode incl. tampering and real signed binaries,
cycles), argument rules, detection and versions, parameter parsing, restriction patterns, CSV safety.
Agent (25 new): detection, download, **resume after interruption**, corrupted package, signature and local
policy, exit-code handling, uninstall, executor re-verification (tamper, replay, foreign device, swapped
package, pinned identity), fixed command lines, the real process runner (timeout, cancel, no shell
interpretation) and the named pipe (disconnect stops the installer). Integration (22 new, real PostgreSQL +
Redis): full workflow with download and ranges; approvals (four/six eyes, DB constraint); rejection;
**failed installations** and retries; **duplicate deployments** (API and DB); **interrupted deployments**
(silent device, crash between claim and dispatch); expiry; cancellation; scheduling; permission loss before
delivery; **package corruption** in storage; invalid/forged uploads; permissions; cross-device / cross-tenant
access and RLS; inventory, restriction rules, deployment groups, detection, audit append-only; and the real
agent core installing a package against the real API.

## Known limits

* **Windows-only code is compiled but was not run on Windows** (registry detection, `WinVerifyTrust`, pipe ACLs,
  `msiexec`, services). The portable logic around it is tested; first Windows 10/11 and Server runs are still
  to be done. The process runner is tested on Linux with `sh`.
* MSI signature digests are checked on the device (WinVerifyTrust), not at upload; the server verifies the
  PKCS#7 signature and records the signer. EXE digests are verified at both.
* Installers are run non-interactively; software that needs the signed-in user's session (per-user installs,
  UI) is out of scope. Repair, patches (`.msp`) and MSIX are not supported.
* Package storage is the local file system (one API node or a shared volume); `IPackageStorage` is the seam for
  object storage. The deployment engine is safe on several nodes (tenant-serialized, idempotent) but polls.
* Rolling out in waves/rings, bandwidth limits and peer distribution are not implemented.
