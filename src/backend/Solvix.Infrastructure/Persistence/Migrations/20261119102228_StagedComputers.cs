using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StagedComputers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staged_computers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    normalized_hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staged_computers", x => x.id);
                    table.ForeignKey(
                        name: "fk_staged_computers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staged_computers_tenant_id_normalized_hostname",
                table: "staged_computers",
                columns: new[] { "tenant_id", "normalized_hostname" },
                unique: true);

            // Tenant isolation, and the cross-module placement links: tenant-safe composite keys that only
            // clear their own column when a branch/department is deleted (same pattern as devices).
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, "staged_computers");
            migrationBuilder.Sql(
                """
                ALTER TABLE staged_computers
                    ADD CONSTRAINT fk_staged_computers_branches_tenant_id_branch_id
                        FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                    ADD CONSTRAINT fk_staged_computers_departments_tenant_id_department_id
                        FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id);
                CREATE INDEX ix_staged_computers_tenant_id_branch_id ON staged_computers (tenant_id, branch_id) WHERE branch_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, "staged_computers");
            migrationBuilder.DropTable(
                name: "staged_computers");
        }
    }
}
