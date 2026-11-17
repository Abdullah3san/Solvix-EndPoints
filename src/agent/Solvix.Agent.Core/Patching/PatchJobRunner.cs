namespace Solvix.Agent.Core.Patching;

using Microsoft.Extensions.Logging;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;

/// <summary>Status of one update on the device, as the Windows Update Agent reports it.</summary>
public sealed record UpdateStatus(bool Found, bool IsInstalled, bool RebootRequired);

public sealed record UpdateInstallOutcome(OperationResultCode ResultCode, int HResult, bool RebootRequired);

/// <summary>Windows Update Agent API (IUpdateSession / IUpdateSearcher / IUpdateInstaller).</summary>
public interface IWindowsUpdateClient
{
    Task<PatchInventoryReport> ScanAsync(CancellationToken cancellationToken);

    Task<UpdateStatus> GetStatusAsync(PatchJobUpdate update, CancellationToken cancellationToken);

    /// <summary>Downloads (if needed) and installs exactly this update revision.</summary>
    Task<UpdateInstallOutcome> InstallAsync(PatchJobUpdate update, CancellationToken cancellationToken);
}

public interface IRebootManager
{
    DateTimeOffset LastBootUtc { get; }

    RebootStatus GetRebootStatus();

    /// <summary>Starts a restart countdown that signed-in users see (shutdown.exe / InitiateShutdown).</summary>
    void ScheduleRestart(int gracePeriodSeconds, string message);
}

/// <summary>
/// Write-ahead record of the job being installed. Written before installation
/// starts and deleted after the result is delivered; if the agent finds one at
/// start-up, the previous installation was interrupted.
/// </summary>
public interface IPatchJobJournal
{
    PatchJobJournalEntry? Read();

    void Write(PatchJobJournalEntry entry);

    void Clear();
}

/// <param name="PendingResult">Set when the job finished but its result could not be delivered yet.</param>
public sealed record PatchJobJournalEntry(PatchJobDto Job, DateTimeOffset StartedAtUtc, DateTimeOffset BootTimeAtStart, PatchJobResult? PendingResult = null);

public sealed class InMemoryPatchJobJournal : IPatchJobJournal
{
    private PatchJobJournalEntry? _entry;

    public PatchJobJournalEntry? Read() => _entry;

    public void Write(PatchJobJournalEntry entry) => _entry = entry;

    public void Clear() => _entry = null;
}

public sealed class FilePatchJobJournal(string path) : IPatchJobJournal
{
    public PatchJobJournalEntry? Read() =>
        File.Exists(path) ? ContractJson.Deserialize<PatchJobJournalEntry>(File.ReadAllText(path)) : null;

