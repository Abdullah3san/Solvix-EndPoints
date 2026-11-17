using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>Row-Level Security for file transfer operations (audit rows) and the file data in flight.</summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261117200000_RemoteScreenAndFilesSecurity")]
public sealed class RemoteScreenAndFilesSecurityMigration : Migration
{
    public static readonly string[] TenantTables = ["remote_file_operations", "remote_file_chunks"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            RowLevelSecurity.EnableTenantIsolation(migrationBuilder, table);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables.Reverse())
        {
            RowLevelSecurity.DisableTenantIsolation(migrationBuilder, table);
        }
    }
}
