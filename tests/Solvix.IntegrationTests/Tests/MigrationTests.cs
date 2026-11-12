namespace Solvix.IntegrationTests.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Persistence.Migrations;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Domain;
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
        Assert.Contains(applied, m => m.EndsWith("_IdentityAccessManagement", StringComparison.Ordinal));
        Assert.Contains(applied, m => m.EndsWith("_IdentityAccessRowLevelSecurity", StringComparison.Ordinal));

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
        Assert.Equal(tenantTables, RowLevelSecurity.AllTenantTables.OrderBy(t => t, StringComparer.Ordinal).ToList());

        // Rows with an optional tenant (the audit trail) carry the same policy.
        tenantTables.AddRange(db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantVisible).IsAssignableFrom(e.ClrType))
            .Select(e => e.GetTableName()!));
        Assert.Contains(IdentityAccessRowLevelSecurityMigration.AuditTable, tenantTables);

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
            """
            SELECT relrowsecurity FROM pg_class
            WHERE relname IN ('tenants', 'users', 'user_tokens', 'user_password_history', 'user_recovery_codes')
              AND relnamespace = 'public'::regnamespace
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                Assert.False(reader.GetBoolean(0));
            }

            Assert.Equal(5, rows);
        }

        foreach (var table in RowLevelSecurity.AllTenantTables)
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
    public async Task System_roles_in_the_database_match_the_catalog()
    {
        // Provisioned by code at registration.
        var tenant = await fixture.RegisterTenantAsync();
        await AssertSystemRolesMatchCatalogAsync(tenant.TenantId);
    }

    [Fact]
    public async Task Phase_01_tenants_and_members_are_carried_over_by_the_upgrade_and_back()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();
        db.Database.SetConnectionString(fixture.Database.MigratorConnectionString);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // Back to the Phase 01 schema, then create data the way Phase 01 stored it.
        await migrator.MigrateAsync("20261111213000_RowLevelSecurity");
        var tenantId = Guid.NewGuid();
        var (owner, admin, member) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await fixture.ExecuteAsOwnerAsync(
            "INSERT INTO tenants (id, name, slug, status, created_at_utc, updated_at_utc) VALUES (@t, 'Legacy Co', @s, 'Active', now(), now())",
            ("t", tenantId), ("s", $"legacy-{tenantId:N}"[..20]));
        foreach (var (userId, role) in new[] { (owner, "Owner"), (admin, "Admin"), (member, "Member") })
        {
            await fixture.ExecuteAsOwnerAsync(
                """
                INSERT INTO users (id, email, normalized_email, display_name, password_hash, is_active, created_at_utc, updated_at_utc)
                VALUES (@u, @e, upper(@e), @r, 'hash', TRUE, now() - interval '40 days', now())
                """,
                ("u", userId), ("e", $"{role.ToLowerInvariant()}-{userId:N}@legacy.test"), ("r", role));
            await fixture.ExecuteAsOwnerAsync(
                "INSERT INTO tenant_memberships (id, tenant_id, user_id, role, is_active, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, @u, @r, TRUE, now(), now())",
                ("t", tenantId), ("u", userId), ("r", role));
        }

        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Every tenant got the built-in roles exactly as the catalogue defines them, and a policy.
        await AssertSystemRolesMatchCatalogAsync(tenantId);
        Assert.Equal(1L, await fixture.ScalarAsOwnerAsync<long>("SELECT count(*) FROM tenant_security_policies WHERE tenant_id = @t AND password_min_length = 10", ("t", tenantId)));

        async Task<string?> RoleOfAsync(Guid userId) => await fixture.ScalarAsOwnerAsync<string>(
            """
            SELECT string_agg(coalesce(r.system_key, r.name), ',') FROM tenant_memberships m
            JOIN membership_roles mr ON mr.membership_id = m.id JOIN roles r ON r.id = mr.role_id
            WHERE m.user_id = @u
            """, ("u", userId));
        Assert.Equal("super_admin", await RoleOfAsync(owner));
        Assert.Equal("tenant_admin", await RoleOfAsync(admin));
        Assert.Equal("Member", await RoleOfAsync(member));

        // Phase 01 members could read the organisation and the member list, and that is all they get.
        var memberPermissions = await fixture.ScalarAsOwnerAsync<string>(
            """
            SELECT string_agg(rp.permission, ',' ORDER BY rp.permission) FROM roles r JOIN role_permissions rp ON rp.role_id = r.id
            WHERE r.tenant_id = @t AND r.system_key IS NULL
            """, ("t", tenantId));
        Assert.Equal("organization.view,users.view", memberPermissions);

        // Existing accounts keep working: verified, and with a sensible password date.
        Assert.Equal(3L, await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM users WHERE id = ANY(@ids) AND email_verified_at_utc IS NOT NULL AND password_changed_at_utc = created_at_utc",
            ("ids", new[] { owner, admin, member })));

        // And the way back restores the Phase 01 role column from the assignments.
        await migrator.MigrateAsync("20261111213000_RowLevelSecurity");
        Assert.Equal("Admin,Member,Owner", await fixture.ScalarAsOwnerAsync<string>(
            "SELECT string_agg(role, ',' ORDER BY role) FROM tenant_memberships WHERE tenant_id = @t", ("t", tenantId)));
        await migrator.MigrateAsync();
    }

    private async Task AssertSystemRolesMatchCatalogAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass', 'on', false)", connection))
        {
            await bypass.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT r.system_key, r.name, r.description, coalesce(string_agg(rp.permission, ',' ORDER BY rp.permission), '')
            FROM roles r LEFT JOIN role_permissions rp ON rp.role_id = r.id
            WHERE r.tenant_id = @t AND r.system_key IS NOT NULL
            GROUP BY r.system_key, r.name, r.description
            """, connection);
        command.Parameters.AddWithValue("t", tenantId);
        var actual = new Dictionary<string, (string Name, string Description, string Permissions)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }

        Assert.Equal(SystemRoles.All.Select(r => r.Key).Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var definition in SystemRoles.All)
        {
            var row = actual[definition.Key];
            Assert.Equal(definition.Name, row.Name);
            Assert.Equal(definition.Description, row.Description);
            Assert.Equal(string.Join(",", definition.Permissions.Order(StringComparer.Ordinal)), row.Permissions);
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
