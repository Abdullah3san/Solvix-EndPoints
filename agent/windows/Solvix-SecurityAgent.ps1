<#
.SYNOPSIS
    Solvix endpoint security agent for Windows: reports security posture and (only if explicitly enabled)
    carries out administrator-approved remediation.

.DESCRIPTION
    This script is NOT antivirus and NOT an EDR sensor. It reads the state of Windows' own security features through
    documented interfaces and reports it. It never scans files, inspects processes, blocks anything or sends file contents.

    Modes
      Collect  Collect and print the report as JSON. Nothing is sent. Use this to review exactly what would be reported.
      Report   Collect and send the report to the Solvix API.
      Poll     Fetch approved remediation commands and run them. Requires "allowRemediation": true in the config.
      Run      Report, then Poll. This is what the scheduled task runs.

    Remediation is off unless the device owner sets "allowRemediation": true in the local config file. Even then the
    agent runs only the six built-in actions listed in $script:Remediations, each with a parameter from a closed set;
    the server cannot make it run arbitrary commands.

    Requires Windows PowerShell 5.1 or PowerShell 7, run elevated (SYSTEM) so BitLocker, SMB and registry state can be read.

.PARAMETER ConfigPath
    JSON file: { "apiBaseUrl": "https://solvix.example.com", "deviceKey": "sxk1....", "allowRemediation": false }
    Protect it: only SYSTEM and Administrators should be able to read it (see README.md).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Collect', 'Report', 'Poll', 'Run')]
    [string]$Mode = 'Collect',

    [string]$ConfigPath = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Solvix\agent.json'),

    [string]$StatePath = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Solvix\agent-state.json')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:AgentVersion = '1.0.0'
$script:Errors = New-Object System.Collections.Generic.List[object]

# Event ids the agent may forward. Must match the server's allow-list; the server drops everything else anyway.
$script:EventFilters = @(
    @{ Log = 'Security'; Ids = @(1102, 4625, 4720, 4732) },
    @{ Log = 'System'; Ids = @(104) },
    @{ Log = 'Microsoft-Windows-Windows Defender/Operational'; Ids = @(1116, 1117, 5001) }
)

#region helpers

function Add-CollectionError {
    param([string]$Section, [string]$Message)
    $clean = ($Message -replace '[\r\n\t]+', ' ')
    if ($clean.Length -gt 400) { $clean = $clean.Substring(0, 400) }
    $script:Errors.Add([ordered]@{ section = $Section; message = $clean })
}

# Runs one collection section. Any failure becomes a recorded error and a $null section, which the server
# evaluates as "unknown" - never as a pass.
function Invoke-Section {
    param([string]$Name, [scriptblock]$Body)
    try { return & $Body } catch { Add-CollectionError -Section $Name -Message $_.Exception.Message; return $null }
}

function Get-RegistryValue {
    param([string]$Path, [string]$Name)
    $item = Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return $item.$Name
}

function Test-IsWindows {
    return ($PSVersionTable.PSVersion.Major -le 5) -or [bool]$IsWindows
}

#endregion

#region collection (read-only)

function Get-DefenderSection {
    if (-not (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue)) {
        throw 'Get-MpComputerStatus is not available (Microsoft Defender module missing).'
    }
    $s = Get-MpComputerStatus
    $threats = 0
    try { $threats = @(Get-MpThreat -ErrorAction Stop | Where-Object { $_.IsActive }).Count }
    catch { Add-CollectionError -Section 'defender.threats' -Message $_.Exception.Message; $threats = $null }

    $thirdParty = $null
    try {
        $av = @(Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop |
                Where-Object { $_.displayName -notlike '*Defender*' })
        $thirdParty = ($av.Count -gt 0)
    } catch { $thirdParty = $null } # Security Center does not exist on server SKUs; leave unknown.

    $neverScanned = 4294967295
    $quick = $s.QuickScanAge
    if ($null -ne $quick -and $quick -eq $neverScanned) { $quick = 9999 }

    [ordered]@{
        amRunningMode = [string]$s.AMRunningMode
        antivirusEnabled = [bool]$s.AntivirusEnabled
        realTimeProtectionEnabled = [bool]$s.RealTimeProtectionEnabled
        tamperProtected = [bool]$s.IsTamperProtected
        signatureAgeDays = [int]$s.AntivirusSignatureAge
        quickScanAgeDays = if ($null -eq $quick) { $null } else { [int][Math]::Min([double]$quick, 99999) }
        activeThreatCount = $threats
        signatureVersion = [string]$s.AntivirusSignatureVersion
        thirdPartyAntivirusRegistered = $thirdParty
    }
}

