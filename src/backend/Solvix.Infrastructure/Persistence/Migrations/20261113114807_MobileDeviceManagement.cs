using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MobileDeviceManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mobile_apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    store_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_apps", x => x.id);
                    table.UniqueConstraint("ak_mobile_apps_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_mobile_apps_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_enterprises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pending_signup_url_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    enterprise_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    bound_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_enterprises", x => x.id);
                    table.ForeignKey(
                        name: "fk_mobile_enterprises_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    published_version = table.Column<int>(type: "integer", nullable: false),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_policies", x => x.id);
                    table.UniqueConstraint("ak_mobile_policies_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_mobile_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    provider_device_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    management_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    enrollment_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_policy_version = table.Column<int>(type: "integer", nullable: true),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    os_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    security_patch_level = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_encrypted = table.Column<bool>(type: "boolean", nullable: true),
                    has_passcode = table.Column<bool>(type: "boolean", nullable: true),
                    is_supervised = table.Column<bool>(type: "boolean", nullable: true),
                    integrity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    installed_apps_json = table.Column<string>(type: "jsonb", nullable: false),
                    app_inventory_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enrolled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    compliance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    compliance_evaluated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    compliance_violations_json = table.Column<string>(type: "jsonb", nullable: false),
                    push_token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    push_magic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    push_topic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    protected_unlock_token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    identity_thumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_devices", x => x.id);
                    table.UniqueConstraint("ak_mobile_devices_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_mobile_devices_mobile_policies_tenant_id_policy_id",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalTable: "mobile_policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mobile_devices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_enrollment_tokens", x => x.id);
                    table.UniqueConstraint("ak_mobile_enrollment_tokens_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_mobile_enrollment_tokens_mobile_policies_tenant_id_policy_id",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalTable: "mobile_policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mobile_enrollment_tokens_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_policy_apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    install_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    managed_configuration_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_policy_apps", x => x.id);
                    table.ForeignKey(
                        name: "fk_mobile_policy_apps_mobile_apps_tenant_id_app_id",
                        columns: x => new { x.tenant_id, x.app_id },
                        principalTable: "mobile_apps",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mobile_policy_apps_mobile_policies_tenant_id_policy_id",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalTable: "mobile_policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mobile_policy_apps_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_device_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_device_commands", x => x.id);
                    table.ForeignKey(
                        name: "fk_mobile_device_commands_mobile_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "mobile_devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mobile_device_commands_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_device_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_device_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_mobile_device_events_mobile_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "mobile_devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mobile_device_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_apps_tenant_id_platform_identifier",
                table: "mobile_apps",
                columns: new[] { "tenant_id", "platform", "identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mobile_device_commands_tenant_id_device_id_state",
                table: "mobile_device_commands",
                columns: new[] { "tenant_id", "device_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_device_commands_tenant_id_state",
                table: "mobile_device_commands",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_device_events_tenant_id_device_id_created_at_utc",
                table: "mobile_device_events",
                columns: new[] { "tenant_id", "device_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_devices_platform_provider_device_id",
                table: "mobile_devices",
                columns: new[] { "platform", "provider_device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mobile_devices_tenant_id_compliance",
                table: "mobile_devices",
                columns: new[] { "tenant_id", "compliance" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_devices_tenant_id_policy_id",
                table: "mobile_devices",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_devices_tenant_id_state",
                table: "mobile_devices",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_enrollment_tokens_provider_reference",
                table: "mobile_enrollment_tokens",
                column: "provider_reference",
                filter: "provider_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mobile_enrollment_tokens_secret_hash",
                table: "mobile_enrollment_tokens",
                column: "secret_hash",
                unique: true,
                filter: "secret_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mobile_enrollment_tokens_tenant_id_policy_id",
                table: "mobile_enrollment_tokens",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_enterprises_enterprise_name",
                table: "mobile_enterprises",
                column: "enterprise_name",
                unique: true,
                filter: "enterprise_name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mobile_enterprises_tenant_id_platform",
                table: "mobile_enterprises",
                columns: new[] { "tenant_id", "platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mobile_policies_tenant_id_name",
                table: "mobile_policies",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mobile_policy_apps_tenant_id_app_id",
                table: "mobile_policy_apps",
                columns: new[] { "tenant_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_policy_apps_tenant_id_policy_id_app_id",
                table: "mobile_policy_apps",
                columns: new[] { "tenant_id", "policy_id", "app_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_device_commands");

            migrationBuilder.DropTable(
                name: "mobile_device_events");

            migrationBuilder.DropTable(
                name: "mobile_enrollment_tokens");

            migrationBuilder.DropTable(
                name: "mobile_enterprises");

            migrationBuilder.DropTable(
                name: "mobile_policy_apps");

            migrationBuilder.DropTable(
                name: "mobile_devices");

            migrationBuilder.DropTable(
                name: "mobile_apps");

            migrationBuilder.DropTable(
                name: "mobile_policies");
        }
    }
}
