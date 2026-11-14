namespace Solvix.IntegrationTests.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Tenancy;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;

/// <summary>Background work must be bound to exactly one tenant, with the same guarantees as HTTP requests.</summary>
[Collection(ApiCollection.Name)]
public sealed class BackgroundJobIsolationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Job_sees_only_its_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var runner = fixture.Factory.Services.GetRequiredService<ITenantJobRunner>();

        var seenByA = await runner.RunForTenantAsync(
            a.TenantId,
            (sp, ct) => sp.GetRequiredService<ISolvixDbContext>().Set<Branch>().Select(x => x.TenantId).Distinct().ToListAsync(ct),
            CancellationToken.None);
        Assert.Equal([a.TenantId], seenByA);

        var seenByB = await runner.RunForTenantAsync(
            b.TenantId,
            (sp, ct) => sp.GetRequiredService<ISolvixDbContext>().Set<Branch>().Select(x => x.TenantId).Distinct().ToListAsync(ct),
            CancellationToken.None);
        Assert.Equal([b.TenantId], seenByB);
    }

    [Fact]
    public async Task Job_cannot_write_into_another_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var runner = fixture.Factory.Services.GetRequiredService<ITenantJobRunner>();

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => runner.RunForTenantAsync(
            b.TenantId,
            async (sp, ct) =>
            {
                var db = sp.GetRequiredService<ISolvixDbContext>();
                db.Set<Branch>().Add(Branch.Create(a.TenantId, "Injected", "INJ"));
                await db.SaveChangesAsync(ct);
            },
            CancellationToken.None));

        await using var scope = fixture.Factory.Services.GetRequiredService<SharedKernel.Tenancy.ITenantScopeFactory>().CreateScope(a.TenantId);
        var count = await scope.ServiceProvider.GetRequiredService<SolvixDbContext>().Set<Branch>().CountAsync(x => x.Code == "INJ");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Job_inside_tenant_scope_writes_land_in_that_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var runner = fixture.Factory.Services.GetRequiredService<ITenantJobRunner>();

        var id = await runner.RunForTenantAsync(
            a.TenantId,
            async (sp, ct) =>
            {
                var db = sp.GetRequiredService<ISolvixDbContext>();
                var branch = Branch.Create(Guid.Empty, "Job branch", "JOB"); // tenant assigned by the interceptor
                db.Set<Branch>().Add(branch);
                await db.SaveChangesAsync(ct);
                return branch.Id;
            },
            CancellationToken.None);

        using var client = fixture.CreateClient(a.Token);
        (await client.GetAsync($"/api/v1/branches/{id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Root_scope_without_tenant_sees_nothing()
    {
        await fixture.RegisterTenantAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();
        Assert.False(db.TenantContext.IsResolved);
        Assert.Equal(0, await db.Set<Branch>().CountAsync());
        Assert.Equal(0, await db.Set<Branch>().IgnoreQueryFilters().CountAsync());
        await Assert.ThrowsAsync<TenantContextMissingException>(async () =>
        {
            db.Set<Branch>().Add(Branch.Create(Guid.Empty, "Nowhere", "NOW"));
            await db.SaveChangesAsync();
        });
    }
}
