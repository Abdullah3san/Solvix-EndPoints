namespace Solvix.UnitTests.Agent;

using Microsoft.Extensions.Logging.Abstractions;
using Solvix.Agent.Core.Api;
using Solvix.Agent.Core.Patching;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;

internal sealed class RecordingApi : IAgentApi
{
    public List<(Guid JobId, PatchJobProgress Progress)> Progress { get; } = [];

    public List<(Guid JobId, PatchJobResult Result)> Results { get; } = [];

    public bool ProgressConflict { get; set; }

    public bool ResultUnavailable { get; set; }

    public Task<EnrollDeviceResponse> EnrollAsync(EnrollDeviceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AgentWorkResponse> HeartbeatAsync(AgentHeartbeatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task UploadInventoryAsync(PatchInventoryReport report, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReportProgressAsync(Guid jobId, PatchJobProgress progress, CancellationToken cancellationToken)
    {
        if (ProgressConflict)
        {
            throw new AgentConflictException("stale");
        }

        Progress.Add((jobId, progress));
        return Task.CompletedTask;
    }

    public Task ReportResultAsync(Guid jobId, PatchJobResult result, CancellationToken cancellationToken)
    {
        if (ResultUnavailable)
        {
            throw new HttpRequestException("server unreachable");
        }

        Results.Add((jobId, result));
        return Task.CompletedTask;
    }

    public Task AcknowledgeRestartAsync(Guid restartId, RestartAck ack, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ConfigurationDocument?> GetConfigurationAsync(string? knownRevision, CancellationToken cancellationToken) => Task.FromResult<ConfigurationDocument?>(null);

    public Task ReportConfigurationAsync(ConfigurationReport report, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeWindowsUpdate : IWindowsUpdateClient
{
    public Dictionary<string, UpdateStatus> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, UpdateInstallOutcome> Outcomes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Installed { get; } = [];

    public Action<string>? OnInstall { get; set; }

    public Task<PatchInventoryReport> ScanAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<UpdateStatus> GetStatusAsync(PatchJobUpdate update, CancellationToken cancellationToken) =>
        Task.FromResult(Updates.GetValueOrDefault(update.UpdateId) ?? new UpdateStatus(false, false, false));

    public Task<UpdateInstallOutcome> InstallAsync(PatchJobUpdate update, CancellationToken cancellationToken)
    {
        OnInstall?.Invoke(update.UpdateId);
        Installed.Add(update.UpdateId);
        var outcome = Outcomes.GetValueOrDefault(update.UpdateId) ?? new UpdateInstallOutcome(OperationResultCode.Succeeded, 0, false);
        if (outcome.ResultCode == OperationResultCode.Succeeded)
        {
            Updates[update.UpdateId] = new UpdateStatus(true, true, outcome.RebootRequired);
        }

        return Task.FromResult(outcome);
    }
}

internal sealed class FakeReboot : IRebootManager
{
    public DateTimeOffset LastBootUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(-1);

    public bool Pending { get; set; }

    public List<int> Scheduled { get; } = [];

    public RebootStatus GetRebootStatus() => new(Pending, Pending ? ["Windows Update"] : [], LastBootUtc);

    public void ScheduleRestart(int gracePeriodSeconds, string message) => Scheduled.Add(gracePeriodSeconds);
}

public sealed class PatchJobRunnerTests
{
    private readonly RecordingApi _api = new();
    private readonly FakeWindowsUpdate _wu = new();
    private readonly FakeReboot _reboot = new();
    private readonly InMemoryPatchJobJournal _journal = new();

    private PatchJobRunner Runner() => new(_api, _wu, _reboot, _journal, TimeProvider.System, NullLogger<PatchJobRunner>.Instance);

    private static PatchJobDto Job(DateTimeOffset? windowEnds = null, params string[] ids) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Nov",
        1,
        ids.Select(id => new PatchJobUpdate(id, 1, $"Update {id}")).ToList(),
        DateTimeOffset.UtcNow.AddMinutes(30),
        windowEnds,
        RestartBehavior.WithinMaintenanceWindow,
        null);

    [Fact]
    public async Task Installs_missing_updates_skips_installed_ones_and_reports_restart()
    {
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        _wu.Updates["b"] = new UpdateStatus(true, true, false);
        _wu.Outcomes["a"] = new UpdateInstallOutcome(OperationResultCode.Succeeded, 0, RebootRequired: true);
        var job = Job(null, "a", "b");

        var result = await Runner().RunAsync(job, CancellationToken.None);

        Assert.Equal(PatchJobOutcome.Succeeded, result!.Outcome);
        Assert.True(result.RebootRequired);
        Assert.Equal(["a"], _wu.Installed);
        Assert.Contains(_api.Progress, p => p.Progress.Stage == PatchJobStage.Installing);
        Assert.Single(_api.Results);
        Assert.Null(_journal.Read());
    }

    [Fact]
    public async Task Partial_failure_carries_the_failing_hresult()
    {
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        _wu.Updates["b"] = new UpdateStatus(true, false, false);
        _wu.Outcomes["b"] = new UpdateInstallOutcome(OperationResultCode.Failed, unchecked((int)0x80070643), false);

        var result = await Runner().RunAsync(Job(null, "a", "b"), CancellationToken.None);
        Assert.Equal(PatchJobOutcome.PartiallySucceeded, result!.Outcome);
        Assert.Equal(unchecked((int)0x80070643), result.HResult);
    }

    [Fact]
    public async Task Updates_not_offered_to_the_device_are_not_applicable()
    {
        var result = await Runner().RunAsync(Job(null, "unknown"), CancellationToken.None);
        Assert.Equal(PatchJobOutcome.NotApplicable, result!.Outcome);
    }

    [Fact]
    public async Task Stops_before_the_maintenance_window_closes()
    {
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        var result = await Runner().RunAsync(Job(DateTimeOffset.UtcNow.AddMinutes(5), "a"), CancellationToken.None);
        Assert.Equal(PatchJobOutcome.Aborted, result!.Outcome);
        Assert.Empty(_wu.Installed);
    }

    [Fact]
    public async Task Stale_attempt_is_abandoned_without_reporting()
    {
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        _api.ProgressConflict = true;
        Assert.Null(await Runner().RunAsync(Job(null, "a"), CancellationToken.None));
        Assert.Empty(_api.Results);
        Assert.Empty(_wu.Installed);
        Assert.Null(_journal.Read());
    }

    [Fact]
    public async Task Undeliverable_result_is_kept_in_the_journal_and_delivered_later()
    {
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        _api.ResultUnavailable = true;
        var job = Job(null, "a");
        await Runner().RunAsync(job, CancellationToken.None);
        Assert.NotNull(_journal.Read());

        Assert.NotNull(_journal.Read()!.PendingResult);

        // Next cycle: the server is reachable again and receives the original result.
        _api.ResultUnavailable = false;
        var recovered = await Runner().RecoverAsync(CancellationToken.None);
        Assert.Equal(PatchJobOutcome.Succeeded, recovered!.Outcome);
        Assert.Equal(job.JobId, Assert.Single(_api.Results).JobId);
        Assert.Null(_journal.Read());
    }

    [Fact]
    public async Task Interrupted_installation_is_reported_as_failed_after_restart()
    {
        // Simulates power loss mid-install: the journal was written, no result was sent.
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        var job = Job(null, "a");
        _journal.Write(new PatchJobJournalEntry(job, DateTimeOffset.UtcNow.AddMinutes(-20), _reboot.LastBootUtc));
        _reboot.LastBootUtc = DateTimeOffset.UtcNow; // the device rebooted

        var recovered = await Runner().RecoverAsync(CancellationToken.None);
        Assert.Equal(PatchJobOutcome.Failed, recovered!.Outcome);
        Assert.Contains("Interrupted", recovered.Error, StringComparison.Ordinal);
        Assert.Equal(job.Attempt, recovered.Attempt);
        Assert.Null(_journal.Read());
        Assert.Null(await Runner().RecoverAsync(CancellationToken.None));
    }
}
