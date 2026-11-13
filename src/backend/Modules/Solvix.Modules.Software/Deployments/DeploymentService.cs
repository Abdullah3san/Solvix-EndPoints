namespace Solvix.Modules.Software.Deployments;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Domain;
using Solvix.Modules.Software.Packages;
using Solvix.Modules.Software.Policies;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IDeploymentService
{
    Task<DeploymentPreviewDto> PreviewAsync(CreateDeploymentRequest request, CancellationToken cancellationToken);

    Task<DeploymentDetailDto> CreateAsync(CreateDeploymentRequest request, CancellationToken cancellationToken);

    Task<PagedResult<DeploymentDto>> ListAsync(DeploymentListQuery query, CancellationToken cancellationToken);

    Task<DeploymentDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeploymentDetailDto> ApproveAsync(Guid id, string? comment, CancellationToken cancellationToken);

    Task<DeploymentDetailDto> RejectAsync(Guid id, string? comment, CancellationToken cancellationToken);

    Task<DeploymentDetailDto> CancelAsync(Guid id, CancellationToken cancellationToken);

    Task<DeploymentDetailDto> RedeployFailedAsync(Guid id, CancellationToken cancellationToken);

    Task<DeploymentReport> ReportAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeploymentTargetDto>> DeviceHistoryAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<DetectionRequestResult> RequestDetectionAsync(Guid packageId, Guid deviceId, CancellationToken cancellationToken);

    Task<DetectionResultDto> GetDetectionAsync(Guid executionId, CancellationToken cancellationToken);
}

