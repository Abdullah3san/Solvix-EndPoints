using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EndpointManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_entries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "configuration_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    active_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    latest_version_number = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_policies", x => x.id);
                    table.UniqueConstraint("ak_configuration_policies_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_configuration_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_groups", x => x.id);
                    table.UniqueConstraint("ak_device_groups_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_device_groups_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    dns_domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    hardware_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    os_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    os_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    os_build = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    agent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enrolled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_boot_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    credential_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    credential_issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.UniqueConstraint("ak_devices_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_devices_branches_tenant_id_branch_id",
                        columns: x => new { x.tenant_id, x.branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_devices_departments_tenant_id_department_id",
                        columns: x => new { x.tenant_id, x.department_id },
                        principalTable: "departments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_devices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recurrence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    days_of_week = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    once_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    once_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_windows", x => x.id);
                    table.UniqueConstraint("ak_maintenance_windows_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_maintenance_windows_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patch_auto_approval_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    classifications = table.Column<int[]>(type: "integer[]", nullable: false),
                    severities = table.Column<int[]>(type: "integer[]", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patch_auto_approval_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_patch_auto_approval_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    update_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    kb_article_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    cve_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    release_date_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_download_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    reboot_behavior = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    support_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    first_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    approval_decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approval_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    auto_approval_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patches", x => x.id);
                    table.UniqueConstraint("ak_patches_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_patches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "configuration_policy_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    max_risk = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    change_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    restored_from_number = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    authorized_risk = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    destructive_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_policy_versions", x => x.id);
                    table.UniqueConstraint("ak_configuration_policy_versions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_configuration_policy_versions_configuration_policies_tenant~",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalTable: "configuration_policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_configuration_policy_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    default_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_device_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_enrollment_tokens_branches_tenant_id_default_branch_id",
                        columns: x => new { x.tenant_id, x.default_branch_id },
                        principalTable: "branches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_enrollment_tokens_departments_tenant_id_default_department_~",
                        columns: x => new { x.tenant_id, x.default_department_id },
                        principalTable: "departments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_enrollment_tokens_device_groups_tenant_id_default_device_gr~",
                        columns: x => new { x.tenant_id, x.default_device_group_id },
                        principalTable: "device_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_enrollment_tokens_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "configuration_drift_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    setting_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    expected_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    actual_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_drift_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_configuration_drift_events_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_configuration_drift_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_configuration_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    setting_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    is_desired = table.Column<bool>(type: "boolean", nullable: false),
                    is_conflicted = table.Column<bool>(type: "boolean", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    expected_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    removal_behavior = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    expected_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    actual_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    rollback_available = table.Column<bool>(type: "boolean", nullable: false),
                    was_changed_by_solvix = table.Column<bool>(type: "boolean", nullable: false),
                    last_evaluated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    drift_detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_configuration_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_configuration_states_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_configuration_states_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_group_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_group_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_group_members_device_groups_tenant_id_device_group_id",
                        columns: x => new { x.tenant_id, x.device_group_id },
                        principalTable: "device_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_group_members_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_group_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_update_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_scan_started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_scan_completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_scan_result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_scan_h_result = table.Column<int>(type: "integer", nullable: true),
                    last_scan_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_successful_scan_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    service_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    automatic_updates_notification_level = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    update_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_successful_install_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reboot_required = table.Column<bool>(type: "boolean", nullable: false),
                    reboot_reasons = table.Column<string[]>(type: "text[]", nullable: false),
                    reboot_required_since_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_boot_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    inventory_requested = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_update_summaries", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_update_summaries_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_update_summaries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "configuration_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_exclusion = table.Column<bool>(type: "boolean", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    maintenance_window_id = table.Column<Guid>(type: "uuid", nullable: true),
                    removal_behavior = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_configuration_assignments_configuration_policies_tenant_id_~",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalTable: "configuration_policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_configuration_assignments_maintenance_windows_tenant_id_mai~",
                        columns: x => new { x.tenant_id, x.maintenance_window_id },
                        principalTable: "maintenance_windows",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_configuration_assignments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_restart_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    not_before_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    maintenance_window_id = table.Column<Guid>(type: "uuid", nullable: true),
                    force = table.Column<bool>(type: "boolean", nullable: false),
                    grace_period_seconds = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    initiated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_restart_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_restart_requests_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_restart_requests_maintenance_windows_tenant_id_maint~",
                        columns: x => new { x.tenant_id, x.maintenance_window_id },
                        principalTable: "maintenance_windows",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_device_restart_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patch_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scheduled_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    maintenance_window_id = table.Column<Guid>(type: "uuid", nullable: true),
                    restart_behavior = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    restart_grace_period_seconds = table.Column<int>(type: "integer", nullable: false),
                    pilot_device_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pilot_percentage = table.Column<int>(type: "integer", nullable: true),
                    pilot_success_threshold_percent = table.Column<int>(type: "integer", nullable: false),
                    pilot_soak_minutes = table.Column<int>(type: "integer", nullable: false),
                    auto_promote = table.Column<bool>(type: "boolean", nullable: false),
                    failure_threshold_percent = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorized_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pilot_completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    promoted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    promoted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    halt_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patch_deployments", x => x.id);
                    table.UniqueConstraint("ak_patch_deployments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_patch_deployments_device_groups_tenant_id_pilot_device_grou~",
                        columns: x => new { x.tenant_id, x.pilot_device_group_id },
                        principalTable: "device_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_patch_deployments_maintenance_windows_tenant_id_maintenance~",
                        columns: x => new { x.tenant_id, x.maintenance_window_id },
                        principalTable: "maintenance_windows",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_patch_deployments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_patch_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_downloaded = table.Column<bool>(type: "boolean", nullable: false),
                    first_detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    installed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_h_result = table.Column<int>(type: "integer", nullable: true),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_patch_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_patch_states_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_patch_states_patches_tenant_id_patch_id",
                        columns: x => new { x.tenant_id, x.patch_id },
                        principalTable: "patches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_patch_states_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patch_deployment_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ring = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    lease_expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_after_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_progress_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    percent_complete = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_h_result = table.Column<int>(type: "integer", nullable: true),
                    reboot_required = table.Column<bool>(type: "boolean", nullable: false),
                    results_json = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patch_deployment_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_patch_deployment_jobs_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_patch_deployment_jobs_patch_deployments_tenant_id_deploymen~",
                        columns: x => new { x.tenant_id, x.deployment_id },
                        principalTable: "patch_deployments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_patch_deployment_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patch_deployment_patches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patch_deployment_patches", x => x.id);
                    table.ForeignKey(
                        name: "fk_patch_deployment_patches_patch_deployments_tenant_id_deploy~",
                        columns: x => new { x.tenant_id, x.deployment_id },
                        principalTable: "patch_deployments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_patch_deployment_patches_patches_tenant_id_patch_id",
                        columns: x => new { x.tenant_id, x.patch_id },
                        principalTable: "patches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_patch_deployment_patches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patch_deployment_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_exclusion = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patch_deployment_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_patch_deployment_targets_patch_deployments_tenant_id_deploy~",
                        columns: x => new { x.tenant_id, x.deployment_id },
                        principalTable: "patch_deployments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_patch_deployment_targets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_action",
                table: "audit_entries",
                columns: new[] { "tenant_id", "action" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_entity_type_entity_id",
                table: "audit_entries",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_occurred_at_utc",
                table: "audit_entries",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_assignments_tenant_id_maintenance_window_id",
                table: "configuration_assignments",
                columns: new[] { "tenant_id", "maintenance_window_id" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_assignments_tenant_id_policy_id_target_type_t~",
                table: "configuration_assignments",
                columns: new[] { "tenant_id", "policy_id", "target_type", "target_id", "is_exclusion" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_configuration_assignments_tenant_id_target_type_target_id",
                table: "configuration_assignments",
                columns: new[] { "tenant_id", "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_drift_events_tenant_id_detected_at_utc",
                table: "configuration_drift_events",
                columns: new[] { "tenant_id", "detected_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_drift_events_tenant_id_device_id_detected_at_~",
                table: "configuration_drift_events",
                columns: new[] { "tenant_id", "device_id", "detected_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_configuration_policies_tenant_id_name",
                table: "configuration_policies",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_configuration_policy_versions_single_published",
                table: "configuration_policy_versions",
                columns: new[] { "tenant_id", "policy_id" },
                unique: true,
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_policy_versions_tenant_id_policy_id_number",
                table: "configuration_policy_versions",
                columns: new[] { "tenant_id", "policy_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_configuration_states_tenant_id_device_id_setting_key",
                table: "device_configuration_states",
                columns: new[] { "tenant_id", "device_id", "setting_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_configuration_states_tenant_id_policy_id_status",
                table: "device_configuration_states",
                columns: new[] { "tenant_id", "policy_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_device_group_members_tenant_id_device_group_id_device_id",
                table: "device_group_members",
                columns: new[] { "tenant_id", "device_group_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_group_members_tenant_id_device_id",
                table: "device_group_members",
                columns: new[] { "tenant_id", "device_id" });

            migrationBuilder.CreateIndex(
                name: "ix_device_groups_tenant_id_name",
                table: "device_groups",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_patch_states_tenant_id_device_id_patch_id",
                table: "device_patch_states",
                columns: new[] { "tenant_id", "device_id", "patch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_patch_states_tenant_id_patch_id_status",
                table: "device_patch_states",
                columns: new[] { "tenant_id", "patch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_device_restart_requests_tenant_id_device_id_status",
                table: "device_restart_requests",
                columns: new[] { "tenant_id", "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_device_restart_requests_tenant_id_maintenance_window_id",
                table: "device_restart_requests",
                columns: new[] { "tenant_id", "maintenance_window_id" });

            migrationBuilder.CreateIndex(
                name: "ix_device_update_summaries_tenant_id_device_id",
                table: "device_update_summaries",
                columns: new[] { "tenant_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_branch_id",
                table: "devices",
                columns: new[] { "tenant_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_department_id",
                table: "devices",
                columns: new[] { "tenant_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_hardware_id",
                table: "devices",
                columns: new[] { "tenant_id", "hardware_id" },
                unique: true,
                filter: "hardware_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_hostname",
                table: "devices",
                columns: new[] { "tenant_id", "hostname" });

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tokens_tenant_id_created_at_utc",
                table: "enrollment_tokens",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tokens_tenant_id_default_branch_id",
                table: "enrollment_tokens",
                columns: new[] { "tenant_id", "default_branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tokens_tenant_id_default_department_id",
                table: "enrollment_tokens",
                columns: new[] { "tenant_id", "default_department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_tokens_tenant_id_default_device_group_id",
                table: "enrollment_tokens",
                columns: new[] { "tenant_id", "default_device_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_tenant_id_name",
                table: "maintenance_windows",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_patch_auto_approval_rules_tenant_id_name",
                table: "patch_auto_approval_rules",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_jobs_tenant_id_deployment_id_device_id",
                table: "patch_deployment_jobs",
                columns: new[] { "tenant_id", "deployment_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_jobs_tenant_id_device_id_status",
                table: "patch_deployment_jobs",
                columns: new[] { "tenant_id", "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_jobs_tenant_id_status_lease_expires_utc",
                table: "patch_deployment_jobs",
                columns: new[] { "tenant_id", "status", "lease_expires_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_patches_tenant_id_deployment_id_patch_id",
                table: "patch_deployment_patches",
                columns: new[] { "tenant_id", "deployment_id", "patch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_patches_tenant_id_patch_id",
                table: "patch_deployment_patches",
                columns: new[] { "tenant_id", "patch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_targets_tenant_id_deployment_id",
                table: "patch_deployment_targets",
                columns: new[] { "tenant_id", "deployment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployment_targets_tenant_id_target_type_target_id",
                table: "patch_deployment_targets",
                columns: new[] { "tenant_id", "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployments_tenant_id_maintenance_window_id",
                table: "patch_deployments",
                columns: new[] { "tenant_id", "maintenance_window_id" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployments_tenant_id_pilot_device_group_id",
                table: "patch_deployments",
                columns: new[] { "tenant_id", "pilot_device_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_patch_deployments_tenant_id_status",
                table: "patch_deployments",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_patches_tenant_id_approval_state",
                table: "patches",
                columns: new[] { "tenant_id", "approval_state" });

            migrationBuilder.CreateIndex(
                name: "ix_patches_tenant_id_update_id_revision_number",
                table: "patches",
                columns: new[] { "tenant_id", "update_id", "revision_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "configuration_assignments");

            migrationBuilder.DropTable(
                name: "configuration_drift_events");

            migrationBuilder.DropTable(
                name: "configuration_policy_versions");

            migrationBuilder.DropTable(
                name: "device_configuration_states");

            migrationBuilder.DropTable(
                name: "device_group_members");

            migrationBuilder.DropTable(
                name: "device_patch_states");

            migrationBuilder.DropTable(
                name: "device_restart_requests");

            migrationBuilder.DropTable(
                name: "device_update_summaries");

            migrationBuilder.DropTable(
                name: "enrollment_tokens");

            migrationBuilder.DropTable(
                name: "patch_auto_approval_rules");

            migrationBuilder.DropTable(
                name: "patch_deployment_jobs");

            migrationBuilder.DropTable(
                name: "patch_deployment_patches");

            migrationBuilder.DropTable(
                name: "patch_deployment_targets");

            migrationBuilder.DropTable(
                name: "configuration_policies");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "patches");

            migrationBuilder.DropTable(
                name: "patch_deployments");

            migrationBuilder.DropTable(
                name: "device_groups");

            migrationBuilder.DropTable(
                name: "maintenance_windows");
        }
    }
}
