#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Solvix agent service, its updater task and (optionally) its device identity.
.PARAMETER RemoveData
    Also delete the device key, certificate, state and logs. The device record in Solvix
    remains until an administrator retires or deletes it.
#>
[CmdletBinding()]
param(
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Solvix\Agent'),
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$Exe = Join-Path $InstallPath 'Solvix.Agent.exe'
$DataPath = Join-Path $env:ProgramData 'Solvix\Agent'

if (Get-Service -Name SolvixRemoteExecutor -ErrorAction SilentlyContinue) {
    Stop-Service -Name SolvixRemoteExecutor -Force -ErrorAction SilentlyContinue
    & sc.exe delete SolvixRemoteExecutor | Out-Null
}
if (Get-Service -Name SolvixAgent -ErrorAction SilentlyContinue) {
    Stop-Service -Name SolvixAgent -Force -ErrorAction SilentlyContinue
    & sc.exe delete SolvixAgent | Out-Null
}
Unregister-ScheduledTask -TaskName 'Solvix Agent Updater' -TaskPath '\Solvix\' -Confirm:$false -ErrorAction SilentlyContinue

if ($RemoveData) {
    if (Test-Path $Exe) { & $Exe reset | Out-Null }   # deletes the CNG/TPM device key
    Remove-Item -Recurse -Force $DataPath -ErrorAction SilentlyContinue
}
Remove-Item -Recurse -Force $InstallPath -ErrorAction SilentlyContinue
Write-Host 'Solvix Agent removed.'
