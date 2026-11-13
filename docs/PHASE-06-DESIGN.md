# Phase 06 – Secure remote endpoint administration: design and implementation plan

**Status:** in progress. This document is the design of record for Phase 06 and tracks which
increments are implemented. Section 9 is the authoritative status table; nothing is described as
working unless it is listed there as *Done* with its verification.

## 1. Repository audit (before Phase 06)

| Finding | Consequence |
|---------|-------------|
| `main` held only Phase 01. Phase 02 (IAM) and Phases 03–05 (endpoints, monitoring, assets) were developed on **separate branches from Phase 01** and had never been combined. | Phase 06 needs both the Phase 02 permission model and the Phase 03 agent plane. They were merged first (commit *Integrate Phase 02 (IAM) with Phases 03-05*), porting Phases 03–05 from Phase 01 role policies to Phase 02 permissions. All suites green after the merge. |
| Authorization (Phase 02): permission keys in `SharedKernel/Authorization`, tenant-owned roles, `[RequirePermission]` on every action, `ExplicitAuthorizationConvention` refuses to start the API if an action has no declaration, permissions resolved server-side per request (never from the token), `PrivilegeGuard` against escalation. | Remote permissions are added to the same catalog; no second authorization system. |
| Device identity (Phase 03): X.509 device certificate from the platform device CA, SAN `urn:solvix:device:{tenant}:{device}`, mutual TLS, `Device` policy, tenant taken from the CA-signed certificate. | The command channel reuses this transport and identity. |
| Agent (Phase 03/04): outbound-only HTTPS, heartbeat (default 60 s) returning configuration and two booleans; contracts deliberately carry **no instructions** ("never execute arbitrary server instructions"). Runs as the virtual account `NT SERVICE\SolvixAgent` with only `SeChangeNotifyPrivilege/SeCreateGlobalPrivilege`. Privileged work (updates) is done by a separate SYSTEM component that **re-verifies** what it is given. | Remote operations need privileges the agent intentionally lacks (restart, shutdown) and access to the interactive desktop that a session-0 service cannot reach (lock, screen capture, call prompt). Phase 06 keeps the agent least-privileged and follows the update pattern (§4.3). |
| Real-time (Phase 04): SignalR hub through the Next.js BFF using SSE/long polling (route handlers cannot proxy WebSockets). | Browser-side streaming (terminal output, session status, call signalling) uses the same BFF approach. |
| Audit (Phase 02 `auth_audit_events`, Phase 05 asset audit log): append-only via database triggers. | Remote audit uses the same technique. |
| Gap noted during the merge: Phase 02's single-VPS deployment (`docker-compose.prod.yml`, `deploy/Caddyfile`) does not expose the Phase 03 agent mutual-TLS endpoint. | Not Phase 06 scope; tracked as a follow-up. |

## 2. Scope mapping

The brief's module list maps onto the existing structure instead of creating parallel projects:

| Brief | Implemented as |
|-------|----------------|
| Backend `RemoteManagement/*` | New module `src/backend/Modules/Solvix.Modules.RemoteManagement` with folders `Authorization`, `CommandBroker`, `Approvals`, `Auditing`, `SessionManagement`, `SystemOperations`, `PowerShell`, `FileTransfer`, `RemoteTerminal`, `RemoteDesktop`, `MultiMonitor`, `VoiceCommunication`. Devices, identity, tenancy are consumed through `SharedKernel` contracts. |
| `Solvix.WindowsAgent/*` | The existing agent (`src/agent/Solvix.Agent.Core` portable logic, `Solvix.Agent.Windows` Win32 code). New folder `RemoteManagement/` in each, plus the privileged executor and user-session helper hosts (§4.3). No second agent. |
| Agent/server wire contracts | `src/shared/Solvix.Agent.Contracts/RemoteManagement/*` |

## 3. Permissions and scope

### 3.1 Permission keys (added to `PermissionCatalog`)

`remote.view`, `remote.desktop`, `remote.desktop.control`, `remote.multimonitor`, `remote.terminal`,
`remote.powershell`, `remote.execute_approved_commands`, `remote.file.upload`, `remote.file.download`,
`remote.restart`, `remote.shutdown`, `remote.lock`, `remote.voice_call`, `remote.sessions.view`,
`remote.sessions.terminate`, `remote.approvals.manage`. Every key implies `remote.view`, which implies
`devices.view`. `remote.desktop.control` implies `remote.desktop`.

