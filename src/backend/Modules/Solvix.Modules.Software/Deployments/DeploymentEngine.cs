namespace Solvix.Modules.Software.Deployments;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Configuration;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record DeploymentPassResult(int Started, int Dispatched, int Collected, int Retried, int Expired, int Completed);

/// <summary>
/// Executes approved deployments in one tenant: starts them at their scheduled time, sends one
/// signed command per device through the remote management broker, collects results, retries
/// transient failures, closes the deployment window and generates the final report.
///
/// Every transition is idempotent: a target's command id is written before the command is
/// dispatched (and dispatching is idempotent on that id), results are applied only to the target
/// whose current command they belong to, and all writes for a tenant are serialized by the
/// software audit lock — so a crash, a restart or two API nodes never send an attempt twice.
/// </summary>
public sealed partial class DeploymentEngine(
    ISolvixDbContext db,
    IRemoteCommandDispatcher dispatcher,
    SoftwareAuditTrail audit,
    IOptions<SoftwareOptions> options,
    IClock clock,
    ILogger<DeploymentEngine> logger)
{
    internal static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly TimeSpan DownloadAllowance = TimeSpan.FromMinutes(30);

    public async Task<DeploymentPassResult> ProcessTenantAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var started = 0;
        var dispatched = 0;
        var expired = 0;

        // 1. Approval periods that ran out.
        foreach (var id in await db.Set<SoftwareDeployment>().AsNoTracking()
                     .Where(d => d.Status == DeploymentStatus.PendingApproval && d.ApprovalExpiresAtUtc <= now).Select(d => d.Id).ToListAsync(cancellationToken))
        {
            await audit.AtomicAsync(
                async ct =>
                {
                    var deployment = await db.Set<SoftwareDeployment>().FirstAsync(d => d.Id == id, ct);
                    if (deployment.Status != DeploymentStatus.PendingApproval)
                    {
                        return false;
                    }

                    deployment.Status = DeploymentStatus.Expired;
                    deployment.CompletedAtUtc = now;
                    await CloseOpenTargetsAsync(id, TargetStatus.Cancelled, "approval_expired", "The deployment was not approved in time.", now, ct);
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentExpired, SoftwareAuditOutcome.Failure, deployment.PackageId, id, Detail: "approval period ended"));
                    return true;
                },
                cancellationToken);
            expired++;
        }

        // 2. Scheduled deployments whose start time arrived.
        foreach (var id in await db.Set<SoftwareDeployment>().AsNoTracking()
                     .Where(d => d.Status == DeploymentStatus.Scheduled && d.ScheduledStartUtc <= now).Select(d => d.Id).ToListAsync(cancellationToken))
        {
            await audit.AtomicAsync(
                async ct =>
                {
                    var deployment = await db.Set<SoftwareDeployment>().FirstAsync(d => d.Id == id, ct);
                    if (deployment.Status != DeploymentStatus.Scheduled)
                    {
                        return false;
                    }

                    deployment.Status = DeploymentStatus.Running;
                    deployment.StartedAtUtc = now;
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentStarted, SoftwareAuditOutcome.Success, deployment.PackageId, id));
                    return true;
                },
                cancellationToken);
            started++;
        }

        // 3. Running deployments: close the window or send the next commands.
        var running = await db.Set<SoftwareDeployment>().AsNoTracking().Where(d => d.Status == DeploymentStatus.Running).ToListAsync(cancellationToken);
        foreach (var deployment in running)
        {
            if (deployment.DeadlineUtc <= now)
            {
                expired += await audit.AtomicAsync(
                    ct => CloseOpenTargetsAsync(deployment.Id, TargetStatus.Expired, "deployment_window_closed", "The deployment window closed before this device could be reached.", now, ct, pendingOnly: true),
                    cancellationToken);
                continue;
            }

            dispatched += await DispatchDueAsync(deployment, now, cancellationToken);
        }

        // 4. Collect results of commands in flight (also of cancelled deployments).
        var (collected, retried) = await ReconcileAsync(null, now, cancellationToken);

        // 5. Finish deployments with nothing left to do and write their reports.
        var completed = 0;
        foreach (var id in await db.Set<SoftwareDeployment>().AsNoTracking()
                     .Where(d => d.Status == DeploymentStatus.Running || (d.Status >= DeploymentStatus.Completed && d.ReportJson == null))
                     .Select(d => d.Id).ToListAsync(cancellationToken))
        {
            if (await FinalizeIfDoneAsync(id, cancellationToken))
            {
                completed++;
            }
        }

        return new DeploymentPassResult(started, dispatched, collected, retried, expired, completed);
    }

    /// <summary>Collects the results of one deployment's commands now (used after a cancellation).</summary>
    public Task ReconcileDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken) => ReconcileAsync(deploymentId, clock.UtcNow, cancellationToken);

    /// <summary>Applies a finished command to its target right away (called when the agent reports).</summary>
    public async Task ApplyAsync(RemoteCommandState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var deploymentId = await audit.AtomicAsync(
            async ct =>
            {
                var target = await db.Set<DeploymentTarget>().FirstOrDefaultAsync(t => t.CurrentExecutionId == state.ExecutionId, ct);
                if (target is null || !DeploymentTarget.IsActive(target.Status))
                {
                    return (Guid?)null;
                }

                var deployment = await db.Set<SoftwareDeployment>().AsNoTracking().FirstAsync(d => d.Id == target.DeploymentId, ct);
                Apply(deployment, target, state, clock.UtcNow);
                return target.DeploymentId;
            },
            cancellationToken);
        if (deploymentId is { } id)
        {
            await FinalizeIfDoneAsync(id, cancellationToken);
        }
    }

    /// <summary>Within the caller's unit of work: ends targets that have not been sent (or, unless <paramref name="pendingOnly"/>, all open ones).</summary>
    internal async Task<int> CloseOpenTargetsAsync(Guid deploymentId, TargetStatus status, string code, string message, DateTimeOffset now, CancellationToken cancellationToken, bool pendingOnly = false)
    {
        var open = await db.Set<DeploymentTarget>()
            .Where(t => t.DeploymentId == deploymentId && (t.Status == TargetStatus.Pending || (!pendingOnly && (t.Status == TargetStatus.Dispatched || t.Status == TargetStatus.Running))))
            .ToListAsync(cancellationToken);
        foreach (var target in open)
        {
            target.Status = status;
            target.ErrorCode = code;
            target.ErrorMessage = message;
            target.NextAttemptAtUtc = null;
            target.CompletedAtUtc = now;
        }

        return open.Count;
    }

    /// <summary>Completes a running deployment once no device is left in progress and stores the report of a finished one.</summary>
    internal async Task<bool> FinalizeIfDoneAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var finalized = await audit.AtomicAsync(
            async ct =>
            {
                var deployment = await db.Set<SoftwareDeployment>().FirstAsync(d => d.Id == deploymentId, ct);
                if ((deployment.IsFinal() && deployment.ReportJson is not null) || deployment.Status is DeploymentStatus.PendingApproval or DeploymentStatus.Scheduled)
                {
                    return false;
                }

                var statuses = await db.Set<DeploymentTarget>().Where(t => t.DeploymentId == deploymentId).GroupBy(t => t.Status)
                    .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                if (statuses.Any(s => DeploymentTarget.IsActive(s.Key)))
                {
                    return false;
                }

                if (deployment.Status == DeploymentStatus.Running)
                {
                    var succeeded = statuses.Where(s => s.Key == TargetStatus.Succeeded).Sum(s => s.Count);
                    var failed = statuses.Where(s => s.Key is TargetStatus.Failed or TargetStatus.TimedOut or TargetStatus.Expired).Sum(s => s.Count);
                    deployment.Status = failed == 0 ? DeploymentStatus.Completed : succeeded == 0 ? DeploymentStatus.Failed : DeploymentStatus.CompletedWithErrors;
                    deployment.CompletedAtUtc = now;
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentCompleted, failed == 0 ? SoftwareAuditOutcome.Success : SoftwareAuditOutcome.Failure,
                        deployment.PackageId, deploymentId, Detail: $"{deployment.Status}: {succeeded.ToString(CultureInfo.InvariantCulture)} succeeded, {failed.ToString(CultureInfo.InvariantCulture)} failed"));
                }

                deployment.ReportJson = JsonSerializer.Serialize(await BuildReportAsync(deployment, final: true, ct), ReportJson);
                return true;
            },
            cancellationToken);
        return finalized;
    }

    internal async Task<DeploymentReport> BuildReportAsync(SoftwareDeployment deployment, bool final, CancellationToken cancellationToken)
    {
        var targets = await db.Set<DeploymentTarget>().AsNoTracking().Where(t => t.DeploymentId == deployment.Id).OrderBy(t => t.DeviceName).ToListAsync(cancellationToken);
        var approvals = await db.Set<DeploymentApproval>().AsNoTracking().Where(a => a.DeploymentId == deployment.Id).OrderBy(a => a.DecidedAtUtc)
            .Select(a => new DeploymentApprovalDto(a.Id, a.ApproverId, a.Decision, a.Comment, a.DecidedAtUtc)).ToListAsync(cancellationToken);
        var counts = Count(targets.GroupBy(t => t.Status).Select(g => (g.Key, g.Count())));
        var attempted = counts.Succeeded + counts.Failed + counts.TimedOut + counts.Expired;
        var durations = targets.Where(t => t.Status == TargetStatus.Succeeded && t.StartedAtUtc is not null && t.CompletedAtUtc is not null)
            .Select(t => (t.CompletedAtUtc!.Value - t.StartedAtUtc!.Value).TotalSeconds).ToList();
        return new DeploymentReport(
            deployment.Id,
            deployment.Name,
            deployment.Action,
            deployment.Status,
            deployment.PackageName,
            deployment.PackageVersion,
            deployment.PackageSha256,
            deployment.CreatedBy,
            deployment.RequestedAtUtc,
            approvals,
            deployment.StartedAtUtc,
            deployment.CompletedAtUtc,
            counts,
            attempted == 0 ? 0 : Math.Round(100.0 * counts.Succeeded / attempted, 1),
            targets.Count(t => t.RebootRequired),
            durations.Count == 0 ? null : Math.Round(durations.Average(), 1),
            targets.Where(t => t.Status is TargetStatus.Failed or TargetStatus.TimedOut or TargetStatus.Expired)
                .GroupBy(t => t.ErrorCode ?? "unknown")
                .Select(g => new FailureSummary(g.Key, g.Count(), g.Select(t => t.ErrorMessage).FirstOrDefault(m => m is not null)))
                .OrderByDescending(f => f.Devices)
                .ToList(),
            targets.Select(t => new DeploymentReportRow(
                t.DeviceId, t.DeviceName, t.Status, t.Outcome, t.Attempts, t.ExitCode, t.ErrorCode, t.ErrorMessage, t.DetectedVersion, t.RebootRequired,
                t.StartedAtUtc, t.CompletedAtUtc, t.StartedAtUtc is { } s && t.CompletedAtUtc is { } c ? Math.Round((c - s).TotalSeconds, 1) : null)).ToList(),
            clock.UtcNow,
            final);
    }

    internal static TargetCountsDto Count(IEnumerable<(TargetStatus Status, int Count)> counts)
    {
        var map = counts.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        int Of(params TargetStatus[] statuses) => statuses.Sum(s => map.GetValueOrDefault(s));
        return new TargetCountsDto(
            map.Values.Sum(),
            Of(TargetStatus.Pending),
            Of(TargetStatus.Dispatched, TargetStatus.Running),
            Of(TargetStatus.Succeeded),
            Of(TargetStatus.Failed),
            Of(TargetStatus.Cancelled),
            Of(TargetStatus.Skipped),
            Of(TargetStatus.TimedOut),
            Of(TargetStatus.Expired));
    }

    internal static SoftwareOperationResult? ParseResult(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(output, AgentJsonContext.Default.SoftwareOperationResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> DispatchDueAsync(SoftwareDeployment deployment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await db.Set<DeploymentTarget>().AsNoTracking()
            .Where(t => t.DeploymentId == deployment.Id && t.Status == TargetStatus.Pending && (t.NextAttemptAtUtc == null || t.NextAttemptAtUtc <= now))
            .OrderBy(t => t.Attempts).ThenBy(t => t.DeviceName)
            .Take(Math.Clamp(options.Value.DispatchBatchSize, 1, 1000))
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var packageReady = await db.Set<SoftwarePackage>().AsNoTracking().AnyAsync(p => p.Id == deployment.PackageId && p.Status == PackageStatus.Ready, cancellationToken);
        var commandType = deployment.Action == DeploymentAction.Install ? CommandTypes.SoftwareInstall : CommandTypes.SoftwareUninstall;
        var sent = 0;
        foreach (var candidate in due)
        {
            var executionId = Guid.CreateVersion7();
            var claimed = await audit.AtomicAsync(
                async ct =>
                {
                    var target = await db.Set<DeploymentTarget>().FirstOrDefaultAsync(t => t.Id == candidate.Id && t.Status == TargetStatus.Pending && t.Attempts == candidate.Attempts, ct);
                    var current = await db.Set<SoftwareDeployment>().AsNoTracking().Where(d => d.Id == deployment.Id).Select(d => d.Status).FirstAsync(ct);
                    if (target is null || current != DeploymentStatus.Running)
                    {
                        return false;
                    }

                    if (!packageReady)
                    {
                        target.Status = TargetStatus.Failed;
                        target.ErrorCode = SoftwareErrorCodes.PackageUnavailable;
                        target.ErrorMessage = "The package is no longer available in the repository (retired or failed its integrity check).";
                        target.CompletedAtUtc = now;
                        audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.TargetFinished, SoftwareAuditOutcome.Failure, deployment.PackageId, deployment.Id, target.DeviceId, target.ErrorCode));
                        return false;
                    }

                    target.Status = TargetStatus.Dispatched;
                    target.Attempts++;
                    target.CurrentExecutionId = executionId;
                    target.NextAttemptAtUtc = null;
                    target.StartedAtUtc ??= now;
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.TargetDispatched, SoftwareAuditOutcome.Success, deployment.PackageId, deployment.Id, target.DeviceId,
                        $"attempt {target.Attempts.ToString(CultureInfo.InvariantCulture)}, command {executionId:D}"));
                    return true;
                },
                cancellationToken);
            if (!claimed)
            {
                continue;
            }

            await SendAsync(deployment, candidate.DeviceId, candidate.Id, executionId, commandType, candidate.Attempts + 1, cancellationToken);
            sent++;
        }

        return sent;
    }

    private async Task SendAsync(SoftwareDeployment deployment, Guid deviceId, Guid targetId, Guid executionId, string commandType, int attempt, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(deployment.Parameters, StringComparer.Ordinal)
        {
            ["deploymentId"] = deployment.Id.ToString("D"),
            ["targetId"] = targetId.ToString("D"),
        };

        // The same validation the device and the executor apply; a frozen template that no longer parses is a bug, not a device failure.
        var operation = SoftwareOperation.Parse(commandType, parameters);
        var state = await dispatcher.DispatchAsync(
            new DelegatedCommandRequest(
                executionId,
                deviceId,
                commandType,
                parameters,
                deployment.CreatedBy,
                deployment.FinalApprovalId,
                deployment.FinalApprovedBy,
                $"Software deployment '{deployment.Name}' ({deployment.Id:D}), attempt {attempt.ToString(CultureInfo.InvariantCulture)}",
                deployment.DeadlineUtc,
                TimeSpan.FromSeconds(operation.TimeoutSeconds) + DownloadAllowance),
            cancellationToken);
        if (state.IsFinal)
        {
            await ApplyAsync(state, cancellationToken);
        }
    }

    private async Task<(int Collected, int Retried)> ReconcileAsync(Guid? deploymentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var inFlight = db.Set<DeploymentTarget>().AsNoTracking()
            .Where(t => (t.Status == TargetStatus.Dispatched || t.Status == TargetStatus.Running) && t.CurrentExecutionId != null);
        if (deploymentId is { } only)
        {
            inFlight = inFlight.Where(t => t.DeploymentId == only);
        }

        var targets = await inFlight.OrderBy(t => t.UpdatedAtUtc).Take(1000).ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return (0, 0);
        }

        var states = (await dispatcher.GetStatesAsync(targets.Select(t => t.CurrentExecutionId!.Value).ToList(), cancellationToken)).ToDictionary(s => s.ExecutionId);
        var deployments = await db.Set<SoftwareDeployment>().AsNoTracking().Where(d => targets.Select(t => t.DeploymentId).Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        var collected = 0;
        var retried = 0;
        foreach (var snapshot in targets)
        {
            if (!states.TryGetValue(snapshot.CurrentExecutionId!.Value, out var state))
            {
                // Claimed but never handed to the broker (crash in between): send the same attempt again (idempotent).
                if (snapshot.Status == TargetStatus.Dispatched && deployments[snapshot.DeploymentId] is { Status: DeploymentStatus.Running } d)
                {
                    LogResend(logger, snapshot.CurrentExecutionId.Value);
                    await SendAsync(d, snapshot.DeviceId, snapshot.Id, snapshot.CurrentExecutionId.Value,
                        d.Action == DeploymentAction.Install ? CommandTypes.SoftwareInstall : CommandTypes.SoftwareUninstall, snapshot.Attempts, cancellationToken);
                }

                continue;
            }

            if (state.Phase == RemoteCommandPhase.Pending || (state.Phase == RemoteCommandPhase.Running && snapshot.Status == TargetStatus.Running))
            {
                continue;
            }

            var outcome = await audit.AtomicAsync(
                async ct =>
                {
                    var target = await db.Set<DeploymentTarget>().FirstOrDefaultAsync(t => t.Id == snapshot.Id && t.CurrentExecutionId == state.ExecutionId, ct);
                    return target is null || !DeploymentTarget.IsActive(target.Status) ? (TargetStatus?)null : Apply(deployments[snapshot.DeploymentId], target, state, now);
                },
                cancellationToken);
            if (outcome == TargetStatus.Pending)
            {
                retried++;
            }
            else if (outcome is { } final && DeploymentTarget.IsFinal(final))
            {
                collected++;
            }
        }

        return (collected, retried);
    }

    /// <summary>Applies a command state to its target (inside the caller's unit of work) and returns the target's new status.</summary>
    private TargetStatus Apply(SoftwareDeployment deployment, DeploymentTarget target, RemoteCommandState state, DateTimeOffset now)
    {
        switch (state.Phase)
        {
            case RemoteCommandPhase.Pending:
                return target.Status;
            case RemoteCommandPhase.Running:
                target.Status = TargetStatus.Running;
                return target.Status;
        }

        var result = ParseResult(state.Output);
        target.Outcome = result?.Outcome;
        target.ExitCode = result?.InstallerExitCode ?? state.ExitCode;
        target.RebootRequired = result?.RebootRequired ?? false;
        target.DetectedVersion = result?.DetectedVersion ?? target.DetectedVersion;
        target.LogExcerpt = result?.LogExcerpt;
        if (state.Phase == RemoteCommandPhase.Succeeded)
        {
            target.Status = TargetStatus.Succeeded;
            target.ErrorCode = null;
            target.ErrorMessage = null;
            target.CompletedAtUtc = state.CompletedAtUtc ?? now;
            audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.TargetFinished, SoftwareAuditOutcome.Success, deployment.PackageId, deployment.Id, target.DeviceId,
                $"{target.Outcome?.ToString() ?? "Succeeded"}{(target.DetectedVersion is null ? string.Empty : $", version {target.DetectedVersion}")}{(target.RebootRequired ? ", reboot required" : string.Empty)}"));
            return target.Status;
        }

        var code = state.ErrorCode ?? state.Phase switch
        {
            RemoteCommandPhase.TimedOut => "timed_out",
            RemoteCommandPhase.Expired => "device_unreachable",
            RemoteCommandPhase.Cancelled => SoftwareErrorCodes.Cancelled,
            RemoteCommandPhase.Rejected => "rejected",
            _ => SoftwareErrorCodes.InstallerFailed,
        };
        target.ErrorCode = code.Length > 64 ? code[..64] : code;
        target.ErrorMessage = state.ErrorMessage is { Length: > 2000 } m ? m[..2000] : state.ErrorMessage ?? Describe(state.Phase);

        // Transient failures are tried again while the window is open. A timed-out (interrupted) attempt is safe to
        // repeat: the agent detects an installation that completed before the interruption and reports it as such.
        var retryable = state.Phase == RemoteCommandPhase.TimedOut || (state.Phase == RemoteCommandPhase.Failed && SoftwareErrorCodes.Retryable.Contains(code));
        var nextAttempt = now.AddMinutes(deployment.RetryDelayMinutes);
        if (retryable && deployment.Status == DeploymentStatus.Running && target.Attempts < deployment.MaxAttempts && nextAttempt < deployment.DeadlineUtc)
        {
            target.Status = TargetStatus.Pending;
            target.NextAttemptAtUtc = nextAttempt;
            audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.TargetRetryScheduled, SoftwareAuditOutcome.Failure, deployment.PackageId, deployment.Id, target.DeviceId,
                $"attempt {target.Attempts.ToString(CultureInfo.InvariantCulture)} failed ({code}); next at {nextAttempt:O}"));
            return target.Status;
        }

        target.Status = state.Phase switch
        {
            RemoteCommandPhase.TimedOut => TargetStatus.TimedOut,
            RemoteCommandPhase.Expired => TargetStatus.Expired,
            RemoteCommandPhase.Cancelled => TargetStatus.Cancelled,
            _ => TargetStatus.Failed,
        };
        target.CompletedAtUtc = state.CompletedAtUtc ?? now;
        audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.TargetFinished,
            target.Status == TargetStatus.Cancelled ? SoftwareAuditOutcome.Success : SoftwareAuditOutcome.Failure,
            deployment.PackageId, deployment.Id, target.DeviceId,
            $"{target.Status}: {code}{(target.ExitCode is { } exit ? $", exit {exit.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}"));
        return target.Status;
    }

    private static string Describe(RemoteCommandPhase phase) => phase switch
    {
        RemoteCommandPhase.TimedOut => "The device accepted the command but reported no result in time (it may have restarted or gone offline).",
        RemoteCommandPhase.Expired => "The device did not come online before the command expired.",
        RemoteCommandPhase.Cancelled => "Cancelled.",
        RemoteCommandPhase.Rejected => "The command was refused.",
        _ => "The installation failed.",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-sending software command {ExecutionId} that was claimed but never dispatched")]
    private static partial void LogResend(ILogger logger, Guid executionId);
}

