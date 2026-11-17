# Solvix Windows agent

A .NET 10 Worker Service that enrolls a Windows computer into Solvix EndPoints and
reports its inventory and health. Communication is **outbound HTTPS only** with mutual
TLS; the agent never executes instructions received from the server.

Supported: Windows 10, Windows 11, Windows Server 2016 / 2019 / 2022 / 2025 (x64).

## Projects

| Project | Purpose |
|---------|---------|
| `Solvix.Agent.Core` | Platform-neutral agent: runtime loop, enrollment, credential store, mTLS transport, API client, backoff, health, inventory service, update manager. Fully unit-tested on any OS. |
| `Solvix.Agent.Windows` | Windows modules: WMI/registry inventory collectors, CNG/TPM key store, DPAPI, Authenticode verification, MSI installer. Annotated `[SupportedOSPlatform("windows")]`. |
| `Solvix.Agent` | Service host (`UseWindowsService`), configuration, Serilog file + Event Log logging, CLI commands. |
| `../shared/Solvix.Agent.Contracts` | Wire contracts shared with the API (source-generated JSON). |
| `installer/` | `Build-AgentPackage.ps1` (publish). There is no install script: the exe installs itself. |

## Modules

| # | Module | Implementation |
|---|--------|----------------|
| 1 | Device identity | `DeviceIdentityProvider` (SMBIOS UUID, serial, hostname, OS); server-assigned device id bound to an X.509 certificate |
| 2 | Secure enrollment | `EnrollmentCoordinator`, `BootstrapTokenStore` (DPAPI), CSR from a fresh non-exportable key |
| 3 | Hardware inventory | `SystemCollector`, `BiosCollector` (manufacturer, model, serial, UUID, chassis, firmware, Secure Boot) |
| 4 | Operating system | `OperatingSystemCollector` (name, edition, version, build/UBR, install date, last boot, locale) |
| 5 | Software | `SoftwareCollector` (HKLM 64/32-bit + loaded user hives; skips system components and updates) |
| 6 | Network interfaces | `NetworkInventoryCollector` (adapters, MAC, IPv4/IPv6, gateways, DNS, DHCP, physical vs virtual) |
| 7 | Disks | `DiskCollector` (MSFT_PhysicalDisk with Win32_DiskDrive fallback), `VolumeInventoryCollector` |
| 8 | Processor | `ProcessorCollector` |
| 9 | Memory | `MemoryCollector` (total/available, modules) |
| 10 | Windows version | `WindowsVersionInfo` (Windows 10 vs 11 by build, Server 2016–2025, support status, display version) |
| 11 | Agent health | `AgentHealthMonitor` → health report on every heartbeat |
| 12 | Secure communication | `MutualTlsTransport` (TLS 1.2/1.3, client certificate, OS trust or SHA-256 pin), `AgentApiClient` |
| 13 | Update management | `UpdateManager` (offer → local policy → download → SHA-256 → Authenticode publisher pin → stage; privileged apply re-verifies) |
| 14 | Diagnostic logging | Rolling JSON logs in `%ProgramData%\Solvix\Agent\logs` (14 days, 10 MB), Windows Event Log for warnings |

## Install (no command line)

1. An administrator creates an enrollment token in *Endpoints → Enrollment* (optionally with group, branch
   and department) and clicks **Download agent package**. The server builds a zip containing the
   **unmodified, code-signed `Solvix.Agent.exe`**, `appsettings.json` and `enrollment.json` (server URL,
   short-lived token, optional certificate pin).
2. On the Windows computer: extract the whole zip and **double-click `Solvix.Agent.exe`**, approve the
   administrator (UAC) prompt. The exe installs itself, enrolls and starts; the device appears under
   *Endpoints → Devices* within a minute. `enrollment.json` is deleted after use.

The token lives in a separate file rather than inside the exe so the exe keeps its Authenticode signature
(agents and updates rely on it). It is short-lived, use-limited and revocable; the exe stores it DPAPI-encrypted
until enrollment completes. Treat the zip like a password.

For deployment tools (Intune, SCCM, GPO) run `Solvix.Agent.exe install --silent` from the extracted folder as
administrator, or pass settings explicitly: `install --silent --server <https url> --token <token> [--pin <sha256>]`
(the token may also come from `SOLVIX_ENROLLMENT_TOKEN`). Exit code 0 = installed.

What the installer does (`AgentInstaller`, unit-tested command by command):

* checks the Windows version (Windows 10/11, Server 2016+, 64-bit);
* copies the exe to `%ProgramFiles%\Solvix\Agent` (writable only by SYSTEM/Administrators);
* creates `%ProgramData%\Solvix\Agent` accessible only to SYSTEM, Administrators and the service;
* installs the `SolvixAgent` service as the virtual account **`NT SERVICE\SolvixAgent`** (no administrative rights,
  restricted privileges, delayed auto-start, restart on failure);
