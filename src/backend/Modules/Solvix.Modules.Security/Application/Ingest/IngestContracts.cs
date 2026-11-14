namespace Solvix.Modules.Security.Application.Ingest;

using Solvix.Modules.Security.Domain;

public sealed record ReportReceiptDto(Guid SnapshotId, int Score, ComplianceBand Band, int PendingCommands, int NextReportAfterSeconds);

/// <summary>
/// An approved change for the agent to perform. It carries only a catalogue key and at most one value from a closed set:
/// never a script. The agent maps the key to its own built-in implementation.
/// </summary>
public sealed record AgentCommandDto(Guid Id, string Action, string? Parameter, DateTimeOffset ExpiresAtUtc);

public sealed record CommandResultRequest(bool Succeeded, string? Message);
