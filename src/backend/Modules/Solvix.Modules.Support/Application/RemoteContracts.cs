namespace Solvix.Modules.Support.Application;

using Solvix.Agent.Contracts;
using Solvix.Modules.Support.Domain;

public sealed record RemoteSessionDto(
    Guid Id,
    Guid DeviceId,
    string? DeviceName,
    string RequestedByName,
    string? Reason,
    RemoteSessionStatus Status,
    RemoteConsent? Consent,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? EndReason,
    RemoteSessionKind Kind = RemoteSessionKind.Shell,
    bool ControlRequested = false,
    bool ControlGranted = false);

/// <summary><paramref name="Control"/> only matters for screen sessions: ask to control, not just watch.</summary>
public sealed record StartRemoteSessionRequest(string? Reason, RemoteSessionKind Kind = RemoteSessionKind.Shell, bool Control = false);

public sealed record RemoteInputRequest(string Text);

/// <summary>Terminal output after a position, plus the session state so the console knows when it is over.</summary>
public sealed record RemoteOutputDto(IReadOnlyList<RemoteChunk> Chunks, RemoteSessionStatus Status, string? EndReason);

public sealed record RemoteSessionListQuery(Guid? DeviceId, int Page = 1, int PageSize = 25);

/// <summary>A frame of the shared screen, or just the session state when nothing newer exists.</summary>
public sealed record ScreenFrameResult(long Sequence, int Width, int Height, byte[]? Jpeg, RemoteSessionStatus Status);

public sealed record RemoteFileOperationDto(
    Guid Id,
    Guid SessionId,
    FileOperationKind Kind,
    string Path,
    RemoteFileOperationStatus Status,
    string? Error,
    long? SizeBytes,
    string? Sha256,
    IReadOnlyList<RemoteFileEntry>? Entries,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ListRemoteFilesRequest(string? Path);

/// <summary>A download ready to stream: size known, chunks arrive as the computer reads the file.</summary>
public sealed class RemoteDownload
{
    public required Guid OperationId { get; init; }

    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    public required IAsyncEnumerable<byte[]> Chunks { get; init; }
}