internal sealed class DeploymentService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    TargetResolver targets,
    IEndpointDirectory endpoints,
    DeploymentPolicyProvider policies,
    IRemoteCommandDispatcher dispatcher,
    DeploymentEngine engine,
    SoftwareAuditTrail audit,
    IClock clock) : IDeploymentService
{
    public const int MaxAttemptsLimit = 5;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    public async Task<DeploymentPreviewDto> PreviewAsync(CreateDeploymentRequest request, CancellationToken cancellationToken) =>
        (await PlanAsync(request, cancellationToken)).Preview;

    public async Task<DeploymentDetailDto> CreateAsync(CreateDeploymentRequest request, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(request, cancellationToken);
        var preview = plan.Preview;
        if (preview.ConflictCount > 0 && !request.SkipConflicts)
        {
            var names = string.Join(", ", preview.Targets.Where(t => t.ConflictingDeploymentId is not null).Select(t => t.DeviceName).Take(10));
            throw new ConflictException($"These devices already have an unfinished deployment of this package: {names}. Cancel it or skip these devices.", "duplicate_deployment");
        }

        if (preview.DeployableCount == 0)
        {
            throw new ValidationException("targets", "None of the selected devices can receive this deployment.", "no_deployable_targets");
        }

        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        var policy = plan.Policy;
        var deployment = new SoftwareDeployment(tenantContext.RequireTenantId(), userId, now)
        {
            Name = SoftwarePolicyService.Text(request.Name, 1, 200, nameof(request.Name)),
            Reason = SoftwarePolicyService.Optional(request.Reason, 500, nameof(request.Reason)),
            Action = request.Action,
            PackageId = plan.Package.Id,
            PackageName = plan.Package.Name,
            PackageVersion = plan.Package.Version,
            PackageSha256 = plan.Package.Sha256,
            InstallerType = plan.Package.InstallerType,
            PackageRevision = plan.Package.ConfigurationRevision,
            Parameters = plan.Template,
            DeploymentGroupId = request.DeploymentGroupId,
            ScheduledStartUtc = preview.ScheduledStartUtc,
            DeadlineUtc = preview.DeadlineUtc,
            MaxAttempts = preview.MaxAttempts,
            RetryDelayMinutes = request.RetryDelayMinutes,
            TimeoutMinutes = preview.TimeoutMinutes,
            IsSensitive = preview.IsSensitive,
            SensitivityReasons = [.. preview.SensitivityReasons],
            RequiredApprovals = preview.RequiredApprovals,
            ApprovalExpiresAtUtc = now.AddHours(policy.ApprovalTtlHours),
            TargetCount = preview.Targets.Count,
        };
        if (deployment.RequiredApprovals == 0)
        {
            deployment.Status = DeploymentStatus.Scheduled;
            deployment.ApprovedAtUtc = now;
        }

        try
        {
            await audit.AtomicAsync(
                async ct =>
                {
                    db.Set<SoftwareDeployment>().Add(deployment);
                    foreach (var planned in preview.Targets)
                    {
                        var target = new DeploymentTarget(deployment.TenantId, deployment, planned.DeviceId, planned.DeviceName);
                        if (!planned.WillRun)
                        {
                            target.Status = TargetStatus.Skipped;
                            target.ErrorCode = planned.SkipReason;
                            target.ErrorMessage = SkipMessage(planned.SkipReason);
                            target.CompletedAtUtc = now;
                        }

                        db.Set<DeploymentTarget>().Add(target);
                    }

                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentRequested, SoftwareAuditOutcome.Success, deployment.PackageId, deployment.Id,
                        Detail: $"{deployment.Action} '{deployment.PackageName}' {deployment.PackageVersion} (sha256 {deployment.PackageSha256}, revision {deployment.PackageRevision}) on {preview.DeployableCount} device(s); start {deployment.ScheduledStartUtc:O}; deadline {deployment.DeadlineUtc:O}; approvals required {deployment.RequiredApprovals}{(deployment.IsSensitive ? $" (sensitive: {string.Join(", ", deployment.SensitivityReasons)})" : string.Empty)}"));
                    await Task.CompletedTask;
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another deployment of this package claimed one of the devices in the meantime.
            throw new ConflictException("A device received another deployment of this package at the same time. Review and try again.", "duplicate_deployment", ex);
        }

        return await GetAsync(deployment.Id, cancellationToken);
    }

    public async Task<PagedResult<DeploymentDto>> ListAsync(DeploymentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var deployments = db.Set<SoftwareDeployment>().AsNoTracking();
        if (query.Status is { } status)
        {
            deployments = deployments.Where(d => d.Status == status);
        }

        if (query.PackageId is { } packageId)
        {
            deployments = deployments.Where(d => d.PackageId == packageId);
        }

        if (query.DeviceId is { } deviceId)
        {
            var ids = db.Set<DeploymentTarget>().Where(t => t.DeviceId == deviceId).Select(t => t.DeploymentId);
            deployments = deployments.Where(d => ids.Contains(d.Id));
        }

        if (query.AwaitingMyApproval)
        {
            var me = currentUser.RequireUserId();
            var decided = db.Set<DeploymentApproval>().Where(a => a.ApproverId == me).Select(a => a.DeploymentId);
            deployments = deployments.Where(d => d.Status == DeploymentStatus.PendingApproval && d.CreatedBy != me && !decided.Contains(d.Id));
        }

        var total = await deployments.LongCountAsync(cancellationToken);
        var rows = await deployments.OrderByDescending(d => d.RequestedAtUtc).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var dtos = await ToDtosAsync(rows, cancellationToken);
        return new PagedResult<DeploymentDto>(dtos, page, size, total);
    }

    public async Task<DeploymentDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await db.Set<SoftwareDeployment>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Deployment", id);
        var dto = (await ToDtosAsync([deployment], cancellationToken))[0];
        var approvals = await db.Set<DeploymentApproval>().AsNoTracking().Where(a => a.DeploymentId == id).OrderBy(a => a.DecidedAtUtc)
            .Select(a => new DeploymentApprovalDto(a.Id, a.ApproverId, a.Decision, a.Comment, a.DecidedAtUtc)).ToListAsync(cancellationToken);
        var list = await db.Set<DeploymentTarget>().AsNoTracking().Where(t => t.DeploymentId == id).OrderBy(t => t.DeviceName).ToListAsync(cancellationToken);
        return new DeploymentDetailDto(dto, deployment.Parameters, approvals, list.Select(DeploymentTargetDto.From).ToList());
    }

    public async Task<DeploymentDetailDto> ApproveAsync(Guid id, string? comment, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        await audit.AtomicAsync(
            async ct =>
            {
                var deployment = await LoadPendingAsync(id, userId, now, ct);
                if (await db.Set<DeploymentApproval>().AnyAsync(a => a.DeploymentId == id && a.ApproverId == userId, ct))
                {
                    throw new ConflictException("You have already approved this deployment; a sensitive deployment needs a different second approver.", "approval_already_recorded");
                }

                var approval = new DeploymentApproval(deployment.TenantId, id, deployment.CreatedBy, userId, ApprovalDecision.Approved, Clean(comment), now);
                db.Set<DeploymentApproval>().Add(approval);
                var received = await db.Set<DeploymentApproval>().CountAsync(a => a.DeploymentId == id && a.Decision == ApprovalDecision.Approved, ct) + 1;
                if (received >= deployment.RequiredApprovals)
                {
                    deployment.Status = DeploymentStatus.Scheduled;
                    deployment.ApprovedAtUtc = now;
                    deployment.FinalApprovalId = approval.Id;
                    deployment.FinalApprovedBy = userId;
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentApproved, SoftwareAuditOutcome.Success, deployment.PackageId, id,
                        Detail: $"approval {received.ToString(CultureInfo.InvariantCulture)} of {deployment.RequiredApprovals.ToString(CultureInfo.InvariantCulture)}; {approval.Comment}"));
                }
                else
                {
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentApprovalRecorded, SoftwareAuditOutcome.Success, deployment.PackageId, id,
                        Detail: $"approval {received.ToString(CultureInfo.InvariantCulture)} of {deployment.RequiredApprovals.ToString(CultureInfo.InvariantCulture)}; {approval.Comment}"));
                }

                return true;
            },
            cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDetailDto> RejectAsync(Guid id, string? comment, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        await audit.AtomicAsync(
            async ct =>
            {
                var deployment = await LoadPendingAsync(id, userId, now, ct);
                if (await db.Set<DeploymentApproval>().AnyAsync(a => a.DeploymentId == id && a.ApproverId == userId, ct))
                {
                    throw new ConflictException("You have already decided on this deployment.", "approval_already_recorded");
                }

                db.Set<DeploymentApproval>().Add(new DeploymentApproval(deployment.TenantId, id, deployment.CreatedBy, userId, ApprovalDecision.Rejected, Clean(comment), now));
                deployment.Status = DeploymentStatus.Rejected;
                deployment.CompletedAtUtc = now;
                await engine.CloseOpenTargetsAsync(id, TargetStatus.Cancelled, "deployment_rejected", "The deployment was rejected.", now, ct);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentRejected, SoftwareAuditOutcome.Success, deployment.PackageId, id, Detail: Clean(comment)));
                return true;
            },
            cancellationToken);
        await engine.FinalizeIfDoneAsync(id, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDetailDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        var inFlight = await audit.AtomicAsync(
            async ct =>
            {
                var deployment = await db.Set<SoftwareDeployment>().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Deployment", id);
                if (deployment.IsFinal())
                {
                    throw new ConflictException("The deployment has already finished.", "deployment_finished");
                }

                deployment.Status = DeploymentStatus.Cancelled;
                deployment.CancelledBy = userId;
                deployment.CancelledAtUtc = now;
                deployment.CompletedAtUtc = now;
                await engine.CloseOpenTargetsAsync(id, TargetStatus.Cancelled, "deployment_cancelled", "The deployment was cancelled before this device was reached.", now, ct, pendingOnly: true);
                var running = await db.Set<DeploymentTarget>()
                    .Where(t => t.DeploymentId == id && (t.Status == TargetStatus.Dispatched || t.Status == TargetStatus.Running) && t.CurrentExecutionId != null)
                    .Select(t => t.CurrentExecutionId!.Value)
                    .ToListAsync(ct);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.DeploymentCancelled, SoftwareAuditOutcome.Success, deployment.PackageId, id,
                    Detail: $"{running.Count.ToString(CultureInfo.InvariantCulture)} device(s) in progress asked to stop"));
                return running;
            },
            cancellationToken);

        // Commands not yet accepted by a device are cancelled at once; running ones get a signed cancel.
        foreach (var executionId in inFlight)
        {
            await dispatcher.CancelAsync(executionId, userId, cancellationToken);
        }

        await engine.ReconcileDeploymentAsync(id, cancellationToken);
        await engine.FinalizeIfDoneAsync(id, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDetailDto> RedeployFailedAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await db.Set<SoftwareDeployment>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw new NotFoundException("Deployment", id);
        if (!deployment.IsFinal())
        {
            throw new ConflictException("Wait until the deployment has finished.", "deployment_not_finished");
        }

        var failed = await db.Set<DeploymentTarget>().AsNoTracking()
            .Where(t => t.DeploymentId == id && (t.Status == TargetStatus.Failed || t.Status == TargetStatus.TimedOut || t.Status == TargetStatus.Expired))
            .Select(t => t.DeviceId)
            .ToListAsync(cancellationToken);
        if (failed.Count == 0)
        {
            throw new ConflictException("No device of this deployment failed.", "nothing_to_redeploy");
        }

        return await CreateAsync(
            new CreateDeploymentRequest
            {
                Name = (deployment.Name.Length > 180 ? deployment.Name[..180] : deployment.Name) + " (retry)",
                Reason = $"Retry of failed devices from deployment {deployment.Id:D}",
                PackageId = deployment.PackageId,
                Action = deployment.Action,
                DeviceIds = failed,
                MaxAttempts = deployment.MaxAttempts,
                RetryDelayMinutes = deployment.RetryDelayMinutes,
            },
            cancellationToken);
    }

    public async Task<DeploymentReport> ReportAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await db.Set<SoftwareDeployment>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw new NotFoundException("Deployment", id);
        if (deployment.ReportJson is { } stored)
        {
            return JsonSerializer.Deserialize<DeploymentReport>(stored, DeploymentEngine.ReportJson)!;
        }

        return await engine.BuildReportAsync(deployment, final: false, cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentTargetDto>> DeviceHistoryAsync(Guid deviceId, CancellationToken cancellationToken) =>
        (await db.Set<DeploymentTarget>().AsNoTracking().Where(t => t.DeviceId == deviceId).OrderByDescending(t => t.CreatedAtUtc).Take(200).ToListAsync(cancellationToken))
            .Select(DeploymentTargetDto.From).ToList();

    /// <summary>Application version detection on demand: a read-only signed command evaluating the package's detection rule.</summary>
    public async Task<DetectionRequestResult> RequestDetectionAsync(Guid packageId, Guid deviceId, CancellationToken cancellationToken)
    {
        var package = await db.Set<SoftwarePackage>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken) ?? throw new NotFoundException("Package", packageId);
        if (package.Detection is not { } detection)
        {
            throw new ConflictException("Configure the package's detection rule first.", "package_not_configured");
        }

        var device = await endpoints.FindForAccessAsync(deviceId, cancellationToken) ?? throw new NotFoundException("Device", deviceId);
        if (!device.Endpoint.IsActive)
        {
            throw new ConflictException("The device is retired.", "device_inactive");
        }

        var executionId = Guid.CreateVersion7();
        var operation = new SoftwareOperation
        {
            DeploymentId = executionId,
            TargetId = executionId,
            PackageId = package.Id,
            PackageName = package.Name,
            PackageVersion = package.Version,
            InstallerType = package.InstallerType,
            Detection = detection,
            TimeoutSeconds = SoftwareOperation.MinTimeoutSeconds,
        };
        var userId = currentUser.RequireUserId();
        var state = await dispatcher.DispatchAsync(
            new DelegatedCommandRequest(executionId, deviceId, CommandTypes.SoftwareDetect, operation.ToParameters(), userId, null, null,
                $"Version detection for '{package.Name}'", clock.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(5)),
            cancellationToken);
        await audit.WriteAsync(new SoftwareAuditEntry(SoftwareAuditEvents.DetectionRequested, state.Phase == RemoteCommandPhase.Rejected ? SoftwareAuditOutcome.Denied : SoftwareAuditOutcome.Success,
            package.Id, DeviceId: deviceId, Detail: $"execution {executionId:D}{(state.ErrorCode is null ? string.Empty : $"; {state.ErrorCode}")}"), cancellationToken);
        if (state.Phase == RemoteCommandPhase.Rejected)
        {
            throw new ForbiddenException("This device is outside the devices you are allowed to manage.", state.ErrorCode ?? "permission_denied");
        }

        return new DetectionRequestResult(executionId, deviceId, package.Id);
    }

    public async Task<DetectionResultDto> GetDetectionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var state = (await dispatcher.GetStatesAsync([executionId], cancellationToken)).FirstOrDefault(s => s.CommandType == CommandTypes.SoftwareDetect)
            ?? throw new NotFoundException("Detection", executionId);
        var result = DeploymentEngine.ParseResult(state.Output);
        return new DetectionResultDto(
            executionId, state.DeviceId, state.Phase.ToString(),
            result is null ? null : result.Outcome == SoftwareOutcome.Detected,
            result?.DetectionSatisfied, result?.DetectedVersion, state.ErrorCode, state.ErrorMessage);
    }

    private async Task<SoftwareDeployment> LoadPendingAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deployment = await db.Set<SoftwareDeployment>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw new NotFoundException("Deployment", id);
        if (deployment.Status != DeploymentStatus.PendingApproval)
        {
            throw new ConflictException("This deployment is not waiting for approval.", "approval_decided");
        }

        if (deployment.ApprovalExpiresAtUtc <= now)
        {
            throw new ConflictException("The approval period of this deployment has ended.", "approval_expired");
        }

        if (deployment.CreatedBy == userId)
        {
            throw new ForbiddenException("You cannot approve or reject your own deployment.", "approval_self_decision");
        }

        return deployment;
    }

    private sealed record Plan(SoftwarePackage Package, SoftwareDeploymentPolicy Policy, Dictionary<string, string> Template, DeploymentPreviewDto Preview);

    /// <summary>Validates a request and works out exactly what would happen (shared by review and creation).</summary>
    private async Task<Plan> PlanAsync(CreateDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SoftwarePolicyService.Text(request.Name, 1, 200, nameof(request.Name));
        if (!Enum.IsDefined(request.Action))
        {
            throw new ValidationException(nameof(request.Action), "Unknown action.");
        }

        if (request.MaxAttempts is < 1 or > MaxAttemptsLimit)
        {
            throw new ValidationException(nameof(request.MaxAttempts), $"Between 1 and {MaxAttemptsLimit} attempts.");
        }

        if (request.RetryDelayMinutes is < 1 or > 24 * 60)
        {
            throw new ValidationException(nameof(request.RetryDelayMinutes), "Between 1 minute and 24 hours.");
        }

        var now = clock.UtcNow;
        var start = request.ScheduledStartUtc is { } s && s > now ? s : now;
        if (start > now.AddDays(90))
        {
            throw new ValidationException(nameof(request.ScheduledStartUtc), "A deployment can be scheduled at most 90 days ahead.");
        }

        var deadline = request.DeadlineUtc ?? start.Add(DefaultWindow);
        if (deadline <= start.AddMinutes(5) || deadline > start.AddDays(30))
        {
            throw new ValidationException(nameof(request.DeadlineUtc), "The deadline must be between 5 minutes and 30 days after the start.");
        }

        var package = await db.Set<SoftwarePackage>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PackageId, cancellationToken)
            ?? throw new NotFoundException("Package", request.PackageId);
        if (package.Status != PackageStatus.Ready)
        {
            throw new ConflictException(
                package.Status == PackageStatus.Corrupted ? "The stored package failed its integrity check and cannot be deployed." : "The package has been retired.",
                package.Status == PackageStatus.Corrupted ? PackageValidationCodes.Corrupted : "package_retired");
        }

        if (!package.IsConfigured)
        {
            throw new ConflictException("Configure the package's installation parameters and detection rule first.", "package_not_configured");
        }

        if (request.Action == DeploymentAction.Uninstall && !package.CanUninstall)
        {
            throw new ConflictException(
                package.InstallerType == InstallerType.Msi ? "MSI packages are removed by product code: use an MSI product code detection rule." : "Configure the package's uninstall switches first.",
                "package_cannot_uninstall");
        }

        var policy = await policies.GetAsync(cancellationToken);
        if (package.InstallerType == InstallerType.Exe && !policy.AllowExeInstallers)
        {
            throw new ConflictException("Your organisation no longer allows EXE installers.", "installer_type_not_allowed");
        }

        var rules = await db.Set<SoftwareRestrictionRule>().AsNoTracking().Where(r => r.Enabled && r.RuleType == RestrictionRuleType.Blocked).ToListAsync(cancellationToken);
        if (request.Action == DeploymentAction.Install && PackageService.BlockedBy(package, rules) is { } blocking)
        {
            throw new ConflictException($"The package is blocked by the software restriction rule '{blocking.Name}'.", "package_blocked");
        }

        // Targets.
        var deviceIds = new HashSet<Guid>(request.DeviceIds ?? []);
        var groupIds = new HashSet<Guid>(request.DeviceGroupIds ?? []);
        var branchIds = new HashSet<Guid>(request.BranchIds ?? []);
        var excluded = new HashSet<Guid>();
        if (request.DeploymentGroupId is { } groupId)
        {
            var group = await db.Set<DeploymentGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken) ?? throw new NotFoundException("Deployment group", groupId);
            deviceIds.UnionWith(group.DeviceIds);
            groupIds.UnionWith(group.DeviceGroupIds);
            branchIds.UnionWith(group.BranchIds);
            excluded.UnionWith(group.ExcludedDeviceIds);
        }

        if (deviceIds.Count + groupIds.Count + branchIds.Count == 0)
        {
            throw new ValidationException("targets", "Select a deployment group, devices, device groups or branches.");
        }

        var resolved = await targets.ResolveAsync(deviceIds, groupIds, branchIds, excluded, cancellationToken);
        if (deviceIds.FirstOrDefault(d => !excluded.Contains(d) && resolved.All(r => r.Endpoint.Id != d)) is var missing && missing != Guid.Empty)
        {
            throw new ValidationException("deviceIds", $"Device '{missing}' was not found.");
        }

        if (resolved.Count > SoftwareDeployment.MaxTargets)
        {
            throw new ValidationException("targets", $"A deployment can target at most {SoftwareDeployment.MaxTargets} devices.");
        }

        var ids = resolved.Select(r => r.Endpoint.Id).ToList();
        var conflicts = await db.Set<DeploymentTarget>().AsNoTracking()
            .Where(t => t.PackageId == package.Id && ids.Contains(t.DeviceId)
                && (t.Status == TargetStatus.Pending || t.Status == TargetStatus.Dispatched || t.Status == TargetStatus.Running))
            .Select(t => new { t.DeviceId, t.DeploymentId })
            .ToListAsync(cancellationToken);
        var conflictByDevice = conflicts.GroupBy(c => c.DeviceId).ToDictionary(g => g.Key, g => g.First().DeploymentId);
        var planned = resolved.Select(r =>
        {
            string? skip = null;
            if (!r.Endpoint.IsActive)
            {
                skip = "device_retired";
            }
            else if (conflictByDevice.ContainsKey(r.Endpoint.Id))
            {
                skip = "duplicate_deployment";
            }
            else if (request.Action == DeploymentAction.Install && PackageService.BlockedBy(package, rules, r.GroupIds) is not null)
            {
                skip = "blocked_by_policy";
            }

            return new PlannedTargetDto(r.Endpoint.Id, r.Endpoint.Hostname, r.Endpoint.OsName, r.Endpoint.Connectivity, skip is null, skip,
                conflictByDevice.TryGetValue(r.Endpoint.Id, out var other) ? other : null);
        }).ToList();
        var deployable = planned.Count(p => p.WillRun);

        // Sensitivity: everything that needs the additional (second) approval.
        var reasons = new List<string>();
        if (package.SignatureState != SignatureState.Signed)
        {
            reasons.Add("unsigned_package");
        }
        else if (!policy.IsTrustedPublisher(package.SignerThumbprint))
        {
            reasons.Add("untrusted_publisher");
        }

        if (package.IsSensitive)
        {
            reasons.Add("package_marked_sensitive");
        }

        if (request.Action == DeploymentAction.Uninstall && policy.UninstallIsSensitive)
        {
            reasons.Add("uninstall");
        }

        if (deployable > policy.LargeDeploymentThreshold)
        {
            reasons.Add("large_deployment");
        }

        if (policy.ServerTargetsAreSensitive && resolved.Any(r => r.Endpoint.IsActive && r.Endpoint.OsName?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true))
        {
            reasons.Add("server_targets");
        }

        var sensitive = reasons.Count > 0;
        var required = sensitive ? 2 : policy.RequireApproval ? 1 : 0;

        var commandType = request.Action == DeploymentAction.Install ? CommandTypes.SoftwareInstall : CommandTypes.SoftwareUninstall;
        var needsPackage = request.Action == DeploymentAction.Install || package.InstallerType == InstallerType.Exe;
        var operation = new SoftwareOperation
        {
            DeploymentId = Guid.CreateVersion7(),
            TargetId = Guid.CreateVersion7(),
            PackageId = package.Id,
            PackageName = package.Name,
            PackageVersion = package.Version,
            InstallerType = package.InstallerType,
            RequiresPackage = needsPackage,
            Sha256 = needsPackage ? package.Sha256 : null,
            SizeBytes = needsPackage ? package.SizeBytes : 0,
            Arguments = request.Action == DeploymentAction.Install ? package.InstallArguments : package.UninstallArguments,
            SuccessExitCodes = package.SuccessExitCodes,
            RebootExitCodes = package.RebootExitCodes,
            Detection = package.Detection,
            ExpectedSignerThumbprint = package.SignatureState == SignatureState.Signed ? package.SignerThumbprint : null,
            RequireSignature = needsPackage && package.SignatureState == SignatureState.Signed,
            TimeoutSeconds = package.TimeoutMinutes * 60,
        };
        if (operation.Validate(commandType) is { } problem)
        {
            throw new ConflictException($"The package configuration cannot be deployed: {problem}", "package_not_configured");
        }

        // The template every target receives; ids are filled in per target at dispatch.
        var template = operation.ToParameters().Where(p => p.Key is not ("deploymentId" or "targetId")).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var preview = new DeploymentPreviewDto(
            package.Id, package.Name, package.Version, package.Sha256, package.InstallerType, request.Action, commandType, operation.Arguments, operation.Detection,
            operation.RequireSignature, package.TimeoutMinutes, start, deadline, request.MaxAttempts, planned, deployable,
            planned.Count(p => p.ConflictingDeploymentId is not null), sensitive, reasons, required);
        return new Plan(package, policy, template, preview);
    }

    private async Task<List<DeploymentDto>> ToDtosAsync(IReadOnlyList<SoftwareDeployment> deployments, CancellationToken cancellationToken)
    {
        var ids = deployments.Select(d => d.Id).ToList();
        var counts = await db.Set<DeploymentTarget>().AsNoTracking().Where(t => ids.Contains(t.DeploymentId))
            .GroupBy(t => new { t.DeploymentId, t.Status })
            .Select(g => new { g.Key.DeploymentId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var approvals = await db.Set<DeploymentApproval>().AsNoTracking().Where(a => ids.Contains(a.DeploymentId) && a.Decision == ApprovalDecision.Approved)
            .GroupBy(a => a.DeploymentId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var byDeployment = counts.ToLookup(c => c.DeploymentId, c => (c.Status, c.Count));
        var approvalCounts = approvals.ToDictionary(a => a.Key, a => a.Count);
        return deployments.Select(d => new DeploymentDto(
            d.Id, d.Name, d.Reason, d.Action, d.PackageId, d.PackageName, d.PackageVersion, d.Status, d.CreatedBy, d.RequestedAtUtc, d.ScheduledStartUtc,
            d.DeadlineUtc, d.IsSensitive, d.SensitivityReasons, d.RequiredApprovals, approvalCounts.GetValueOrDefault(d.Id), d.ApprovalExpiresAtUtc,
            d.ApprovedAtUtc, d.StartedAtUtc, d.CompletedAtUtc, d.CancelledBy, d.MaxAttempts, DeploymentEngine.Count(byDeployment[d.Id]))).ToList();
    }

    internal static string? SkipMessage(string? code) => code switch
    {
        "device_retired" => "The device is retired.",
        "duplicate_deployment" => "Another unfinished deployment of this package already targets this device.",
        "blocked_by_policy" => "A software restriction rule blocks this package on this device.",
        _ => null,
    };

    private static string? Clean(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];

    /// <summary>CSV of a report's device rows (UTF-8, RFC 4180 quoting, formula-injection safe).</summary>
    internal static string ToCsv(DeploymentReport report)
    {
        var csv = new StringBuilder();
        csv.AppendLine("device,status,outcome,attempts,exit_code,error_code,error_message,detected_version,reboot_required,started_at_utc,completed_at_utc,duration_seconds");
        foreach (var row in report.Devices)
        {
            csv.AppendLine(string.Join(',',
                Cell(row.DeviceName), Cell(row.Status.ToString()), Cell(row.Outcome?.ToString()), row.Attempts.ToString(CultureInfo.InvariantCulture),
                row.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, Cell(row.ErrorCode), Cell(row.ErrorMessage), Cell(row.DetectedVersion),
                row.RebootRequired ? "true" : "false", row.StartedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.DurationSeconds?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return csv.ToString();
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Spreadsheet formula injection: neutralise leading =, +, -, @.
        var safe = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