function Get-FirewallSection {
    $profiles = @(Get-NetFirewallProfile | ForEach-Object {
            [ordered]@{
                name = [string]$_.Name
                enabled = ([string]$_.Enabled -eq 'True')
                defaultInboundAction = [string]$_.DefaultInboundAction
            }
        })
    [ordered]@{ profiles = $profiles }
}

function Get-BitLockerSection {
    if (-not (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue)) {
        # The BitLocker cmdlets ship only with editions that include BitLocker.
        return [ordered]@{ available = $false; tpmPresent = $null; volumes = @() }
    }
    $tpm = $null
    try { $tpm = [bool](Get-Tpm -ErrorAction Stop).TpmPresent } catch { $tpm = $null }
    $volumes = @(Get-BitLockerVolume | ForEach-Object {
            [ordered]@{
                mountPoint = [string]$_.MountPoint
                volumeType = [string]$_.VolumeType
                protectionOn = ([string]$_.ProtectionStatus -in @('On', '1'))
                encryptionPercentage = [int]$_.EncryptionPercentage
                encryptionMethod = [string]$_.EncryptionMethod
                volumeStatus = [string]$_.VolumeStatus
            }
        })
    [ordered]@{ available = $true; tpmPresent = $tpm; volumes = $volumes }
}

function Get-LocalAdminSection {
    # Well-known SIDs make this independent of the OS language: S-1-5-32-544 = Administrators.
    $members = @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object {
            [ordered]@{ name = [string]$_.Name; objectClass = [string]$_.ObjectClass; sid = [string]$_.SID.Value }
        })
    $builtIn = $null
    $guest = $null
    try {
        $users = @(Get-LocalUser)
        $admin = $users | Where-Object { $_.SID.Value -like '*-500' } | Select-Object -First 1
        $g = $users | Where-Object { $_.SID.Value -like '*-501' } | Select-Object -First 1
        if ($admin) { $builtIn = [bool]$admin.Enabled }
        if ($g) { $guest = [bool]$g.Enabled }
    } catch { Add-CollectionError -Section 'localAdmins.accounts' -Message $_.Exception.Message }
    [ordered]@{ members = $members; builtInAdministratorEnabled = $builtIn; guestAccountEnabled = $guest }
}

function Get-UsbSection {
    $start = Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' 'Start'
    $policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'
    $denyWrite = Get-RegistryValue $policyKey 'Deny_Write'
    $denyRead = Get-RegistryValue $policyKey 'Deny_Read'
    [ordered]@{
        storageDriverDisabled = if ($null -eq $start) { $null } else { [int]$start -eq 4 }
        denyWrite = ($denyWrite -eq 1)  # an absent policy value means "not denied"
        denyRead = ($denyRead -eq 1)
    }
}

