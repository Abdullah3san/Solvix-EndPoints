namespace Solvix.SharedKernel.Web;

/// <summary>Authorization policy names. Registered by the API host.</summary>
public static class Policies
{
    /// <summary>
    /// Authenticated user with an active session and an active membership in the
    /// resolved tenant. Applied to every controller; individual actions add a
    /// permission on top with <c>[RequirePermission]</c>.
    /// </summary>
    public const string TenantMember = "TenantMember";

    /// <summary>
    /// An enrolled device authenticated by its client certificate (mutual TLS).
    /// User tokens never satisfy this policy and device certificates never
    /// satisfy the user policies.
    /// </summary>
    public const string Device = "Device";
}

/// <summary>Rate limiter policy names. Registered by the API host.</summary>
public static class RateLimitPolicies
{
    /// <summary>Credential checks: login, MFA verification, token redemption.</summary>
    public const string Credentials = "auth-credentials";

    /// <summary>Endpoints that send e-mail: password reset and verification requests.</summary>
    public const string Email = "auth-email";

    /// <summary>Refresh token rotation.</summary>
    public const string Refresh = "auth-refresh";

    /// <summary>Self-service sign-up.</summary>
    public const string Registration = "auth-registration";
}
