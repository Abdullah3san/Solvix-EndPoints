#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the Solvix endpoint agent as a least-privilege Windows service and enrolls the device.

.DESCRIPTION
    The installer package contains NO secrets. The enrollment token is a short-lived,
    use-limited credential created by a tenant administrator in Solvix and passed at
    install time (parameter, or the SOLVIX_ENROLLMENT_TOKEN environment variable for
    deployment tools). The agent stores it encrypted (DPAPI, machine scope), uses it
    once to obtain a device certificate and deletes it.

    What this script does:
      1. checks the Windows version (Windows 10/11, Windows Server 2016/2019/2022/2025);
      2. copies the agent to Program Files;
      3. creates %ProgramData%\Solvix\Agent readable only by SYSTEM, Administrators and the service;
      4. registers the "Solvix Agent" event log source;
      5. creates the "SolvixAgent" service running as the virtual account NT SERVICE\SolvixAgent
         (no administrative rights) with restart-on-failure;
      6. registers the "Solvix Agent Updater" scheduled task (SYSTEM) that installs only
         updates the agent has staged AND that pass hash + Authenticode publisher checks again;
      7. creates the "SolvixSoftwareExecutor" service (LocalSystem) and its data directory
         %ProgramData%\Solvix\Executor (SYSTEM and Administrators only). It listens on a
         local named pipe that only SYSTEM and NT SERVICE\SolvixAgent may open, accepts only
         signed software install/uninstall commands that it verifies itself, and has no
         network function. -DisableSoftwareDeployment skips it;
      8. stores the server URL and enrollment token and starts the services.

.EXAMPLE
    .\Install-SolvixAgent.ps1 -ServerUrl https://agents.solvix.example:8443/ -EnrollmentToken svxe_...

.EXAMPLE
    # Self-hosted server with a private TLS certificate: pin its SHA-256 thumbprint.
    .\Install-SolvixAgent.ps1 -ServerUrl https://solvix.corp.local:8443/ -EnrollmentToken svxe_... -ServerCertificateSha256 3F2A...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string] $ServerUrl,

    [string] $EnrollmentToken = $env:SOLVIX_ENROLLMENT_TOKEN,

    [ValidatePattern('^([0-9A-Fa-f]{2}:?){32}$')]
    [string] $ServerCertificateSha256,

    [string] $SourcePath = $PSScriptRoot,

    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Solvix\Agent'),

    # Virtual service account by default (least privilege). Use LocalSystem only if the
    # device key cannot be created under the virtual account on a hardened image.
    [ValidateSet('NT SERVICE\SolvixAgent', 'LocalSystem')]
    [string] $ServiceAccount = 'NT SERVICE\SolvixAgent',

    # Do not install the privileged software executor: software deployments then fail on this
    # device with software_executor_unavailable (detection still works).
    [switch] $DisableSoftwareDeployment
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'SolvixAgent'
$TaskName = 'Solvix Agent Updater'
$ExecutorServiceName = 'SolvixSoftwareExecutor'
$DataPath = Join-Path $env:ProgramData 'Solvix\Agent'
$ExecutorDataPath = Join-Path $env:ProgramData 'Solvix\Executor'
$Exe = Join-Path $InstallPath 'Solvix.Agent.exe'

if ([string]::IsNullOrWhiteSpace($EnrollmentToken)) {
    throw 'An enrollment token is required (-EnrollmentToken or SOLVIX_ENROLLMENT_TOKEN).'
}

# 1. Supported Windows versions --------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$build = [int]$os.BuildNumber
$isServer = $os.ProductType -ne 1
if (($isServer -and $build -lt 14393) -or (-not $isServer -and $build -lt 10240)) {
    throw "Unsupported Windows version: $($os.Caption) (build $build). Supported: Windows 10, Windows 11, Windows Server 2016 or later."
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'The Solvix agent requires 64-bit Windows.'
}

# 2. Binaries --------------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $SourcePath 'Solvix.Agent.exe'))) {
    throw "Solvix.Agent.exe not found in $SourcePath."
}
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
}
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item -Path (Join-Path $SourcePath 'Solvix.Agent.exe'), (Join-Path $SourcePath 'appsettings.json') -Destination $InstallPath -Force

# 3. Data directory: SYSTEM + Administrators full control, service account modify, nobody else.
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
$serviceIdentity = if ($ServiceAccount -eq 'LocalSystem') { '*S-1-5-18' } else { $ServiceAccount }
& icacls.exe $DataPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
# (The virtual service account exists only once the service does; its ACL entry is added in step 5.)

