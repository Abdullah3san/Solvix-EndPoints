using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Database-level protection and role data for remote endpoint administration (Phase 06):
///  * forced Row-Level Security on every remote management table;
///  * an append-only remote audit trail: UPDATE and TRUNCATE are always rejected, DELETE only
///    inside an explicit platform scope (tenant deletion);
///  * remote permissions for existing tenants: the coarse Phase 02 placeholders
///    <c>devices.remote_access</c> / <c>devices.execute_command</c> (never checked by any endpoint)
///    are replaced by the granular <c>remote.*</c> keys, built-in roles receive the remote
///    permissions of their definition, and custom roles keep equivalent access.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261113061100_RemoteManagementSecurity")]
public sealed class RemoteManagementSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "remote_access_policies",
        "remote_access_scopes",
        "remote_command_executions",
        "remote_approvals",
        "remote_device_command_sequences",
        "remote_audit_events",
    ];

    private static readonly string[] AllRemote =
    [
        "remote.view", "remote.desktop", "remote.desktop.control", "remote.multimonitor", "remote.terminal", "remote.powershell",
        "remote.execute_approved_commands", "remote.file.upload", "remote.file.download", "remote.restart", "remote.shutdown",
        "remote.lock", "remote.voice_call", "remote.sessions.view", "remote.sessions.terminate", "remote.approvals.manage",
    ];

    // Snapshot of the built-in role definitions at this migration (not the live SystemRoles,
    // which may change later and would otherwise rewrite history).
    private static readonly (string Role, string[] Permissions)[] BuiltInRemote =
    [
        ("super_admin", AllRemote),
        ("tenant_admin", AllRemote),
        ("it_manager", AllRemote.Where(p => p != "remote.powershell").ToArray()),
        ("it_technician",
        [
            "remote.view", "remote.desktop", "remote.desktop.control", "remote.multimonitor", "remote.terminal",
            "remote.execute_approved_commands", "remote.file.upload", "remote.file.download", "remote.restart", "remote.lock",
            "remote.voice_call", "remote.sessions.view",
        ]),
        ("security_admin", ["remote.view", "remote.sessions.view"]),
        ("read_only_auditor", ["remote.view", "remote.sessions.view"]),
    ];

    // Custom roles that held a placeholder keep the access it described.
    private static readonly (string Old, string[] New)[] CustomRoleMapping =
    [
        ("devices.remote_access", ["remote.view", "remote.desktop", "remote.desktop.control", "remote.multimonitor", "remote.terminal", "remote.voice_call"]),
        ("devices.execute_command", ["remote.view", "remote.execute_approved_commands"]),
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
            CREATE FUNCTION remote_audit_events_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' AND app_rls_bypass() THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'remote_audit_events is append-only (% rejected)', TG_OP
                    USING ERRCODE = 'insufficient_privilege';
            END
            $$;
            CREATE TRIGGER trg_remote_audit_events_append_only
                BEFORE UPDATE OR DELETE ON remote_audit_events
                FOR EACH ROW EXECUTE FUNCTION remote_audit_events_append_only();
            CREATE TRIGGER trg_remote_audit_events_no_truncate
                BEFORE TRUNCATE ON remote_audit_events
                FOR EACH STATEMENT EXECUTE FUNCTION remote_audit_events_append_only();
            """);

        // Role data spans all tenants: platform operation, transaction-local bypass.
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");

        var custom = string.Join(",\n    ", CustomRoleMapping.SelectMany(m => m.New.Select(n => $"('{m.Old}', '{n}')")));
        migrationBuilder.Sql($"""
            INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
            SELECT DISTINCT ON (rp.tenant_id, rp.role_id, m.new_permission)
                   gen_random_uuid(), rp.tenant_id, rp.role_id, m.new_permission, now(), now()
            FROM role_permissions rp
            JOIN roles r ON r.id = rp.role_id AND r.system_key IS NULL
            JOIN (VALUES
                {custom}
            ) AS m(old_permission, new_permission) ON m.old_permission = rp.permission
            ON CONFLICT DO NOTHING;
            """);

        var builtIn = string.Join(",\n    ", BuiltInRemote.SelectMany(r => r.Permissions.Select(p => $"('{r.Role}', '{p}')")));
        migrationBuilder.Sql($"""
            INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), r.tenant_id, r.id, p.permission, now(), now()
            FROM roles r
            JOIN (VALUES
                {builtIn}
            ) AS p(role_key, permission) ON p.role_key = r.system_key
            ON CONFLICT DO NOTHING;
            """);

        migrationBuilder.Sql("DELETE FROM role_permissions WHERE permission IN ('devices.remote_access', 'devices.execute_command');");
        migrationBuilder.Sql($"UPDATE roles SET description = '{ItTechnicianDescription}', updated_at_utc = now() WHERE system_key = 'it_technician';");
    }

    private const string ItTechnicianDescription =
        "Supports users: enrols devices, connects remotely, runs approved operations and keeps asset records up to date. Cannot shut devices down, run ad-hoc PowerShell or retire devices.";

    private const string PreviousItTechnicianDescription =
        "Supports users: enrols devices, connects remotely and keeps asset records up to date. Cannot run commands or retire devices.";

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");

        // Restore the placeholders where their granular equivalents are held, then drop remote.* grants.
        migrationBuilder.Sql("""
            INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), tenant_id, role_id, 'devices.remote_access', now(), now()
            FROM role_permissions WHERE permission = 'remote.desktop'
            ON CONFLICT DO NOTHING;
            INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), tenant_id, role_id, 'devices.execute_command', now(), now()
            FROM role_permissions WHERE permission = 'remote.execute_approved_commands'
            ON CONFLICT DO NOTHING;
            DELETE FROM role_permissions WHERE permission LIKE 'remote.%';
            """);
        migrationBuilder.Sql($"UPDATE roles SET description = '{PreviousItTechnicianDescription}', updated_at_utc = now() WHERE system_key = 'it_technician';");

        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_remote_audit_events_no_truncate ON remote_audit_events;
            DROP TRIGGER IF EXISTS trg_remote_audit_events_append_only ON remote_audit_events;
            DROP FUNCTION IF EXISTS remote_audit_events_append_only();
            """);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
