using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Grants the endpoint security permissions (Phase 10) to the built-in roles of existing tenants,
/// matching <c>SystemRoles</c> at the time of this migration. New tenants get them from the
/// live role definitions when they are provisioned.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261114090500_EndpointSecurityPermissions")]
public sealed class EndpointSecurityPermissionsMigration : Migration
{
    private static readonly string[] AllEndpointSecurity =
        ["posture.view", "posture.manage", "posture.approve", "posture.approve_high"];

    // Snapshot of the built-in role definitions at this migration (not the live SystemRoles).
    private static readonly (string Role, string[] Permissions)[] BuiltInEndpointSecurity =
    [
        ("super_admin", AllEndpointSecurity),
        ("tenant_admin", AllEndpointSecurity),
        ("it_manager", ["posture.view", "posture.manage", "posture.approve"]),
        ("security_admin", AllEndpointSecurity),
        ("read_only_auditor", ["posture.view"]),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Role data spans all tenants: platform operation, transaction-local bypass.
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");
        var builtIn = string.Join(",\n    ", BuiltInEndpointSecurity.SelectMany(r => r.Permissions.Select(p => $"('{r.Role}', '{p}')")));
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
        migrationBuilder.Sql("DELETE FROM role_permissions WHERE permission LIKE 'posture.%';");
    }
}
