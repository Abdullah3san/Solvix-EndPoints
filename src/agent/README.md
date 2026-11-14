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
| `installer/` | `Install-SolvixAgent.ps1`, `Uninstall-SolvixAgent.ps1`, `Build-AgentPackage.ps1`. |

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

## Install

The installer package contains **no secret**. A tenant administrator creates a
short-lived enrollment token in *Endpoints → Enrollment* and passes it at install time:

```powershell
# elevated PowerShell, from the folder produced by Build-AgentPackage.ps1
.\Install-SolvixAgent.ps1 -ServerUrl https://agents.example.com:8443/ -EnrollmentToken svxe_...
```

For deployment tools, set `SOLVIX_ENROLLMENT_TOKEN` instead of the parameter. For a
server with a private (self-signed) TLS certificate add `-ServerCertificateSha256 <thumbprint>`.

The installer:

* checks the Windows version;
* creates `%ProgramData%\Solvix\Agent` accessible only to SYSTEM, Administrators and the service;
* installs the `SolvixAgent` service running as the virtual account **`NT SERVICE\SolvixAgent`**
  (no administrative rights, restricted privileges, restart on failure);
* registers the **Solvix Agent Updater** scheduled task (SYSTEM) which only installs packages the
  agent staged *and* that pass the hash + Authenticode checks again;
* stores the server URL and the DPAPI-encrypted token, then starts the service.

On first start the agent generates an ECDSA P-256 key in the TPM (Microsoft Platform Crypto
Provider) or, if unavailable, the software key storage provider — always non-exportable —
sends a CSR with the token, receives its device certificate and deletes the token.

## Commands

```
Solvix.Agent.exe                       run (service or console)
Solvix.Agent.exe enroll --server <url> --token <token> [--pin <sha256>]
Solvix.Agent.exe status                local enrollment / certificate / contact state
Solvix.Agent.exe inventory             collect and print inventory locally (no network)
Solvix.Agent.exe apply-update          privileged updater (scheduled task)
Solvix.Agent.exe reset                 delete device key, certificate and state
```

## Smoke test

`scripts/smoke-agent.sh <api-url> <agent-url> [agent-binary]` drives the commands above against a running
API (workspace with two stores → one token per store → three terminals → placement checks).

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

Sign `Solvix.Agent.exe` and the MSI with the organisation's code-signing certificate and put its
thumbprint into `Updates:TrustedPublisherThumbprints` of the shipped `appsettings.json`.
