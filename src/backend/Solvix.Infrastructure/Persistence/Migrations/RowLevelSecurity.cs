namespace Solvix.Infrastructure.Persistence.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Helpers to declare PostgreSQL Row-Level Security in migrations. Tables are
/// FORCEd so that even the owning role is subject to the policy.
/// </summary>
public static class RowLevelSecurity
{
    public const string PolicyName = "tenant_isolation";

    /// <summary>
    /// Every tenant-owned table that must carry the tenant isolation policy, across all
    /// migrations. A new tenant-owned table belongs in the list of the migration that
    /// protects it; <c>MigrationTests</c> compares this with the EF model and fails on a gap.
    /// </summary>
    public static IReadOnlyList<string> AllTenantTables { get; } =
    [
        .. RowLevelSecurityMigration.TenantTables,
        .. IdentityAccessRowLevelSecurityMigration.TenantTables,
        .. DeviceManagementSecurityMigration.TenantTables,
        .. RealTimeMonitoringSecurityMigration.TenantTables,
        .. AssetManagementSecurityMigration.TenantTables,
    ];

    public static void CreateHelperFunctions(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION app_current_tenant_id() RETURNS uuid
            LANGUAGE sql STABLE PARALLEL SAFE AS $$
                SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
            $$;
            """);
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION app_rls_bypass() RETURNS boolean
            LANGUAGE sql STABLE PARALLEL SAFE AS $$
                SELECT COALESCE(current_setting('app.rls_bypass', true), '') = 'on'
            $$;
            """);
    }

    public static void DropHelperFunctions(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_rls_bypass();");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_current_tenant_id();");
    }

    public static void EnableTenantIsolation(MigrationBuilder migrationBuilder, string table, string tenantColumn = "tenant_id")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var quoted = Quote(table);
        migrationBuilder.Sql($"ALTER TABLE {quoted} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {quoted} FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"DROP POLICY IF EXISTS {PolicyName} ON {quoted};");
        migrationBuilder.Sql(
            $"""
            CREATE POLICY {PolicyName} ON {quoted}
                FOR ALL
                USING (app_rls_bypass() OR {Quote(tenantColumn)} = app_current_tenant_id())
                WITH CHECK (app_rls_bypass() OR {Quote(tenantColumn)} = app_current_tenant_id());
            """);
    }

    public static void DisableTenantIsolation(MigrationBuilder migrationBuilder, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var quoted = Quote(table);
        migrationBuilder.Sql($"DROP POLICY IF EXISTS {PolicyName} ON {quoted};");
        migrationBuilder.Sql($"ALTER TABLE {quoted} NO FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {quoted} DISABLE ROW LEVEL SECURITY;");
    }

    private static string Quote(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
        {
            throw new ArgumentException($"'{identifier}' is not a valid snake_case identifier.", nameof(identifier));
        }

        return $"\"{identifier}\"";
    }
}
