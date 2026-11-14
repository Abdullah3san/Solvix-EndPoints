namespace Solvix.Modules.Devices.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>
/// Latest full inventory report of a device (1:1), stored as a validated
/// JSON document. The previous snapshot is replaced on every upload.
/// </summary>
public sealed class DeviceInventory : TenantEntity
{
    public const int ContentHashMaxLength = 64;

    private DeviceInventory()
    {
    }

    private DeviceInventory(Guid tenantId, Guid deviceId)
        : base(tenantId)
    {
        DeviceId = deviceId;
    }

    public Guid DeviceId { get; private set; }

    public int SchemaVersion { get; private set; }

    public DateTimeOffset CollectedAtUtc { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public string? ContentHash { get; private set; }

    public int SoftwareCount { get; private set; }

    /// <summary><c>InventoryReport</c> serialized as JSON (jsonb).</summary>
    public string ReportJson { get; private set; } = "{}";

    public static DeviceInventory Create(Guid tenantId, Guid deviceId) => new(tenantId, deviceId);

    public void Replace(int schemaVersion, DateTimeOffset collectedAtUtc, DateTimeOffset receivedAtUtc, string? contentHash, int softwareCount, string reportJson)
    {
        SchemaVersion = schemaVersion;
        CollectedAtUtc = collectedAtUtc;
        ReceivedAtUtc = receivedAtUtc;
        ContentHash = contentHash;
        SoftwareCount = softwareCount;
        ReportJson = reportJson;
    }
}
