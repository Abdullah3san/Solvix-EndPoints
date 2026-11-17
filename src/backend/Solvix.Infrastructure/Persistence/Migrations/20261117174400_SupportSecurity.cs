using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the help-desk tables plus the cross-module references of a ticket
/// (device, assignee). Both are tenant-safe and only clear their own column when the target is
/// deleted, so removing a device or a user never deletes the ticket history.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261117174400_SupportSecurity")]
public sealed class SupportSecurityMigration : Migration
{
    public static readonly string[] TenantTables = ["tickets", "ticket_comments", "device_chat_messages"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql(
            """
            ALTER TABLE tickets
                ADD CONSTRAINT fk_tickets_devices_tenant_id_device_id
                    FOREIGN KEY (tenant_id, device_id) REFERENCES devices (tenant_id, id) ON DELETE SET NULL (device_id),
                ADD CONSTRAINT fk_tickets_users_assigned_user_id
                    FOREIGN KEY (assigned_user_id) REFERENCES users (id) ON DELETE SET NULL;
            CREATE INDEX ix_tickets_assigned_user_id ON tickets (tenant_id, assigned_user_id) WHERE assigned_user_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS ix_tickets_assigned_user_id;
            ALTER TABLE tickets
                DROP CONSTRAINT IF EXISTS fk_tickets_users_assigned_user_id,
                DROP CONSTRAINT IF EXISTS fk_tickets_devices_tenant_id_device_id;
            """);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
