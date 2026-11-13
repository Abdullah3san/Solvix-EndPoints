namespace Solvix.SharedKernel.Authorization;

/// <summary>
/// Permission keys. Endpoints are protected with these keys and nothing else:
/// role names never appear in authorization decisions. Roles are just named,
/// tenant-owned bundles of permissions.
/// </summary>
public static class Permissions
{
    public static class Devices
    {
        public const string View = "devices.view";
        public const string Enroll = "devices.enroll";
        public const string Manage = "devices.manage";
    }

    /// <summary>
    /// Remote endpoint administration (Phase 06). Every key implies <see cref="View"/>,
    /// which implies <see cref="Devices.View"/>. Holding a key is necessary but not
    /// sufficient: the remote access policy (scope, hours, approvals) is checked as well.
    /// </summary>
    public static class Remote
    {
        public const string View = "remote.view";
        public const string Desktop = "remote.desktop";
        public const string DesktopControl = "remote.desktop.control";
        public const string MultiMonitor = "remote.multimonitor";
        public const string Terminal = "remote.terminal";
        public const string PowerShell = "remote.powershell";
        public const string ExecuteApprovedCommands = "remote.execute_approved_commands";
        public const string FileUpload = "remote.file.upload";
        public const string FileDownload = "remote.file.download";
        public const string Restart = "remote.restart";
        public const string Shutdown = "remote.shutdown";
        public const string Lock = "remote.lock";
        public const string VoiceCall = "remote.voice_call";
        public const string SessionsView = "remote.sessions.view";
        public const string SessionsTerminate = "remote.sessions.terminate";
        public const string ApprovalsManage = "remote.approvals.manage";
    }

    /// <summary>
    /// Enterprise software management (Phase 07). Every key implies <see cref="View"/>,
    /// which implies <see cref="Devices.View"/>.
    /// </summary>
    public static class Software
    {
        public const string View = "software.view";
        public const string PackagesManage = "software.packages.manage";
        public const string Deploy = "software.deploy";
        public const string Approve = "software.approve";
        public const string PoliciesManage = "software.policies.manage";
    }

    public static class Assets
    {
        public const string View = "assets.view";
        public const string Create = "assets.create";
        public const string Edit = "assets.edit";
        public const string Transfer = "assets.transfer";
    }

    public static class Mdm
    {
        public const string Manage = "mdm.manage";
    }

    public static class Users
    {
        public const string View = "users.view";
        public const string Manage = "users.manage";
    }

    public static class Roles
    {
        public const string View = "roles.view";
        public const string Manage = "roles.manage";
    }

    public static class Security
    {
        public const string View = "security.view";
        public const string Manage = "security.manage";
    }

    public static class Audit
    {
        public const string View = "audit.view";
    }

    public static class Reports
    {
        public const string View = "reports.view";
    }

    public static class Billing
    {
        public const string Manage = "billing.manage";
    }

    public static class Organization
    {
        public const string View = "organization.view";
        public const string Manage = "organization.manage";
    }

    public static class Workspace
    {
        public const string Manage = "tenant.manage";
    }
}
