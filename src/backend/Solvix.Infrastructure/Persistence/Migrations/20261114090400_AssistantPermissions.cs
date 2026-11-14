using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Grants the AI assistant permissions (Phase 11) to the built-in roles of existing tenants,
/// matching <c>SystemRoles</c> at the time of this migration. New tenants get them from the
/// live role definitions when they are provisioned.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261114090400_AssistantPermissions")]
public sealed class AssistantPermissionsMigration : Migration
{
    private static readonly string[] AllAssistant = ["assistant.use", "assistant.manage", "assistant.approve_high"];

    // Snapshot of the built-in role definitions at this migration (not the live SystemRoles).
    private static readonly (string Role, string[] Permissions)[] BuiltInAssistant =
    [
        ("super_admin", AllAssistant),
        ("tenant_admin", AllAssistant),
        ("it_manager", ["assistant.use", "assistant.manage"]),
        ("it_technician", ["assistant.use"]),
        ("asset_manager", ["assistant.use"]),
        ("security_admin", ["assistant.use"]),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Role data spans all tenants: platform operation, transaction-local bypass.
        migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");
        var builtIn = string.Join(",\n    ", BuiltInAssistant.SelectMany(r => r.Permissions.Select(p => $"('{r.Role}', '{p}')")));
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
        migrationBuilder.Sql("DELETE FROM role_permissions WHERE permission LIKE 'assistant.%';");
    }
}
