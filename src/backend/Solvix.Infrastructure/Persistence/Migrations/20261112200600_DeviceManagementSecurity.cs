using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the device management tables plus the database-level
/// integrity of device assignments. The assignment foreign keys cross module
/// boundaries (Devices → Identity/Organization) and are therefore declared here
/// rather than in the EF model; they are tenant-safe composite keys and only
/// clear the referencing column (PostgreSQL 15+ <c>SET NULL (column)</c>), so a
/// deleted branch/department/user never orphans or deletes a device.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261112200600_DeviceManagementSecurity")]
public sealed class DeviceManagementSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "devices",
        "device_credentials",
        "device_inventories",
        "device_groups",
        "device_group_memberships",
        "enrollment_tokens",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql(
            """
            ALTER TABLE devices
                ADD CONSTRAINT fk_devices_users_assigned_user_id
                    FOREIGN KEY (assigned_user_id) REFERENCES users (id) ON DELETE SET NULL,
                ADD CONSTRAINT fk_devices_branches_tenant_id_branch_id
                    FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                ADD CONSTRAINT fk_devices_departments_tenant_id_department_id
                    FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id);
            CREATE INDEX ix_devices_tenant_id_branch_id ON devices (tenant_id, branch_id) WHERE branch_id IS NOT NULL;
            CREATE INDEX ix_devices_tenant_id_department_id ON devices (tenant_id, department_id) WHERE department_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS ix_devices_tenant_id_department_id;
            DROP INDEX IF EXISTS ix_devices_tenant_id_branch_id;
            ALTER TABLE devices
                DROP CONSTRAINT IF EXISTS fk_devices_departments_tenant_id_department_id,
                DROP CONSTRAINT IF EXISTS fk_devices_branches_tenant_id_branch_id,
                DROP CONSTRAINT IF EXISTS fk_devices_users_assigned_user_id;
            """);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}

/// <summary>Every table protected by the tenant isolation policy, across all migrations.</summary>
public static class TenantIsolatedTables
{
    public static IReadOnlyList<string> All { get; } =
        [
            .. RowLevelSecurityMigration.TenantTables,
            .. DeviceManagementSecurityMigration.TenantTables,
            .. RealTimeMonitoringSecurityMigration.TenantTables,
            .. AssetManagementSecurityMigration.TenantTables,
        ];
}
