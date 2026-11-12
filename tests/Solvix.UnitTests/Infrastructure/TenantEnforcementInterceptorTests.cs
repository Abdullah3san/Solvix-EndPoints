namespace Solvix.UnitTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Solvix.Infrastructure.DependencyInjection;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Persistence.Interceptors;
using Solvix.Modules.Organization.Domain;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Exercises the SaveChanges guard with the EF Core in-memory provider. The
/// provider cannot evaluate RLS, but the interceptor logic is provider-agnostic.
/// </summary>
public sealed class TenantEnforcementInterceptorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SolvixDbContext CreateContext(TenantContext tenantContext, string dbName)
    {
        var options = new DbContextOptionsBuilder<SolvixDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new TenantEnforcementInterceptor(tenantContext, new FixedClock(Now)))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SolvixDbContext(options, tenantContext, SolvixModules.ConfigurationAssemblies);
    }

    [Fact]
    public async Task Assigns_ambient_tenant_and_audit_stamps_to_new_entities()
    {
        var tenantId = Guid.NewGuid();
        var context = new TenantContext();
        context.SetTenant(tenantId, "acme");
        await using var db = CreateContext(context, nameof(Assigns_ambient_tenant_and_audit_stamps_to_new_entities));

        var branch = Branch.Create(Guid.Empty, "HQ", "HQ");
        db.Set<Branch>().Add(branch);
        await db.SaveChangesAsync();

        Assert.Equal(tenantId, branch.TenantId);
        Assert.Equal(Now, branch.CreatedAtUtc);
        Assert.Equal(Now, branch.UpdatedAtUtc);
    }

    [Fact]
    public async Task Rejects_insert_for_another_tenant()
    {
        var context = new TenantContext();
        context.SetTenant(Guid.NewGuid(), "acme");
        await using var db = CreateContext(context, nameof(Rejects_insert_for_another_tenant));

        db.Set<Branch>().Add(Branch.Create(Guid.NewGuid(), "Other", "OTH"));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_insert_without_any_tenant()
    {
        await using var db = CreateContext(new TenantContext(), nameof(Rejects_insert_without_any_tenant));
        db.Set<Branch>().Add(Branch.Create(Guid.Empty, "HQ", "HQ"));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_update_and_delete_of_rows_owned_by_another_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var dbName = nameof(Rejects_update_and_delete_of_rows_owned_by_another_tenant);

        var contextA = new TenantContext();
        contextA.SetTenant(tenantA, "a");
        Guid branchId;
        await using (var dbA = CreateContext(contextA, dbName))
        {
            var branch = Branch.Create(tenantA, "A", "A");
            dbA.Set<Branch>().Add(branch);
            await dbA.SaveChangesAsync();
            branchId = branch.Id;
        }

        var contextB = new TenantContext();
        contextB.SetTenant(tenantB, "b");
        await using var dbB = CreateContext(contextB, dbName);

        // Query filter: tenant B cannot even see the row.
        Assert.Null(await dbB.Set<Branch>().FirstOrDefaultAsync(b => b.Id == branchId));

        // Even if code bypasses the filter, SaveChanges refuses the write.
        var leaked = await dbB.Set<Branch>().IgnoreQueryFilters().FirstAsync(b => b.Id == branchId);
        leaked.Update("Hijacked", "A", null, null, null, Address.Empty);
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());

        dbB.ChangeTracker.Clear();
        var leakedAgain = await dbB.Set<Branch>().IgnoreQueryFilters().FirstAsync(b => b.Id == branchId);
        dbB.Set<Branch>().Remove(leakedAgain);
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_moving_a_row_between_tenants()
    {
        var tenantA = Guid.NewGuid();
        var context = new TenantContext();
        context.SetTenant(tenantA, "a");
        await using var db = CreateContext(context, nameof(Rejects_moving_a_row_between_tenants));

        // CompanyProfile has no (tenant_id, id) alternate key, so EF would happily
        // track a TenantId change: the interceptor must refuse it.
        var profile = CompanyProfile.Create(tenantA, "A");
        db.Set<CompanyProfile>().Add(profile);
        await db.SaveChangesAsync();

        db.Entry(profile).Property(nameof(CompanyProfile.TenantId)).CurrentValue = Guid.NewGuid();
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => db.SaveChangesAsync());

        // Branch/Department carry TenantId in an alternate key: EF itself makes it immutable.
        db.ChangeTracker.Clear();
        var branch = Branch.Create(tenantA, "A", "A");
        db.Set<Branch>().Add(branch);
        await db.SaveChangesAsync();
        Assert.Throws<InvalidOperationException>(() =>
            db.Entry(branch).Property(nameof(Branch.TenantId)).CurrentValue = Guid.NewGuid());
    }

    [Fact]
    public async Task Platform_scope_allows_explicit_cross_tenant_provisioning()
    {
        var context = new TenantContext();
        await using var db = CreateContext(context, nameof(Platform_scope_allows_explicit_cross_tenant_provisioning));

        using (context.BeginPlatformScope())
        {
            var tenant = Tenant.Create("Acme", "acme");
            db.Set<Tenant>().Add(tenant);
            db.Set<CompanyProfile>().Add(CompanyProfile.Create(tenant.Id, "Acme"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await db.Set<Tenant>().CountAsync());
        // Outside any tenant scope the profile is invisible.
        Assert.Equal(0, await db.Set<CompanyProfile>().CountAsync());
        Assert.Equal(1, await db.Set<CompanyProfile>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Query_filter_isolates_tenants_and_updates_audit_stamp()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var dbName = nameof(Query_filter_isolates_tenants_and_updates_audit_stamp);

        var platform = new TenantContext();
        await using (var seed = CreateContext(platform, dbName))
        using (platform.BeginPlatformScope())
        {
            seed.Set<Branch>().AddRange(Branch.Create(tenantA, "A", "A"), Branch.Create(tenantB, "B", "B"));
            await seed.SaveChangesAsync();
        }

        var contextA = new TenantContext();
        contextA.SetTenant(tenantA, "a");
        await using var dbA = CreateContext(contextA, dbName);
        var visible = await dbA.Set<Branch>().ToListAsync();
        var only = Assert.Single(visible);
        Assert.Equal("A", only.Code);

        only.Update("A renamed", "A", null, null, null, Address.Empty);
        await dbA.SaveChangesAsync();
        Assert.Equal(Now, only.UpdatedAtUtc);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
