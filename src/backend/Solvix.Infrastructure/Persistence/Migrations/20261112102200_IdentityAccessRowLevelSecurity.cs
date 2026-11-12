using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Database-level protection for the Identity and Access Management tables.
/// Hand-written (no model changes), like <see cref="RowLevelSecurityMigration"/>:
///
///  * every tenant-owned IAM table gets FORCE ROW LEVEL SECURITY and the
///    <c>tenant_isolation</c> policy, so roles, role assignments, sessions,
///    refresh tokens, invitations and security policies cannot cross tenants
///    even through raw SQL;
///  * the audit trail gets the same policy (rows without a tenant are visible in
///    platform scope only) and is made append-only by triggers: UPDATE, DELETE
///    and TRUNCATE are rejected for every role, including the application's.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261112102200_IdentityAccessRowLevelSecurity")]
public sealed class IdentityAccessRowLevelSecurityMigration : Migration
{
    public const string AuditTable = "auth_audit_events";

    /// <summary>Tenant-owned tables introduced by Phase 02.</summary>
    public static readonly string[] TenantTables =
    [
        "roles",
        "role_permissions",
        "membership_roles",
        "user_sessions",
        "refresh_tokens",
        "user_invitations",
        "user_invitation_roles",
        "tenant_security_policies",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }

        // tenant_id is nullable here: "tenant_id = app_current_tenant_id()" is never true for NULL,
        // so platform-level rows are readable and writable in platform scope only.
        RowLevelSecurity.EnableTenantIsolation(migrationBuilder, AuditTable);

        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION auth_audit_events_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'auth_audit_events is append-only: % is not allowed', TG_OP
                    USING ERRCODE = 'insufficient_privilege';
            END;
            $$;
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER trg_auth_audit_events_append_only
                BEFORE UPDATE OR DELETE ON auth_audit_events
                FOR EACH ROW EXECUTE FUNCTION auth_audit_events_append_only();
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER trg_auth_audit_events_no_truncate
                BEFORE TRUNCATE ON auth_audit_events
                FOR EACH STATEMENT EXECUTE FUNCTION auth_audit_events_append_only();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_auth_audit_events_no_truncate ON auth_audit_events;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_auth_audit_events_append_only ON auth_audit_events;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS auth_audit_events_append_only();");

        RowLevelSecurity.DisableTenantIsolation(migrationBuilder, AuditTable);
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
