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
    string? EndReason);

public sealed record StartRemoteSessionRequest(string? Reason);

public sealed record RemoteInputRequest(string Text);

/// <summary>Terminal output after a position, plus the session state so the console knows when it is over.</summary>
public sealed record RemoteOutputDto(IReadOnlyList<RemoteChunk> Chunks, RemoteSessionStatus Status, string? EndReason);

public sealed record RemoteSessionListQuery(Guid? DeviceId, int Page = 1, int PageSize = 25);
