namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// Versioned desired state for a family of devices that share one
/// <see cref="ManagementScenario"/>. Updating the settings increments
/// <see cref="Version"/>; the version applied to each device is tracked so
/// out-of-date devices are visible.
/// </summary>
public sealed class MobilePolicy : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    private MobilePolicy()
    {
    }

    private MobilePolicy(Guid tenantId, string name, ManagementScenario scenario)
        : base(tenantId)
    {
        Name = name;
        Platform = scenario.Platform;
        Mode = scenario.Mode;
        Ownership = scenario.Ownership;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public MobilePlatform Platform { get; private set; }

    public ManagementMode Mode { get; private set; }

    public DeviceOwnership Ownership { get; private set; }

    public int Version { get; private set; } = 1;

    public MobilePolicySettings Settings { get; private set; } = new();

    /// <summary>Highest version successfully handed to the platform provider. Zero = never published.</summary>
    public int PublishedVersion { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public ManagementScenario Scenario => new(Platform, Mode, Ownership);

    public static MobilePolicy Create(Guid tenantId, string name, string? description, ManagementScenario scenario, MobilePolicySettings settings)
    {
        scenario.EnsureValid();
        var policy = new MobilePolicy(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), scenario)
        {
            Description = Guard.Optional(description, "description", DescriptionMaxLength),
            Settings = settings ?? new MobilePolicySettings(),
        };
        return policy;
    }

    /// <summary>The scenario cannot change after creation: that would silently re-target devices.</summary>
    public void Update(string name, string? description, MobilePolicySettings settings)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
        Settings = settings ?? new MobilePolicySettings();
        Version++;
    }

    public void MarkPublished(DateTimeOffset now)
    {
        PublishedVersion = Version;
        PublishedAtUtc = now;
    }

    /// <summary>Bumps the version when assigned applications change.</summary>
    public void TouchAssignments() => Version++;
}
