using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceEnrollmentPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "branch_id",
                table: "enrollment_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                table: "enrollment_tokens",
                type: "uuid",
                nullable: true);

            // Cross-module, tenant-safe references (declared in SQL like devices.branch_id):
            // the referenced row must carry the same tenant_id, and deleting a branch or
            // department only clears that column so the token stays usable.
            migrationBuilder.Sql(
                """
                ALTER TABLE enrollment_tokens
                    ADD CONSTRAINT fk_enrollment_tokens_branches_tenant_id_branch_id
                        FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE SET NULL (branch_id),
                    ADD CONSTRAINT fk_enrollment_tokens_departments_tenant_id_department_id
                        FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON DELETE SET NULL (department_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE enrollment_tokens
                    DROP CONSTRAINT IF EXISTS fk_enrollment_tokens_departments_tenant_id_department_id,
                    DROP CONSTRAINT IF EXISTS fk_enrollment_tokens_branches_tenant_id_branch_id;
                """);

            migrationBuilder.DropColumn(
                name: "branch_id",
                table: "enrollment_tokens");

            migrationBuilder.DropColumn(
                name: "department_id",
                table: "enrollment_tokens");
        }
    }
}
