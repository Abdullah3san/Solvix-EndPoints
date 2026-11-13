# Phase 03 – Windows endpoint management: technical summary

Authorized Windows computers enroll into Solvix EndPoints and report their inventory and health.
Design details: `docs/ARCHITECTURE.md` (section *Device management*), agent operations:
`src/agent/README.md`. Remote command execution is intentionally **not** implemented.

## Scope delivered

### Windows agent (C#, .NET 10 Worker Service)

| # | Module | Where |
|---|--------|-------|
| 1 | Device identity | `Agent.Core/Inventory/DeviceIdentityProvider.cs`; server-assigned id bound to an X.509 certificate |
| 2 | Secure enrollment | `Agent.Core/Enrollment/EnrollmentCoordinator.cs`, `Security/BootstrapTokenStore.cs` (DPAPI) |
| 3 | Hardware inventory | `Agent.Windows/Inventory/HardwareCollectors.cs` (`SystemCollector`, `BiosCollector`) |
| 4 | Operating system inventory | `Agent.Windows/Inventory/OperatingSystemCollector.cs` |
| 5 | Software inventory | `Agent.Windows/Inventory/SoftwareCollector.cs` |
| 6 | Network interface inventory | `Agent.Core/Inventory/PortableCollectors.cs` (`NetworkInventoryCollector`) |
| 7 | Disk inventory | `DiskCollector` (physical), `VolumeInventoryCollector` (logical) |
| 8 | Processor information | `ProcessorCollector` |
| 9 | Memory information | `MemoryCollector` |
| 10 | Windows version information | `Agent.Core/Inventory/WindowsVersionInfo.cs` |
| 11 | Agent health | `Agent.Core/Health/AgentHealthMonitor.cs` |
| 12 | Secure communication | `Agent.Core/Communication/MutualTlsTransport` + `AgentApiClient` |
| 13 | Agent update management | `Agent.Core/Updates/UpdateManager.cs`, `Agent.Windows/Updates/AuthenticodeVerifier.cs` |
| 14 | Local diagnostic logging | `Agent/Program.cs` (rolling JSON files + Event Log), `status` / `inventory` commands |

### Device information collected

| Item | Source |
|------|--------|
| Hostname | `Environment.MachineName` |
| Serial number | `Win32_BIOS.SerialNumber` (fallback `Win32_ComputerSystemProduct.IdentifyingNumber`) |
| Manufacturer, model | `Win32_ComputerSystem` |
| BIOS information | `Win32_BIOS`, `GetFirmwareType`, Secure Boot registry state |
| CPU | `Win32_Processor` |
| RAM | `Win32_ComputerSystem`, `Win32_OperatingSystem`, `Win32_PhysicalMemory` |
| Disk drives | `MSFT_PhysicalDisk` (fallback `Win32_DiskDrive`), `DriveInfo` volumes |
| Operating system, OS version | `Win32_OperatingSystem` + `HKLM\…\Windows NT\CurrentVersion` (DisplayVersion, UBR, EditionID) |
| IP / MAC addresses, network adapters | `System.Net.NetworkInformation` |
| Installed applications | Uninstall registry keys (64-bit, 32-bit, per-user) |
| Last boot time | `Win32_OperatingSystem.LastBootUpTime` |
| Device enrollment status | `devices.status`, credentials, enrollment count/time; agent `status` command |

### Backend (`Solvix.Modules.Devices`)

| Requirement | Implementation |
|-------------|----------------|
| Device enrollment API | `POST /api/v1/agent/enroll` |
| Device identity | device CA (`Security/DeviceCertificateAuthority.cs`), `device_credentials`, mTLS auth handler |
| Device registration | `Device.Enroll` / `ReEnroll`, duplicate detection by hardware fingerprint |
| Device inventory API | `PUT /api/v1/agent/inventory`, `GET /api/v1/devices/{id}/inventory` |
| Device group management | `/api/v1/device-groups` (+ membership endpoints), `PUT /devices/{id}/groups` |
| Device assignment | `PUT /api/v1/devices/{id}/assignment` (member, branch, department; DB-level SET NULL FKs) |
| Device details | `GET /api/v1/devices/{id}` (summary, health, groups, assignment, credentials) |
| Agent version tracking | `devices.agent_version` + change timestamp, `GET /devices/stats`, release catalog, update check |

### Frontend (Next.js)

*Endpoints → Devices* (stats, filters, list), device details (overview, hardware, network, storage,
software, security tabs; edit, assign, groups, refresh inventory, retire, delete), *Device groups*,
*Enrollment* (admin only; token shown once with the install command), dashboard tile.

## Security

