namespace Solvix.Modules.Security.Application;

using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;

// ------------------------------------------------------------------ explanations

/// <summary>A rule and everything needed to understand a finding raised by it.</summary>
public sealed record RuleDto(
    string Key,
    string Title,
    RuleCategory Category,
    Severity DefaultSeverity,
    string WhatItChecks,
    string WhyItMatters,
    string SeverityRationale,
    string HowToFix,
    string? RemediationActionKey,
    string WindowsInterface)
{
    public static RuleDto From(RuleDefinition r) => new(
        r.Key, r.Title, r.Category, r.DefaultSeverity, r.WhatItChecks, r.WhyItMatters, r.SeverityRationale, r.HowToFix, r.RemediationActionKey, r.WindowsInterface);
}

public sealed record RemediationActionDto(
    string Key, string Title, string Description, RemediationRisk Risk, string? ParameterName, IReadOnlyList<string> AllowedValues, string WindowsInterface, string Impact)
{
    public static RemediationActionDto From(RemediationAction a) => new(a.Key, a.Title, a.Description, a.Risk, a.ParameterName, a.AllowedValues, a.WindowsInterface, a.Impact);
}

public sealed record SeverityDto(Severity Severity, string Meaning, string Example, int ScorePenalty);

public sealed record RulesCatalogDto(
    IReadOnlyList<RuleDto> Rules,
    IReadOnlyList<SeverityDto> Severities,
    IReadOnlyList<RemediationActionDto> RemediationActions,
    ScoringDto Scoring,
    string ProductScope);

public sealed record ScoringDto(string Formula, int CompliantThreshold, int NonCompliantThreshold, double MinimumCoverage, string BandRules);

/// <summary>The short explanation attached to every check result and finding.</summary>
public sealed record ExplanationDto(string WhyItMatters, string SeverityRationale, string HowToFix, string WindowsInterface)
{
    public static ExplanationDto For(string ruleKey) =>
        RuleCatalog.TryGet(ruleKey, out var rule)
            ? new ExplanationDto(rule.WhyItMatters, rule.SeverityRationale, rule.HowToFix, rule.WindowsInterface)
            : new ExplanationDto(string.Empty, string.Empty, string.Empty, string.Empty);
}

// ------------------------------------------------------------------ endpoints

public sealed record EndpointSummaryDto(
    Guid Id,
    string Hostname,
    EndpointStatus Status,
    string? OsCaption,
    string? OsVersion,
    string? AgentVersion,
    bool? IsDomainJoined,
    DateTimeOffset? LastReportAtUtc,
    bool IsStale,
    int? Score,
    ComplianceBand Band,
    double? Coverage,
    int OpenCritical,
    int OpenHigh,
    int OpenMedium,
    int OpenLow,
    DateTimeOffset KeyIssuedAtUtc)
{
    public static EndpointSummaryDto From(ManagedEndpoint e, DateTimeOffset now, int staleAfterDays)
    {
        var stale = e.Status == EndpointStatus.Active && e.IsStale(now, staleAfterDays);
        return new EndpointSummaryDto(
            e.Id, e.Hostname, e.Status, e.OsCaption, e.OsVersion, e.AgentVersion, e.IsDomainJoined, e.LastReportAtUtc, stale,
            e.Score, stale || e.Status == EndpointStatus.Revoked ? ComplianceBand.Unknown : e.Band, e.Coverage,
            e.OpenCritical, e.OpenHigh, e.OpenMedium, e.OpenLow, e.KeyIssuedAtUtc);
    }
}

public sealed record EndpointListQuery(string? Search, ComplianceBand? Band, bool IncludeRevoked, int Page = 1, int PageSize = 25);

public sealed record CheckResultDto(
    string RuleKey,
    string SubjectKey,
    RuleCategory Category,
    CheckOutcome Outcome,
    Severity Severity,
    string Title,
    string Evidence,
    string? RemediationActionKey,
    ExplanationDto Explanation);

