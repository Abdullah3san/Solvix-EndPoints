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
        public const string RemoteAccess = "devices.remote_access";
        public const string ExecuteCommand = "devices.execute_command";
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
