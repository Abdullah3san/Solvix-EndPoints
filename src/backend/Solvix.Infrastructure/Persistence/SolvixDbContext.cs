namespace Solvix.Infrastructure.Persistence;

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Solvix.Infrastructure.Persistence.Conventions;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Single EF Core context for the modular monolith. Module entity
/// configurations are discovered from the registered module assemblies.
///
/// Tenant isolation is layered:
///  1. every <see cref="ITenantScoped"/> entity gets a named global query filter
///     (<see cref="TenantFilterName"/>) bound to the ambient tenant context;
///  2. every <see cref="ITenantScoped"/> entity gets a foreign key to <c>tenants</c>;
///  3. <c>TenantEnforcementInterceptor</c> validates writes;
///  4. <c>TenantSessionConnectionInterceptor</c> sets the PostgreSQL session
///     variable consumed by Row-Level Security policies.
/// </summary>
public sealed class SolvixDbContext(
    DbContextOptions<SolvixDbContext> options,
    ITenantContext tenantContext,
    IEnumerable<Assembly> configurationAssemblies) : DbContext(options), ISolvixDbContext
{
    public const string TenantFilterName = "Tenant";

    private readonly IReadOnlyList<Assembly> _configurationAssemblies = configurationAssemblies.Distinct().ToList();

    /// <summary>The tenant the ambient scope is bound to. Null means "no tenant": filters match nothing.</summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    public ITenantContext TenantContext => tenantContext;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var assembly in _configurationAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned() || !typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            ConfigureTenantScopedEntity(modelBuilder, entityType);
        }

        SnakeCaseNaming.Apply(modelBuilder);
    }

    private void ConfigureTenantScopedEntity(ModelBuilder modelBuilder, IMutableEntityType entityType)
    {
        var builder = modelBuilder.Entity(entityType.ClrType);

        builder.Property(nameof(ITenantScoped.TenantId)).IsRequired();

        // Database-level ownership: FK to tenants, never cascading (tenant deletion is an explicit workflow).
        builder.HasOne(typeof(Tenant))
            .WithMany()
            .HasForeignKey(nameof(ITenantScoped.TenantId))
            .OnDelete(DeleteBehavior.Restrict);

        // e => e.TenantId == CurrentTenantId  (CurrentTenantId is re-read per query, EF parameterises it)
        var parameter = Expression.Parameter(entityType.ClrType, "e");
        var tenantProperty = Expression.Property(parameter, nameof(ITenantScoped.TenantId));
        var currentTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        var body = Expression.Equal(
            Expression.Convert(tenantProperty, typeof(Guid?)),
            currentTenant);
        var lambda = Expression.Lambda(body, parameter);
        builder.HasQueryFilter(TenantFilterName, lambda);
    }
}
