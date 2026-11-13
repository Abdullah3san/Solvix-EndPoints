namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Authorization;

public sealed record SystemRoleDefinition(string Key, string Name, string Description, IReadOnlySet<string> Permissions);

/// <summary>
/// The built-in roles every tenant starts with. They are copied into the
/// tenant's own <c>roles</c> / <c>role_permissions</c> rows when the tenant is
/// provisioned, so what a role grants is always visible as data.
///
/// Changing a definition here does not change existing tenants: add a migration
/// that updates their rows (see <c>IdentityAccessManagement</c> for the pattern).
/// The integration test <c>System_roles_in_the_database_match_the_catalog</c>
/// fails when the two drift apart.
/// </summary>
public static class SystemRoles
{
    public const string SuperAdministratorKey = "super_admin";
    public const string TenantAdministratorKey = "tenant_admin";
    public const string ItManagerKey = "it_manager";
    public const string ItTechnicianKey = "it_technician";
    public const string AssetManagerKey = "asset_manager";
    public const string SecurityAdministratorKey = "security_admin";
    public const string ReadOnlyAuditorKey = "read_only_auditor";

    /// <summary>Remote permissions of the IT Manager: everything except interactive/ad-hoc PowerShell.</summary>
    internal static readonly string[] ItManagerRemote =
    [
        Permissions.Remote.View, Permissions.Remote.Desktop, Permissions.Remote.DesktopControl, Permissions.Remote.MultiMonitor,
        Permissions.Remote.Terminal, Permissions.Remote.ExecuteApprovedCommands, Permissions.Remote.FileUpload,
        Permissions.Remote.FileDownload, Permissions.Remote.Restart, Permissions.Remote.Shutdown, Permissions.Remote.Lock,
        Permissions.Remote.VoiceCall, Permissions.Remote.SessionsView, Permissions.Remote.SessionsTerminate,
        Permissions.Remote.ApprovalsManage,
    ];

    /// <summary>Remote permissions of the IT Technician: support work, no shutdown, approvals or PowerShell.</summary>
    internal static readonly string[] ItTechnicianRemote =
    [
        Permissions.Remote.View, Permissions.Remote.Desktop, Permissions.Remote.DesktopControl, Permissions.Remote.MultiMonitor,
        Permissions.Remote.Terminal, Permissions.Remote.ExecuteApprovedCommands, Permissions.Remote.FileUpload,
        Permissions.Remote.FileDownload, Permissions.Remote.Restart, Permissions.Remote.Lock, Permissions.Remote.VoiceCall,
        Permissions.Remote.SessionsView,
    ];

    /// <summary>Software management of the IT Manager: the whole lifecycle, including approving others' deployments.</summary>
    internal static readonly string[] ItManagerSoftware =
    [
        Permissions.Software.View, Permissions.Software.PackagesManage, Permissions.Software.Deploy,
        Permissions.Software.Approve, Permissions.Software.PoliciesManage,
    ];

    /// <summary>Holds every permission, including billing and workspace settings. A tenant always keeps at least one.</summary>
    public static SystemRoleDefinition SuperAdministrator { get; } = Define(
        SuperAdministratorKey,
        "Super Administrator",
        "Full control of the workspace, including billing, workspace settings and every other role.",
        [.. PermissionCatalog.AllKeys]);

    public static SystemRoleDefinition TenantAdministrator { get; } = Define(
        TenantAdministratorKey,
        "Tenant Administrator",
        "Administers users, roles, security, the organisation and all devices and assets. Cannot manage billing or workspace settings.",
        [.. PermissionCatalog.AllKeys.Where(k => k is not (Permissions.Billing.Manage or Permissions.Workspace.Manage))]);

    public static SystemRoleDefinition ItManager { get; } = Define(
        ItManagerKey,
        "IT Manager",
        "Runs day-to-day IT: full control of devices, MDM and assets, plus reports.",
        [
            Permissions.Devices.View, Permissions.Devices.Enroll, Permissions.Devices.Manage,
            .. ItManagerRemote,
            Permissions.Assets.View, Permissions.Assets.Create, Permissions.Assets.Edit, Permissions.Assets.Transfer,
            Permissions.Mdm.Manage, Permissions.Reports.View, Permissions.Users.View, Permissions.Organization.View,
            .. ItManagerSoftware,
        ]);

    public static SystemRoleDefinition ItTechnician { get; } = Define(
        ItTechnicianKey,
        "IT Technician",
        "Supports users: enrols devices, connects remotely, runs approved operations and keeps asset records up to date. Cannot shut devices down, run ad-hoc PowerShell or retire devices.",
        [
            Permissions.Devices.View, Permissions.Devices.Enroll,
            .. ItTechnicianRemote,
            Permissions.Assets.View, Permissions.Assets.Edit, Permissions.Organization.View,
            Permissions.Software.View, Permissions.Software.Deploy,
        ]);

    public static SystemRoleDefinition AssetManager { get; } = Define(
        AssetManagerKey,
        "Asset Manager",
        "Owns the asset register: creates, edits and transfers assets.",
        [
            Permissions.Assets.View, Permissions.Assets.Create, Permissions.Assets.Edit, Permissions.Assets.Transfer,
            Permissions.Devices.View, Permissions.Reports.View, Permissions.Organization.View,
            Permissions.Software.View,
        ]);

    public static SystemRoleDefinition SecurityAdministrator { get; } = Define(
        SecurityAdministratorKey,
        "Security Administrator",
        "Sets the security policy, revokes sessions and reviews audit logs. Cannot change who has which role.",
        [
            Permissions.Security.View, Permissions.Security.Manage, Permissions.Audit.View,
            Permissions.Users.View, Permissions.Roles.View, Permissions.Devices.View,
            Permissions.Remote.View, Permissions.Remote.SessionsView,
            Permissions.Reports.View, Permissions.Organization.View,
            Permissions.Software.View,
        ]);

    public static SystemRoleDefinition ReadOnlyAuditor { get; } = Define(
        ReadOnlyAuditorKey,
        "Read-Only Auditor",
        "Can view everything, including audit logs, and change nothing.",
        [
            Permissions.Devices.View, Permissions.Assets.View, Permissions.Users.View, Permissions.Roles.View,
            Permissions.Security.View, Permissions.Audit.View, Permissions.Reports.View, Permissions.Organization.View,
            Permissions.Remote.View, Permissions.Remote.SessionsView,
            Permissions.Software.View,
        ]);

    public static IReadOnlyList<SystemRoleDefinition> All { get; } =
    [
        SuperAdministrator, TenantAdministrator, ItManager, ItTechnician, AssetManager, SecurityAdministrator, ReadOnlyAuditor,
    ];

    public static SystemRoleDefinition? Find(string? key) => All.FirstOrDefault(r => r.Key == key);

    private static SystemRoleDefinition Define(string key, string name, string description, string[] permissions) =>
        new(key, name, description, PermissionCatalog.Expand(permissions));
}
