namespace Solvix.UnitTests.Security;

using System.Text.Json;
using Solvix.Modules.Security.Application.Ingest;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;

/// <summary>
/// Guards the contract between agent/windows/Solvix-SecurityAgent.ps1 and the server. The fixture is the unmodified
/// output of the script run on a machine where no Windows security interface exists, i.e. the worst case: every
/// section failed. The server must accept it and treat the missing data as unknown, never as compliant.
/// </summary>
public sealed class AgentContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static PostureReport Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Security", "Fixtures", "agent-report-degraded.json");
        return JsonSerializer.Deserialize<PostureReport>(File.ReadAllText(path), Web)!;
    }

    [Fact]
    public void Real_agent_output_deserializes_and_passes_validation()
    {
        var report = Load();
        Assert.Equal(PostureReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal("1.0.0", report.AgentVersion);
        Assert.NotEmpty(report.Errors!);
        var result = new PostureReportValidator().Validate(report);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void A_device_that_could_read_nothing_is_never_scored_compliant()
    {
        var report = Load();
        var results = PostureEvaluator.Evaluate(report, PolicySettings.Default, []);
        Assert.Contains(results, r => r.Outcome == CheckOutcome.Unknown);
        var score = ComplianceScorer.Score(results, new HashSet<(string, string)>(), isStale: false);
        Assert.NotEqual(ComplianceBand.Compliant, score.Band);
        Assert.True(score.Coverage < ComplianceScorer.MinimumCoverageForCompliant);
    }

    [Fact]
    public void Validator_rejects_hostile_values()
    {
        var v = new PostureReportValidator();
        Assert.False(v.Validate(Load() with { SchemaVersion = 99 }).IsValid);
        Assert.False(v.Validate(Load() with { Hostname = new string('x', 201) }).IsValid);
        Assert.False(v.Validate(Load() with { Defender = new DefenderStatus { SignatureAgeDays = -1 } }).IsValid);
        Assert.False(v.Validate(Load() with { BitLocker = new BitLockerStatus { Volumes = [new("C:", "x", true, 500, null, null)] } }).IsValid);
        Assert.False(v.Validate(Load() with { Events = Enumerable.Range(0, 501).Select(i => new ReportedEvent { Log = "Security", EventId = 4625, RecordId = i }).ToList() }).IsValid);
    }
}
