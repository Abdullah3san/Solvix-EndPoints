namespace Solvix.SharedKernel.Identity;

/// <summary>Identity of the caller for the current scope. Derived from validated JWT claims.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? Email { get; }

    TenantRole? Role { get; }

    /// <summary><c>true</c> when the caller is an endpoint agent rather than a person.</summary>
    bool IsAgent { get; }

    /// <summary>The calling device, when <see cref="IsAgent"/> is true.</summary>
    Guid? DeviceId { get; }

    Guid RequireUserId();
}

public enum TenantRole
{
    Member = 1,
    Admin = 2,
    Owner = 3,
}