The Phase 02 placeholders `devices.remote_access` and `devices.execute_command` are **retired**:
they were never checked by any endpoint, and keeping coarse keys next to the granular ones would
be ambiguous. A migration removes them from existing roles and grants the granular equivalents
the built-in role held (see §3.3).

### 3.2 Scope (checked on the server for every request, in addition to the permission)

`RemoteAccessPolicy` (one per tenant; defaults shown) and optional per-role scope rows:

| Setting | Default | Meaning |
|---------|---------|---------|
| Device-group restriction | none | Role may only reach devices in the listed groups |
| Branch restriction | none | Role may only reach devices assigned to the listed branches |
| Allowed hours | always | Time window (tenant time zone) in which sessions/commands may start |
| Max concurrent sessions per device / per technician | 1 interactive per device, 3 per technician | Interactive types: desktop, terminal, voice |
| Session idle timeout / max duration | 15 min / 4 h | Applied to every session type |
| Notify the device user | on for desktop and terminal | Toast in the user session when a session starts |
| Attended consent required | desktop: on; voice: always (not configurable) | The employee must accept |
| Approval required | restart, shutdown, logoff, PowerShell scripts marked sensitive | Second person with `remote.approvals.manage`; requester can never approve |
| Clipboard | disabled | `None`, `ToDevice`, `FromDevice`, `Both` |

Scope evaluation lives in `RemoteManagement/Authorization/RemoteAuthorizer` and is the single
entry point every module calls: tenant (from the token, never the request) → device exists in
tenant (RLS) → device active/enrolled → permission (re-read from the server-side resolver) →
role scope (group/branch) → time window → concurrency → approval requirement. Each decision is
written to the remote audit log with its result.

### 3.3 Built-in roles

| Role | Remote permissions |
|------|-------------------|
| Super Administrator, Tenant Administrator | all |
| IT Manager | all except `remote.powershell` (ad-hoc) |
| IT Technician | `remote.view`, `remote.desktop`, `remote.desktop.control`, `remote.multimonitor`, `remote.terminal`, `remote.execute_approved_commands`, `remote.file.upload`, `remote.file.download`, `remote.restart`, `remote.lock`, `remote.voice_call`, `remote.sessions.view` |
| Security Administrator, Read-Only Auditor | `remote.view`, `remote.sessions.view` |
| Asset Manager | none |

`remote.powershell` (interactive PowerShell / ad-hoc scripts) is deliberately absent from every
built-in role below Tenant Administrator; tenants grant it explicitly through a custom role.

## 4. Command broker

### 4.1 Envelope

Every operation that reaches a device — including the ones that open interactive sessions — is a
**command execution** created by the broker. The browser never submits a command body: it calls
a typed endpoint (`POST /devices/{id}/operations/restart`, `POST /remote/scripts/{id}/run`, …); the
server builds the envelope.

```jsonc
{
  "v": 1,
  "executionId": "0193…",          // UUIDv7, unique, the replay key
  "tenantId": "…", "deviceId": "…", // from the database, never from the request
  "requestedBy": "…",               // user id from the validated session
  "commandType": "system.restart",  // allow-listed definition key
  "commandVersion": 1,
  "parameters": { … },              // validated against the definition's schema
  "sequence": 42,                   // per-device, strictly increasing
  "nonce": "base64url(16 bytes)",
  "createdAtUtc": "…", "expiresAtUtc": "…",
  "authorization": "Authorized",
  "approval": { "status": "Approved", "approvalId": "…", "approvedBy": "…" } | { "status": "NotRequired" },
  "auditId": "…",
  "keyId": "cmd-2026-11"
}
```

Signature: **ECDSA P-256 / SHA-256** over the UTF-8 bytes of the canonical envelope JSON
(property order fixed by the contract type, no insignificant whitespace), transported as
`{ "envelope": "<base64url canonical bytes>", "signature": "<base64url>", "keyId": "…" }`. The
agent verifies the bytes it received and only then deserializes them — there is no
re-serialization ambiguity.

