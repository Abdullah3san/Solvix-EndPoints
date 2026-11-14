namespace Solvix.IntegrationTests.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Persistence.Migrations;
using Solvix.IntegrationTests.Fixtures;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Tenancy;

[Collection(ApiCollection.Name)]
public sealed class MigrationTests(ApiFixture fixture)
{
    [Fact]
    public async Task All_migrations_are_applied_and_model_matches_snapshot()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();

        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Contains(applied, m => m.EndsWith("_InitialSchema", StringComparison.Ordinal));
        Assert.Contains(applied, m => m.EndsWith("_RowLevelSecurity", StringComparison.Ordinal));

        // Guards against editing entities/configurations without adding a migration.
        Assert.False(db.Database.HasPendingModelChanges(), "The EF model has changes that are not covered by a migration.");
    }

    [Fact]
    public async Task Every_tenant_scoped_table_has_forced_rls_policy()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();

        var tenantTables = db.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .Select(e => e.GetTableName()!)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(tenantTables);
        Assert.Equal(tenantTables, TenantIsolatedTables.All.OrderBy(t => t, StringComparer.Ordinal).ToList());

        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        foreach (var table in tenantTables)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT c.relrowsecurity, c.relforcerowsecurity,
                       (SELECT count(*) FROM pg_policies p WHERE p.tablename = c.relname AND p.policyname = 'tenant_isolation')
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = @table
                """,
                connection);
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"table {table} not found");
            Assert.True(reader.GetBoolean(0), $"{table}: RLS not enabled");
            Assert.True(reader.GetBoolean(1), $"{table}: RLS not forced");
            Assert.Equal(1, reader.GetInt64(2));
        }
    }

    [Fact]
    public async Task Platform_tables_are_not_rls_protected_but_tenant_fk_exists_everywhere()
    {
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            "SELECT relrowsecurity FROM pg_class WHERE relname IN ('tenants', 'users') AND relnamespace = 'public'::regnamespace", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                Assert.False(reader.GetBoolean(0));
            }

            Assert.Equal(2, rows);
        }

        foreach (var table in TenantIsolatedTables.All)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name
                WHERE tc.table_name = @table AND tc.constraint_type = 'FOREIGN KEY' AND kcu.column_name = 'tenant_id'
                """,
                connection);
            command.Parameters.AddWithValue("table", table);
            Assert.True((long)(await command.ExecuteScalarAsync())! >= 1, $"{table} has no FK on tenant_id");
        }
    }

    [Fact]
    public async Task Migrations_can_be_rolled_back_and_reapplied()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();
        db.Database.SetConnectionString(fixture.Database.MigratorConnectionString);

        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("0");
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());

        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
