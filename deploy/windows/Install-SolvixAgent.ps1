<#
.SYNOPSIS
    Installs and enrols the Solvix endpoint agent as a Windows service.

.DESCRIPTION
    Copies the published agent to %ProgramFiles%\Solvix\Agent, enrols the device
    once (the enrolment token is passed on the command line and never written to
    disk; the resulting device credential is stored with DPAPI under
    %ProgramData%\Solvix\Agent, readable only by SYSTEM and Administrators),
    then registers the "SolvixAgent" service (LocalSystem, delayed automatic
    start, restart on failure) and starts it.

    Run from an elevated PowerShell, or deploy through Intune/ConfigMgr/GPO.

.EXAMPLE
    .\Install-SolvixAgent.ps1 -ServerUrl https://solvix.example.com -EnrollmentToken sxe_... -SourcePath .\publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [uri] $ServerUrl,
    [Parameter(Mandatory)] [string] $EnrollmentToken,
    [string] $SourcePath = (Join-Path $PSScriptRoot 'publish'),
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Solvix\Agent')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'SolvixAgent'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
if ($ServerUrl.Scheme -ne 'https') {
    throw 'ServerUrl must use https.'
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force

@{ Solvix = @{ Agent = @{ ServerUrl = $ServerUrl.AbsoluteUri } } } |
    ConvertTo-Json -Depth 5 |
    Set-Content -Path (Join-Path $InstallPath 'appsettings.Production.json') -Encoding UTF8

$exe = Join-Path $InstallPath 'Solvix.Agent.exe'

# Enrolment must run as SYSTEM so the DPAPI-protected credential is readable by the service.
$taskName = "SolvixAgentEnroll-$([guid]::NewGuid().ToString('N'))"
$action = New-ScheduledTaskAction -Execute $exe -Argument "enroll --server $($ServerUrl.AbsoluteUri) --token $EnrollmentToken" -WorkingDirectory $InstallPath
Register-ScheduledTask -TaskName $taskName -Action $action -User 'NT AUTHORITY\SYSTEM' -RunLevel Highest | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    do { Start-Sleep -Seconds 2; $info = Get-ScheduledTaskInfo -TaskName $taskName } while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running')
    if ($info.LastTaskResult -ne 0) { throw "Enrolment failed (exit code $($info.LastTaskResult)). See the 'Solvix Agent' event log source." }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'Solvix Endpoint Agent' `
        -Description 'Solvix patch and configuration management agent.' -StartupType Automatic | Out-Null
}
& sc.exe config $serviceName start= delayed-auto obj= LocalSystem | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
Start-Service -Name $serviceName
Write-Host "Solvix agent installed and running."
