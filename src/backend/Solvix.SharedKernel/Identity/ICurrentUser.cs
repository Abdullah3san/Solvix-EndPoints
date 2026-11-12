namespace Solvix.SharedKernel.Identity;

/// <summary>
/// Identity and effective access of the caller for the current scope. Identity
/// comes from the validated access token; permissions are resolved on the server
/// for the tenant the request is bound to and are never read from the token.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? Email { get; }

    /// <summary>The server-side session the access token belongs to.</summary>
    Guid? SessionId { get; }

    /// <summary>Permissions held in the current tenant. Empty when unresolved.</summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>Names of the roles held in the current tenant (display only; never used for authorization).</summary>
    IReadOnlyList<string> Roles { get; }

    bool HasPermission(string permission);

    Guid RequireUserId();

    Guid RequireSessionId();
}
