using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityAccessManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failed_access_count",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "lockout_count",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lockout_end_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "mfa_enabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mfa_enabled_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "mfa_last_used_step",
                table: "users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mfa_pending_secret",
                table: "users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mfa_secret",
                table: "users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_changed_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deactivated_at_utc",
                table: "tenant_memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tenant_memberships_tenant_id_id",
                table: "tenant_memberships",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "auth_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_audit_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    system_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.UniqueConstraint("ak_roles_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_roles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_security_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_min_length = table.Column<int>(type: "integer", nullable: false),
                    password_require_uppercase = table.Column<bool>(type: "boolean", nullable: false),
                    password_require_lowercase = table.Column<bool>(type: "boolean", nullable: false),
                    password_require_digit = table.Column<bool>(type: "boolean", nullable: false),
                    password_require_symbol = table.Column<bool>(type: "boolean", nullable: false),
                    password_history_count = table.Column<int>(type: "integer", nullable: false),
                    password_max_age_days = table.Column<int>(type: "integer", nullable: false),
                    session_idle_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    session_absolute_lifetime_hours = table.Column<int>(type: "integer", nullable: false),
                    require_mfa = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_security_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_security_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_invitations", x => x.id);
                    table.UniqueConstraint("ak_user_invitations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_user_invitations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_invitations_users_invited_by_user_id",
                        column: x => x.invited_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_password_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_password_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_password_history_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    mfa_verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.UniqueConstraint("ak_user_sessions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_user_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "membership_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_membership_roles_roles_tenant_id_role_id",
                        columns: x => new { x.tenant_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_membership_roles_tenant_memberships_tenant_id_membership_id",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalTable: "tenant_memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_membership_roles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_tenant_id_role_id",
                        columns: x => new { x.tenant_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_permissions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_invitation_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_invitation_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_invitation_roles_roles_tenant_id_role_id",
                        columns: x => new { x.tenant_id, x.role_id },
                        principalTable: "roles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_invitation_roles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_invitation_roles_user_invitations_tenant_id_invitation~",
                        columns: x => new { x.tenant_id, x.invitation_id },
                        principalTable: "user_invitations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_user_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalTable: "user_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_events_tenant_id_event_type_occurred_at_utc",
                table: "auth_audit_events",
                columns: new[] { "tenant_id", "event_type", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_events_tenant_id_occurred_at_utc",
                table: "auth_audit_events",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_events_tenant_id_user_id_occurred_at_utc",
                table: "auth_audit_events",
                columns: new[] { "tenant_id", "user_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_roles_tenant_id_membership_id_role_id",
                table: "membership_roles",
                columns: new[] { "tenant_id", "membership_id", "role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_membership_roles_tenant_id_role_id",
                table: "membership_roles",
                columns: new[] { "tenant_id", "role_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_tenant_id_session_id",
                table: "refresh_tokens",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_tenant_id_role_id_permission",
                table: "role_permissions",
                columns: new[] { "tenant_id", "role_id", "permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_tenant_id_normalized_name",
                table: "roles",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_tenant_id_system_key",
                table: "roles",
                columns: new[] { "tenant_id", "system_key" },
                unique: true,
                filter: "system_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_security_policies_tenant_id",
                table: "tenant_security_policies",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_roles_tenant_id_invitation_id_role_id",
                table: "user_invitation_roles",
                columns: new[] { "tenant_id", "invitation_id", "role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_roles_tenant_id_role_id",
                table: "user_invitation_roles",
                columns: new[] { "tenant_id", "role_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_invitations_invited_by_user_id",
                table: "user_invitations",
                column: "invited_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_invitations_tenant_pending_email",
                table: "user_invitations",
                columns: new[] { "tenant_id", "normalized_email" },
                unique: true,
                filter: "accepted_at_utc IS NULL AND revoked_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_invitations_token_hash",
                table: "user_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_password_history_user_id_created_at_utc",
                table: "user_password_history",
                columns: new[] { "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_user_recovery_codes_user_id_code_hash",
                table: "user_recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_tenant_id_user_id",
                table: "user_sessions",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_token_hash",
                table: "user_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_user_id_purpose",
                table: "user_tokens",
                columns: new[] { "user_id", "purpose" });

            MigrateExistingData(migrationBuilder);

            // Only now that every membership has roles is the Phase 01 role column removed.
            migrationBuilder.DropColumn(
                name: "role",
                table: "tenant_memberships");
        }

        /// <summary>
        /// Carries Phase 01 data over: every existing tenant gets the built-in roles and a default
        /// security policy, and each membership's single Owner/Admin/Member value becomes a role
        /// assignment that grants what it granted before:
        ///   Owner  -> Super Administrator
        ///   Admin  -> Tenant Administrator
        ///   Member -> a custom "Member" role (read the organisation, see who is in the workspace)
        /// Existing accounts are treated as e-mail-verified so nobody is locked out by the upgrade.
        /// </summary>
        private static void MigrateExistingData(MigrationBuilder migrationBuilder)
        {
            // tenant_memberships is already under FORCE ROW LEVEL SECURITY; this is a platform-level
            // operation over all tenants. Transaction-local, so it cannot outlive the migration.
            migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");

            var roleValues = string.Join(",\n    ", SystemRoleSnapshot.Select(r => $"({Literal(r.Key)}, {Literal(r.Name)}, {Literal(r.Description)})"));
            migrationBuilder.Sql($"""
                INSERT INTO roles (id, tenant_id, name, normalized_name, description, system_key, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), t.id, r.name, upper(r.name), r.description, r.key, now(), now()
                FROM tenants t
                CROSS JOIN (VALUES
                    {roleValues}
                ) AS r(key, name, description);
                """);

            var permissionValues = string.Join(",\n    ", SystemRoleSnapshot.SelectMany(r => r.Permissions.Select(p => $"({Literal(r.Key)}, {Literal(p)})")));
            migrationBuilder.Sql($"""
                INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), ro.tenant_id, ro.id, p.permission, now(), now()
                FROM roles ro
                JOIN (VALUES
                    {permissionValues}
                ) AS p(key, permission) ON p.key = ro.system_key;
                """);

            // A plain "Member" role, only where Phase 01 members exist. Deliberately NOT one of the
            // built-in roles: none of them matches "read-only member" without granting more.
            migrationBuilder.Sql("""
                INSERT INTO roles (id, tenant_id, name, normalized_name, description, system_key, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), m.tenant_id, 'Member', 'MEMBER',
                       'Created by the upgrade for existing members: can view the organisation and the list of users.',
                       NULL, now(), now()
                FROM (SELECT DISTINCT tenant_id FROM tenant_memberships WHERE role NOT IN ('Owner', 'Admin')) m;
                """);
            migrationBuilder.Sql("""
                INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), ro.tenant_id, ro.id, p.permission, now(), now()
                FROM roles ro
                CROSS JOIN (VALUES ('organization.view'), ('users.view')) AS p(permission)
                WHERE ro.system_key IS NULL AND ro.normalized_name = 'MEMBER';
                """);

            migrationBuilder.Sql("""
                INSERT INTO membership_roles (id, tenant_id, membership_id, role_id, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), m.tenant_id, m.id, ro.id, now(), now()
                FROM tenant_memberships m
                JOIN roles ro ON ro.tenant_id = m.tenant_id AND (
                       (m.role = 'Owner' AND ro.system_key = 'super_admin')
                    OR (m.role = 'Admin' AND ro.system_key = 'tenant_admin')
                    OR (m.role NOT IN ('Owner', 'Admin') AND ro.system_key IS NULL AND ro.normalized_name = 'MEMBER'));
                """);

            // The platform baseline at the time of this migration.
            migrationBuilder.Sql("""
                INSERT INTO tenant_security_policies (
                    id, tenant_id, password_min_length, password_require_uppercase, password_require_lowercase,
                    password_require_digit, password_require_symbol, password_history_count, password_max_age_days,
                    session_idle_timeout_minutes, session_absolute_lifetime_hours, require_mfa, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), t.id, 10, FALSE, TRUE, TRUE, FALSE, 5, 0, 480, 168, FALSE, now(), now()
                FROM tenants t;
                """);

            migrationBuilder.Sql("""
                UPDATE users
                SET email_verified_at_utc = created_at_utc,
                    password_changed_at_utc = created_at_utc;
                """);
        }

        private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        private sealed record RoleSnapshot(string Key, string Name, string Description, string[] Permissions);

        /// <summary>
        /// The built-in roles exactly as they were when this migration was written. A migration must
        /// keep doing the same thing forever, so this is a frozen copy and NOT a reference to
        /// <c>SystemRoles</c>; later changes to the catalogue ship with their own migration.
        /// </summary>
        private static readonly RoleSnapshot[] SystemRoleSnapshot =
        [
            new("super_admin", "Super Administrator", "Full control of the workspace, including billing, workspace settings and every other role.",
                ["assets.create", "assets.edit", "assets.transfer", "assets.view", "audit.view", "billing.manage", "devices.enroll", "devices.execute_command", "devices.manage", "devices.remote_access", "devices.view", "mdm.manage", "organization.manage", "organization.view", "reports.view", "roles.manage", "roles.view", "security.manage", "security.view", "tenant.manage", "users.manage", "users.view"]),
            new("tenant_admin", "Tenant Administrator", "Administers users, roles, security, the organisation and all devices and assets. Cannot manage billing or workspace settings.",
                ["assets.create", "assets.edit", "assets.transfer", "assets.view", "audit.view", "devices.enroll", "devices.execute_command", "devices.manage", "devices.remote_access", "devices.view", "mdm.manage", "organization.manage", "organization.view", "reports.view", "roles.manage", "roles.view", "security.manage", "security.view", "users.manage", "users.view"]),
            new("it_manager", "IT Manager", "Runs day-to-day IT: full control of devices, MDM and assets, plus reports.",
                ["assets.create", "assets.edit", "assets.transfer", "assets.view", "devices.enroll", "devices.execute_command", "devices.manage", "devices.remote_access", "devices.view", "mdm.manage", "organization.view", "reports.view", "users.view"]),
            new("it_technician", "IT Technician", "Supports users: enrols devices, connects remotely and keeps asset records up to date. Cannot run commands or retire devices.",
                ["assets.edit", "assets.view", "devices.enroll", "devices.remote_access", "devices.view", "organization.view"]),
            new("asset_manager", "Asset Manager", "Owns the asset register: creates, edits and transfers assets.",
                ["assets.create", "assets.edit", "assets.transfer", "assets.view", "devices.view", "organization.view", "reports.view"]),
            new("security_admin", "Security Administrator", "Sets the security policy, revokes sessions and reviews audit logs. Cannot change who has which role.",
                ["audit.view", "devices.view", "organization.view", "reports.view", "roles.view", "security.manage", "security.view", "users.view"]),
            new("read_only_auditor", "Read-Only Auditor", "Can view everything, including audit logs, and change nothing.",
                ["assets.view", "audit.view", "devices.view", "organization.view", "reports.view", "roles.view", "security.view", "users.view"]),
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bring the Phase 01 role column back and fill it from the role assignments before they are dropped.
            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "tenant_memberships",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Member");

            migrationBuilder.Sql("SELECT set_config('app.rls_bypass', 'on', true);");
            migrationBuilder.Sql("""
                UPDATE tenant_memberships m
                SET role = CASE
                    WHEN EXISTS (SELECT 1 FROM membership_roles mr JOIN roles r ON r.id = mr.role_id
                                 WHERE mr.membership_id = m.id AND r.system_key = 'super_admin') THEN 'Owner'
                    WHEN EXISTS (SELECT 1 FROM membership_roles mr JOIN roles r ON r.id = mr.role_id
                                 WHERE mr.membership_id = m.id AND r.system_key = 'tenant_admin') THEN 'Admin'
                    ELSE 'Member'
                END;
                """);
            migrationBuilder.Sql("ALTER TABLE tenant_memberships ALTER COLUMN role DROP DEFAULT;");

            migrationBuilder.DropTable(
                name: "auth_audit_events");

            migrationBuilder.DropTable(
                name: "membership_roles");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "tenant_security_policies");

            migrationBuilder.DropTable(
                name: "user_invitation_roles");

            migrationBuilder.DropTable(
                name: "user_password_history");

            migrationBuilder.DropTable(
                name: "user_recovery_codes");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "user_invitations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tenant_memberships_tenant_id_id",
                table: "tenant_memberships");

            migrationBuilder.DropColumn(
                name: "email_verified_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "failed_access_count",
                table: "users");

            migrationBuilder.DropColumn(
                name: "lockout_count",
                table: "users");

            migrationBuilder.DropColumn(
                name: "lockout_end_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_enabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_enabled_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_last_used_step",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_pending_secret",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_secret",
                table: "users");

            migrationBuilder.DropColumn(
                name: "password_changed_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "deactivated_at_utc",
                table: "tenant_memberships");
        }
    }
}
