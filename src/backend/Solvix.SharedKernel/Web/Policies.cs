namespace Solvix.SharedKernel.Web;

/// <summary>Authorization policy names. Registered by the API host.</summary>
public static class Policies
{
    /// <summary>Authenticated user with an active membership in the resolved tenant.</summary>
    public const string TenantMember = "TenantMember";

    /// <summary>Tenant Owner or Admin.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Tenant Owner only.</summary>
    public const string TenantOwner = "TenantOwner";

    /// <summary>Read security posture, findings, alerts, events, reports and the security audit trail.</summary>
    public const string SecurityRead = "SecurityRead";

    /// <summary>Enrol devices, triage findings and alerts, author policies, manage advisories, request remediation.</summary>
    public const string SecurityManage = "SecurityManage";

    /// <summary>Decide approvals (policies, remediation). Segregated from <see cref="SecurityManage"/> by a "not the requester" rule.</summary>
    public const string SecurityApprove = "SecurityApprove";
}
