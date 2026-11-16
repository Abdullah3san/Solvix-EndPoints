using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the helpdesk tables, the tenant-safe link from tickets to devices (declared here because it
/// crosses a module boundary) and default SLA policy/categories for workspaces that already exist.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261116184400_HelpdeskSecurity")]
public sealed class HelpdeskSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "ticket_categories",
        "technicians",
        "sla_policies",
        "sla_targets",
        "escalation_rules",
        "ticket_counters",
        "inbound_email_channels",
        "tickets",
        "ticket_comments",
        "ticket_attachments",
        "ticket_attachment_contents",
        "ticket_events",
        "ticket_resolutions",
        "ticket_applied_escalations",
    ];

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
                    FOREIGN KEY (tenant_id, device_id) REFERENCES devices (tenant_id, id) ON DELETE SET NULL (device_id);
            CREATE INDEX ix_tickets_tenant_id_device_id ON tickets (tenant_id, device_id) WHERE device_id IS NOT NULL;
            """);

        // Defaults for existing workspaces (new ones get them at registration).
        migrationBuilder.Sql(
            """
            SELECT set_config('app.rls_bypass', 'on', true);
            INSERT INTO sla_policies (id, tenant_id, name, normalized_name, is_default, is_active, business_hours_only, time_zone_id,
                                      workday_start_minute, workday_end_minute, work_days_mask, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), t.id, 'Standard', 'standard', true, true, false, 'UTC', 540, 1020, 62, now(), now()
            FROM tenants t WHERE NOT EXISTS (SELECT 1 FROM sla_policies p WHERE p.tenant_id = t.id);
            INSERT INTO sla_targets (id, tenant_id, sla_policy_id, priority, first_response_minutes, resolution_minutes, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), p.tenant_id, p.id, v.priority, v.fr, v.res, now(), now()
            FROM sla_policies p
            CROSS JOIN (VALUES ('Critical', 30, 240), ('High', 120, 480), ('Medium', 480, 4320), ('Low', 1440, 10080)) AS v(priority, fr, res)
            WHERE p.is_default AND NOT EXISTS (SELECT 1 FROM sla_targets x WHERE x.sla_policy_id = p.id);
            INSERT INTO ticket_categories (id, tenant_id, name, normalized_name, is_active, keywords, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), t.id, c.name, lower(c.name), true, ARRAY[]::text[], now(), now()
            FROM tenants t
            CROSS JOIN (VALUES ('Hardware'), ('Software'), ('Network'), ('Access & Accounts'), ('Security'), ('Other')) AS c(name)
            WHERE NOT EXISTS (SELECT 1 FROM ticket_categories x WHERE x.tenant_id = t.id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS ix_tickets_tenant_id_device_id;
            ALTER TABLE tickets DROP CONSTRAINT IF EXISTS fk_tickets_devices_tenant_id_device_id;
            """);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