### 4.2 Server side

* `CommandDefinitionRegistry` – code-defined allow-list: key, version, required permission,
  parameter validator, default timeout, risk (approval), whether it needs the privileged
  executor, session type (if any). Unknown keys cannot be created.
* `ICommandSigner` – `FileCommandSigner` (PEM private key from configuration or generated on first
  start in Development, like the Phase 03 device CA) behind an interface so a cloud KMS signer
  can replace it without code changes elsewhere. The public key set is handed to agents at
  enrollment and with each heartbeat (key id + SPKI), and pinned by the agent on first receipt;
  additional keys are only accepted when the key-set document is signed by an already pinned key.
* Tables (all tenant-owned, FORCE RLS, composite tenant FKs): `remote_command_executions`,
  `remote_device_command_sequences`, `remote_approvals`, `remote_sessions`,
  `remote_audit_events` (append-only via triggers), `remote_access_policies`,
  `remote_role_scopes`, `remote_scripts`, `remote_file_transfers`.
* Lifecycle: `PendingApproval → Queued → Delivered → Acknowledged → Running →
  Succeeded | Failed | Cancelled | Expired | Rejected | TimedOut`. Transitions are single
  conditional UPDATEs (`WHERE status = @expected`), so a result is accepted exactly once and two
  racing deliveries cannot both win.
* Delivery (offline-tolerant, at-least-once to the device, exactly-once execution): the
  heartbeat response carries `pendingCommands`; the agent then long-polls
  `GET agent/commands` (held ≤ 25 s, released early by a Redis/in-process signal when a command
  is queued). Commands not acknowledged within the redelivery interval are offered again until
  they expire. A background sweeper expires commands past `expiresAtUtc` and times out running
  ones past their execution timeout.
* Cancellation: `Queued`/`Delivered` → cancelled immediately on the server; `Acknowledged`/`Running`
  → a signed `command.cancel` for that execution id is queued; the handler is cancelled if it
  supports it, and the result records whether cancellation took effect.
* Re-authorization at delivery: when the agent fetches a command, the requester's permission and
  scope are evaluated again; if they lost it (role change, deactivation, session revoked) the
  command is marked `Rejected` and never handed out. Interactive sessions re-check every 60 s
  and are terminated on loss of permission.

### 4.3 Agent side

```
SolvixAgent service (NT SERVICE\SolvixAgent, unprivileged)            [exists]
  └ CommandChannel: long-poll, ack, result upload
  └ CommandVerifier: key pin → signature → schema v → tenant/device binding → expiry (±60 s skew)
                     → sequence/executionId replay store (persisted) → local allow-list/policy
  └ Read-only handlers (system info, processes, services, uptime, sessions)
  └ Privileged requests → named pipe ─────────────┐
                                                  ▼
Solvix Remote Executor service (LocalSystem, pipe ACL: SolvixAgent SID only)   [new]
  └ re-verifies signature, binding, expiry and replay itself (does not trust the agent)
  └ fixed handler table: restart, shutdown, logoff, PowerShell (approved script hash only),
    file write/read within allowed roots
  └ launches the user-session helper with WTSQueryUserToken/CreateProcessAsUser when needed
                                                  ▼
Solvix Session Helper (runs as the signed-in user, per interactive session)      [new]
  └ lock workstation, notifications, consent prompts, incoming call window,
    screen capture and input injection for remote desktop
```

The executor has no network access of its own and no generic "run this" entry point; a
compromised agent process can only forward envelopes the executor would accept anyway.

## 5. Interactive sessions

`remote_sessions` (type: RemoteDesktop, RemoteTerminal, PowerShell, FileTransfer, VoiceCall;
status: Requested → AwaitingConsent → Connecting → Active → Ended | Rejected | Failed |
Terminated | Expired) is the common record for history, concurrency, termination and audit.
Each session gets a short-lived (≤ 60 s to start), single-use, session-bound relay token for
the agent and the browser; the relay is a server endpoint both sides connect **out** to.

