using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Enables PostgreSQL Row-Level Security on every tenant-owned table.
/// Hand-written (no model changes) so that regenerating the schema migration
/// never silently drops the policies. The integration test
/// <c>MigrationTests.Every_tenant_scoped_table_has_forced_rls_policy</c> fails
/// if a new tenant-owned table is added without being listed here.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261111213000_RowLevelSecurity")]
public sealed class RowLevelSecurityMigration : Migration
{
    /// <summary>Tables protected by the tenant isolation policy.</summary>
    public static readonly string[] TenantTables =
    [
        "tenant_memberships",
        "company_profiles",
        "branches",
        "departments",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        RowLevelSecurity.CreateHelperFunctions(migrationBuilder);
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }

        RowLevelSecurity.DropHelperFunctions(migrationBuilder);
    }
}