* registers the **Solvix Agent Updater** scheduled tasks (SYSTEM) which only install packages the agent staged
  *and* that pass the hash + Authenticode checks again;
* stores the server settings and the DPAPI-encrypted token, then starts the service.

Re-running the exe on an installed computer reinstalls in place. On first start the agent generates an ECDSA P-256
key in the TPM (Microsoft Platform Crypto Provider) or, if unavailable, the software key storage provider —
always non-exportable — sends a CSR with the token, receives its device certificate and deletes the token.

## Commands

```
Solvix.Agent.exe                       double-click: install + enroll from enrollment.json (as a service: run the agent)
Solvix.Agent.exe install [--silent]    install as a Windows service and enroll (UAC prompt)
Solvix.Agent.exe uninstall [--remove-data]   remove the service; --remove-data also deletes the device identity
Solvix.Agent.exe run                   run in the console
Solvix.Agent.exe enroll [--server <url> --token <token> [--pin <sha256>]]   (no arguments: read enrollment.json)
Solvix.Agent.exe status                local enrollment / certificate / contact state
Solvix.Agent.exe inventory             collect and print inventory locally (no network)
Solvix.Agent.exe apply-update          privileged updater (scheduled task)
Solvix.Agent.exe reset                 delete device key, certificate and state
```

## Smoke test

`scripts/smoke-agent.sh <api-url>` does what an administrator does against a running API: two stores → one token
per store → download each package → extract → run the extracted agent → placement checks. (The API needs the agent
package configured; see ARCHITECTURE.md.)

## Behaviour

* **Heartbeat** every 60 s (server-configurable, clamped to 15 s – 1 h) with a health report.
* **Inventory** every 4 h, uploaded only when the content hash (excluding volatile values such as
  free space) changed, at least every 24 h, or when an administrator requests a refresh.
* **Certificate renewal** when the server recommends it or 70 % of the lifetime has elapsed; the old
  key is deleted only after the new certificate has been accepted.
* **Reconnection**: failures back off exponentially (5 s → 15 min, ±20 % jitter); the next heartbeat
  reports how many attempts failed. Pooled TLS connections are recycled whenever the credential changes.
* **Credential loss** (device retired/deleted): after 3 consecutive authentication failures the agent
  re-enrolls if a new token has been supplied, otherwise reports an error and keeps backing off.
* **Rejected enrollment tokens** are moved aside (`enrollment.token.rejected`) and never retried in a loop.

## What the server can and cannot do

The heartbeat response contains only schedules (integers, clamped locally) and two booleans
(`inventoryRequested`, `certificateRenewalRecommended`) that trigger built-in actions. There is
no command, script or executable payload in any contract, and a unit test guards the
`HeartbeatResponse` shape. Agent updates are *offered*, never pushed: the agent installs nothing
unless the package URL is HTTPS on a locally allow-listed host, the SHA-256 matches, the version is
newer, and the MSI's Authenticode signer is one of the locally pinned publisher thumbprints.

## Configuration (`appsettings.json`, `%ProgramData%\Solvix\Agent\agent.json`, env `SOLVIX_AGENT_*`)

| Key | Default | |
|-----|---------|--|
| `SolvixAgent:ServerUrl` | – | HTTPS agent endpoint (written by `enroll`) |
| `SolvixAgent:TrustedServerCertificateSha256` | `[]` | Pins for private server certificates |
| `SolvixAgent:Backoff:*` | 5 s / 900 s / ×2 / 0.2 | Reconnection backoff |
| `SolvixAgent:MaxAuthenticationFailures` | 3 | Before the credential is considered lost |
| `SolvixAgent:InventoryMaxAgeHours` | 24 | Upload at least this often |
| `SolvixAgent:Updates:Enabled` | `true` | Local kill switch |
| `SolvixAgent:Updates:AllowedDownloadHosts` | `[]` | Empty = updates refused |
| `SolvixAgent:Updates:TrustedPublisherThumbprints` | `[]` | Code-signing certificate pins (SHA-1 or SHA-256) |
| `Logging:MinimumLevel` | `Information` | |

## Build

```bash
dotnet test tests/Solvix.Agent.Tests
dotnet publish src/agent/Solvix.Agent -c Release -r win-x64   # self-contained single-file exe
```

Sign `Solvix.Agent.exe` with the organisation's code-signing certificate, place it with `appsettings.json` in the
folder the API serves as the agent package, and put the certificate thumbprint into `Updates:TrustedPublisherThumbprints`
of that `appsettings.json`.