    public void Write(PatchJobJournalEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ContractJson.Serialize(entry));
        File.Move(temp, path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Installs the updates of one patch job, one update at a time. Runs in the privileged executor for a
/// verified <c>patch.install</c> command; the result travels back as the command output. Stops before
/// the maintenance window closes (reported as Aborted, which the server re-queues without counting a failure).
/// </summary>
public sealed partial class PatchJobRunner(
    IWindowsUpdateClient windowsUpdate,
    IRebootManager reboot,
    IPatchJobJournal journal,
    TimeProvider time,
    ILogger<PatchJobRunner> logger)
{
    /// <summary>No new update starts when the window closes within this margin.</summary>
    public TimeSpan WindowSafetyMargin { get; init; } = TimeSpan.FromMinutes(10);

    public async Task<PatchJobResult?> RunAsync(PatchJobDto job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var existing = journal.Read();
        if (existing is not null && existing.Job.JobId == job.JobId && existing.Job.Attempt == job.Attempt)
        {
            LogResuming(logger, job.JobId, job.Attempt);
        }

        journal.Write(new PatchJobJournalEntry(job, time.GetUtcNow(), reboot.LastBootUtc));
        var results = new List<PatchUpdateResult>();
        var aborted = false;
        string? abortReason = null;
        try
        {
            for (var i = 0; i < job.Updates.Count; i++)
            {
                var update = job.Updates[i];
                if (job.InstallWindowEndsUtc is { } end && time.GetUtcNow() >= end - WindowSafetyMargin)
                {
                    aborted = true;
                    abortReason = "the maintenance window or deadline is about to end";
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var status = await windowsUpdate.GetStatusAsync(update, cancellationToken).ConfigureAwait(false);
                if (!status.Found)
                {
                    results.Add(new PatchUpdateResult(update.UpdateId, OperationResultCode.NotStarted, 0, false));
                    continue;
                }

                if (status.IsInstalled)
                {
                    results.Add(new PatchUpdateResult(update.UpdateId, OperationResultCode.Succeeded, 0, status.RebootRequired));
                    continue;
                }

                var outcome = await windowsUpdate.InstallAsync(update, cancellationToken).ConfigureAwait(false);
                LogInstalled(logger, update.UpdateId, outcome.ResultCode, outcome.HResult);
                results.Add(new PatchUpdateResult(update.UpdateId, outcome.ResultCode, outcome.HResult, outcome.RebootRequired));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            aborted = true;
            abortReason = "the agent is stopping";
        }

        var result = Summarize(job, results, aborted, abortReason);
        journal.Clear();
        return result;
    }

    /// <summary>
    /// Called at start-up. If a journal exists the previous installation was
    /// interrupted (service crash, power loss, unexpected restart). The agent asks
    /// Windows Update what actually happened and reports that for the interrupted
    /// attempt, so the server neither waits for the lease to expire nor retries
    /// updates that did install.
    /// </summary>
    public async Task<PatchJobResult?> RecoverAsync(CancellationToken cancellationToken)
    {
        var entry = journal.Read();
        if (entry is null)
        {
            return null;
        }

        if (entry.PendingResult is { } pending)
        {
            // The job finished earlier; only the delivery failed.
            journal.Clear();
            return pending;
        }

        LogRecovering(logger, entry.Job.JobId, entry.Job.Attempt, reboot.LastBootUtc > entry.BootTimeAtStart.AddSeconds(30));
        var results = new List<PatchUpdateResult>();
        foreach (var update in entry.Job.Updates)
        {
            var status = await windowsUpdate.GetStatusAsync(update, cancellationToken).ConfigureAwait(false);
            results.Add(new PatchUpdateResult(
                update.UpdateId,
                !status.Found ? OperationResultCode.NotStarted : status.IsInstalled ? OperationResultCode.Succeeded : OperationResultCode.Aborted,
                0,
                status.RebootRequired));
        }

        var result = Summarize(entry.Job, results, aborted: false, null);
        if (result.Outcome != PatchJobOutcome.Succeeded && result.Outcome != PatchJobOutcome.NotApplicable)
        {
            result = result with
            {
                Outcome = PatchJobOutcome.Failed,
                Error = "Interrupted: the agent stopped or the device restarted during installation.",
            };
        }

        journal.Clear();
        return result;
    }

    private PatchJobResult Summarize(PatchJobDto job, List<PatchUpdateResult> results, bool aborted, string? abortReason)
    {
        var succeeded = results.Count(r => r.ResultCode is OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors);
        var failed = results.Count(r => r.ResultCode is OperationResultCode.Failed or OperationResultCode.Aborted);
        var notApplicable = results.Count(r => r.ResultCode == OperationResultCode.NotStarted);
        var installedSomething = succeeded > 0;
        var rebootRequired = results.Any(r => r.RebootRequired) || (installedSomething && reboot.GetRebootStatus().RebootRequired);

        PatchJobOutcome outcome;
        if (aborted && failed == 0)
        {
            outcome = PatchJobOutcome.Aborted;
        }
        else if (results.Count > 0 && notApplicable == results.Count)
        {
            outcome = PatchJobOutcome.NotApplicable;
        }
        else if (failed == 0 && !aborted)
        {
            outcome = PatchJobOutcome.Succeeded;
        }
        else
        {
            outcome = succeeded > 0 ? PatchJobOutcome.PartiallySucceeded : PatchJobOutcome.Failed;
        }

        var firstFailure = results.FirstOrDefault(r => r.ResultCode is OperationResultCode.Failed or OperationResultCode.Aborted);
        return new PatchJobResult(
            job.Attempt,
            outcome,
            rebootRequired,
            results,
            outcome switch
            {
                PatchJobOutcome.Aborted => $"Stopped because {abortReason}.",
                PatchJobOutcome.Failed or PatchJobOutcome.PartiallySucceeded => $"{failed} update(s) failed to install.",
                _ => null,
            },
            firstFailure?.HResult);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming patch job {JobId} attempt {Attempt}")]
    private static partial void LogResuming(ILogger logger, Guid jobId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {UpdateId} installation result {Result} (0x{HResult:X8})")]
    private static partial void LogInstalled(ILogger logger, string updateId, OperationResultCode result, int hresult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovering interrupted patch job {JobId} attempt {Attempt} (device restarted: {Rebooted})")]
    private static partial void LogRecovering(ILogger logger, Guid jobId, int attempt, bool rebooted);
}
