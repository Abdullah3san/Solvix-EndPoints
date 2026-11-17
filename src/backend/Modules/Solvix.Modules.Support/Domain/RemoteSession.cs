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

    public RemoteSessionKind Kind { get; private set; } = RemoteSessionKind.Shell;

    /// <summary>The technician asked to control the screen.</summary>
    public bool ControlRequested { get; private set; }

    /// <summary>Control was actually granted (the computer's policy and the person both allowed it).</summary>
    public bool ControlGranted { get; private set; }

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

    public static RemoteSession Request(
        Guid tenantId, Guid deviceId, Guid userId, string userName, string? reason, DateTimeOffset now,
        RemoteSessionKind kind = RemoteSessionKind.Shell, bool control = false)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ValidationException("kind", "Unknown session kind.");
        }

        var session = new RemoteSession
        {
            DeviceId = deviceId,
            Kind = kind,
            ControlRequested = control && kind == RemoteSessionKind.Screen,
            RequestedByUserId = userId,
            RequestedByName = Guard.NotNullOrWhiteSpace(userName, "requestedByName", NameMaxLength),
            Reason = Guard.Optional(reason, "reason", ReasonMaxLength),
            LastActivityAtUtc = now,
        };
        session.AssignTenant(tenantId);
        return session;
    }

    public void Accept(RemoteConsent consent, DateTimeOffset now, bool controlGranted = false)
    {
        if (Status != RemoteSessionStatus.Pending)
        {
            throw new ConflictException("The session is not waiting to be accepted.", "remote_session_not_pending");
        }

        Status = RemoteSessionStatus.Active;
        Consent = consent;
        ControlGranted = controlGranted && ControlRequested;
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

public enum RemoteFileOperationStatus
{
    /// <summary>An upload is still arriving at the server; the computer does not see it yet.</summary>
    Staging = 0,

    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>One file operation inside a Files session; also its audit record (what, where, how big, hash, outcome).</summary>
public sealed class RemoteFileOperation : TenantEntity
{
    private RemoteFileOperation()
    {
    }

    public Guid SessionId { get; private set; }

    public FileOperationKind Kind { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public RemoteFileOperationStatus Status { get; private set; } = RemoteFileOperationStatus.Pending;

    public string? Error { get; private set; }

    /// <summary>Known size: from the upload, or announced by the agent for a download.</summary>
    public long? SizeBytes { get; private set; }

    public string? Sha256 { get; private set; }

    public bool Overwrite { get; private set; }

    /// <summary>JSON of the entries of a completed list.</summary>
    public string? EntriesJson { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static RemoteFileOperation Request(Guid tenantId, Guid sessionId, FileOperationKind kind, string path, long? size, string? sha256, bool overwrite)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ValidationException("kind", "Unknown file operation.");
        }

        var operation = new RemoteFileOperation
        {
            SessionId = sessionId,
            Kind = kind,
            Path = path.Length > FileLimits.MaxPathLength ? throw new ValidationException("path", "The path is too long.") : path,
            SizeBytes = size,
            Sha256 = sha256,
            Overwrite = overwrite,
        };
        operation.AssignTenant(tenantId);
        return operation;
    }

    public static RemoteFileOperation Stage(Guid tenantId, Guid sessionId, string path, bool overwrite)
    {
        var operation = Request(tenantId, sessionId, FileOperationKind.Upload, path, null, null, overwrite);
        operation.Status = RemoteFileOperationStatus.Staging;
        return operation;
    }

    /// <summary>The whole upload is stored: from now on the computer can fetch it.</summary>
    public void MarkReady(long size, string sha256)
    {
        if (Status != RemoteFileOperationStatus.Staging)
        {
            throw new ConflictException("The upload is not being staged.", "file_operation_not_staging");
        }

        SizeBytes = size;
        Sha256 = sha256;
        Status = RemoteFileOperationStatus.Pending;
    }

    public void Claim()
    {
        if (Status == RemoteFileOperationStatus.Pending)
        {
            Status = RemoteFileOperationStatus.Running;
        }
    }

    public void Announce(long size)
    {
        if (Status is not (RemoteFileOperationStatus.Running or RemoteFileOperationStatus.Pending) || Kind != FileOperationKind.Download)
        {
            throw new ConflictException("The operation is not a running download.", "file_operation_not_running");
        }

        SizeBytes = size;
    }

    public void Complete(FileOperationResult result, string? entriesJson, DateTimeOffset now)
    {
        if (Status is RemoteFileOperationStatus.Completed or RemoteFileOperationStatus.Failed)
        {
            throw new ConflictException("The operation already finished.", "file_operation_finished");
        }

        if (result.Success)
        {
            Status = RemoteFileOperationStatus.Completed;
            EntriesJson = entriesJson;
            SizeBytes ??= result.SizeBytes;
            Sha256 = result.Sha256 ?? Sha256;
        }
        else
        {
            Status = RemoteFileOperationStatus.Failed;
            Error = Guard.Optional(result.Error ?? "The operation failed.", "error", 500);
        }

        CompletedAtUtc = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        if (Status is RemoteFileOperationStatus.Staging or RemoteFileOperationStatus.Pending or RemoteFileOperationStatus.Running)
        {
            Status = RemoteFileOperationStatus.Failed;
            Error = Guard.Optional(error, "error", 500);
            CompletedAtUtc = now;
        }
    }
}

/// <summary>A slice of a file in flight (either direction). Deleted shortly after the operation ends.</summary>
public sealed class RemoteFileChunk : TenantEntity
{
    private RemoteFileChunk()
    {
    }

    public Guid OperationId { get; private set; }

    public int Index { get; private set; }

    public byte[] Data { get; private set; } = [];

    public static RemoteFileChunk Create(Guid tenantId, Guid operationId, int index, byte[] data)
    {
        if (data.Length is 0 or > FileLimits.ChunkBytes)
        {
            throw new ValidationException("chunk", $"A chunk must be 1 to {FileLimits.ChunkBytes} bytes.");
        }

        var chunk = new RemoteFileChunk { OperationId = operationId, Index = index, Data = data };
        chunk.AssignTenant(tenantId);
        return chunk;
    }
}