function Get-SoftwareSection {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $seen = @{}
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($p in $paths) {
        foreach ($e in @(Get-ItemProperty -Path $p -ErrorAction SilentlyContinue)) {
            $name = $e.PSObject.Properties['DisplayName']
            if ($null -eq $name -or [string]::IsNullOrWhiteSpace([string]$name.Value)) { continue }
            $sys = $e.PSObject.Properties['SystemComponent']
            if ($null -ne $sys -and $sys.Value -eq 1) { continue }
            $version = if ($e.PSObject.Properties['DisplayVersion']) { [string]$e.DisplayVersion } else { $null }
            $publisher = if ($e.PSObject.Properties['Publisher']) { [string]$e.Publisher } else { $null }
            $key = "$($name.Value)|$version"
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            $n = [string]$name.Value
            if ($n.Length -gt 300) { $n = $n.Substring(0, 300) }
            if ($items.Count -lt 5000) { $items.Add([ordered]@{ name = $n; version = $version; publisher = $publisher }) }
        }
    }
    return ,$items.ToArray()
}

function Get-ConfigurationSection {
    $smb1 = $null
    try { $smb1 = [bool](Get-SmbServerConfiguration -ErrorAction Stop).EnableSMB1Protocol }
    catch { Add-CollectionError -Section 'configuration.smb1' -Message $_.Exception.Message }

    $lua = Get-RegistryValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'EnableLUA'
    $deny = Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' 'fDenyTSConnections'
    $nla = Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' 'UserAuthentication'
    $noAuto = Get-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoUpdate'

    $secureBoot = $null
    try { $secureBoot = [bool](Confirm-SecureBootUEFI -ErrorAction Stop) } catch { $secureBoot = $null } # throws on legacy BIOS

    [ordered]@{
        smb1ServerEnabled = $smb1
        uacEnabled = if ($null -eq $lua) { $null } else { [int]$lua -eq 1 }
        rdpEnabled = if ($null -eq $deny) { $null } else { [int]$deny -eq 0 }
        rdpNlaRequired = if ($null -eq $nla) { $null } else { [int]$nla -eq 1 }
        secureBootEnabled = $secureBoot
        windowsUpdateDisabledByPolicy = ($noAuto -eq 1)
    }
}

# Short, fixed-format summaries only. Full event messages are never forwarded.
function Get-EventSection {
    param($State)
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($f in $script:EventFilters) {
        $since = (Get-Date).AddHours(-24)
        if ($State.eventCursor.PSObject.Properties[$f.Log]) { $since = [DateTime]::Parse($State.eventCursor.($f.Log)).ToUniversalTime().AddSeconds(1) }
        try {
            $events = @(Get-WinEvent -FilterHashtable @{ LogName = $f.Log; Id = $f.Ids; StartTime = $since } -MaxEvents 200 -ErrorAction Stop)
        } catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { continue }
            Add-CollectionError -Section "events.$($f.Log)" -Message $_.Exception.Message
            continue
        }
        foreach ($e in $events) {
            $summary = switch ($e.Id) {
                1102 { 'Security audit log cleared' }
                104 { 'System event log cleared' }
                4625 { 'Failed logon attempt' }
                4720 { 'Local user account created' + $(try { ': ' + $e.Properties[0].Value } catch { '' }) }
                4732 { 'Member added to a local group' }
                1116 { 'Defender detected malware' + $(try { ': ' + $e.Properties[7].Value } catch { '' }) }
                1117 { 'Defender took action on malware' }
                5001 { 'Defender real-time protection disabled' }
                default { "Event $($e.Id)" }
            }
            $group = $null
            if ($e.Id -eq 4732) { try { $group = [string]$e.Properties[2].Value } catch { $group = $null } }
            if ($summary.Length -gt 300) { $summary = $summary.Substring(0, 300) }
            if ($out.Count -lt 500) {
                $out.Add([ordered]@{
                        log = $f.Log
                        recordId = [long]$e.RecordId
                        eventId = [int]$e.Id
                        provider = [string]$e.ProviderName
                        timeCreatedUtc = $e.TimeCreated.ToUniversalTime().ToString('o')
                        summary = $summary
                        targetGroup = $group
                    })
            }
        }
        if ($events.Count -gt 0) {
            $latest = ($events | Sort-Object TimeCreated -Descending | Select-Object -First 1).TimeCreated.ToUniversalTime().ToString('o')
            $State.eventCursor | Add-Member -NotePropertyName $f.Log -NotePropertyValue $latest -Force
        }
    }
    return ,$out.ToArray()
}