/// <summary>Applies agent results to deployment targets as soon as they arrive.</summary>
internal sealed class DeploymentCommandObserver(DeploymentEngine engine) : IRemoteCommandObserver
{
    public string CommandTypePrefix => "software.";

    public Task OnCommandFinishedAsync(RemoteCommandState state, CancellationToken cancellationToken) =>
        state.CommandType == CommandTypes.SoftwareDetect ? Task.CompletedTask : engine.ApplyAsync(state, cancellationToken);
}

/// <summary>
/// Runs the deployment engine for every tenant with work. Discovery happens in platform scope;
/// each tenant is processed in its own tenant scope (RLS applies to every write).
/// </summary>
public sealed partial class DeploymentProcessor(
    ISolvixDbContext db,
    ITenantContextMutator tenantMutator,
    ITenantScopeFactory tenantScopes,
    ILogger<DeploymentProcessor> logger)
{
    public async Task<DeploymentPassResult> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<Guid> tenants;
        using (tenantMutator.BeginPlatformScope())
        {
            var fromDeployments = db.Set<SoftwareDeployment>().IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Status < DeploymentStatus.Completed || d.ReportJson == null)
                .Select(d => d.TenantId);
            var fromTargets = db.Set<DeploymentTarget>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Status == TargetStatus.Dispatched || t.Status == TargetStatus.Running)
                .Select(t => t.TenantId);
            tenants = await fromDeployments.Union(fromTargets).Distinct().ToListAsync(cancellationToken);
        }

        var total = new DeploymentPassResult(0, 0, 0, 0, 0, 0);
        foreach (var tenantId in tenants)
        {
            try
            {
                await using var scope = tenantScopes.CreateScope(tenantId);
                var r = await scope.ServiceProvider.GetRequiredService<DeploymentEngine>().ProcessTenantAsync(now, cancellationToken);
                total = new DeploymentPassResult(
                    total.Started + r.Started, total.Dispatched + r.Dispatched, total.Collected + r.Collected, total.Retried + r.Retried, total.Expired + r.Expired, total.Completed + r.Completed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTenantFailed(logger, ex, tenantId);
            }
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Software deployment pass failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}

public sealed partial class DeploymentProcessorService(
    IServiceScopeFactory scopes,
    IOptions<SoftwareOptions> options,
    IClock clock,
    ILogger<DeploymentProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunProcessor)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.ProcessorIntervalSeconds, 5, 300)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DeploymentProcessor>().RunAsync(clock.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Software deployment pass failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
