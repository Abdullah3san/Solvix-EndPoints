<#
.SYNOPSIS
    Removes the Solvix endpoint agent. Retire the device in Solvix first so its
    credential is revoked. Settings applied by policies are left as they are;
    remove the policy assignments (with RemovalBehavior=Revert) before
    uninstalling to restore the original values.
#>
[CmdletBinding()]
param(
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Solvix\Agent'),
    [switch] $RemoveState
)

$ErrorActionPreference = 'Stop'
$serviceName = 'SolvixAgent'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force
    & sc.exe delete $serviceName | Out-Null
}
Remove-Item -Path $InstallPath -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveState) {
    Remove-Item -Path (Join-Path $env:ProgramData 'Solvix\Agent') -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Solvix agent removed.'
