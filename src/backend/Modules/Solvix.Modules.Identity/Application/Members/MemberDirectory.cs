namespace Solvix.Modules.Identity.Application.Members;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Resolves user references for other modules. Runs in the caller's tenant
/// scope: the membership query filter + RLS restrict results to that tenant,
/// so a user of another tenant is reported as "not found".
/// </summary>
internal sealed class MemberDirectory(ISolvixDbContext db) : IMemberDirectory
{
    public async Task<IReadOnlyDictionary<Guid, MemberSummary>> FindAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, MemberSummary>();
        }

        var ids = userIds.Distinct().ToList();
        return await db.Set<TenantMembership>()
            .AsNoTracking()
            .Where(m => ids.Contains(m.UserId))
            .Select(m => new MemberSummary(m.UserId, m.User!.DisplayName, m.User.Email, m.IsActive && m.User.IsActive))
            .ToDictionaryAsync(m => m.UserId, cancellationToken);
    }
}
