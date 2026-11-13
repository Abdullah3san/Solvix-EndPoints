using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the mobile device management tables (Phase 08). Kept separate from the Phase 01
/// migration so existing deployments are never asked to re-create policies. The integration test
/// <c>MigrationTests.Every_tenant_scoped_table_has_forced_rls_policy</c> fails if a tenant-owned table is
/// missing from both lists.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261113114900_MobileDeviceManagementRls")]
public sealed class MobileDeviceManagementRlsMigration : Migration
{
    /// <summary>Tables protected by the tenant isolation policy in this migration.</summary>
    public static readonly string[] TenantTables =
    [
        "mobile_apps",
        "mobile_device_commands",
        "mobile_device_events",
        "mobile_devices",
        "mobile_enrollment_tokens",
        "mobile_enterprises",
        "mobile_policies",
        "mobile_policy_apps",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
    }
}
