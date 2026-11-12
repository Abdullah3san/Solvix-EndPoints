namespace Solvix.Modules.Devices.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>Static, administrator-managed collection of devices. A device can be in many groups.</summary>
public sealed class DeviceGroup : TenantEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private DeviceGroup()
    {
    }

    private DeviceGroup(Guid tenantId, string name, string? description)
        : base(tenantId)
    {
        Rename(name);
        Description = description;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Upper-cased name; unique per tenant (case-insensitive names).</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public static DeviceGroup Create(Guid tenantId, string name, string? description) =>
        new(tenantId, name, Guard.Optional(description, "description", DescriptionMaxLength));

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    public void Update(string name, string? description)
    {
        Rename(name);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
    }

    private void Rename(string name)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        NormalizedName = Normalize(Name);
    }
}

/// <summary>Membership of a device in a group. Tenant-safe composite foreign keys on both sides.</summary>
public sealed class DeviceGroupMembership : TenantEntity
{
    private DeviceGroupMembership()
    {
    }

    private DeviceGroupMembership(Guid tenantId, Guid deviceGroupId, Guid deviceId)
        : base(tenantId)
    {
        DeviceGroupId = deviceGroupId;
        DeviceId = deviceId;
    }

    public Guid DeviceGroupId { get; private set; }

    public Guid DeviceId { get; private set; }

    public static DeviceGroupMembership Create(Guid tenantId, Guid deviceGroupId, Guid deviceId) =>
        new(tenantId, deviceGroupId, deviceId);
}