# 4. Event log source --------------------------------------------------------------------------
if (-not [System.Diagnostics.EventLog]::SourceExists('Solvix Agent')) {
    New-EventLog -LogName Application -Source 'Solvix Agent'
}

# 5. Service ---------------------------------------------------------------------------------
if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$Exe`"" -DisplayName 'Solvix Agent' `
        -Description 'Reports device inventory and health to Solvix EndPoints. Outbound HTTPS only.' `
        -StartupType Automatic | Out-Null
}
& sc.exe config $ServiceName obj= "$ServiceAccount" | Out-Null
& sc.exe sidtype $ServiceName unrestricted | Out-Null
& sc.exe privs $ServiceName 'SeChangeNotifyPrivilege/SeCreateGlobalPrivilege' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null
if ($ServiceAccount -ne 'LocalSystem') {
    & icacls.exe $DataPath /grant:r "$($serviceIdentity):(OI)(CI)M" | Out-Null
}

# 5b. Remote Executor: the only component that may restart, shut down, sign off or lock the device.
# Runs as LocalSystem, listens on a local pipe reachable only by SYSTEM, Administrators and the agent
# service, and re-verifies every signed command itself. Its state directory is NOT readable or writable
# by the agent service account (inheritance removed, no entry for it).
$ExecutorService = 'SolvixRemoteExecutor'
$ExecutorData = Join-Path $DataPath 'executor'
New-Item -ItemType Directory -Force -Path $ExecutorData | Out-Null
& icacls.exe $ExecutorData /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if (-not (Get-Service -Name $ExecutorService -ErrorAction SilentlyContinue)) {
    New-Service -Name $ExecutorService -BinaryPathName "`"$Exe`" executor" -DisplayName 'Solvix Remote Executor' `
        -Description 'Performs restart, shutdown, sign-off and lock requested by Solvix EndPoints. Accepts only signed commands from the local Solvix Agent.' `
        -StartupType Automatic | Out-Null
}
& sc.exe config $ExecutorService obj= 'LocalSystem' | Out-Null
& sc.exe failure $ExecutorService reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# 6. Privileged updater: installs only staged packages that pass hash + publisher checks again.
$action = New-ScheduledTaskAction -Execute $Exe -Argument 'apply-update'
$triggers = @(
    (New-ScheduledTaskTrigger -AtStartup),
    (New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Hours 1))
)
$principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -StartWhenAvailable -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $TaskName -TaskPath '\Solvix\' -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null

# 7. Software executor (Phase 07): LocalSystem service, local named pipe only ------------------
$executor = Get-Service -Name $ExecutorServiceName -ErrorAction SilentlyContinue
if ($executor -and $executor.Status -ne 'Stopped') {
    Stop-Service -Name $ExecutorServiceName -Force
}
if ($DisableSoftwareDeployment) {
    if ($executor) { & sc.exe delete $ExecutorServiceName | Out-Null }
}
else {
    # Pinned keys, identity, replay records and package copies: SYSTEM and Administrators only, never the agent.
    New-Item -ItemType Directory -Force -Path $ExecutorDataPath | Out-Null
    & icacls.exe $ExecutorDataPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if (-not $executor) {
        New-Service -Name $ExecutorServiceName -BinaryPathName "`"$Exe`" software-executor" -DisplayName 'Solvix Software Executor' `
            -Description 'Runs software installations approved in Solvix EndPoints after verifying their signed command, checksum and publisher. No network access.' `
            -StartupType Automatic | Out-Null
    }
    & sc.exe config $ExecutorServiceName obj= LocalSystem | Out-Null
    & sc.exe sidtype $ExecutorServiceName unrestricted | Out-Null
    & sc.exe failure $ExecutorServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
}

# 8. Enrollment configuration + start ---------------------------------------------------------
$enrollArgs = @('enroll', '--server', $ServerUrl)
if ($ServerCertificateSha256) { $enrollArgs += @('--pin', ($ServerCertificateSha256 -replace ':', '')) }
$env:SOLVIX_ENROLLMENT_TOKEN = $EnrollmentToken   # passed via environment, not the command line
try {
    & $Exe @enrollArgs
    if ($LASTEXITCODE -ne 0) { throw "Agent enrollment configuration failed ($LASTEXITCODE)." }
}
finally {
    Remove-Item Env:\SOLVIX_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue
}

Start-Service -Name $ExecutorService
Start-Service -Name $ServiceName
if (-not $DisableSoftwareDeployment) { Start-Service -Name $ExecutorServiceName }
Write-Host "Solvix Agent installed. Check enrollment with: & '$Exe' status"
