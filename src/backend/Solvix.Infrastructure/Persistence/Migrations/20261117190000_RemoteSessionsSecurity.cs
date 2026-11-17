using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>Row-Level Security for remote sessions (audit rows) and the terminal text in flight.</summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261117190000_RemoteSessionsSecurity")]
public sealed class RemoteSessionsSecurityMigration : Migration
{
    public static readonly string[] TenantTables = ["remote_sessions", "remote_session_chunks"];

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
