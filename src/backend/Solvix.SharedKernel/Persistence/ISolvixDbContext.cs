namespace Solvix.SharedKernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Narrow persistence contract consumed by modules. The concrete context lives
/// in Solvix.Infrastructure and applies tenant filters, auditing and RLS.
/// </summary>
public interface ISolvixDbContext
{
    DbSet<TEntity> Set<TEntity>()
        where TEntity : class;

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
