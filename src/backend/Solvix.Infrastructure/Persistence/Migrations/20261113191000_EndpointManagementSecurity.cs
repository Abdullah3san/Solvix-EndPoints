using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Phase 09 database protections, hand-written so that regenerating schema
/// migrations never drops them:
/// <list type="bullet">
/// <item>FORCE ROW LEVEL SECURITY + the <c>tenant_isolation</c> policy on every new tenant table;</item>
/// <item><c>audit_entries</c> is append-only: UPDATE, DELETE and TRUNCATE are rejected by triggers,
///   for the runtime role and the schema owner alike.</item>
/// </list>
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261113191000_EndpointManagementSecurity")]
public sealed class EndpointManagementSecurityMigration : Migration
{
    /// <summary>Tenant tables introduced in Phase 09.</summary>
    public static readonly string[] TenantTables =
    [
        "audit_entries",
        "devices",
        "device_groups",
        "device_group_members",
        "enrollment_tokens",
        "maintenance_windows",
        "patches",
        "device_patch_states",
        "device_update_summaries",
        "patch_auto_approval_rules",
        "patch_deployments",
        "patch_deployment_patches",
        "patch_deployment_targets",
        "patch_deployment_jobs",
        "device_restart_requests",
        "configuration_policies",
        "configuration_policy_versions",
        "configuration_assignments",
        "device_configuration_states",
        "configuration_drift_events",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION audit_entries_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'audit_entries is append-only (% rejected)', TG_OP
                    USING ERRCODE = 'insufficient_privilege';
            END
            $$;
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER audit_entries_no_update_delete
                BEFORE UPDATE OR DELETE ON audit_entries
                FOR EACH ROW EXECUTE FUNCTION audit_entries_append_only();
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER audit_entries_no_truncate
                BEFORE TRUNCATE ON audit_entries
                FOR EACH STATEMENT EXECUTE FUNCTION audit_entries_append_only();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_entries_no_truncate ON audit_entries;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_entries_no_update_delete ON audit_entries;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_entries_append_only();");
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
