namespace Solvix.SharedKernel.Authorization;

using Microsoft.AspNetCore.Authorization;

/// <summary>
/// Requires the caller to hold <paramref name="permission"/> in the tenant the
/// request is bound to. Evaluated on the server for every request against the
/// caller's current roles, so a role change takes effect immediately.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequirePermissionAttribute(string permission)
    {
        if (!PermissionCatalog.IsKnown(permission))
        {
            throw new ArgumentException($"'{permission}' is not a known permission.", nameof(permission));
        }

        Permission = permission;
        Policy = PolicyPrefix + permission;
    }

    public string Permission { get; }
}

/// <summary>
/// Marks an endpoint that is deliberately available to every signed-in member of
/// the tenant without a specific permission (the caller's own profile, sessions,
/// MFA and password). Every non-anonymous endpoint must carry either this
/// attribute or <see cref="RequirePermissionAttribute"/>; the host refuses to start otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AuthenticatedOnlyAttribute : Attribute
{
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Reasons attached to a failed authorization so the response can say why.</summary>
public static class AuthorizationFailureCodes
{
    public const string PermissionDenied = "permission_denied";
    public const string MfaEnrollmentRequired = "mfa_enrollment_required";
}

/// <summary>
/// Marks an endpoint called by an enrolled device agent, authenticated by its
/// client certificate (mutual TLS) under <see cref="Web.Policies.Device"/>. User
/// tokens never satisfy it. Counts as the endpoint's access declaration.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DeviceAuthenticatedAttribute : AuthorizeAttribute
{
    public DeviceAuthenticatedAttribute()
    {
        Policy = Web.Policies.Device;
    }
}