| Feature | Transport | Notes |
|---------|-----------|-------|
| Remote terminal / PowerShell console | Agent ↔ API: WebSocket (outbound from agent, mTLS). Browser ↔ BFF: SSE down, POST up (the BFF cannot proxy WebSockets, same as Phase 04). | Executor hosts a ConPTY running `powershell.exe -NoProfile` in **Constrained Language Mode** unless the tenant enables full language for `remote.powershell` holders. Full transcript hash + command lines audited. |
| File transfer | HTTPS chunked upload/download through the API with tenant-prefixed object storage, SHA-256 per file and per chunk, resumable by offset. | Path policy enforced on the server **and** in the executor (allowed roots, canonicalized, no reparse points/ADS/UNC/device paths, deny-list of system directories), configurable size limit, optional AV scan hook (`IFileScanner`; Microsoft Defender `MpCmdRun` on the device). |
| Multi-monitor | Session helper enumerates displays (`EnumDisplayMonitors` + `DisplayConfigGetDeviceInfo` for names, `EnumDisplaySettings` for refresh/orientation, `WM_DISPLAYCHANGE` for changes); reported in inventory/heartbeat and pushed live during a session. | Switching changes the capture source of the running session (RFB DesktopSize), no reconnect. |
| Remote desktop | See §6. | |
| Voice | WebRTC (browser ↔ session helper), signalling over the broker/session relay, STUN/TURN (coturn). | §7. |

## 6. Remote desktop: technology evaluation

| Option | Licence | Outbound-only | Attended (see the user's console) | Multi-monitor switching | Verdict |
|--------|---------|---------------|-----------------------------------|-------------------------|---------|
| **Apache Guacamole** (guacd + guacamole-common-js), RDP to the device | Apache-2.0 (guacd links FreeRDP, Apache-2.0) | Only with a reverse tunnel: guacd must reach the device's port, so the agent has to open an outbound tunnel to the gateway | **No** on client Windows: an RDP login disconnects the console user | RDP shows one virtual desktop; per-monitor selection not supported | Good gateway/client, but RDP alone does not meet attended support |
| Guacamole with **VNC/RFB** to a server on the device | guacd's VNC support links libvncclient (**GPL-2.0**); fine to run server-side unmodified in SaaS (no distribution), source-offer obligation if the gateway image is shipped to self-hosting customers | Same reverse-tunnel requirement | Yes, if the RFB server captures the console session | Yes: the server chooses the captured monitor and resizes the framebuffer | **Selected** |
| Third-party RFB servers (TightVNC, UltraVNC) on the device | GPL-2.0 | — | Yes | Partial | Rejected: bundling a GPL server in the agent package forces source distribution and adds an inbound listener |
| Commercial SDKs (e.g. RustDesk server-pro, TeamViewer/AnyDesk SDKs) | Commercial / AGPL (RustDesk OSS) | Yes | Yes | Yes | Viable alternative behind the same interface; AGPL or per-seat licensing is a product decision (`OD-5` in the earlier architecture proposal) |
| Custom protocol | — | — | — | — | Rejected (brief) |

**Decision:** guacd as the gateway and `guacamole-common-js` in the browser, speaking the
standard RFB protocol to an **RFB server implemented in the session helper** on top of the
Windows Desktop Duplication API (capture) and `SendInput` (control). The agent opens an
outbound mTLS WebSocket tunnel to the API; the API exposes it to guacd on the internal
network only; the browser reaches guacd through the API's Guacamole tunnel endpoint (HTTP
tunnel, which the BFF can proxy). guacd never faces the internet, and no inbound port is
opened on the device. Everything provider-specific sits behind `IRemoteDesktopProvider`, so a
commercial SDK can replace it. View-only sessions (`remote.desktop` without
`remote.desktop.control`) are enforced twice: guacd `read-only=true` and the helper drops input
events for the session.

Unattended sessions (no user signed in, or consent disabled by tenant policy for the device
group) capture the secure desktop/log-on screen; they are still notified on screen when a
user signs in.

## 7. Voice (“Call user”)

* Signalling: `voice.call.offer` command (signed) → helper shows the incoming-call window with
  technician name and organisation; nothing is captured before the user presses **Accept**.
  Reject/timeout (45 s) ends the call with status `Rejected`/`Failed`.
