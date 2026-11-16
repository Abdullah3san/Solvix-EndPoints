namespace Solvix.Modules.Reporting.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>Hourly fleet availability sample: how many active devices were online. Feeds the availability trend.</summary>
public sealed class AvailabilitySnapshot : TenantEntity
{
    private AvailabilitySnapshot()
    {
    }

    public DateTimeOffset HourUtc { get; private set; }

    public int ActiveDevices { get; private set; }

    public int OnlineDevices { get; private set; }

    public static AvailabilitySnapshot Create(Guid tenantId, DateTimeOffset hourUtc, int active, int online)
    {
        var s = new AvailabilitySnapshot { HourUtc = hourUtc, ActiveDevices = active, OnlineDevices = online };
        s.AssignTenant(tenantId);
        return s;
    }
}
