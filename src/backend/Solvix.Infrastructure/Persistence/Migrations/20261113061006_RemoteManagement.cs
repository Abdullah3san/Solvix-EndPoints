using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "remote_access_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_required_commands = table.Column<List<string>>(type: "text[]", nullable: false),
                    command_ttl_minutes = table.Column<int>(type: "integer", nullable: false),
                    approval_ttl_minutes = table.Column<int>(type: "integer", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    allowed_from_minute = table.Column<int>(type: "integer", nullable: true),
                    allowed_to_minute = table.Column<int>(type: "integer", nullable: true),
                    allowed_days_mask = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_access_policies", x => x.id);
                    table.CheckConstraint("ck_remote_access_policies_ttl", "command_ttl_minutes BETWEEN 5 AND 10080 AND approval_ttl_minutes BETWEEN 5 AND 10080");
                    table.CheckConstraint("ck_remote_access_policies_window", "(allowed_from_minute IS NULL) = (allowed_to_minute IS NULL) AND COALESCE(allowed_from_minute, 0) BETWEEN 0 AND 1439 AND COALESCE(allowed_to_minute, 0) BETWEEN 0 AND 1440");
                    table.ForeignKey(
                        name: "fk_remote_access_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_access_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_group_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    branch_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_access_scopes", x => x.id);
                    table.ForeignKey(
                        name: "fk_remote_access_scopes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("pk_remote_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_remote_audit_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_command_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    required_permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parameters = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    execution_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    source_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence = table.Column<long>(type: "bigint", nullable: true),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    signed_command = table.Column<string>(type: "text", nullable: true),
                    queued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_delivered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivery_attempts = table.Column<int>(type: "integer", nullable: false),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    execution_deadline_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    output = table.Column<string>(type: "text", nullable: true),
                    output_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    cancel_requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    target_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_command_executions", x => x.id);
                    table.UniqueConstraint("ak_remote_command_executions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_remote_command_executions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_device_command_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_device_command_sequences", x => x.id);
                    table.ForeignKey(
                        name: "fk_remote_device_command_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_approvals", x => x.id);
                    table.CheckConstraint("ck_remote_approvals_four_eyes", "decided_by IS NULL OR decided_by <> requested_by");
                    table.ForeignKey(
                        name: "fk_remote_approvals_remote_command_executions_tenant_id_execut~",
                        columns: x => new { x.tenant_id, x.execution_id },
                        principalTable: "remote_command_executions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_remote_approvals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_access_policies_tenant_id",
                table: "remote_access_policies",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_access_scopes_tenant_id_user_id",
                table: "remote_access_scopes",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_approvals_tenant_id_execution_id",
                table: "remote_approvals",
                columns: new[] { "tenant_id", "execution_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_approvals_tenant_id_status_expires_at_utc",
                table: "remote_approvals",
                columns: new[] { "tenant_id", "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_audit_events_tenant_id_device_id_occurred_at_utc",
                table: "remote_audit_events",
                columns: new[] { "tenant_id", "device_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_audit_events_tenant_id_execution_id",
                table: "remote_audit_events",
                columns: new[] { "tenant_id", "execution_id" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_audit_events_tenant_id_occurred_at_utc",
                table: "remote_audit_events",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_audit_events_tenant_id_sequence",
                table: "remote_audit_events",
                columns: new[] { "tenant_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_command_executions_status_execution_deadline_utc",
                table: "remote_command_executions",
                columns: new[] { "status", "execution_deadline_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_command_executions_status_expires_at_utc",
                table: "remote_command_executions",
                columns: new[] { "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_command_executions_tenant_id_device_id_sequence",
                table: "remote_command_executions",
                columns: new[] { "tenant_id", "device_id", "sequence" },
                unique: true,
                filter: "sequence IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_remote_command_executions_tenant_id_device_id_status",
                table: "remote_command_executions",
                columns: new[] { "tenant_id", "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_command_executions_tenant_id_requested_at_utc",
                table: "remote_command_executions",
                columns: new[] { "tenant_id", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_device_command_sequences_tenant_id_device_id",
                table: "remote_device_command_sequences",
                columns: new[] { "tenant_id", "device_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remote_access_policies");

            migrationBuilder.DropTable(
                name: "remote_access_scopes");

            migrationBuilder.DropTable(
                name: "remote_approvals");

            migrationBuilder.DropTable(
                name: "remote_audit_events");

            migrationBuilder.DropTable(
                name: "remote_device_command_sequences");

            migrationBuilder.DropTable(
                name: "remote_command_executions");
        }
    }
}
