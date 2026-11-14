using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase10Security : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_advisories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    product_pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    affected_from_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    fixed_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_advisories", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_advisories_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_label = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    source_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_audit_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enrolled_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    os_caption = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    os_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    agent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_domain_joined = table.Column<bool>(type: "boolean", nullable: true),
                    last_report_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    latest_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score = table.Column<int>(type: "integer", nullable: true),
                    band = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    coverage = table.Column<double>(type: "double precision", nullable: true),
                    open_critical = table.Column<int>(type: "integer", nullable: false),
                    open_high = table.Column<int>(type: "integer", nullable: false),
                    open_medium = table.Column<int>(type: "integer", nullable: false),
                    open_low = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_endpoints", x => x.id);
                    table.UniqueConstraint("ak_security_endpoints_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_security_endpoints_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    based_on_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    record_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    time_created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    target_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_events_security_endpoints_tenant_id_endpoint_id",
                        columns: x => new { x.tenant_id, x.endpoint_id },
                        principalTable: "security_endpoints",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    remediation_action_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    remediation_parameter = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    first_detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recurrence_count = table.Column<int>(type: "integer", nullable: false),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acceptance_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_findings", x => x.id);
                    table.UniqueConstraint("ak_security_findings_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_security_findings_security_endpoints_tenant_id_endpoint_id",
                        columns: x => new { x.tenant_id, x.endpoint_id },
                        principalTable: "security_endpoints",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_findings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    report = table.Column<string>(type: "jsonb", nullable: true),
                    evaluation = table.Column<string>(type: "jsonb", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    band = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    coverage = table.Column<double>(type: "double precision", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_version = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_snapshots_security_endpoints_tenant_id_endpoint_id",
                        columns: x => new { x.tenant_id, x.endpoint_id },
                        principalTable: "security_endpoints",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_snapshots_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    finding_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raised_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closure_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_alerts_security_endpoints_tenant_id_endpoint_id",
                        columns: x => new { x.tenant_id, x.endpoint_id },
                        principalTable: "security_endpoints",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_alerts_security_findings_tenant_id_finding_id",
                        columns: x => new { x.tenant_id, x.finding_id },
                        principalTable: "security_findings",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_security_alerts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_remediations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finding_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parameter = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    risk = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    auto_approved = table.Column<bool>(type: "boolean", nullable: false),
                    approved_under_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_remediations", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_remediations_security_endpoints_tenant_id_endpoint~",
                        columns: x => new { x.tenant_id, x.endpoint_id },
                        principalTable: "security_endpoints",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_remediations_security_findings_tenant_id_finding_id",
                        columns: x => new { x.tenant_id, x.finding_id },
                        principalTable: "security_findings",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_security_remediations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_advisories_tenant_id_is_enabled",
                table: "security_advisories",
                columns: new[] { "tenant_id", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_security_alerts_tenant_id_dedupe_key",
                table: "security_alerts",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_alerts_tenant_id_endpoint_id",
                table: "security_alerts",
                columns: new[] { "tenant_id", "endpoint_id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_alerts_tenant_id_finding_id",
                table: "security_alerts",
                columns: new[] { "tenant_id", "finding_id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_alerts_tenant_id_status_raised_at_utc",
                table: "security_alerts",
                columns: new[] { "tenant_id", "status", "raised_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_tenant_id_action",
                table: "security_audit",
                columns: new[] { "tenant_id", "action" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_tenant_id_entity_type_entity_id",
                table: "security_audit",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_tenant_id_occurred_at_utc",
                table: "security_audit",
                columns: new[] { "tenant_id", "occurred_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_security_endpoints_tenant_id_band",
                table: "security_endpoints",
                columns: new[] { "tenant_id", "band" });

            migrationBuilder.CreateIndex(
                name: "ix_security_endpoints_tenant_id_hostname",
                table: "security_endpoints",
                columns: new[] { "tenant_id", "hostname" });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_tenant_id_endpoint_id_log_record_id",
                table: "security_events",
                columns: new[] { "tenant_id", "endpoint_id", "log", "record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_events_tenant_id_time_created_utc",
                table: "security_events",
                columns: new[] { "tenant_id", "time_created_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_security_findings_tenant_id_endpoint_id_rule_key_subject_key",
                table: "security_findings",
                columns: new[] { "tenant_id", "endpoint_id", "rule_key", "subject_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_findings_tenant_id_status_severity",
                table: "security_findings",
                columns: new[] { "tenant_id", "status", "severity" });

            migrationBuilder.CreateIndex(
                name: "ix_security_policies_tenant_active",
                table: "security_policies",
                column: "tenant_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_security_policies_tenant_id_version",
                table: "security_policies",
                columns: new[] { "tenant_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_remediations_tenant_id_endpoint_id_status",
                table: "security_remediations",
                columns: new[] { "tenant_id", "endpoint_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_security_remediations_tenant_id_finding_id",
                table: "security_remediations",
                columns: new[] { "tenant_id", "finding_id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_remediations_tenant_id_status_requested_at_utc",
                table: "security_remediations",
                columns: new[] { "tenant_id", "status", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_security_snapshots_tenant_id_endpoint_id_received_at_utc",
                table: "security_snapshots",
                columns: new[] { "tenant_id", "endpoint_id", "received_at_utc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_security_snapshots_tenant_id_received_at_utc",
                table: "security_snapshots",
                columns: new[] { "tenant_id", "received_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_advisories");

            migrationBuilder.DropTable(
                name: "security_alerts");

            migrationBuilder.DropTable(
                name: "security_audit");

            migrationBuilder.DropTable(
                name: "security_events");

            migrationBuilder.DropTable(
                name: "security_policies");

            migrationBuilder.DropTable(
                name: "security_remediations");

            migrationBuilder.DropTable(
                name: "security_snapshots");

            migrationBuilder.DropTable(
                name: "security_findings");

            migrationBuilder.DropTable(
                name: "security_endpoints");
        }
    }
}
