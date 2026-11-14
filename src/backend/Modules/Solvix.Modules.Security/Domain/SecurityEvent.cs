namespace Solvix.Modules.Security.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>A forwarded Windows event from the allow-list. Stored as a short summary, never as a full event message.</summary>
public sealed class SecurityEvent : TenantEntity
{
    public const int SummaryMaxLength = 300;

    private SecurityEvent()
    {
    }

    public SecurityEvent(Guid tenantId, Guid endpointId, string log, long recordId, int eventId, string? provider, DateTimeOffset timeCreatedUtc, string? summary, string? targetGroup)
        : base(tenantId)
    {
        EndpointId = endpointId;
        Log = log;
        RecordId = recordId;
        EventId = eventId;
        Provider = provider is { Length: > 200 } ? provider[..200] : provider;
        TimeCreatedUtc = timeCreatedUtc;
        Summary = summary is { Length: > SummaryMaxLength } ? summary[..SummaryMaxLength] : summary;
        TargetGroup = targetGroup is { Length: > 100 } ? targetGroup[..100] : targetGroup;
    }

    public Guid EndpointId { get; private set; }

    public string Log { get; private set; } = string.Empty;

    public long RecordId { get; private set; }

    public int EventId { get; private set; }

    public string? Provider { get; private set; }

    public DateTimeOffset TimeCreatedUtc { get; private set; }

    public string? Summary { get; private set; }

    public string? TargetGroup { get; private set; }
}