* Media: WebRTC (Opus), DTLS-SRTP, peer-to-peer where possible, TURN relay otherwise. The helper
  hosts the call in **WebView2** (Edge WebRTC stack, OS microphone privacy settings and device
  pickers apply); no third-party native media stack. Microphone/speaker selection and mute on
  both sides; permission failures surface as `MicrophoneUnavailable` / `SpeakerUnavailable`.
* Never recorded. There is no recording code path; any future recording would be a separate
  feature with its own consent flow, per-tenant legal configuration and indicator.
* ICE restart on network change; the call survives short interruptions (`Reconnecting`), and
  ends as `Failed` after 30 s without media.

## 8. Third-party components and licences

| Component | Use | Licence | Obligation |
|-----------|-----|---------|------------|
| Apache Guacamole guacd | Server-side gateway container | Apache-2.0 | Notice |
| FreeRDP (linked by guacd) | not used by the selected design (RDP disabled in guacd config) | Apache-2.0 | — |
| libvncclient (linked by guacd) | RFB client inside guacd | GPL-2.0-or-later | No obligation for SaaS operation; when shipping the guacd image to customers, provide corresponding source (the upstream image already publishes it) |
| guacamole-common-js | Browser client | Apache-2.0 | Notice |
| coturn | TURN/STUN for voice | BSD-3-Clause | Notice |
| xterm.js | Browser terminal | MIT | Notice |
| Microsoft Edge WebView2 Runtime | Call window in the session helper | Microsoft redistributable licence (Evergreen runtime is part of Windows 11; bootstrapper may be redistributed) | Follow redistribution terms |

Licences listed from the upstream projects as known at design time; re-verify the exact
versions before the first production release.

## 9. Increments and status

| # | Increment | Status |
|---|-----------|--------|
| 0 | Integrate Phase 02 with Phases 03–05 | **Done** – agent 71, unit 198, integration 180 tests, web checks and build green |
| 1 | Permissions, remote access policy and scope (`Authorization`), remote audit log (`Auditing`) | **Done** – see §10 |
| 2 | Command broker: definitions, signer, envelope, sequences, delivery/ack/result, redelivery, expiry, cancellation; approvals | **Done** – see §10 |
| 3 | Agent: key pinning, verifier, replay store, command channel, read-only system operations | **Done** – see §11. Windows-only handlers compiled, not run on Windows |
| 4 | Privileged executor service + restart / shutdown / logoff / lock | **Implemented, not verified on Windows** – see §12. The user-session helper is still open (lock uses session disconnect meanwhile) |
| 5 | Session management (records, concurrency, idle/max expiry, termination, history) | Planned |
| 6 | Approved PowerShell scripts (library, approval, hash-pinned execution) | Planned |
| 7 | File transfer | Planned |
| 8 | Remote terminal | Planned |
| 9 | Multi-monitor inventory and live updates | Planned |
| 10 | Voice calls | Planned |
| 11 | Remote desktop (guacd, tunnel, RFB server in the helper) | Planned |
| 12 | Web UI (device actions, remote workspace, sessions, approvals, audit) | Planned |

**Verification limits in this environment:** there is no Windows machine here. Windows-only code
(executor service, session helper, Desktop Duplication, `SendInput`, ConPTY, WTS APIs, WebView2)
is compiled but cannot be executed; portable logic around it is unit-tested, and each such
item is marked *Implemented, not verified on Windows* until it has run on Windows 10/11 and
Server 2016/2022.

## 10. Implemented: increments 1–2 (server side)

Module `src/backend/Modules/Solvix.Modules.RemoteManagement`, migrations `20261113061006_RemoteManagement`
and `20261113061100_RemoteManagementSecurity`.

