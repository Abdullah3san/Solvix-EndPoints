namespace Solvix.Modules.Mdm.Application.Devices;

using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Persistence;

/// <summary>Shared state transitions and audit entries used by the Android, Apple and command flows.</summary>
internal static class DeviceLifecycle
{
    public static void Record(ISolvixDbContext db, MobileDevice device, DeviceEventType type, string summary, Guid? actor = null, Guid? commandId = null) =>
        db.Set<DeviceEvent>().Add(new DeviceEvent(device.TenantId, device.Id, type, summary, actor, commandId));

    /// <summary>Whether removing management from this device also erases the whole device.</summary>
    public static bool RemovalErasesDevice(MobileDevice device, DeviceCommandType type) =>
        type == DeviceCommandType.Wipe
        || (device.Platform == MobilePlatform.Android && device.Mode is ManagementMode.FullyManaged or ManagementMode.Dedicated);

    public static void CompleteRemoval(ISolvixDbContext db, MobileDevice device, DeviceCommandType type, DateTimeOffset now, Guid? actor, Guid? commandId)
    {
        if (!device.IsActive)
        {
            return;
        }

        var erased = RemovalErasesDevice(device, type);
        device.CompleteRemoval(now, erased);
        var summary = erased
            ? "Device wiped (factory reset)."
            : device.Ownership == DeviceOwnership.PersonallyOwned
                ? "Management and corporate data removed; personal data untouched."
                : "Management and corporate data removed.";
        Record(db, device, DeviceEventType.Unenrolled, summary, actor, commandId);
    }
}
