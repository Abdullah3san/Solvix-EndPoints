namespace Solvix.SharedKernel.Identity;

/// <summary>
/// Read-only view of the people in the current tenant, for modules that need
/// to display or validate a user reference (e.g. a device's primary user)
/// without depending on the Identity module. Implemented by Identity.
/// </summary>
public interface IMemberDirectory
{
    /// <summary>Returns the members of the current tenant among <paramref name="userIds"/>.</summary>
    Task<IReadOnlyDictionary<Guid, MemberSummary>> FindAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public sealed record MemberSummary(Guid UserId, string DisplayName, string Email, TenantRole Role, bool IsActive);