| Area | What exists | Where |
|------|-------------|-------|
| Permissions | 16 `remote.*` keys with implications; built-in roles per §3.3; placeholders retired; data migration for existing tenants (custom roles keep equivalent access) | `SharedKernel/Authorization`, `Identity/Domain/SystemRoles.cs`, security migration |
| Authorization | `RemoteAuthorizer`: device in tenant → active → permission → technician scope (groups/branches) → allowed hours; `ReauthorizeAsync` for queued work using the member's *current* roles (`IAccessResolver.ResolveMemberPermissionsAsync`) | `Authorization/` |
| Policy & scope API | `GET/PUT /remote/policy` (`remote.view` / `security.manage`), `GET/PUT/DELETE /remote/scopes/{userId}` (`security.view` / `security.manage`) | `Api/RemoteControllers.cs` |
| Command definitions | Allow-list: system info, processes, services, uptime, logged-in users, lock, logoff, restart, shutdown (+ internal cancel); validated parameters; typed device-name confirmation for disruptive operations | `CommandBroker/CommandDefinitions.cs` |
| Signing | ECDSA P-256 over canonical envelope bytes; `ICommandSigner` (file/PEM; generated in Development); public key set at `GET agent/command-keys` | `CommandBroker/CommandSigner.cs`, `Agent.Contracts/RemoteManagement/Commands.cs` |
| Delivery | heartbeat `pendingCommands` hint → `GET agent/commands?waitSeconds=` (long poll ≤ 30 s) → `POST …/ack` → `POST …/result`; redelivery of the identical signed bytes after 60 s without acknowledgement; per-device sequence; requester re-authorized at delivery | `CommandBroker/AgentCommandService.cs` |
| Exactly-once | conditional updates: ack only from `Delivered` and before expiry (a cancelled/expired command answers 409 and must not run); one result per acknowledged command (second → 409, audited) | same |
| Approvals | configurable per operation (default: restart, shutdown, logoff); four-eyes enforced in code and by a CHECK constraint; approver must hold the operation's permission and scope; requester re-checked at approval | `CommandBroker/CommandBroker.cs` |
| Expiry / timeouts | sweeper: unfetched → `Expired`, approval → `Expired`, acknowledged without result past the deadline → `TimedOut` (device offline mid-execution) | `CommandBroker/CommandSweeper.cs` |
| Cancellation | before acknowledgement: immediate; after: signed `command.cancel` for the execution | `CommandBroker.CancelAsync` |
| Audit | `remote_audit_events`: every request, denial, queue, delivery, ack, rejection, result, approval decision, policy/scope change; actor, device, execution, permission, source IP; append-only triggers + per-tenant SHA-256 hash chain; `GET /remote/audit`, `GET /remote/audit/verify` (`audit.view`) | `Auditing/` |
| Tenant isolation | tenant from token/certificate only; RLS forced on all six tables; devices only see their own commands | migration, `AgentCommandService` |

**Tests** (`tests/Solvix.IntegrationTests/Tests/RemoteCommandBrokerTests.cs`, real PostgreSQL + Redis): lifecycle with
signature verification and audit; duplicate result / idempotent ack; redelivery with identical bytes; 12 concurrent
requests from two technicians → unique sequences; unauthorized technicians; device-name confirmation; approvals incl.
self-approval and approver without the permission; permission removed after request → rejected at delivery; offline
device → expired, offline during execution → timed out, late ack/result refused; cancellation before delivery, after
delivery and while running; device-side rejection; cross-tenant and cross-device access; user tokens on the agent
channel; scope and allowed hours; append-only audit and tamper detection. The Phase 02 endpoint-protection sweep covers
every new endpoint.

**Known limits:** the long-poll wake-up signal is in-process (with several API nodes a poll on another node returns at
its timeout, ≤ 30 s later). The command signing key is a file/PEM key; a KMS-backed `ICommandSigner` is still to be written.

## 11. Implemented: increment 3 (agent)

`src/agent/Solvix.Agent.Core/RemoteManagement`, `src/agent/Solvix.Agent.Windows/RemoteManagement`.

