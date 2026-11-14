# Solvix Windows agent

`Solvix-SecurityAgent.ps1` reports the security posture of a Windows device to Solvix and, **only if the device owner opts in**,
carries out remediation that an administrator approved in Solvix.

## What it is and is not

* It **reads** state through supported Windows interfaces: `Get-MpComputerStatus` / `Get-MpThreat` (Defender),
  `Get-NetFirewallProfile`, `Get-BitLockerVolume`, `Get-LocalGroupMember`/`Get-LocalUser`, the documented removable-storage
  policy registry values, the Uninstall registry keys, `Get-SmbServerConfiguration`, `Confirm-SecureBootUEFI`, and `Get-WinEvent`
  for eight allow-listed event ids.
* It is **not antivirus or EDR**. It does not scan files, watch processes, block anything or upload file contents. Malware
  information is only what Microsoft Defender itself reports.
* Event messages are never forwarded, only a short fixed-format summary per allow-listed event.

## Review before you deploy

```powershell
# Prints exactly what would be sent. Sends nothing.
.\Solvix-SecurityAgent.ps1 -Mode Collect
```

## Install

1. In Solvix, enrol the device (Security > Devices > Enrol). The device key is shown once.
2. Create `C:\ProgramData\Solvix\agent.json`:
   ```json
   { "apiBaseUrl": "https://solvix.example.com", "deviceKey": "sxk1....", "allowRemediation": false }
   ```
3. Restrict the file to SYSTEM and Administrators (the key authenticates the device):
   ```powershell
   icacls C:\ProgramData\Solvix\agent.json /inheritance:r /grant:r "SYSTEM:(R)" "Administrators:(R)"
   ```
4. Run hourly as SYSTEM:
   ```powershell
   $action  = New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy RemoteSigned -File C:\Solvix\Solvix-SecurityAgent.ps1 -Mode Run'
   $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 1)
   Register-ScheduledTask -TaskName 'Solvix Security Agent' -Action $action -Trigger $trigger -User 'SYSTEM' -RunLevel Highest
   ```
   Sign the script with your code-signing certificate and use `-ExecutionPolicy AllSigned` where your policy requires it.

## Remediation (off by default)

Setting `"allowRemediation": true` lets the agent run approved commands. The agent can only run these built-in actions:
`defender.update_signatures`, `defender.quick_scan`, `defender.enable_realtime`, `firewall.enable_profile` (Domain/Private/Public),
`usb.apply_storage_policy` (BlockWrite/BlockAll) and `config.disable_smb1`. The server sends a key and at most one value from
a closed set, never a script. With `allowRemediation` left false, approved commands are reported back as "disabled on this device".
`-WhatIf` shows what would run.

## Compatibility

Windows PowerShell 5.1 or PowerShell 7 on Windows 10/11 and Windows Server 2016+, run elevated. Sections that a device cannot
provide (for example BitLocker on Windows Home, Secure Boot on legacy BIOS) are reported as unknown and never counted as a pass.

> This script was syntax-checked and exercised with PowerShell 7 on Linux, where every Windows cmdlet is absent. That verifies parsing,
> error handling and the report contract, **not** the Windows cmdlet behaviour. Pilot on a few Windows devices before rollout.
