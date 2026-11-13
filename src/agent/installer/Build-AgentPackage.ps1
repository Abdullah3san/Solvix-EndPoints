#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the agent deployment folder: self-contained Solvix.Agent.exe + scripts.
    Sign Solvix.Agent.exe (and the MSI built from this folder) with the organisation's
    code-signing certificate; agents only accept updates from pinned publishers.
#>
param(
    [string] $Configuration = 'Release',
    [string] $Output = (Join-Path $PSScriptRoot '..\..\..\artifacts\agent')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Solvix.Agent\Solvix.Agent.csproj'
dotnet publish $project -c $Configuration -r win-x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item (Join-Path $PSScriptRoot 'Install-SolvixAgent.ps1'), (Join-Path $PSScriptRoot 'Uninstall-SolvixAgent.ps1') -Destination $Output -Force
Get-ChildItem $Output -Filter *.pdb | Remove-Item
Write-Host "Agent package written to $Output"
