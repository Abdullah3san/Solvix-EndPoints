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

/// <summary>What a remote session is for.</summary>
public enum RemoteSessionKind
{
    /// <summary>Command shell.</summary>
    Shell = 1,

    /// <summary>View (and optionally control) the signed-in person's screen.</summary>
    Screen = 2,

    /// <summary>List, download and upload files in folders the computer allows.</summary>
    Files = 3,
}

/// <summary>
/// What this computer's local policy allows, reported with every heartbeat. Like
/// <see cref="RemoteAccessMode"/>, it is set on the computer and never by the server.
/// </summary>
[Flags]
public enum RemoteFeatures
{
    None = 0,
    Shell = 1,
    Screen = 2,
    Files = 4,

    /// <summary>Mouse and keyboard control while viewing the screen (without it, view only).</summary>
    Control = 8,
}

public static class RemoteFeaturesExtensions
{
    public static RemoteFeatures For(this RemoteSessionKind kind) => kind switch
    {
        RemoteSessionKind.Shell => RemoteFeatures.Shell,
        RemoteSessionKind.Screen => RemoteFeatures.Screen,
        RemoteSessionKind.Files => RemoteFeatures.Files,
        _ => RemoteFeatures.None,
    };
}

/// <summary>A technician asked to open a remote session on this computer.</summary>
public sealed record AgentRemoteRequest
{
    public RemoteSessionKind Kind { get; init; } = RemoteSessionKind.Shell;

    /// <summary>The technician asked to control the screen, not only watch it.</summary>
    public bool ControlRequested { get; init; }

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

    /// <summary>Control was granted (requested, allowed by local policy and approved). False means view only.</summary>
    public bool ControlGranted { get; init; }
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

/// <summary>
/// A mouse or keyboard event from the technician (screen sessions with control), one JSON object per
/// input chunk. Coordinates are fractions (0..1) of the shared screen so resolution never matters.
/// </summary>
public sealed record ScreenInputEvent
{
    public const string Move = "move";
    public const string Down = "down";
    public const string Up = "up";
    public const string Wheel = "wheel";
    public const string Key = "key";
    public const string Text = "text";

    public required string Type { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    /// <summary>"left", "right" or "middle" for down/up.</summary>
    public string? Button { get; init; }

    /// <summary>Wheel movement in notches (positive scrolls up).</summary>
    public int Delta { get; init; }

    /// <summary>Key name for <c>key</c> events (e.g. "Enter", "ArrowLeft", "a").</summary>
    public string? Name { get; init; }

    public bool Pressed { get; init; }

    /// <summary>Printable text typed (for <c>text</c> events).</summary>
    public string? Characters { get; init; }
}

public sealed record ScreenFrame(byte[] Jpeg, int Width, int Height);

public static class ScreenLimits
{
    public const int MaxFrameBytes = 2 * 1024 * 1024;
    public const int MaxInputBytes = 1024;
}
