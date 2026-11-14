namespace Solvix.Modules.Security.Evaluation;

using Solvix.Modules.Security.Domain;

/// <summary>A change an agent may be asked to make. This catalogue is the complete allow-list.</summary>
/// <param name="Key">Stable identifier sent to the agent. The agent maps it to a fixed local implementation.</param>
/// <param name="Risk">Drives who may approve and whether a policy may auto-approve it.</param>
/// <param name="ParameterName">The single parameter the action accepts, if any.</param>
/// <param name="AllowedValues">The closed set of values for the parameter. Anything else is rejected.</param>
/// <param name="WindowsInterface">The documented Windows interface the agent uses.</param>
public sealed record RemediationAction(
    string Key,
    string Title,
    string Description,
    RemediationRisk Risk,
    string? ParameterName,
    IReadOnlyList<string> AllowedValues,
    string WindowsInterface,
    string Impact);

/// <summary>
/// Remediation is deliberately narrow. The server never sends scripts or commands: it sends a
/// catalogue <c>Key</c> plus at most one value from a closed set, and the agent runs its own
/// built-in implementation (and only if the endpoint owner enabled remediation locally).
/// Actions that can lock users out or lose data (BitLocker enablement, removing administrators,
/// uninstalling software) are intentionally absent: they are guided, manual fixes.
/// </summary>
public static class RemediationCatalog
{
    private static readonly RemediationAction[] Actions =
    [
        new(
            "defender.update_signatures",
            "Update Microsoft Defender signatures",
            "Runs Update-MpSignature to pull the latest definitions.",
            RemediationRisk.Low,
            null,
            [],
            "Update-MpSignature",
            "Downloads definition updates. No configuration change."),
        new(
            "defender.quick_scan",
            "Start a Microsoft Defender quick scan",
            "Runs Start-MpScan -ScanType QuickScan.",
            RemediationRisk.Low,
            null,
            [],
            "Start-MpScan",
            "Uses some CPU and disk while scanning. No configuration change."),
        new(
            "defender.enable_realtime",
            "Enable Microsoft Defender real-time protection",
            "Runs Set-MpPreference -DisableRealtimeMonitoring $false.",
            RemediationRisk.Medium,
            null,
            [],
            "Set-MpPreference",
            "Changes a security setting. Fails if Tamper Protection or another product's policy blocks the change."),
        new(
            "firewall.enable_profile",
            "Enable a Windows Firewall profile",
            "Runs Set-NetFirewallProfile -Profile <profile> -Enabled True.",
            RemediationRisk.Medium,
            "profile",
            ["Domain", "Private", "Public"],
            "Set-NetFirewallProfile",
            "May block inbound connections that previously worked (file sharing, remote tools)."),
        new(
            "usb.apply_storage_policy",
            "Apply the removable-storage restriction",
            "Sets the documented Removable Storage Access policy values (Deny_Write / Deny_Read) for removable disks.",
            RemediationRisk.Medium,
            "mode",
            ["BlockWrite", "BlockAll"],
            "Registry: HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\RemovableStorageDevices",
            "Users lose the ability to write to (or use) USB storage. Takes effect for new device connections."),
        new(
            "config.disable_smb1",
            "Disable the SMBv1 server",
            "Runs Set-SmbServerConfiguration -EnableSMB1Protocol $false.",
            RemediationRisk.High,
            null,
            [],
            "Set-SmbServerConfiguration",
            "Legacy devices and software that depend on SMBv1 will stop connecting. A reboot may be required."),
    ];

    private static readonly Dictionary<string, RemediationAction> ByKey =
        Actions.ToDictionary(a => a.Key, StringComparer.Ordinal);

    public static IReadOnlyList<RemediationAction> All => Actions;

    public static bool TryGet(string? key, out RemediationAction action)
    {
        if (key is not null && ByKey.TryGetValue(key, out var found))
        {
            action = found;
            return true;
        }

        action = null!;
        return false;
    }

    /// <summary>Returns an error message when <paramref name="value"/> is not acceptable for the action; otherwise null.</summary>
    public static string? ValidateParameter(RemediationAction action, string? value)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.ParameterName is null)
        {
            return string.IsNullOrEmpty(value) ? null : $"Action '{action.Key}' takes no parameter.";
        }

        if (string.IsNullOrEmpty(value))
        {
            return $"Action '{action.Key}' requires '{action.ParameterName}'.";
        }

        return action.AllowedValues.Contains(value, StringComparer.Ordinal)
            ? null
            : $"'{value}' is not allowed for '{action.ParameterName}'. Allowed: {string.Join(", ", action.AllowedValues)}.";
    }
}
