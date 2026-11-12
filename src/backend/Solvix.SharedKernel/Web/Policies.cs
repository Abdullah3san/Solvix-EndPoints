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
}
