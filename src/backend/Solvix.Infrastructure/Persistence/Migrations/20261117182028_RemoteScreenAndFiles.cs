using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteScreenAndFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_remote_sessions_open_per_device",
                table: "remote_sessions");

            migrationBuilder.AddColumn<bool>(
                name: "control_granted",
                table: "remote_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "control_requested",
                table: "remote_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "remote_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Shell");

            migrationBuilder.CreateTable(
                name: "remote_file_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    overwrite = table.Column<bool>(type: "boolean", nullable: false),
                    entries_json = table.Column<string>(type: "text", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_file_operations", x => x.id);
                    table.UniqueConstraint("ak_remote_file_operations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_remote_file_operations_remote_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalTable: "remote_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_remote_file_operations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_file_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_file_chunks", x => x.id);
                    table.ForeignKey(
                        name: "fk_remote_file_chunks_remote_file_operations_tenant_id_operati~",
                        columns: x => new { x.tenant_id, x.operation_id },
                        principalTable: "remote_file_operations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_remote_file_chunks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_remote_sessions_open_per_device_kind",
                table: "remote_sessions",
                columns: new[] { "device_id", "kind" },
                unique: true,
                filter: "status IN ('Pending', 'Active')");

            migrationBuilder.CreateIndex(
                name: "ix_remote_file_chunks_operation_id_index",
                table: "remote_file_chunks",
                columns: new[] { "operation_id", "index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_file_chunks_tenant_id_operation_id",
                table: "remote_file_chunks",
                columns: new[] { "tenant_id", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_file_operations_session_id_status_created_at_utc",
                table: "remote_file_operations",
                columns: new[] { "session_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_remote_file_operations_tenant_id_session_id",
                table: "remote_file_operations",
                columns: new[] { "tenant_id", "session_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back removes screen and file sessions together with their file tables: without the
            // kind column they would be indistinguishable from shell sessions (misleading audit rows), and a
            // shell and a screen session open on one device would break the old one-per-device index.
            migrationBuilder.Sql("DELETE FROM remote_sessions WHERE kind <> 'Shell';");

            migrationBuilder.DropTable(
                name: "remote_file_chunks");

            migrationBuilder.DropTable(
                name: "remote_file_operations");

            migrationBuilder.DropIndex(
                name: "ux_remote_sessions_open_per_device_kind",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "control_granted",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "control_requested",
                table: "remote_sessions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "remote_sessions");

            migrationBuilder.CreateIndex(
                name: "ux_remote_sessions_open_per_device",
                table: "remote_sessions",
                column: "device_id",
                unique: true,
                filter: "status IN ('Pending', 'Active')");
        }
    }
}