| Requirement | How |
|-------------|-----|
| Unique device identities / secure device credentials | Server-assigned UUIDv7 bound to an X.509 client certificate issued by the platform device CA; SAN `urn:solvix:device:{tenant}:{device}` |
| TLS | TLS 1.2/1.3 only on the agent; dedicated Kestrel HTTPS endpoint (or trusted TLS proxy) |
| Short-lived enrollment credentials | Tokens: 24 h default, 7 days max, use-limited, revocable, SHA-256 at rest, shown once |
| No shared permanent secret in the installer | Installer contains no secret; token supplied at install time, DPAPI-encrypted, deleted after use |
| Protect device credentials | Private key generated on the device, non-exportable CNG machine key, TPM-backed when available; server stores only public certificates; data directory ACL’d to SYSTEM/Admins/service |
| Certificate-based authentication | `DeviceCertificate` scheme: chain to device CA only, validity, EKU, then DB check (not revoked, device + tenant active) under RLS |
| Least privilege | Service runs as `NT SERVICE\SolvixAgent` with restricted privileges; only the updater task runs as SYSTEM and only installs re-verified, publisher-pinned MSIs; admin-only writes in the API |
| Never execute arbitrary server instructions | Contracts carry schedules (clamped) and two booleans mapped to built-in actions; updates are offers validated against local policy (HTTPS, allow-listed host, SHA-256, newer version, Authenticode publisher pin) |
| Tenant isolation | Tenant from the CA-signed certificate or the token; RLS on all 6 new tables; tenant-safe composite FKs; per-tenant duplicate detection |

## Agent communication

Outbound only. Heartbeat (default 60 s) with health; inventory on change/interval/request;
certificate renewal at the server's recommendation or 70 % of lifetime (old cert valid until the
new one is used); exponential backoff with jitter (5 s → 15 min); automatic re-enrollment after
repeated authentication failures when a new token is available; TLS connections recycled whenever
the credential changes.

## Verification

All suites run against real PostgreSQL 16 and Redis 7.

| Suite | Result |
|-------|--------|
| `Solvix.UnitTests` | **44 / 44** (unchanged Phase 01 suite) |
| `Solvix.IntegrationTests` | **71 / 71** (31 Phase 01 + 40 Phase 03) |
| `Solvix.Agent.Tests` | **68 / 68** |
| Frontend `npm run check` | typecheck + lint clean, **16 / 16** tests; `next build` succeeds |
| `dotnet build` | 0 warnings (warnings are errors) |

Requested test areas:

| Area | Tests |
|------|-------|
| Enrollment | CA-signed cert with correct SAN/EKU/validity; single- and multi-use tokens; token group assignment; token never returned by list API |
| Duplicate devices | same hardware re-enrolls into the same record and revokes old credentials; placeholder UUID/serial never merges; same hardware in two tenants stays separate; retired device blocked until deleted; failed attempts do not consume the token |
| Invalid credentials | malformed/forged/re-targeted tokens; tampered and weak CSRs; no cert; self-signed cert with copied identity; foreign CA; CA-signed cert never issued; revoked; deleted device; suspended tenant; user JWT ↔ device cert cross-use |
| Expired credentials | expired enrollment token (`enrollment_token_expired`); expired and not-yet-valid certificates; renewal window signalling; renewal hand-over |
| Tenant isolation | lists/stats/ids of devices, groups, tokens → 404; cross-tenant group, branch, user and device references; CA-signed certificate re-targeted to another tenant; raw-SQL RLS checks; migration test covers every new table |
| Agent disconnection | API: offline after missed heartbeats, online on reconnect with reported failure count; agent: exponential/capped backoff, recovery, credential loss and re-enrollment; end-to-end with the real agent core against the real API |
| Real mTLS | `AgentMutualTlsTests` runs the API on Kestrel with the `Agents` HTTPS endpoint: handshake with client certificate, rogue cert rejected, forwarded header ignored |

Additional manual verification performed in this environment:

* the real agent host process (Linux development code path, file key store) against the real API
  over mutual TLS with certificate pinning: enrollment, heartbeats, inventory, `status`/`enroll`
  commands; a wrong pin is refused at TLS. This found and fixed a defect (pooled pre-enrollment
  TLS connection reused without client certificate), now covered by a unit test;
* Docker images built; `migrator` applied all 4 migrations; the containerized API generated its
  device CA on the `solvix-pki` volume and served an agent over mTLS (the build used a scratch copy of
  the Dockerfile that trusts this sandbox's TLS-intercepting proxy CA; the committed Dockerfile is unchanged in that respect);
* `dotnet publish -r win-x64` produces a self-contained single-file `Solvix.Agent.exe`;
* the UI driven in Chromium (login → devices → details tabs → groups → token creation) with no console errors.

### Not verified here (no Windows machine available)

* The Windows-only code paths were compiled (and their pure mapping logic unit-tested) but **not
  executed**: WMI/registry collectors, CNG/TPM key creation under the virtual service account,
  SChannel client authentication with a CNG key, DPAPI, `WinVerifyTrust`, msiexec, Event Log.
* `Install-SolvixAgent.ps1` / `Uninstall-SolvixAgent.ps1` were not run (no PowerShell/Windows).
* No MSI (WiX) project is included yet; `Build-AgentPackage.ps1` produces the folder an MSI would wrap.

Recommended next step: run the integration checklist above on Windows 10, Windows 11 and Windows
Server 2016/2022 VMs (with and without TPM), then add a WiX MSI and CI code signing.

## Database changes

Migrations `20261112200553_DeviceManagement` (EF) and `20261112200600_DeviceManagementSecurity`
(RLS on `devices`, `device_credentials`, `device_inventories`, `device_groups`,
`device_group_memberships`, `enrollment_tokens`; assignment FKs). Both roll back cleanly
(`MigrationTests.Migrations_can_be_rolled_back_and_reapplied`).