function New-PostureReport {
    param($State)
    $script:Errors.Clear()

    $os = Invoke-Section 'os' { Get-CimInstance -ClassName Win32_OperatingSystem }
    $cs = Invoke-Section 'computer' { Get-CimInstance -ClassName Win32_ComputerSystem }

    $report = [ordered]@{
        schemaVersion = 1
        collectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        agentVersion = $script:AgentVersion
        hostname = [string]$env:COMPUTERNAME
        osCaption = if ($os) { [string]$os.Caption } else { $null }
        osVersion = if ($os) { [string]$os.Version } else { $null }
        isDomainJoined = if ($cs) { [bool]$cs.PartOfDomain } else { $null }
        defender = Invoke-Section 'defender' { Get-DefenderSection }
        firewall = Invoke-Section 'firewall' { Get-FirewallSection }
        bitLocker = Invoke-Section 'bitLocker' { Get-BitLockerSection }
        localAdmins = Invoke-Section 'localAdmins' { Get-LocalAdminSection }
        usb = Invoke-Section 'usb' { Get-UsbSection }
        software = Invoke-Section 'software' { Get-SoftwareSection }
        configuration = Invoke-Section 'configuration' { Get-ConfigurationSection }
        events = Invoke-Section 'events' { Get-EventSection -State $State }
        errors = $script:Errors.ToArray()
    }
    return $report
}

#endregion

#region state and configuration

function Read-AgentState {
    $state = [pscustomobject]@{ eventCursor = [pscustomobject]@{} }
    if (Test-Path -LiteralPath $StatePath) {
        try { $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json } catch { }
        if (-not $state.PSObject.Properties['eventCursor']) { $state | Add-Member -NotePropertyName eventCursor -NotePropertyValue ([pscustomobject]@{}) }
    }
    return $state
}

function Save-AgentState {
    param($State)
    $dir = Split-Path -Parent $StatePath
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StatePath -Encoding UTF8
}

function Read-AgentConfig {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Config file not found: $ConfigPath" }
    $cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if (-not $cfg.apiBaseUrl -or -not $cfg.deviceKey) { throw 'Config must contain apiBaseUrl and deviceKey.' }
    $uri = [Uri]$cfg.apiBaseUrl
    if ($uri.Scheme -ne 'https' -and $uri.Host -notin @('localhost', '127.0.0.1')) {
        throw 'apiBaseUrl must use https (plain http is only accepted for localhost development).'
    }
    if ($cfg.deviceKey -notmatch '^sxk1\.[0-9a-f]{32}\.[0-9a-f]{32}\.[A-Za-z0-9_-]{43}$') { throw 'deviceKey does not look like a Solvix device key.' }
    if (Test-IsWindows) {
        # Warn if ordinary users could read the device key.
        try {
            $acl = Get-Acl -LiteralPath $ConfigPath
            $open = $acl.Access | Where-Object { $_.IdentityReference -match 'Users$|Everyone$|Authenticated Users$' -and $_.FileSystemRights -match 'Read|FullControl' -and $_.AccessControlType -eq 'Allow' }
            if ($open) { Write-Warning "The config file $ConfigPath is readable by ordinary users; restrict it to SYSTEM and Administrators." }
        } catch { }
    }
    return $cfg
}

#endregion

#region server communication

function Invoke-SolvixApi {
    param($Config, [string]$Method, [string]$Path, $Body)
    $headers = @{ 'X-Solvix-Device-Key' = [string]$Config.deviceKey }
    $uri = ($Config.apiBaseUrl.TrimEnd('/')) + $Path
    $args = @{ Uri = $uri; Method = $Method; Headers = $headers; TimeoutSec = 60; ContentType = 'application/json' }
    if ($null -ne $Body) { $args.Body = ($Body | ConvertTo-Json -Depth 10 -Compress) }
    return Invoke-RestMethod @args
}

