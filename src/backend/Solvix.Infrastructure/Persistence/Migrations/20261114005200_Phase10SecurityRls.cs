using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the Phase 10 security tables. Hand-written for the same reason as the Phase 01 RLS migration.
/// <c>security_audit</c> is append-only: it gets separate SELECT/INSERT policies instead of the all-commands
/// <c>tenant_isolation</c> policy, a trigger that rejects UPDATE/DELETE/TRUNCATE for every role, and the runtime role
/// loses the UPDATE/DELETE/TRUNCATE privileges.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261114005200_Phase10SecurityRls")]
public sealed class Phase10SecurityRlsMigration : Migration
{
    /// <summary>Tables protected by the standard tenant isolation policy.</summary>
    public static readonly string[] TenantTables =
    [
        "security_endpoints",
        "security_snapshots",
        "security_findings",
        "security_alerts",
        "security_events",
        "security_policies",
        "security_advisories",
        "security_remediations",
    ];

    /// <summary>Tables that are append-only and carry dedicated policies.</summary>
    public static readonly string[] AppendOnlyTables = ["security_audit"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        migrationBuilder.Sql("ALTER TABLE \"security_audit\" ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"security_audit\" FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql(
            """
            CREATE POLICY audit_select ON "security_audit" FOR SELECT
                USING (app_rls_bypass() OR tenant_id = app_current_tenant_id());
            """);
        migrationBuilder.Sql(
            """
            CREATE POLICY audit_insert ON "security_audit" FOR INSERT
                WITH CHECK (app_rls_bypass() OR tenant_id = app_current_tenant_id());
            """);
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION security_audit_immutable() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'security_audit is append-only: % is not permitted', TG_OP
                    USING ERRCODE = '42501';
            END;
            $$;
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER security_audit_no_change BEFORE UPDATE OR DELETE ON "security_audit"
                FOR EACH ROW EXECUTE FUNCTION security_audit_immutable();
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER security_audit_no_truncate BEFORE TRUNCATE ON "security_audit"
                FOR EACH STATEMENT EXECUTE FUNCTION security_audit_immutable();
            """);

        // Default privileges grant the runtime role UPDATE/DELETE on new tables; take them back for the audit table.
        migrationBuilder.Sql(
            """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'solvix_app') THEN
                    REVOKE UPDATE, DELETE, TRUNCATE ON "security_audit" FROM solvix_app;
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS security_audit_no_truncate ON \"security_audit\";");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS security_audit_no_change ON \"security_audit\";");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS security_audit_immutable();");
        migrationBuilder.Sql("DROP POLICY IF EXISTS audit_insert ON \"security_audit\";");
        migrationBuilder.Sql("DROP POLICY IF EXISTS audit_select ON \"security_audit\";");
        migrationBuilder.Sql("ALTER TABLE \"security_audit\" NO FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"security_audit\" DISABLE ROW LEVEL SECURITY;");
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
