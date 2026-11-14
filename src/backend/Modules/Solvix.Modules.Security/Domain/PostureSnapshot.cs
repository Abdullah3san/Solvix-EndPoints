namespace Solvix.Modules.Security.Domain;

using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Entities;

/// <summary>
/// One collection from one device plus its evaluation. The raw report is kept only for the latest snapshot
/// (older ones are compacted to their evaluation) because software inventories are large and not needed for history.
/// </summary>
public sealed class PostureSnapshot : TenantEntity
{
    private PostureSnapshot()
    {
    }

    public PostureSnapshot(
        Guid tenantId,
        Guid endpointId,
        PostureReport report,
        DateTimeOffset receivedAtUtc)
        : base(tenantId)
    {
        ArgumentNullException.ThrowIfNull(report);
        EndpointId = endpointId;
        CollectedAtUtc = report.CollectedAtUtc;
        ReceivedAtUtc = receivedAtUtc;
        SchemaVersion = report.SchemaVersion;
        Report = report;
    }

    public Guid EndpointId { get; private set; }

    public DateTimeOffset CollectedAtUtc { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public int SchemaVersion { get; private set; }

    /// <summary>The raw report. Null once the snapshot has been compacted.</summary>
    public PostureReport? Report { get; private set; }

    public IReadOnlyList<CheckResult> Evaluation { get; private set; } = [];

    public int Score { get; private set; }

    public ComplianceBand Band { get; private set; }

    public double Coverage { get; private set; }

    public Guid? PolicyId { get; private set; }

    public int? PolicyVersion { get; private set; }

    public void SetEvaluation(IReadOnlyList<CheckResult> evaluation, ComplianceScore score, Guid? policyId, int? policyVersion)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(score);
        Evaluation = evaluation;
        Score = score.Score;
        Band = score.Band;
        Coverage = score.Coverage;
        PolicyId = policyId;
        PolicyVersion = policyVersion;
    }

    public void Compact() => Report = null;
}
