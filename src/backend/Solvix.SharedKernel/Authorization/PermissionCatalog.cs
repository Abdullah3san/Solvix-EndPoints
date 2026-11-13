namespace Solvix.SharedKernel.Authorization;

using System.Collections.Frozen;

/// <summary>One grantable permission: its key, the group it is shown under and what it allows.</summary>
public sealed record PermissionDefinition(string Key, string Group, string Description, IReadOnlyList<string> Implies);

/// <summary>
/// The closed set of permissions the platform understands. A role can only hold
/// keys from this catalog, so a typo or an injected key can never become a grant.
/// </summary>
public static class PermissionCatalog
{
    private static readonly PermissionDefinition[] Definitions =
    [
        Define(Permissions.Devices.View, "Devices", "View devices and their inventory."),
        Define(Permissions.Devices.Enroll, "Devices", "Enroll new devices.", Permissions.Devices.View),
        Define(Permissions.Devices.Manage, "Devices", "Edit, retire and delete devices.", Permissions.Devices.View),
        Define(Permissions.Remote.View, "Remote", "See remote management: live system information, command history and remote status of devices.", Permissions.Devices.View),
        Define(Permissions.Remote.Desktop, "Remote", "View the screen of a device in a remote desktop session.", Permissions.Remote.View),
        Define(Permissions.Remote.DesktopControl, "Remote", "Control keyboard and mouse in a remote desktop session.", Permissions.Remote.Desktop),
        Define(Permissions.Remote.MultiMonitor, "Remote", "Switch between the monitors of a device during a remote desktop session.", Permissions.Remote.Desktop),
        Define(Permissions.Remote.Terminal, "Remote", "Open an interactive remote terminal on a device.", Permissions.Remote.View),
        Define(Permissions.Remote.PowerShell, "Remote", "Run PowerShell interactively and manage the script library. High risk: grant explicitly.", Permissions.Remote.View),
        Define(Permissions.Remote.ExecuteApprovedCommands, "Remote", "Run approved scripts and predefined administrative operations.", Permissions.Remote.View),
        Define(Permissions.Remote.FileUpload, "Remote", "Upload files to approved folders on a device.", Permissions.Remote.View),
        Define(Permissions.Remote.FileDownload, "Remote", "Download files from approved folders on a device.", Permissions.Remote.View),
        Define(Permissions.Remote.Restart, "Remote", "Restart devices and sign users off.", Permissions.Remote.View),
        Define(Permissions.Remote.Shutdown, "Remote", "Shut devices down.", Permissions.Remote.View),
        Define(Permissions.Remote.Lock, "Remote", "Lock the workstation of a device.", Permissions.Remote.View),
        Define(Permissions.Remote.VoiceCall, "Remote", "Call the person using a device (they must accept).", Permissions.Remote.View),
        Define(Permissions.Remote.SessionsView, "Remote", "View active and past remote sessions and their audit trail.", Permissions.Remote.View),
        Define(Permissions.Remote.SessionsTerminate, "Remote", "End other technicians' remote sessions.", Permissions.Remote.SessionsView),
        Define(Permissions.Remote.ApprovalsManage, "Remote", "Approve or reject sensitive remote operations requested by others.", Permissions.Remote.View),
        Define(Permissions.Assets.View, "Assets", "View assets."),
        Define(Permissions.Assets.Create, "Assets", "Create assets.", Permissions.Assets.View),
        Define(Permissions.Assets.Edit, "Assets", "Edit assets.", Permissions.Assets.View),
        Define(Permissions.Assets.Transfer, "Assets", "Transfer assets between people, branches and departments.", Permissions.Assets.View),
        Define(Permissions.Mdm.Manage, "MDM", "Manage mobile device management policies and profiles.", Permissions.Devices.View),
        Define(Permissions.Users.View, "Users", "View users, their roles and pending invitations."),
        Define(Permissions.Users.Manage, "Users", "Invite, activate, deactivate and remove users and assign their roles.", Permissions.Users.View),
        Define(Permissions.Roles.View, "Roles", "View roles and the permissions they grant."),
        Define(Permissions.Roles.Manage, "Roles", "Create, edit and delete custom roles.", Permissions.Roles.View),
        Define(Permissions.Security.View, "Security", "View the security policy and user sessions."),
        Define(Permissions.Security.Manage, "Security", "Change the security policy and revoke user sessions.", Permissions.Security.View),
        Define(Permissions.Audit.View, "Audit", "View authentication and access audit logs."),
        Define(Permissions.Reports.View, "Reports", "View reports."),
        Define(Permissions.Billing.Manage, "Billing", "Manage the subscription, invoices and payment methods."),
        Define(Permissions.Organization.View, "Organization", "View the company profile, branches and departments."),
        Define(Permissions.Organization.Manage, "Organization", "Edit the company profile, branches and departments.", Permissions.Organization.View),
        Define(Permissions.Workspace.Manage, "Workspace", "Rename the workspace and change workspace-level settings."),
    ];

    private static readonly FrozenDictionary<string, PermissionDefinition> ByKey =
        Definitions.ToFrozenDictionary(d => d.Key, StringComparer.Ordinal);

    public static IReadOnlyList<PermissionDefinition> All => Definitions;

    public static IReadOnlySet<string> AllKeys { get; } = Definitions.Select(d => d.Key).ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? key) => key is not null && ByKey.ContainsKey(key);

    /// <summary>
    /// Returns the given keys plus everything they imply (e.g. <c>users.manage</c>
    /// implies <c>users.view</c>). Unknown keys are rejected by the caller first.
    /// </summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(keys);
        while (pending.Count > 0)
        {
            var key = pending.Pop();
            if (!ByKey.TryGetValue(key, out var definition) || !result.Add(key))
            {
                continue;
            }

            foreach (var implied in definition.Implies)
            {
                pending.Push(implied);
            }
        }

        return result;
    }

    private static PermissionDefinition Define(string key, string group, string description, params string[] implies) =>
        new(key, group, description, implies);
}
