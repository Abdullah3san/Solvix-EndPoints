using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RealTimeMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_tenant_id_last_seen_at_utc",
                table: "devices");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "agent_health_updated_at_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_availability_days",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    online_seconds = table.Column<long>(type: "bigint", nullable: false),
                    warning_seconds = table.Column<long>(type: "bigint", nullable: false),
                    offline_seconds = table.Column<long>(type: "bigint", nullable: false),
                    offline_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_availability_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_availability_days_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_availability_days_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_connection_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    signal = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    from_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    to_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_seconds = table.Column<long>(type: "bigint", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_connection_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_connection_events_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_connection_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_presence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_communication_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    online_since_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    offline_since_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    agent_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    system_booted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    network_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    network_checked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    network_json = table.Column<string>(type: "jsonb", nullable: true),
                    os_health_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    os_health_checked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    os_health_json = table.Column<string>(type: "jsonb", nullable: true),
                    signals_persisted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accounted_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_presence", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_presence_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_presence_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitor_checkpoints",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_sweep_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observing_since_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_floor_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_rollup_day = table.Column<DateOnly>(type: "date", nullable: false),
                    last_purge_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitor_checkpoints", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    opened_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    downtime_seconds = table.Column<long>(type: "bigint", nullable: true),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitoring_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_monitoring_alerts_devices_tenant_id_device_id",
                        columns: x => new { x.tenant_id, x.device_id },
                        principalTable: "devices",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_monitoring_alerts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    heartbeat_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    warning_after_seconds = table.Column<int>(type: "integer", nullable: false),
                    offline_after_seconds = table.Column<int>(type: "integer", nullable: false),
                    offline_alerts_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    recovery_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitoring_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_monitoring_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_availability_days_tenant_id_day",
                table: "device_availability_days",
                columns: new[] { "tenant_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_device_availability_days_tenant_id_device_id_day",
                table: "device_availability_days",
                columns: new[] { "tenant_id", "device_id", "day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_connection_events_tenant_id_device_id_occurred_at_utc",
                table: "device_connection_events",
                columns: new[] { "tenant_id", "device_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_device_connection_events_tenant_id_occurred_at_utc",
                table: "device_connection_events",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_device_presence_tenant_id_device_id",
                table: "device_presence",
                columns: new[] { "tenant_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_presence_tenant_id_state",
                table: "device_presence",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_alerts_tenant_id_status_opened_at_utc",
                table: "monitoring_alerts",
                columns: new[] { "tenant_id", "status", "opened_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_monitoring_alerts_open_per_device",
                table: "monitoring_alerts",
                columns: new[] { "tenant_id", "device_id", "type" },
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_monitoring_settings_tenant_id",
                table: "monitoring_settings",
                column: "tenant_id",
                unique: true);

            // Contact time moves from the wide device row to the narrow presence table.
            // Existing devices start Online when heard from within the default offline
            // threshold, otherwise Offline (since their last contact).
            migrationBuilder.Sql(
                """
                ALTER TABLE device_presence SET (fillfactor = 70);
                INSERT INTO device_presence (
                    id, tenant_id, device_id, state, state_changed_at_utc, last_seen_at_utc, last_communication_at_utc,
                    online_since_utc, offline_since_utc, network_status, os_health_status, accounted_until_utc, revision,
                    created_at_utc, updated_at_utc)
                SELECT d.id, d.tenant_id, d.id,
                       CASE WHEN COALESCE(d.last_seen_at_utc, d.enrolled_at_utc) >= now() - interval '300 seconds' THEN 'Online' ELSE 'Offline' END,
                       now(),
                       COALESCE(d.last_seen_at_utc, d.enrolled_at_utc),
                       d.last_seen_at_utc,
                       CASE WHEN COALESCE(d.last_seen_at_utc, d.enrolled_at_utc) >= now() - interval '300 seconds' THEN now() END,
                       CASE WHEN COALESCE(d.last_seen_at_utc, d.enrolled_at_utc) <  now() - interval '300 seconds' THEN COALESCE(d.last_seen_at_utc, d.enrolled_at_utc) END,
                       'Unknown', 'Unknown', now(), 0, now(), now()
                FROM devices d;
                """);

            migrationBuilder.DropColumn(
                name: "last_seen_at_utc",
                table: "devices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.Sql("UPDATE devices d SET last_seen_at_utc = p.last_seen_at_utc FROM device_presence p WHERE p.id = d.id;");

            migrationBuilder.DropTable(
                name: "device_availability_days");

            migrationBuilder.DropTable(
                name: "device_connection_events");

            migrationBuilder.DropTable(
                name: "device_presence");

            migrationBuilder.DropTable(
                name: "monitor_checkpoints");

            migrationBuilder.DropTable(
                name: "monitoring_alerts");

            migrationBuilder.DropTable(
                name: "monitoring_settings");

            migrationBuilder.DropColumn(
                name: "agent_health_updated_at_utc",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "ix_devices_tenant_id_last_seen_at_utc",
                table: "devices",
                columns: new[] { "tenant_id", "last_seen_at_utc" });
        }
    }
}
