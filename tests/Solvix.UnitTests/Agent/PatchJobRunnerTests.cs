namespace Solvix.UnitTests.Agent;

using Microsoft.Extensions.Logging.Abstractions;
using Solvix.Agent.Core.Patching;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;

internal sealed class FakeWindowsUpdate : IWindowsUpdateClient
{
    public Dictionary<string, UpdateStatus> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, UpdateInstallOutcome> Outcomes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Installed { get; } = [];

    public Action<string>? OnInstall { get; set; }

    public TimeSpan InstallDuration { get; set; }

    public Task<PatchInventoryReport> ScanAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<UpdateStatus> GetStatusAsync(PatchJobUpdate update, CancellationToken cancellationToken) =>
        Task.FromResult(Updates.GetValueOrDefault(update.UpdateId) ?? new UpdateStatus(false, false, false));

    public async Task<UpdateInstallOutcome> InstallAsync(PatchJobUpdate update, CancellationToken cancellationToken)
    {
        if (InstallDuration > TimeSpan.Zero)
        {
            await Task.Delay(InstallDuration, cancellationToken);
        }

        OnInstall?.Invoke(update.UpdateId);
        Installed.Add(update.UpdateId);
        var outcome = Outcomes.GetValueOrDefault(update.UpdateId) ?? new UpdateInstallOutcome(OperationResultCode.Succeeded, 0, false);
        if (outcome.ResultCode == OperationResultCode.Succeeded)
        {
            Updates[update.UpdateId] = new UpdateStatus(true, true, outcome.RebootRequired);
        }

        return outcome;
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
    private readonly FakeWindowsUpdate _wu = new();
    private readonly FakeReboot _reboot = new();
    private readonly InMemoryPatchJobJournal _journal = new();

    private PatchJobRunner Runner() => new(_wu, _reboot, _journal, TimeProvider.System, NullLogger<PatchJobRunner>.Instance);

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
    public async Task Finished_job_clears_its_journal()
    {
        _wu.Updates["a"] = new UpdateStatus(true, false, false);
        var result = await Runner().RunAsync(Job(null, "a"), CancellationToken.None);
        Assert.Equal(PatchJobOutcome.Succeeded, result!.Outcome);
        Assert.Null(_journal.Read());
        Assert.Null(await Runner().RecoverAsync(CancellationToken.None));
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
