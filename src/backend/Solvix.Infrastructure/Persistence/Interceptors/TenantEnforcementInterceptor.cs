namespace Solvix.Infrastructure.Persistence.Interceptors;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Guards every SaveChanges:
///  - assigns the ambient tenant to new tenant-scoped entities that have none;
///  - rejects inserts/updates/deletes whose TenantId differs from the ambient tenant;
///  - rejects any modification of TenantId (rows cannot move between tenants);
///  - stamps CreatedAtUtc / UpdatedAtUtc.
/// Platform scope (registration, login) is the only exception and is explicit.
/// </summary>
public sealed class TenantEnforcementInterceptor(ITenantContext tenantContext, IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enforce(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Enforce(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Enforce(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            StampAudit(entry, now);

            if (entry.Entity is not ITenantScoped scoped)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    EnforceAdded(entry, scoped);
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    EnforceExisting(entry, scoped);
                    break;
                default:
                    break;
            }
        }
    }

    private void EnforceAdded(EntityEntry entry, ITenantScoped scoped)
    {
        if (scoped.TenantId == Guid.Empty)
        {
            if (entry.Entity is TenantEntity tenantEntity && tenantContext.IsResolved)
            {
                tenantEntity.AssignTenant(tenantContext.TenantId!.Value);
                return;
            }

            throw new TenantContextMissingException(
                $"Cannot insert {entry.Metadata.ClrType.Name} without a tenant: no tenant is resolved and none was assigned.");
        }

        if (!tenantContext.IsPlatformScope && scoped.TenantId != tenantContext.TenantId)
        {
            throw new CrossTenantAccessException(
                $"Attempted to insert {entry.Metadata.ClrType.Name} for tenant {scoped.TenantId} while acting for tenant {tenantContext.TenantId?.ToString() ?? "<none>"}.");
        }
    }

    private void EnforceExisting(EntityEntry entry, ITenantScoped scoped)
    {
        var tenantProperty = entry.Property(nameof(ITenantScoped.TenantId));
        if (entry.State == EntityState.Modified && tenantProperty.IsModified)
        {
            throw new CrossTenantAccessException(
                $"TenantId of {entry.Metadata.ClrType.Name} cannot be changed (from {tenantProperty.OriginalValue} to {tenantProperty.CurrentValue}).");
        }

        if (!tenantContext.IsPlatformScope && scoped.TenantId != tenantContext.TenantId)
        {
            throw new CrossTenantAccessException(
                $"Attempted to {entry.State.ToString().ToLowerInvariant()} {entry.Metadata.ClrType.Name} of tenant {scoped.TenantId} while acting for tenant {tenantContext.TenantId?.ToString() ?? "<none>"}.");
        }
    }

    private static void StampAudit(EntityEntry entry, DateTimeOffset now)
    {
        if (entry.Entity is not IAuditable auditable)
        {
            return;
        }

        if (entry.State == EntityState.Added)
        {
            auditable.CreatedAtUtc = now;
            auditable.UpdatedAtUtc = now;
        }
        else if (entry.State == EntityState.Modified)
        {
            auditable.UpdatedAtUtc = now;
            entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
        }
    }
}