/// <summary>What the latest report says about the device, grouped by Windows security feature.</summary>
public sealed record DeviceFactsDto(
    DefenderStatus? Defender,
    FirewallStatus? Firewall,
    BitLockerStatus? BitLocker,
    LocalAdminStatus? LocalAdmins,
    UsbStatus? Usb,
    UsbStoragePolicy? EffectiveUsbPolicy,
    ConfigurationStatus? Configuration,
    int? SoftwareCount,
    IReadOnlyList<CollectionError> CollectionErrors);

public sealed record EndpointDetailDto(
    EndpointSummaryDto Summary,
    string? PolicyName,
    int? PolicyVersion,
    IReadOnlyList<string> ScoreReasons,
    IReadOnlyList<ScorePenalty> ScorePenalties,
    IReadOnlyList<CheckResultDto> Checks,
    DeviceFactsDto? Facts);

public sealed record EndpointHistoryPointDto(DateTimeOffset At, int Score, ComplianceBand Band);

public sealed record EnrollEndpointRequest(string Hostname);

/// <summary>Returned once. The device key cannot be retrieved again; rotate it if lost.</summary>
public sealed record EnrollmentDto(EndpointSummaryDto Endpoint, string DeviceKey, string Notice);

public sealed record SoftwareItemDto(string Name, string? Version, string? Publisher);

public sealed record SoftwareListQuery(string? Search, int Page = 1, int PageSize = 50);

// ------------------------------------------------------------------ findings

public sealed record FindingDto(
    Guid Id,
    Guid EndpointId,
    string EndpointHostname,
    string RuleKey,
    string SubjectKey,
    RuleCategory Category,
    FindingStatus Status,
    Severity Severity,
    string Title,
    string Evidence,
    DateTimeOffset FirstDetectedAtUtc,
    DateTimeOffset LastDetectedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    int RecurrenceCount,
    DateTimeOffset? AcceptedUntilUtc,
    string? AcceptanceReason,
    string? RemediationActionKey,
    string? RemediationParameter,
    ExplanationDto Explanation);

public sealed record FindingListQuery(
    FindingStatus? Status,
    Severity? MinSeverity,
    Guid? EndpointId,
    RuleCategory? Category,
    string? RuleKey,
    string? Search,
    int Page = 1,
    int PageSize = 25);

public sealed record AcceptRiskRequest(string Reason, DateTimeOffset AcceptedUntilUtc);

// ------------------------------------------------------------------ alerts & events

public sealed record AlertDto(
    Guid Id,
    Guid EndpointId,
    string EndpointHostname,
    string RuleKey,
    Severity Severity,
    AlertStatus Status,
    string Title,
    string Evidence,
    Guid? FindingId,
    DateTimeOffset RaisedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? ClosureNote,
    ExplanationDto Explanation);

public sealed record AlertListQuery(AlertStatus? Status, bool OpenOnly, Severity? MinSeverity, Guid? EndpointId, int Page = 1, int PageSize = 25);

public sealed record NoteRequest(string? Note);

public sealed record SecurityEventDto(Guid Id, Guid EndpointId, string EndpointHostname, string Log, long RecordId, int EventId, string? Provider, DateTimeOffset TimeCreatedUtc, string? Summary, string? TargetGroup);

public sealed record EventListQuery(Guid? EndpointId, int? EventId, DateTimeOffset? From, DateTimeOffset? To, int Page = 1, int PageSize = 50);

// ------------------------------------------------------------------ policies & advisories

public sealed record PolicyDto(
    Guid Id,
    int Version,
    string Name,
    string? Description,
    PolicyStatus Status,
    PolicySettings Settings,
    Guid AuthorUserId,
    Guid? SubmittedByUserId,
    DateTimeOffset? SubmittedAtUtc,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    public static PolicyDto From(SecurityPolicy p) => new(
        p.Id, p.Version, p.Name, p.Description, p.Status, p.Settings, p.AuthorUserId, p.SubmittedByUserId, p.SubmittedAtUtc,
        p.DecidedByUserId, p.DecidedAtUtc, p.DecisionNote, p.ActivatedAtUtc, p.CreatedAtUtc);
}

