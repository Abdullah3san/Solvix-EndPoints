using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security for the monitoring tables. <c>monitor_checkpoints</c> is
/// platform bookkeeping (no tenant data) and is intentionally not tenant-isolated.
/// </summary>
[DbContext(typeof(SolvixDbContext))]
[Migration("20261112215200_RealTimeMonitoringSecurity")]
public sealed class RealTimeMonitoringSecurityMigration : Migration
{
    public static readonly string[] TenantTables =
    [
        "device_presence",
        "device_connection_events",
        "device_availability_days",
        "monitoring_alerts",
        "monitoring_settings",
    ];

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
