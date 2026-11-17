namespace Solvix.Modules.Support.Domain;

using Solvix.Agent.Contracts;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public enum RemoteSessionStatus
{
    /// <summary>Requested by a technician; waiting for the laptop (and its user) to answer.</summary>
    Pending = 1,

    Active = 2,
    Ended = 3,
    Denied = 4,

    /// <summary>Nobody answered in time (agent offline, or remote access disabled on the computer).</summary>
    Expired = 5,
}

/// <summary>
/// One remote command session, and its audit record: who asked, why, whether the person at the
/// computer consented, and how it ended. The terminal content itself is relayed, not kept.
/// </summary>
public sealed class RemoteSession : TenantEntity
{
    public const int ReasonMaxLength = 500;
    public const int NameMaxLength = 200;
    public static readonly TimeSpan RequestTimeToLive = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    private RemoteSession()
    {
    }

    public Guid DeviceId { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public string RequestedByName { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    public RemoteSessionStatus Status { get; private set; } = RemoteSessionStatus.Pending;

    public RemoteConsent? Consent { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public string? EndReason { get; private set; }

    public DateTimeOffset LastActivityAtUtc { get; private set; }

    public bool IsOpen => Status is RemoteSessionStatus.Pending or RemoteSessionStatus.Active;

    public static RemoteSession Request(Guid tenantId, Guid deviceId, Guid userId, string userName, string? reason, DateTimeOffset now)
    {
        var session = new RemoteSession
        {
            DeviceId = deviceId,
            RequestedByUserId = userId,
            RequestedByName = Guard.NotNullOrWhiteSpace(userName, "requestedByName", NameMaxLength),
            Reason = Guard.Optional(reason, "reason", ReasonMaxLength),
            LastActivityAtUtc = now,
        };
        session.AssignTenant(tenantId);
        return session;
    }

    public void Accept(RemoteConsent consent, DateTimeOffset now)
    {
        if (Status != RemoteSessionStatus.Pending)
        {
            throw new ConflictException("The session is not waiting to be accepted.", "remote_session_not_pending");
        }

        Status = RemoteSessionStatus.Active;
        Consent = consent;
        StartedAtUtc = now;
        LastActivityAtUtc = now;
    }

    public void Deny(string? reason, DateTimeOffset now)
    {
        if (Status != RemoteSessionStatus.Pending)
        {
            throw new ConflictException("The session is not waiting to be accepted.", "remote_session_not_pending");
        }

        Finish(RemoteSessionStatus.Denied, reason ?? "Denied by the computer.", now);
    }

    public void Expire(string reason, DateTimeOffset now)
    {
        if (IsOpen)
        {
            Finish(RemoteSessionStatus.Expired, reason, now);
        }
    }

    /// <summary>Ends the session if it is still open (idempotent).</summary>
    public void End(string? reason, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return;
        }

        // Ending a request nobody accepted is a cancellation, not a conversation that took place.
        Finish(Status == RemoteSessionStatus.Pending ? RemoteSessionStatus.Denied : RemoteSessionStatus.Ended, reason ?? "Ended.", now);
    }

    public void Touch(DateTimeOffset now) => LastActivityAtUtc = now;

    /// <summary>
    /// Applies the time limits: an unanswered request expires, an idle or over-long session ends.
    /// Returns true when the session changed.
    /// </summary>
    public bool ApplyTimeouts(DateTimeOffset now)
    {
        switch (Status)
        {
            case RemoteSessionStatus.Pending when now - CreatedAtUtc > RequestTimeToLive:
                Expire("No answer from the computer. It may be offline or remote access may be disabled on it.", now);
                return true;
            case RemoteSessionStatus.Active when now - LastActivityAtUtc > IdleTimeout:
                End("Ended after being idle.", now);
                return true;
            case RemoteSessionStatus.Active when StartedAtUtc is { } started && now - started > MaxDuration:
                End("Reached the maximum session length.", now);
                return true;
            default:
                return false;
        }
    }

    private void Finish(RemoteSessionStatus status, string reason, DateTimeOffset now)
    {
        Status = status;
        EndedAtUtc = now;
        EndReason = Guard.Optional(reason, "endReason", ReasonMaxLength);
    }
}

public enum RemoteDirection
{
    /// <summary>Technician → computer (keystrokes).</summary>
    ToDevice = 1,

    /// <summary>Computer → technician (terminal output).</summary>
    FromDevice = 2,
}

/// <summary>Terminal text in flight. Deleted when the session ends.</summary>
public sealed class RemoteChunkRow : TenantEntity
{
    private RemoteChunkRow()
    {
    }

    public long Sequence { get; private set; }

    public Guid SessionId { get; private set; }

    public RemoteDirection Direction { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public static RemoteChunkRow Create(Guid tenantId, Guid sessionId, RemoteDirection direction, string text)
    {
        if (text.Length is 0 or > RemoteChunk.MaxTextLength)
        {
            throw new ValidationException("text", $"Must be 1 to {RemoteChunk.MaxTextLength} characters.");
        }

        var row = new RemoteChunkRow { SessionId = sessionId, Direction = direction, Text = text };
        row.AssignTenant(tenantId);
        return row;
    }
}
