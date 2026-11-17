namespace Solvix.Agent.Contracts;

/// <summary>
/// Local, per-computer policy for remote sessions. It is read from the agent's own configuration
/// (written at install time) and can never be changed by the server.
/// </summary>
public enum RemoteAccessMode
{
    /// <summary>Default. The agent never accepts a remote session.</summary>
    Disabled = 1,

    /// <summary>The person at the computer must approve every session.</summary>
    ConsentRequired = 2,

    /// <summary>Sessions start without asking (unattended servers, kiosks). Still audited.</summary>
    Unattended = 3,
}

public enum RemoteConsent
{
    /// <summary>The signed-in person approved this session.</summary>
    User = 1,

    /// <summary>Local policy allows unattended sessions.</summary>
    Unattended = 2,
}

/// <summary>A technician asked to open a remote command session on this computer.</summary>
public sealed record AgentRemoteRequest
{
    public required Guid SessionId { get; init; }

    public required string RequestedBy { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset RequestedAtUtc { get; init; }
}

public sealed record PendingRemoteResponse
{
    public AgentRemoteRequest? Request { get; init; }
}

public sealed record AcceptRemoteRequest
{
    public RemoteConsent Consent { get; init; }
}

public sealed record DenyRemoteRequest
{
    public string? Reason { get; init; }
}

public sealed record EndRemoteRequest
{
    public string? Reason { get; init; }
}

/// <summary>Terminal text travelling in either direction.</summary>
public sealed record RemoteChunk
{
    public const int MaxTextLength = 16 * 1024;

    public required long Sequence { get; init; }

    public required string Text { get; init; }
}

/// <summary>What the agent sends back: output of the shell.</summary>
public sealed record RemoteOutputRequest
{
    public required string Text { get; init; }
}

/// <summary>Input from the technician (long poll). <see cref="Ended"/> tells the agent to stop the shell.</summary>
public sealed record RemoteInputResponse
{
    public IReadOnlyList<RemoteChunk> Chunks { get; init; } = [];

    public bool Ended { get; init; }
}
