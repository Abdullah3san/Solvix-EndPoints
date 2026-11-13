using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SoftwareManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "software_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    package_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    previous_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_audit_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_deployment_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    device_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    device_group_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    branch_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    excluded_device_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployment_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_deployment_groups_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_deployment_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    allow_exe_installers = table.Column<bool>(type: "boolean", nullable: false),
                    require_signed_packages = table.Column<bool>(type: "boolean", nullable: false),
                    trusted_publisher_thumbprints = table.Column<List<string>>(type: "text[]", nullable: false),
                    require_approval = table.Column<bool>(type: "boolean", nullable: false),
                    large_deployment_threshold = table.Column<int>(type: "integer", nullable: false),
                    uninstall_is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    server_targets_are_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    approval_ttl_hours = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployment_policies", x => x.id);
                    table.CheckConstraint("ck_software_deployment_policies_values", "large_deployment_threshold BETWEEN 1 AND 5000 AND approval_ttl_hours BETWEEN 1 AND 720");
                    table.ForeignKey(
                        name: "fk_software_deployment_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_inventory_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    name_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    publisher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    install_date = table.Column<DateOnly>(type: "date", nullable: true),
                    first_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_inventory_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_inventory_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    installer_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character(32)", fixedLength: true, maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    signature_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    signer_subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    signer_thumbprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    signature_content_verified = table.Column<bool>(type: "boolean", nullable: false),
                    architecture = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    silent_preset = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    install_arguments = table.Column<List<string>>(type: "text[]", nullable: false),
                    uninstall_arguments = table.Column<List<string>>(type: "text[]", nullable: false),
                    success_exit_codes = table.Column<List<int>>(type: "integer[]", nullable: false),
                    reboot_exit_codes = table.Column<List<int>>(type: "integer[]", nullable: false),
                    timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    detection_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    detection_product_code = table.Column<string>(type: "character varying(38)", maxLength: 38, nullable: true),
                    detection_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    detection_name_match = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detection_publisher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    detection_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detection_version_operator = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    configuration_revision = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_packages", x => x.id);
                    table.UniqueConstraint("ak_software_packages_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_software_packages_size", "size_bytes > 0");
                    table.CheckConstraint("ck_software_packages_timeout", "timeout_minutes BETWEEN 1 AND 240");
                    table.ForeignKey(
                        name: "fk_software_packages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_restriction_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    rule_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name_pattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    publisher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    minimum_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    device_group_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_restriction_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_restriction_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    package_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    package_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    installer_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    package_revision = table.Column<int>(type: "integer", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    deployment_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scheduled_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    retry_delay_minutes = table.Column<int>(type: "integer", nullable: false),
                    timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    sensitivity_reasons = table.Column<List<string>>(type: "text[]", nullable: false),
                    required_approvals = table.Column<int>(type: "integer", nullable: false),
                    approval_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    final_approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    target_count = table.Column<int>(type: "integer", nullable: false),
                    report_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployments", x => x.id);
                    table.UniqueConstraint("ak_software_deployments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_software_deployments_approvals", "required_approvals BETWEEN 0 AND 2");
                    table.CheckConstraint("ck_software_deployments_attempts", "max_attempts BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_software_deployments_window", "deadline_utc > scheduled_start_utc");
                    table.ForeignKey(
                        name: "fk_software_deployments_software_packages_tenant_id_package_id",
                        columns: x => new { x.tenant_id, x.package_id },
                        principalTable: "software_packages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_software_deployments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_deployment_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployment_approvals", x => x.id);
                    table.CheckConstraint("ck_software_deployment_approvals_four_eyes", "approver_id <> requested_by");
                    table.ForeignKey(
                        name: "fk_software_deployment_approvals_software_deployments_tenant_i~",
                        columns: x => new { x.tenant_id, x.deployment_id },
                        principalTable: "software_deployments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_software_deployment_approvals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_deployment_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    current_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reboot_required = table.Column<bool>(type: "boolean", nullable: false),
                    detected_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    log_excerpt = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployment_targets", x => x.id);
                    table.CheckConstraint("ck_software_deployment_targets_attempts", "attempts BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "fk_software_deployment_targets_software_deployments_tenant_id_~",
                        columns: x => new { x.tenant_id, x.deployment_id },
                        principalTable: "software_deployments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_software_deployment_targets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_software_audit_events_tenant_id_deployment_id",
                table: "software_audit_events",
                columns: new[] { "tenant_id", "deployment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_audit_events_tenant_id_occurred_at_utc",
                table: "software_audit_events",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_software_audit_events_tenant_id_package_id",
                table: "software_audit_events",
                columns: new[] { "tenant_id", "package_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_audit_events_tenant_id_sequence",
                table: "software_audit_events",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_approvals_tenant_id_deployment_id_appro~",
                table: "software_deployment_approvals",
                columns: new[] { "tenant_id", "deployment_id", "approver_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_groups_tenant_id_name",
                table: "software_deployment_groups",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_policies_tenant_id",
                table: "software_deployment_policies",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_targets_current_execution_id",
                table: "software_deployment_targets",
                column: "current_execution_id",
                unique: true,
                filter: "current_execution_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_targets_status_next_attempt_at_utc",
                table: "software_deployment_targets",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_targets_tenant_id_deployment_id_device_~",
                table: "software_deployment_targets",
                columns: new[] { "tenant_id", "deployment_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_targets_tenant_id_device_id",
                table: "software_deployment_targets",
                columns: new[] { "tenant_id", "device_id" });

            migrationBuilder.CreateIndex(
                name: "ux_software_deployment_targets_active",
                table: "software_deployment_targets",
                columns: new[] { "tenant_id", "device_id", "package_id" },
                unique: true,
                filter: "status IN ('Pending', 'Dispatched', 'Running')");

            migrationBuilder.CreateIndex(
                name: "ix_software_deployments_status_scheduled_start_utc",
                table: "software_deployments",
                columns: new[] { "status", "scheduled_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_software_deployments_tenant_id_package_id",
                table: "software_deployments",
                columns: new[] { "tenant_id", "package_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_deployments_tenant_id_requested_at_utc",
                table: "software_deployments",
                columns: new[] { "tenant_id", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_software_deployments_tenant_id_status",
                table: "software_deployments",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_software_inventory_items_tenant_id_device_id",
                table: "software_inventory_items",
                columns: new[] { "tenant_id", "device_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_inventory_items_tenant_id_name_key",
                table: "software_inventory_items",
                columns: new[] { "tenant_id", "name_key" });

            migrationBuilder.CreateIndex(
                name: "ix_software_packages_tenant_id_name",
                table: "software_packages",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_software_packages_tenant_id_sha256",
                table: "software_packages",
                columns: new[] { "tenant_id", "sha256" },
                unique: true,
                filter: "status <> 'Retired'");

            migrationBuilder.CreateIndex(
                name: "ix_software_restriction_rules_tenant_id_enabled",
                table: "software_restriction_rules",
                columns: new[] { "tenant_id", "enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "software_audit_events");

            migrationBuilder.DropTable(
                name: "software_deployment_approvals");

            migrationBuilder.DropTable(
                name: "software_deployment_groups");

            migrationBuilder.DropTable(
                name: "software_deployment_policies");

            migrationBuilder.DropTable(
                name: "software_deployment_targets");

            migrationBuilder.DropTable(
                name: "software_inventory_items");

            migrationBuilder.DropTable(
                name: "software_restriction_rules");

            migrationBuilder.DropTable(
                name: "software_deployments");

            migrationBuilder.DropTable(
                name: "software_packages");
        }
    }
}