| Piece | Behaviour |
|-------|-----------|
| `CommandChannelWorker` | One outbound long poll (`GET agent/commands`, ≤ 25 s) while enrolled; exponential backoff on failures. |
| `CommandKeyStore` | Pins the server's command signing keys on first contact after enrollment (over the device-certificate channel); a pinned set is never replaced by the server. Key rotation therefore needs `AdditionalPublicKeys` on the server *before* the agent first pins, or re-enrollment. |
| `CommandVerifier` | Pinned key → ECDSA signature over the received bytes → envelope version → key id matches → tenant and device equal the agent's own enrolled identity → validity window (±2 min skew, ≤ 8 days) → `Authorized` → approval present and not self-approved → command type implemented here and not disabled by local policy (`SolvixAgent:RemoteManagement:DisabledCommands`, `Enabled`) → execution id and nonce never seen. |
| `CommandReplayStore` | Persisted (survives restarts), pruned after expiry, bounded. |
| `CommandProcessor` | verify → acknowledge (refusals are reported with their code) → only after the server accepted the acknowledgement: record → execute (≤ 4 concurrent, 15 min cap) → report the result with retries; `command.cancel` cancels a running execution. |
| Handlers | portable: `system.info`, `system.uptime`, `system.processes` (no command lines); Windows: `system.services` (WMI), `system.logged_in_users` (WTS API). |
| Privileged operations | `system.restart`, `system.shutdown`, `system.logoff`, `system.lock` go to `IPrivilegedOperationHost` with the signed bytes. **The Windows executor service is not implemented yet (increment 4)**; until then these fail with `privileged_executor_unavailable` – they are never run by the unprivileged agent. |

Tests: `tests/Solvix.Agent.Tests/RemoteCommandTests.cs` (11: valid command; other tenant/device; expired/skew/implausible;
tampered bytes, forged signature with a trusted key id, unknown key, garbage, key-set replacement; unauthorized, unapproved,
self-approved, unknown type, unsupported version; local disable; run-once and replay after restart incl. nonce reuse;
server refuses acknowledgement → not executed; refusal reported; privileged without executor; signed cancel) and
`AgentEndToEndTests.Real_agent_verifies_executes_and_reports_signed_commands_and_refuses_tampered_ones` (real agent core ↔
real API: execution and result, command altered in the database refused by the device, approved restart failing cleanly).

## 12. Implemented: increment 4 (Remote Executor)

`src/agent/Solvix.Agent.Core/RemoteManagement/Privileged`, `src/agent/Solvix.Agent.Windows/RemoteManagement/WindowsPowerOperations.cs`.

| Piece | Behaviour |
|-------|-----------|
| Service | `Solvix.Agent.exe executor` as Windows service **SolvixRemoteExecutor** (LocalSystem), created by the installer, removed by the uninstaller. The power code exists only in this process. |
| Pipe | `\\.\pipe\SolvixRemoteExecutor`; ACL: SYSTEM and Administrators full, `NT SERVICE\SolvixAgent` read/write, network SID denied (`SecuredExecutorPipeFactory`). Length-prefixed JSON, ≤ 128 KiB, 30 s per request. |
| Independent verification | The executor re-runs the full verifier (signature, tenant, device, expiry, approval, allow-list) with its **own** pinned keys, pinned tenant/device identity and replay store under `%ProgramData%\Solvix\Agent\executor` (SYSTEM/Administrators only). It adopts the agent's identity and keys once, at the first command; after that, changes to the agent's files cannot redirect it. It accepts only restart, shutdown, sign-off and lock (never cancel or read-only commands) and re-validates parameters itself. |
| Operations | restart/shutdown: `InitiateSystemShutdownEx` (planned reason, **no forced app close**, optional delay ≤ 600 s and message); sign-off: `WTSLogoffSession` on the active console session; lock: `WTSDisconnectSession` of the console session (lock screen, programs keep running). No console user → `no_active_session`. |
| Agent side | `PipePrivilegedOperationHost`; executor not running → `privileged_executor_unavailable`; refusals surface as `executor_rejected_<code>`. |

**Limits:** first-use trust – a compromised agent that alters its key or identity files *before* the first privileged command could seed the executor; a stricter option is to pin both at install time. A process that creates the pipe before the executor starts could receive (but not forge) signed commands; the executor creating the first pipe instance at boot makes this unlikely. Lock via a user-session helper (`LockWorkStation`, toasts, consent prompts) is still to be built.
**Tests (Linux, portable logic and pipe):** `RemoteExecutorTests` (9): all four operations with parameters; forged, wrong tenant/device, expired, unapproved, cancel and read-only types refused; replay across executor restarts; tampering with the agent's files after first use; parameter re-validation; OS failures reported; pipe round trip incl. missing executor, replay and forged command; full chain processor → pipe → executor. The Win32 calls, the pipe ACL and the installer scripts were **not executed** (no Windows machine, no PowerShell).
