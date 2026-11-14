namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Entities;

public enum DeviceEventType
{
    Enrolled = 1,
    Unenrolled = 2,
    InventoryUpdated = 3,
    ComplianceChanged = 4,
    PolicyApplied = 5,
    CommandRequested = 6,
    CommandApproved = 7,
    CommandRejected = 8,
    CommandCompleted = 9,
    CommandFailed = 10,
}

/// <summary>
/// Append-only audit trail for a device: who did what and what the device
/// reported. Never updated, so it doubles as compliance evidence.
/// </summary>
public sealed class DeviceEvent : TenantEntity
{
    private DeviceEvent()
    {
    }

    public DeviceEvent(Guid tenantId, Guid deviceId, DeviceEventType type, string summary, Guid? actorUserId, Guid? commandId)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Type = type;
        Summary = summary.Length <= 500 ? summary : summary[..500];
        ActorUserId = actorUserId;
        CommandId = commandId;
    }

    public Guid DeviceId { get; private set; }

    public DeviceEventType Type { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    /// <summary>Administrator behind the event; null when the device or provider caused it.</summary>
    public Guid? ActorUserId { get; private set; }

    public Guid? CommandId { get; private set; }
}