public sealed record EffectivePolicyDto(Guid? PolicyId, int? Version, string Name, bool IsBuiltIn, PolicySettings Settings, int AdvisoryCount);

public sealed record SavePolicyRequest(string Name, string? Description, PolicySettings Settings);

public sealed record AdvisoryDto(
    Guid Id, string Title, string ProductPattern, string? Publisher, string? AffectedFromVersion, string FixedVersion,
    Severity Severity, string? Reference, bool IsEnabled, DateTimeOffset UpdatedAtUtc)
{
    public static AdvisoryDto From(SoftwareAdvisory a) => new(
        a.Id, a.Title, a.ProductPattern, a.Publisher, a.AffectedFromVersion, a.FixedVersion, a.Severity, a.Reference, a.IsEnabled, a.UpdatedAtUtc);
}

public sealed record SaveAdvisoryRequest(
    string Title, string ProductPattern, string? Publisher, string? AffectedFromVersion, string FixedVersion, Severity Severity, string? Reference);

// ------------------------------------------------------------------ remediation

public sealed record RemediationDto(
    Guid Id,
    Guid EndpointId,
    string EndpointHostname,
    Guid? FindingId,
    string ActionKey,
    string ActionTitle,
    string? Parameter,
    RemediationRisk Risk,
    string Impact,
    RemediationStatus Status,
    string Reason,
    Guid? RequestedByUserId,
    bool RequestedBySystem,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    bool AutoApproved,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultMessage,
    string RequiredApprover);

public sealed record CreateRemediationRequest(Guid EndpointId, Guid? FindingId, string ActionKey, string? Parameter, string Reason);

public sealed record RemediationListQuery(RemediationStatus? Status, bool PendingOnly, Guid? EndpointId, int Page = 1, int PageSize = 25);

// ------------------------------------------------------------------ audit

public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditActorType ActorType,
    Guid? ActorId,
    string ActorLabel,
    string Action,
    string EntityType,
    Guid? EntityId,
    string Summary,
    string? Details,
    string? SourceIp,
    string? CorrelationId)
{
    public static AuditEntryDto From(SecurityAuditEntry a) => new(
        a.Id, a.OccurredAtUtc, a.ActorType, a.ActorId, a.ActorLabel, a.Action, a.EntityType, a.EntityId, a.Summary, a.Details, a.SourceIp, a.CorrelationId);
}

public sealed record AuditListQuery(string? Action, string? EntityType, Guid? EntityId, Guid? ActorId, DateTimeOffset? From, DateTimeOffset? To, int Page = 1, int PageSize = 50);

// ------------------------------------------------------------------ reports

public sealed record CountDto(string Key, int Count);

public sealed record TopRuleDto(string RuleKey, string Title, Severity Severity, RuleCategory Category, int AffectedDevices);

public sealed record TrendPointDto(DateTimeOffset Day, double AverageScore, int Devices);

public sealed record SecuritySummaryDto(
    DateTimeOffset GeneratedAtUtc,
    int TotalDevices,
    int ReportingDevices,
    int StaleDevices,
    double? AverageScore,
    IReadOnlyDictionary<ComplianceBand, int> Bands,
    IReadOnlyDictionary<Severity, int> OpenFindingsBySeverity,
    IReadOnlyDictionary<RuleCategory, int> OpenFindingsByCategory,
    IReadOnlyList<TopRuleDto> TopRules,
    int OpenAlerts,
    int PendingRemediationApprovals,
    int PendingPolicyApprovals,
    string ActivePolicy,
    bool EnforcementEnabled,
    IReadOnlyList<TrendPointDto> Trend);
