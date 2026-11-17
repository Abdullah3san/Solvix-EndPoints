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

    /// <summary>
    /// Provider-level platform operator. The claim only opens the door: platform
    /// services re-check the flag in the database on every call.
    /// </summary>
    public const string PlatformOperator = "PlatformOperator";

    /// <summary>
    /// An enrolled device authenticated by its client certificate (mutual TLS).
    /// User tokens never satisfy this policy and device certificates never
    /// satisfy the user policies.
    /// </summary>
    public const string Device = "Device";
}
