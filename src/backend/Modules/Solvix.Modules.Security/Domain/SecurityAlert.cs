namespace Solvix.Modules.Security.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// Something an administrator should look at now: a High/Critical finding that opened, or a monitored Windows
/// event that matched a rule. <see cref="DedupeKey"/> guarantees one alert per occurrence even if the agent
/// re-delivers the same data.
/// </summary>
public sealed class SecurityAlert : TenantEntity
{
    public const int DedupeKeyMaxLength = 200;

    private SecurityAlert()
    {
    }

    public SecurityAlert(
        Guid tenantId, Guid endpointId, string ruleKey, string dedupeKey, Severity severity, string title, string evidence,
        Guid? findingId, DateTimeOffset now)
        : base(tenantId)
    {
        EndpointId = endpointId;
        RuleKey = ruleKey;
        DedupeKey = dedupeKey.Length <= DedupeKeyMaxLength ? dedupeKey : dedupeKey[..DedupeKeyMaxLength];
        Severity = severity;
        Title = title.Length <= 300 ? title : title[..299] + "…";
        Evidence = evidence.Length <= 500 ? evidence : evidence[..499] + "…";
        FindingId = findingId;
        Status = AlertStatus.New;
        RaisedAtUtc = now;
    }

    public Guid EndpointId { get; private set; }

    public string RuleKey { get; private set; } = string.Empty;

    public string DedupeKey { get; private set; } = string.Empty;

    public Severity Severity { get; private set; }

    public AlertStatus Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Evidence { get; private set; } = string.Empty;

    public Guid? FindingId { get; private set; }

    public DateTimeOffset RaisedAtUtc { get; private set; }

    public Guid? AcknowledgedByUserId { get; private set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>Null when the system closed the alert (for example because the underlying finding resolved).</summary>
    public Guid? ClosedByUserId { get; private set; }

    public string? ClosureNote { get; private set; }

    public bool IsOpen => Status is AlertStatus.New or AlertStatus.Acknowledged;

    public void Acknowledge(Guid userId, DateTimeOffset now)
    {
        if (Status != AlertStatus.New)
        {
            throw new ConflictException($"Only a new alert can be acknowledged (current status: {Status}).");
        }

        Status = AlertStatus.Acknowledged;
        AcknowledgedByUserId = userId;
        AcknowledgedAtUtc = now;
    }

    public void Resolve(Guid? userId, string? note, DateTimeOffset now) => Close(AlertStatus.Resolved, userId, note, now);

    public void Dismiss(Guid userId, string note, DateTimeOffset now) =>
        Close(AlertStatus.Dismissed, userId, Guard.NotNullOrWhiteSpace(note, "note", 500), now);

    private void Close(AlertStatus status, Guid? userId, string? note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new ConflictException($"The alert is already closed (status: {Status}).");
        }

        Status = status;
        ClosedByUserId = userId;
        ClosedAtUtc = now;
        ClosureNote = Guard.Optional(note, "note", 500);
    }
}
