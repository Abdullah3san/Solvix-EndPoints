namespace Solvix.Modules.Assistant.Remediation;

using System.Text.Json;
using Solvix.Modules.Assistant.Domain;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;

public sealed record ProposalTargetDto(Guid Id, string Name);

public sealed record ProposalResultDto(Guid DeviceId, string Status, string Message);

public sealed record ProposalDto(
    Guid Id,
    string Action,
    string Title,
    string Rationale,
    RiskLevel Risk,
    ProposalStatus Status,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<ProposalTargetDto> Targets,
    int TargetCount,
    string? ProposedByEmail,
    Guid ProposedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    TenantRole RequiredApproverRole,
    string? DecidedByEmail,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    DateTimeOffset? ExecutedAtUtc,
    IReadOnlyList<ProposalResultDto> Results,
    bool AutoApproved);

/// <summary>What the assistant (or any caller) asks for. Everything here is re-validated by the backend.</summary>
public sealed record ProposeRemediationCommand(
    string Action,
    IReadOnlyDictionary<string, JsonElement>? Parameters,
    IReadOnlyList<string>? DeviceRefs,
    FleetScope? Scope,
    string Title,
    string Rationale,
    Guid? ConversationId);

public sealed record ApproveProposalRequest(bool Confirm, string? Note);

public sealed record RejectProposalRequest(string? Reason);
