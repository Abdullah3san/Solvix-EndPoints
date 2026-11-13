using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Database-level protection for asset management:
///  * forced Row-Level Security on every asset table;
///  * tenant-safe composite foreign keys that cross module boundaries
///    (Assets → Organization / Identity / Devices). Organisation references only
///    clear the referencing column when a branch or department is deleted;
///    the current holder of an asset must be a real user (deleting that user is
///    refused until the asset is returned); an endpoint association disappears
///    with its device;
///  * an append-only audit log: UPDATE and TRUNCATE are always rejected, DELETE
///    only inside an explicit platform scope (tenant deletion workflows).
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261112224300_AssetManagementSecurity")]
public sealed class AssetManagementSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "assets",
        "asset_assignments",
        "asset_transfers",
        "asset_maintenance_records",
        "asset_vendors",
        "asset_settings",
        "asset_tag_sequences",
        "asset_physical_audits",
        "asset_physical_audit_items",
        "asset_device_links",
        "asset_audit_log",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql(
            """
            ALTER TABLE assets
                ADD CONSTRAINT fk_assets_users_assigned_user_id
                    FOREIGN KEY (assigned_user_id) REFERENCES users (id) ON DELETE NO ACTION,
                ADD CONSTRAINT fk_assets_branches_tenant_id_branch_id
                    FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                ADD CONSTRAINT fk_assets_departments_tenant_id_department_id
                    FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id);
            CREATE INDEX ix_assets_tenant_id_branch_id ON assets (tenant_id, branch_id) WHERE branch_id IS NOT NULL;
            CREATE INDEX ix_assets_tenant_id_department_id ON assets (tenant_id, department_id) WHERE department_id IS NOT NULL;

            ALTER TABLE asset_assignments
                ADD CONSTRAINT fk_asset_assignments_branches_tenant_id_branch_id
                    FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                ADD CONSTRAINT fk_asset_assignments_departments_tenant_id_department_id
                    FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id);

            ALTER TABLE asset_transfers
                ADD CONSTRAINT fk_asset_transfers_branches_from
                    FOREIGN KEY (tenant_id, from_branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (from_branch_id),
                ADD CONSTRAINT fk_asset_transfers_branches_to
                    FOREIGN KEY (tenant_id, to_branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (to_branch_id),
                ADD CONSTRAINT fk_asset_transfers_departments_from
                    FOREIGN KEY (tenant_id, from_department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (from_department_id),
                ADD CONSTRAINT fk_asset_transfers_departments_to
                    FOREIGN KEY (tenant_id, to_department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (to_department_id);

            ALTER TABLE asset_physical_audits
                ADD CONSTRAINT fk_asset_physical_audits_branches_tenant_id_branch_id
                    FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                ADD CONSTRAINT fk_asset_physical_audits_departments_tenant_id_department_id
                    FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id);

            ALTER TABLE asset_physical_audit_items
                ADD CONSTRAINT fk_asset_physical_audit_items_branches_expected
                    FOREIGN KEY (tenant_id, expected_branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (expected_branch_id);

            ALTER TABLE asset_device_links
                ADD CONSTRAINT fk_asset_device_links_devices_tenant_id_device_id
                    FOREIGN KEY (tenant_id, device_id) REFERENCES devices (tenant_id, id) ON DELETE CASCADE;

            CREATE FUNCTION asset_audit_log_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' AND app_rls_bypass() THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'asset_audit_log is append-only (% rejected)', TG_OP
                    USING ERRCODE = 'insufficient_privilege';
            END
            $$;
            CREATE TRIGGER trg_asset_audit_log_append_only
                BEFORE UPDATE OR DELETE ON asset_audit_log
                FOR EACH ROW EXECUTE FUNCTION asset_audit_log_append_only();
            CREATE TRIGGER trg_asset_audit_log_no_truncate
                BEFORE TRUNCATE ON asset_audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION asset_audit_log_append_only();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_asset_audit_log_no_truncate ON asset_audit_log;
            DROP TRIGGER IF EXISTS trg_asset_audit_log_append_only ON asset_audit_log;
            DROP FUNCTION IF EXISTS asset_audit_log_append_only();
            ALTER TABLE asset_device_links DROP CONSTRAINT IF EXISTS fk_asset_device_links_devices_tenant_id_device_id;
            ALTER TABLE asset_physical_audit_items DROP CONSTRAINT IF EXISTS fk_asset_physical_audit_items_branches_expected;
            ALTER TABLE asset_physical_audits
                DROP CONSTRAINT IF EXISTS fk_asset_physical_audits_departments_tenant_id_department_id,
                DROP CONSTRAINT IF EXISTS fk_asset_physical_audits_branches_tenant_id_branch_id;
            ALTER TABLE asset_transfers
                DROP CONSTRAINT IF EXISTS fk_asset_transfers_departments_to,
                DROP CONSTRAINT IF EXISTS fk_asset_transfers_departments_from,
                DROP CONSTRAINT IF EXISTS fk_asset_transfers_branches_to,
                DROP CONSTRAINT IF EXISTS fk_asset_transfers_branches_from;
            ALTER TABLE asset_assignments
                DROP CONSTRAINT IF EXISTS fk_asset_assignments_departments_tenant_id_department_id,
                DROP CONSTRAINT IF EXISTS fk_asset_assignments_branches_tenant_id_branch_id;
            DROP INDEX IF EXISTS ix_assets_tenant_id_department_id;
            DROP INDEX IF EXISTS ix_assets_tenant_id_branch_id;
            ALTER TABLE assets
                DROP CONSTRAINT IF EXISTS fk_assets_departments_tenant_id_department_id,
                DROP CONSTRAINT IF EXISTS fk_assets_branches_tenant_id_branch_id,
                DROP CONSTRAINT IF EXISTS fk_assets_users_assigned_user_id;
            """);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
