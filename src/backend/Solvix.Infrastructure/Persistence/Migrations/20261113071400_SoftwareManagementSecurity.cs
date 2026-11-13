using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Database-level protection and role data for enterprise software management (Phase 07):
///  * forced Row-Level Security on every software management table;
///  * an append-only software audit trail (UPDATE and TRUNCATE always rejected, DELETE only inside
///    an explicit platform scope for tenant deletion);
///  * the software permissions for the built-in roles of existing tenants.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261113071400_SoftwareManagementSecurity")]
public sealed class SoftwareManagementSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "software_packages",
        "software_deployments",
        "software_deployment_targets",
        "software_deployment_approvals",
        "software_deployment_groups",
        "software_deployment_policies",
        "software_restriction_rules",
        "software_inventory_items",
        "software_audit_events",
    ];

    private static readonly string[] AllSoftware =
    [
        "software.view", "software.packages.manage", "software.deploy", "software.approve", "software.policies.manage",
    ];

    // Snapshot of the built-in role definitions at this migration (not the live SystemRoles).
    private static readonly (string Role, string[] Permissions)[] BuiltInSoftware =
    [
        ("super_admin", AllSoftware),
        ("tenant_admin", AllSoftware),
        ("it_manager", AllSoftware),
        ("it_technician", ["software.view", "software.deploy"]),
        ("asset_manager", ["software.view"]),
        ("security_admin", ["software.view"]),
        ("read_only_auditor", ["software.view"]),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql(
            """
            CREATE FUNCTION software_audit_events_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' AND app_rls_bypass() THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'software_audit_events is append-only (% rejected)', TG_OP
                    USING ERRCODE = 'insufficient_privilege';
            END
            $$;
            CREATE TRIGGER trg_software_audit_events_append_only
                BEFORE UPDATE OR DELETE ON software_audit_events
                FOR EACH ROW EXECUTE FUNCTION software_audit_events_append_only();
            CREATE TRIGGER trg_software_audit_events_no_truncate
                BEFORE TRUNCATE ON software_audit_events
                FOR EACH STATEMENT EXECUTE FUNCTION software_audit_events_append_only();
            """);

        // Role data spans all tenants: platform operation, transaction-local bypass.
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");
        var builtIn = string.Join(",\n    ", BuiltInSoftware.SelectMany(r => r.Permissions.Select(p => $"('{r.Role}', '{p}')")));
        migrationBuilder.Sql($"""
            INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, now(), now()
            FROM roles r
            JOIN (VALUES
                {builtIn}
            ) AS p(role_key, permission) ON p.role_key = r.system_key
            ON CONFLICT DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");
        migrationBuilder.Sql("DELETE FROM role_permissions WHERE permission LIKE 'software.%';");
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_software_audit_events_no_truncate ON software_audit_events;
            DROP TRIGGER IF EXISTS trg_software_audit_events_append_only ON software_audit_events;
            DROP FUNCTION IF EXISTS software_audit_events_append_only();
            """);
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
