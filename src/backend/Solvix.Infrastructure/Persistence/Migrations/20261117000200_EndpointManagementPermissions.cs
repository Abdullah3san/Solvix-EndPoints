using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Grants the endpoint management permissions (Phase 09) to the built-in roles of existing tenants,
/// matching <c>SystemRoles</c> at the time of this migration. New tenants get them from the
/// live role definitions when they are provisioned.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261117000200_EndpointManagementPermissions")]
public sealed class EndpointManagementPermissionsMigration : Migration
{
    // Snapshot of the built-in role definitions at this migration (not the live SystemRoles).
    private static readonly (string Role, string[] Permissions)[] BuiltInEndpoints =
    [
        ("super_admin", ["endpoints.view", "endpoints.manage", "endpoints.approve_high"]),
        ("tenant_admin", ["endpoints.view", "endpoints.manage", "endpoints.approve_high"]),
        ("it_manager", ["endpoints.view", "endpoints.manage"]),
        ("it_technician", ["endpoints.view"]),
        ("security_admin", ["endpoints.view"]),
        ("read_only_auditor", ["endpoints.view"]),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Role data spans all tenants: platform operation, transaction-local bypass.
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");
        var builtIn = string.Join(",\n    ", BuiltInEndpoints.SelectMany(r => r.Permissions.Select(p => $"('{r.Role}', '{p}')")));
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
        migrationBuilder.Sql("DELETE FROM role_permissions WHERE permission IN ('endpoints.view', 'endpoints.manage', 'endpoints.approve_high');");
    }
}
