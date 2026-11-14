#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the self-contained agent (Solvix.Agent.exe). There is no separate installer script:
    the exe installs itself (double-click, or `Solvix.Agent.exe install --silent`).

    Sign Solvix.Agent.exe with the organisation's code-signing certificate, then place it (with
    appsettings.json) in the folder the API serves as the agent package
    (Solvix:Devices:AgentPackage:Directory, /opt/solvix/agent-package in Docker). The API adds the
    enrollment.json and serves the exe byte-for-byte, so the signature stays valid. Agents only accept
    updates from the publisher thumbprints pinned in Updates:TrustedPublisherThumbprints.
#>
param(
    [string] $Configuration = 'Release',
    [string] $Output = (Join-Path $PSScriptRoot '..\..\..\artifacts\agent')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Solvix.Agent\Solvix.Agent.csproj'
dotnet publish $project -c $Configuration -r win-x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Get-ChildItem $Output -Filter *.pdb | Remove-Item
Write-Host "Agent published to $Output - sign Solvix.Agent.exe before deploying it."