function Send-PostureReport {
    param($Config, $Report)
    return Invoke-SolvixApi -Config $Config -Method Post -Path '/api/v1/security/ingest/posture' -Body $Report
}

#endregion

#region remediation (disabled unless the device owner opts in)

# The complete list of changes this agent can make. Each is implemented locally; the server only picks a key
# and (for some) one value from a closed set. There is no code path that executes server-supplied text.
$script:Remediations = @{
    'defender.update_signatures' = {
        param($p)
        Update-MpSignature
    }
    'defender.quick_scan' = {
        param($p)
        Start-MpScan -ScanType QuickScan -AsJob | Out-Null
    }
    'defender.enable_realtime' = {
        param($p)
        Set-MpPreference -DisableRealtimeMonitoring $false
    }
    'firewall.enable_profile' = {
        param($p)
        if ($p -notin @('Domain', 'Private', 'Public')) { throw "Unsupported firewall profile '$p'." }
        Set-NetFirewallProfile -Profile $p -Enabled True
    }
    'usb.apply_storage_policy' = {
        param($p)
        if ($p -notin @('BlockWrite', 'BlockAll')) { throw "Unsupported USB mode '$p'." }
        $key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name 'Deny_Write' -Value 1 -Type DWord
        if ($p -eq 'BlockAll') { Set-ItemProperty -Path $key -Name 'Deny_Read' -Value 1 -Type DWord }
    }
    'config.disable_smb1' = {
        param($p)
        Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force
    }
}

function Invoke-ApprovedCommands {
    param($Config)
    $allow = ($Config.PSObject.Properties['allowRemediation'] -and $Config.allowRemediation -eq $true)
    $commands = @(Invoke-SolvixApi -Config $Config -Method Get -Path '/api/v1/security/ingest/commands' -Body $null)
    foreach ($c in $commands) {
        $result = [ordered]@{ succeeded = $false; message = '' }
        if (-not $allow) {
            $result.message = 'Remediation is disabled on this device (allowRemediation is not true in the local config).'
        } elseif (-not $script:Remediations.ContainsKey([string]$c.action)) {
            $result.message = "Unknown action '$($c.action)'; this agent version does not support it."
        } elseif ($PSCmdlet.ShouldProcess($env:COMPUTERNAME, "$($c.action) $($c.parameter)")) {
            try {
                & $script:Remediations[[string]$c.action] ([string]$c.parameter)
                $result.succeeded = $true
                $result.message = 'Completed.'
            } catch {
                $result.message = $_.Exception.Message
            }
        } else {
            $result.message = 'Skipped by -WhatIf.'
        }
        if ($result.message.Length -gt 400) { $result.message = $result.message.Substring(0, 400) }
        Invoke-SolvixApi -Config $Config -Method Post -Path "/api/v1/security/ingest/commands/$($c.id)/result" -Body $result | Out-Null
    }
}

#endregion

#region entry point

if ($MyInvocation.InvocationName -ne '.') {
    $state = Read-AgentState
    $report = New-PostureReport -State $state

    switch ($Mode) {
        'Collect' {
            $report | ConvertTo-Json -Depth 10
        }
        'Report' {
            $config = Read-AgentConfig
            $receipt = Send-PostureReport -Config $config -Report $report
            Save-AgentState -State $state   # advance the event cursor only after a successful send
            Write-Output ("Reported. Score {0} ({1}); {2} command(s) waiting." -f $receipt.score, $receipt.band, $receipt.pendingCommands)
        }
        'Poll' {
            Invoke-ApprovedCommands -Config (Read-AgentConfig)
        }
        'Run' {
            $config = Read-AgentConfig
            $receipt = Send-PostureReport -Config $config -Report $report
            Save-AgentState -State $state
            Write-Output ("Reported. Score {0} ({1}); {2} command(s) waiting." -f $receipt.score, $receipt.band, $receipt.pendingCommands)
            Invoke-ApprovedCommands -Config $config
        }
    }
}

#endregion
