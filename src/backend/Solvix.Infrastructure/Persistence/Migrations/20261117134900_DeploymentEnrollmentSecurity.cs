using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security and cross-module integrity for the approval-based enrollment (deployment profiles and
/// pending enrollments). The references to branches, departments, device groups and devices cross module
/// boundaries, so they are declared here: tenant-safe composite keys that only clear the referencing column
/// (PostgreSQL 15+ <c>SET NULL (column)</c>) when the referenced row is deleted.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261117134900_DeploymentEnrollmentSecurity")]
public sealed class DeploymentEnrollmentSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "deployment_profiles",
        "pending_enrollments",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql(
            """
            ALTER TABLE deployment_profiles
                ADD CONSTRAINT fk_deployment_profiles_branches_tenant_id_branch_id
                    FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                ADD CONSTRAINT fk_deployment_profiles_departments_tenant_id_department_id
                    FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id),
                ADD CONSTRAINT fk_deployment_profiles_device_groups_tenant_id_device_group_id
                    FOREIGN KEY (tenant_id, device_group_id) REFERENCES device_groups (tenant_id, id) ON DELETE SET NULL (device_group_id);
            ALTER TABLE pending_enrollments
                ADD CONSTRAINT fk_pending_enrollments_devices_tenant_id_existing_device_id
                    FOREIGN KEY (tenant_id, existing_device_id) REFERENCES devices (tenant_id, id) ON DELETE SET NULL (existing_device_id),
                ADD CONSTRAINT fk_pending_enrollments_devices_tenant_id_device_id
                    FOREIGN KEY (tenant_id, device_id) REFERENCES devices (tenant_id, id) ON DELETE SET NULL (device_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE pending_enrollments
                DROP CONSTRAINT IF EXISTS fk_pending_enrollments_devices_tenant_id_device_id,
                DROP CONSTRAINT IF EXISTS fk_pending_enrollments_devices_tenant_id_existing_device_id;
            ALTER TABLE deployment_profiles
                DROP CONSTRAINT IF EXISTS fk_deployment_profiles_device_groups_tenant_id_device_group_id,
                DROP CONSTRAINT IF EXISTS fk_deployment_profiles_departments_tenant_id_department_id,
                DROP CONSTRAINT IF EXISTS fk_deployment_profiles_branches_tenant_id_branch_id;
            """);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
