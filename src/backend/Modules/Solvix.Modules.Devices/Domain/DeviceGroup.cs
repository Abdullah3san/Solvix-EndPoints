namespace Solvix.Modules.Devices.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A static, admin-curated set of devices used for targeting (e.g. "Pilot ring",
/// "Finance laptops", "Kiosks"). A device can be in any number of groups.
/// </summary>
public sealed class DeviceGroup : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    private DeviceGroup()
    {
    }

    private DeviceGroup(Guid tenantId, string name)
        : base(tenantId)
    {
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public static DeviceGroup Create(Guid tenantId, string name, string? description)
    {
        var group = new DeviceGroup(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength));
        group.Update(name, description);
        return group;
    }

    public void Update(string name, string? description)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
    }
}

/// <summary>Membership of a device in a group. Both sides are referenced through tenant-safe composite keys.</summary>
public sealed class DeviceGroupMember : TenantEntity
{
    private DeviceGroupMember()
    {
    }

    public DeviceGroupMember(Guid tenantId, Guid deviceGroupId, Guid deviceId)
        : base(tenantId)
    {
        DeviceGroupId = deviceGroupId;
        DeviceId = deviceId;
    }

    public Guid DeviceGroupId { get; private set; }

    public Guid DeviceId { get; private set; }

    public DeviceGroup? DeviceGroup { get; private set; }

    public Device? Device { get; private set; }
}
