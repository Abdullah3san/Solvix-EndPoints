namespace Solvix.IntegrationTests.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvix.Infrastructure.Persistence;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Verifies isolation at the database layer, i.e. for code paths that bypass
/// the EF Core query filters entirely (raw SQL, ad-hoc tooling, bugs).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RowLevelSecurityTests(ApiFixture fixture)
{
    [Fact]
    public async Task Raw_sql_without_tenant_context_sees_no_tenant_rows()
    {
        var a = await fixture.RegisterTenantAsync();
        Assert.NotEqual(Guid.Empty, a.TenantId);

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM branches"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM company_profiles"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM tenant_memberships"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM departments"));

        // Platform tables remain readable: they are protected by application logic, not RLS.
        Assert.True(await ScalarAsync(connection, "SELECT count(*) FROM tenants") >= 1);
    }

    [Fact]
    public async Task Raw_sql_in_tenant_context_only_sees_that_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();

        await SetTenantAsync(connection, a.TenantId);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM branches"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM branches WHERE tenant_id = @t", ("t", a.TenantId)));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM branches WHERE tenant_id = @t", ("t", b.TenantId)));

        await SetTenantAsync(connection, b.TenantId);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM branches WHERE tenant_id = @t", ("t", a.TenantId)));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM branches WHERE tenant_id = @t", ("t", b.TenantId)));
    }

    [Fact]
    public async Task Writes_for_another_tenant_are_rejected_or_affect_nothing()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, a.TenantId);

        // INSERT with a foreign tenant id violates the policy's WITH CHECK clause.
        var insert = new NpgsqlCommand(
            """
            INSERT INTO branches (id, tenant_id, name, code, is_headquarters, is_active, created_at_utc, updated_at_utc)
            VALUES (@id, @tenant, 'Injected', 'INJ', false, true, now(), now())
            """,
            connection);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tenant", b.TenantId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        await insert.DisposeAsync();

        // UPDATE / DELETE targeting the other tenant silently match zero rows.
        var update = new NpgsqlCommand("UPDATE branches SET name = 'Hijacked' WHERE tenant_id = @tenant", connection);
        update.Parameters.AddWithValue("tenant", b.TenantId);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());
        await update.DisposeAsync();

        var delete = new NpgsqlCommand("DELETE FROM branches WHERE tenant_id = @tenant", connection);
        delete.Parameters.AddWithValue("tenant", b.TenantId);
        Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        await delete.DisposeAsync();

        // Moving a row to another tenant is rejected by WITH CHECK as well.
        var move = new NpgsqlCommand("UPDATE branches SET tenant_id = @other WHERE tenant_id = @mine", connection);
        move.Parameters.AddWithValue("other", b.TenantId);
        move.Parameters.AddWithValue("mine", a.TenantId);
        var moveEx = await Assert.ThrowsAsync<PostgresException>(() => move.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, moveEx.SqlState);
        await move.DisposeAsync();

        await SetTenantAsync(connection, b.TenantId);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM branches WHERE name = 'Headquarters'"));
    }

    [Fact]
    public async Task Schema_owner_is_also_subject_to_rls()
    {
        await fixture.RegisterTenantAsync();
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM branches"));
    }

    [Fact]
    public async Task Ef_ignore_query_filters_cannot_leak_other_tenants_through_rls()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();

        var scopeFactory = fixture.Factory.Services.GetRequiredService<ITenantScopeFactory>();
        await using var scope = scopeFactory.CreateScope(a.TenantId, a.Slug);
        var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();

        // Application filter deliberately bypassed: the database still only returns A's rows.
        var unfiltered = await db.Set<Branch>().IgnoreQueryFilters().Select(x => x.TenantId).Distinct().ToListAsync();
        Assert.Equal([a.TenantId], unfiltered);

        var raw = await db.Database.SqlQueryRaw<Guid>("SELECT tenant_id AS \"Value\" FROM branches").Distinct().ToListAsync();
        Assert.Equal([a.TenantId], raw);
        Assert.DoesNotContain(b.TenantId, raw);
    }

    [Fact]
    public async Task Session_variable_does_not_leak_between_pooled_connections()
    {
        var a = await fixture.RegisterTenantAsync();

        // Open a tenant-bound session, return it to the pool, then take a fresh one.
        await using (var first = new NpgsqlConnection(fixture.Database.AppConnectionString))
        {
            await first.OpenAsync();
            await SetTenantAsync(first, a.TenantId);
            Assert.Equal(1L, await ScalarAsync(first, "SELECT count(*) FROM branches"));
        }

        await using var second = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await second.OpenAsync();
        Assert.Equal(0L, await ScalarAsync(second, "SELECT count(*) FROM branches"));
        Assert.True(string.IsNullOrEmpty((await ScalarObjectAsync(second, "SELECT current_setting('app.tenant_id', true)"))?.ToString()));
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection);
        command.Parameters.AddWithValue("t", tenantId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        (long)(await ScalarObjectAsync(connection, sql, parameters))!;

    private static async Task<object?> ScalarObjectAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }
}
