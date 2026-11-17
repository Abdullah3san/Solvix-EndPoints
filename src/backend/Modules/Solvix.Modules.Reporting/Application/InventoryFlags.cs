namespace Solvix.Modules.Reporting.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.SharedKernel.Persistence;

/// <summary>Per-device facts extracted from the latest inventory document (jsonb), evaluated inside the database.</summary>
public sealed class InventoryFlags
{
    public Guid DeviceId { get; set; }

    public bool? OsSupported { get; set; }

    public bool? SecureBoot { get; set; }

    public DateTimeOffset? BiosReleaseUtc { get; set; }

    /// <summary>True only when every fixed volume reports BitLocker protection; null when no volume data exists.</summary>
    public bool? AllFixedVolumesEncrypted { get; set; }

    public int SoftwareCount { get; set; }

    public DateTimeOffset CollectedAtUtc { get; set; }
}

internal static class InventoryQueries
{
    /// <remarks>SqlQueryRaw formats braces, so the jsonb path literals double them.</remarks>
    /// <summary>Runs under the caller's tenant session, so row-level security limits it to the tenant's own inventories.</summary>
    public static async Task<Dictionary<Guid, InventoryFlags>> LoadFlagsAsync(ISolvixDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<InventoryFlags>("""
            SELECT i.device_id AS "DeviceId",
                   (i.report_json #>> '{{operatingSystem,isSupported}}')::boolean AS "OsSupported",
                   (i.report_json #>> '{{bios,secureBootEnabled}}')::boolean AS "SecureBoot",
                   NULLIF(i.report_json #>> '{{bios,releaseDate}}', '')::timestamptz AS "BiosReleaseUtc",
                   (SELECT bool_and(COALESCE((v ->> 'bitLockerProtected')::boolean, false))
                      FROM jsonb_array_elements(CASE WHEN jsonb_typeof(i.report_json -> 'volumes') = 'array' THEN i.report_json -> 'volumes' ELSE '[]'::jsonb END) v
                     WHERE v ->> 'driveType' = 'Fixed') AS "AllFixedVolumesEncrypted",
                   i.software_count AS "SoftwareCount",
                   i.collected_at_utc AS "CollectedAtUtc"
            FROM device_inventories i
            """).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.DeviceId);
    }
}
