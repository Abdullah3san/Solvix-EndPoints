namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Deployments;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.UnitTests.Software;

/// <summary>
/// The deployment workflow end to end against the real API, PostgreSQL and Redis, with the agent
/// simulated over its device-certificate channel: approval, scheduling, signed delivery, package
/// download, results, retries, duplicates, interruptions, cancellation and reports.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SoftwareDeploymentTests(ApiFixture fixture)
{
    [Fact]
    public async Task A_signed_package_from_a_trusted_publisher_is_approved_once_deployed_installed_and_reported()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        using var signer = TestPackages.Certificate("CN=Fabrikam Software");
        await s.Owner.SetPolicyAsync(SoftwareTestKit.DefaultPolicy(signer.GetCertHashString(HashAlgorithmName.SHA256)));

        // Upload → validate → checksum.
        var bytes = SoftwareTestKit.SignedExe(signer);
        var uploaded = await s.Owner.UploadAsync("FabrikamViewerSetup.exe", bytes);
        Assert.Equal(PackageStatus.Ready, uploaded.Status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), uploaded.Sha256);
        Assert.Equal(SignatureState.Signed, uploaded.SignatureState);
        Assert.True(uploaded.SignerTrusted);
        Assert.True(uploaded.SignatureContentVerified);
        Assert.False(uploaded.IsConfigured);

        // Configure installation parameters (silent switches from the preset).
        var package = await s.Owner.ConfigureAsync(uploaded.Id, SoftwareTestKit.ExeConfiguration());
        Assert.Equal(["/quiet", "/norestart"], package.InstallArguments);
        Assert.Equal(["/uninstall", "/quiet", "/norestart"], package.UninstallArguments);

        // Select devices → review.
        var request = SoftwareTestKit.Deployment(package.Id, s.DeviceId);
        var preview = await HttpAssert.OkAsync<DeploymentPreviewDto>(await s.Owner.PostAsJsonAsync("/api/v1/software/deployments/preview", request, ApiFixture.Json));
        Assert.False(preview.IsSensitive);
        Assert.Equal(1, preview.RequiredApprovals);
        Assert.Equal(1, preview.DeployableCount);
        Assert.Equal(CommandTypes.SoftwareInstall, preview.CommandType);
        Assert.True(preview.RequireSignature);

        // Request → nothing is sent before approval.
        var created = await s.Owner.CreateDeploymentAsync(request);
        Assert.Equal(DeploymentStatus.PendingApproval, created.Deployment.Status);
        await fixture.RunDeploymentsAsync();
        Assert.Empty(await s.Agent.FetchAsync());

        // Approve (four eyes) → execute.
        await HttpAssert.ProblemAsync(await s.Owner.DecideAsync(created.Deployment.Id, true), HttpStatusCode.Forbidden, "approval_self_decision");
        var approved = await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(created.Deployment.Id, true, "CAB-1042"));
        Assert.Equal(DeploymentStatus.Scheduled, approved.Deployment.Status);
        await fixture.RunDeploymentsAsync();

        var (envelope, operation) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(CommandTypes.SoftwareInstall, envelope.CommandType);
        Assert.Equal(CommandApprovalStatus.Approved, envelope.ApprovalStatus);
        Assert.Equal(s.Approver.UserId, envelope.ApprovedBy);
        Assert.Equal(s.Tenant.OwnerUserId, envelope.RequestedBy);
        Assert.Equal(package.Sha256, operation.Sha256);
        Assert.Equal(bytes.Length, operation.SizeBytes);
        Assert.Equal(["/quiet", "/norestart"], operation.Arguments);
        Assert.Equal(package.SignerThumbprint, operation.ExpectedSignerThumbprint);
        Assert.True(operation.RequireSignature);
        Assert.Equal(created.Deployment.Id, operation.DeploymentId);

        // Download only after accepting the command; the bytes match; ranges resume.
        await HttpAssert.ProblemAsync(await s.Agent.DownloadAsync(envelope.ExecutionId), HttpStatusCode.Conflict, "command_not_running");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(envelope.ExecutionId)).StatusCode);
        var download = await s.Agent.DownloadAsync(envelope.ExecutionId);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(package.Sha256, download.Headers.GetValues("X-Content-SHA256").Single());
        var resumed = await s.Agent.DownloadAsync(envelope.ExecutionId, from: 1000);
        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        Assert.Equal(bytes[1000..], await resumed.Content.ReadAsByteArrayAsync());

        // Collect the execution result → the target is updated at once; the engine completes the deployment.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.SoftwareResultAsync(envelope.ExecutionId, CommandOutcome.Succeeded,
            new SoftwareOperationResult { Outcome = SoftwareOutcome.Installed, InstallerExitCode = 0, DetectionSatisfied = true, DetectedVersion = "9.1.2", RebootRequired = true, PackageVerified = true, Signature = "Valid" })).StatusCode);
        var target = Assert.Single((await s.Owner.GetDeploymentAsync(created.Deployment.Id)).Targets);
        Assert.Equal(TargetStatus.Succeeded, target.Status);
        Assert.Equal(SoftwareOutcome.Installed, target.Outcome);
        Assert.Equal("9.1.2", target.DetectedVersion);
        Assert.True(target.RebootRequired);

        var done = await s.Owner.GetDeploymentAsync(created.Deployment.Id);
        Assert.Equal(DeploymentStatus.Completed, done.Deployment.Status);

        // Report (stored when the deployment finished) and CSV.
        var report = await HttpAssert.OkAsync<DeploymentReport>(await s.Owner.GetAsync($"/api/v1/software/deployments/{created.Deployment.Id}/report"));
        Assert.True(report.Final);
        Assert.Equal(100, report.SuccessRate);
        Assert.Equal(1, report.Counts.Succeeded);
        Assert.Equal(1, report.RebootRequired);
        Assert.Equal(s.Approver.UserId, Assert.Single(report.Approvals).ApproverId);
        var csv = await s.Owner.GetAsync($"/api/v1/software/deployments/{created.Deployment.Id}/report.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"Succeeded\",\"Installed\"", await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // History per device and the audit trail.
        var history = await HttpAssert.OkAsync<List<DeploymentTargetDto>>(await s.Owner.GetAsync($"/api/v1/software/devices/{s.DeviceId}/deployments"));
        Assert.Contains(history, h => h.Id == target.Id);
        var audit = await HttpAssert.OkAsync<PagedResult<SoftwareAuditEventDto>>(await s.Owner.GetAsync($"/api/v1/software/audit?deploymentId={created.Deployment.Id}&pageSize=100"));
        Assert.Equal(
            [SoftwareAuditEvents.DeploymentRequested, SoftwareAuditEvents.DeploymentApproved, SoftwareAuditEvents.DeploymentStarted, SoftwareAuditEvents.TargetDispatched, SoftwareAuditEvents.TargetFinished, SoftwareAuditEvents.DeploymentCompleted],
            audit.Items.OrderBy(e => e.Sequence).Select(e => e.EventType));
        Assert.True((await HttpAssert.OkAsync<SoftwareAuditVerification>(await s.Owner.GetAsync("/api/v1/software/audit/verify"))).Valid);
    }

    [Fact]
    public async Task Unsigned_packages_need_two_approvers_other_than_the_requester()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        Assert.Equal(SignatureState.Unsigned, package.SignatureState);

        var created = await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));
        Assert.True(created.Deployment.IsSensitive);
        Assert.Contains("unsigned_package", created.Deployment.SensitivityReasons);
        Assert.Equal(2, created.Deployment.RequiredApprovals);

        var first = await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(created.Deployment.Id, true));
        Assert.Equal(DeploymentStatus.PendingApproval, first.Deployment.Status);
        Assert.Equal(1, first.Deployment.ApprovalsReceived);
        await HttpAssert.ProblemAsync(await s.ApproverClient.DecideAsync(created.Deployment.Id, true), HttpStatusCode.Conflict, "approval_already_recorded");
        await fixture.RunDeploymentsAsync();
        Assert.Empty(await s.Agent.FetchAsync());

        // Awaiting my approval: the second approver sees it, the first and the requester do not.
        var mine = await HttpAssert.OkAsync<PagedResult<DeploymentDto>>(await s.SecondApproverClient.GetAsync("/api/v1/software/deployments?awaitingMyApproval=true"));
        Assert.Contains(mine.Items, d => d.Id == created.Deployment.Id);
        Assert.DoesNotContain((await HttpAssert.OkAsync<PagedResult<DeploymentDto>>(await s.ApproverClient.GetAsync("/api/v1/software/deployments?awaitingMyApproval=true"))).Items, d => d.Id == created.Deployment.Id);

        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.SecondApproverClient.DecideAsync(created.Deployment.Id, true));
        await fixture.RunDeploymentsAsync();
        var (envelope, _) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(s.SecondApprover.UserId, envelope.ApprovedBy);

        // The database refuses a self-approval even if the application check were bypassed.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsOwnerAsync(
            "INSERT INTO software_deployment_approvals (id, tenant_id, deployment_id, requested_by, approver_id, decision, decided_at_utc, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, @d, @u, @u, 'Approved', now(), now(), now())",
            ("t", s.Tenant.TenantId), ("d", created.Deployment.Id), ("u", s.Tenant.OwnerUserId)));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task A_rejected_deployment_never_reaches_the_device_and_frees_its_targets()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var created = await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));

        var rejected = await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(created.Deployment.Id, false, "Not in this window"));
        Assert.Equal(DeploymentStatus.Rejected, rejected.Deployment.Status);
        Assert.Equal(TargetStatus.Cancelled, Assert.Single(rejected.Targets).Status);
        await fixture.RunDeploymentsAsync();
        Assert.Empty(await s.Agent.FetchAsync());

        // The device is free for a new deployment of the same package.
        await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));
    }

    [Fact]
    public async Task A_failed_installation_is_reported_with_its_exit_code_and_a_transient_failure_is_retried()
    {
        using var s = await fixture.SetUpSoftwareAsync(devices: 2);
        var package = await s.Owner.ReadyMsiAsync();
        var deployment = await ApprovedAsync(s, SoftwareTestKit.Deployment(package.Id, s.DeviceId, s.Devices[1].DeviceId) with { RetryDelayMinutes = 5 });
        await fixture.RunDeploymentsAsync();

        // Device 1: a fatal installer error (not retried).
        var (first, _) = await s.Agents[0].NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agents[0].AckAsync(first.ExecutionId)).StatusCode);
        await s.Agents[0].SoftwareResultAsync(first.ExecutionId, CommandOutcome.Failed,
            new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, InstallerExitCode = 1603, FailedPhase = "install", LogExcerpt = "MSI (s) (A4:B8) Product: Contoso Notes -- Installation failed." },
            SoftwareErrorCodes.InstallerFailed, 1603);

        // Device 2: another installation was running (1618) → retried.
        var (second, _) = await s.Agents[1].NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agents[1].AckAsync(second.ExecutionId)).StatusCode);
        await s.Agents[1].SoftwareResultAsync(second.ExecutionId, CommandOutcome.Failed,
            new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, InstallerExitCode = 1618, FailedPhase = "install" }, SoftwareErrorCodes.AnotherInstallInProgress, 1618);

        var detail = await s.Owner.GetDeploymentAsync(deployment);
        var failed = detail.Targets.Single(t => t.DeviceId == s.DeviceId);
        Assert.Equal(TargetStatus.Failed, failed.Status);
        Assert.Equal(1603, failed.ExitCode);
        Assert.Equal(SoftwareErrorCodes.InstallerFailed, failed.ErrorCode);
        Assert.Contains("Installation failed", failed.LogExcerpt, StringComparison.Ordinal);
        var retrying = detail.Targets.Single(t => t.DeviceId == s.Devices[1].DeviceId);
        Assert.Equal(TargetStatus.Pending, retrying.Status);
        Assert.NotNull(retrying.NextAttemptAtUtc);
        Assert.Equal(DeploymentStatus.Running, detail.Deployment.Status);

        // Not before the retry delay; then a new attempt with a new command.
        await fixture.RunDeploymentsAsync();
        Assert.Empty(await s.Agents[1].FetchAsync());
        var retryAt = retrying.NextAttemptAtUtc!.Value.AddSeconds(1);
        var (retry, _) = await AtAsync(retryAt, async () =>
        {
            await fixture.RunDeploymentsAsync(retryAt);
            return await s.Agents[1].NextSoftwareCommandAsync();
        });
        Assert.NotEqual(second.ExecutionId, retry.ExecutionId);
        await s.Agents[1].InstallSucceedsAsync(retry.ExecutionId);

        var final = await s.Owner.GetDeploymentAsync(deployment);
        Assert.Equal(DeploymentStatus.CompletedWithErrors, final.Deployment.Status);
        Assert.Equal(2, final.Targets.Single(t => t.DeviceId == s.Devices[1].DeviceId).Attempts);
        var report = await HttpAssert.OkAsync<DeploymentReport>(await s.Owner.GetAsync($"/api/v1/software/deployments/{deployment}/report"));
        Assert.Equal(50, report.SuccessRate);
        Assert.Equal(SoftwareErrorCodes.InstallerFailed, Assert.Single(report.Failures).ErrorCode);

        // The failed device can be redeployed as a new (again approved) deployment.
        var redeploy = await HttpAssert.OkAsync<DeploymentDetailDto>(await s.Owner.PostAsync($"/api/v1/software/deployments/{deployment}/redeploy-failed", null), HttpStatusCode.Created);
        Assert.Equal(s.DeviceId, Assert.Single(redeploy.Targets).DeviceId);
        Assert.Equal(DeploymentStatus.PendingApproval, redeploy.Deployment.Status);
    }

    [Fact]
    public async Task Duplicate_deployments_are_refused_or_skipped_and_an_existing_installation_counts_as_success()
    {
        using var s = await fixture.SetUpSoftwareAsync(devices: 2);
        var package = await s.Owner.ReadyMsiAsync();
        await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));

        // Same package, same device, while the first is unfinished.
        await HttpAssert.ProblemAsync(await s.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId)), HttpStatusCode.Conflict, "duplicate_deployment");
        var preview = await HttpAssert.OkAsync<DeploymentPreviewDto>(await s.Owner.PostAsJsonAsync("/api/v1/software/deployments/preview", SoftwareTestKit.Deployment(package.Id, s.DeviceId, s.Devices[1].DeviceId), ApiFixture.Json));
        Assert.Equal(1, preview.ConflictCount);
        Assert.Equal(1, preview.DeployableCount);

        var second = await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId, s.Devices[1].DeviceId) with { SkipConflicts = true });
        var skipped = second.Targets.Single(t => t.DeviceId == s.DeviceId);
        Assert.Equal(TargetStatus.Skipped, skipped.Status);
        Assert.Equal("duplicate_deployment", skipped.ErrorCode);

        // The database enforces it too: a second active target for the same package and device is impossible.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsOwnerAsync(
            "UPDATE software_deployment_targets SET status = 'Pending' WHERE id = @id", ("id", skipped.Id)));
        Assert.Equal("23505", ex.SqlState);

        // The device reports the application as already present: success without installing.
        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(second.Deployment.Id, true));
        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.SecondApproverClient.DecideAsync(second.Deployment.Id, true));
        await fixture.RunDeploymentsAsync();
        var (envelope, _) = await s.Agents[1].NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agents[1].AckAsync(envelope.ExecutionId)).StatusCode);
        await s.Agents[1].SoftwareResultAsync(envelope.ExecutionId, CommandOutcome.Succeeded,
            new SoftwareOperationResult { Outcome = SoftwareOutcome.AlreadyInstalled, DetectionSatisfied = true, DetectedVersion = "5.3.0" });
        var detail = await s.Owner.GetDeploymentAsync(second.Deployment.Id);
        var target = detail.Targets.Single(t => t.DeviceId == s.Devices[1].DeviceId);
        Assert.Equal(TargetStatus.Succeeded, target.Status);
        Assert.Equal(SoftwareOutcome.AlreadyInstalled, target.Outcome);
        Assert.Equal(DeploymentStatus.Completed, detail.Deployment.Status);
    }

    [Fact]
    public async Task A_device_that_goes_silent_mid_installation_is_retried_and_then_marked_timed_out()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var deployment = await ApprovedAsync(s, SoftwareTestKit.Deployment(package.Id, s.DeviceId) with { MaxAttempts = 2, RetryDelayMinutes = 1 });
        await fixture.RunDeploymentsAsync();

        var (first, operation) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(first.ExecutionId)).StatusCode);
        await fixture.RunDeploymentsAsync();
        Assert.Equal(TargetStatus.Running, Assert.Single((await s.Owner.GetDeploymentAsync(deployment)).Targets).Status);

        // The device restarts / loses power: no result before the execution deadline (installer timeout + download allowance).
        var afterDeadline = DateTimeOffset.UtcNow.AddSeconds(operation.TimeoutSeconds).AddMinutes(31);
        await fixture.SweepRemoteAsync(afterDeadline);
        await fixture.RunDeploymentsAsync(afterDeadline);
        var retrying = Assert.Single((await s.Owner.GetDeploymentAsync(deployment)).Targets);
        Assert.Equal(TargetStatus.Pending, retrying.Status);
        Assert.Equal("no_result", retrying.ErrorCode);

        // A late result for the interrupted attempt is refused (the command timed out).
        await HttpAssert.ProblemAsync(await s.Agent.SoftwareResultAsync(first.ExecutionId, CommandOutcome.Succeeded, new SoftwareOperationResult { Outcome = SoftwareOutcome.Installed }), HttpStatusCode.Conflict, "command_result_recorded");

        // Second and last attempt also goes silent.
        var retryAt = afterDeadline.AddMinutes(2);
        var (second, _) = await AtAsync(retryAt, async () =>
        {
            await fixture.RunDeploymentsAsync(retryAt);
            return await s.Agent.NextSoftwareCommandAsync();
        });
        await AtAsync(retryAt, async () => Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(second.ExecutionId)).StatusCode));
        var secondDeadline = retryAt.AddSeconds(operation.TimeoutSeconds).AddMinutes(32);
        await fixture.SweepRemoteAsync(secondDeadline);
        await fixture.RunDeploymentsAsync(secondDeadline);

        var final = await s.Owner.GetDeploymentAsync(deployment);
        var target = Assert.Single(final.Targets);
        Assert.Equal(TargetStatus.TimedOut, target.Status);
        Assert.Equal(2, target.Attempts);
        Assert.Equal(DeploymentStatus.Failed, final.Deployment.Status);
    }

    [Fact]
    public async Task Devices_that_stay_offline_expire_when_the_deployment_window_closes()
    {
        using var s = await fixture.SetUpSoftwareAsync(devices: 2);
        var package = await s.Owner.ReadyMsiAsync();
        var now = DateTimeOffset.UtcNow;
        var deployment = await ApprovedAsync(s, SoftwareTestKit.Deployment(package.Id, s.DeviceId, s.Devices[1].DeviceId) with { DeadlineUtc = now.AddHours(2) });
        await fixture.RunDeploymentsAsync();

        // Device 1 installs; device 2 never comes online.
        var (envelope, _) = await s.Agents[0].NextSoftwareCommandAsync();
        await s.Agents[0].InstallSucceedsAsync(envelope.ExecutionId);

        var closed = now.AddHours(2).AddMinutes(1);
        await fixture.SweepRemoteAsync(closed); // the waiting command expires with the window
        await fixture.RunDeploymentsAsync(closed);
        var final = await s.Owner.GetDeploymentAsync(deployment);
        Assert.Equal(TargetStatus.Expired, final.Targets.Single(t => t.DeviceId == s.Devices[1].DeviceId).Status);
        Assert.Equal(DeploymentStatus.CompletedWithErrors, final.Deployment.Status);
        Assert.Empty(await s.Agents[1].FetchAsync());
    }

    [Fact]
    public async Task Cancelling_a_running_deployment_stops_queued_and_running_commands()
    {
        using var s = await fixture.SetUpSoftwareAsync(devices: 3);
        var package = await s.Owner.ReadyMsiAsync();
        var deployment = await ApprovedAsync(s, SoftwareTestKit.Deployment(package.Id, s.Devices.Select(d => d.DeviceId).ToArray()));
        await fixture.RunDeploymentsAsync();

        // Device 1 is installing, device 2 has fetched but not accepted, device 3 has not fetched.
        var (running, _) = await s.Agents[0].NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agents[0].AckAsync(running.ExecutionId)).StatusCode);
        var (delivered, _) = await s.Agents[1].NextSoftwareCommandAsync();

        var cancelled = await HttpAssert.OkAsync<DeploymentDetailDto>(await s.Owner.PostAsync($"/api/v1/software/deployments/{deployment}/cancel", null));
        Assert.Equal(DeploymentStatus.Cancelled, cancelled.Deployment.Status);

        // Not yet accepted: never executed. Not fetched: never delivered.
        await HttpAssert.ProblemAsync(await s.Agents[1].AckAsync(delivered.ExecutionId), HttpStatusCode.Conflict, "command_not_executable");
        Assert.Empty(await s.Agents[2].FetchAsync());

        // Running: the device receives a signed cancel and reports the installation as cancelled.
        var cancel = await s.Agents[0].VerifyAsync(Assert.Single(await s.Agents[0].FetchAsync()));
        Assert.Equal(CommandTypes.Cancel, cancel.CommandType);
        Assert.Equal(running.ExecutionId.ToString("D"), cancel.Parameters["executionId"]);
        await s.Agents[0].SoftwareResultAsync(running.ExecutionId, CommandOutcome.Cancelled, new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "install" }, SoftwareErrorCodes.Cancelled);

        var final = await s.Owner.GetDeploymentAsync(deployment);
        Assert.All(final.Targets, t => Assert.Equal(TargetStatus.Cancelled, t.Status));
        var report = await HttpAssert.OkAsync<DeploymentReport>(await s.Owner.GetAsync($"/api/v1/software/deployments/{deployment}/report"));
        Assert.True(report.Final);
        Assert.Equal(3, report.Counts.Cancelled);
        await HttpAssert.ProblemAsync(await s.Owner.PostAsync($"/api/v1/software/deployments/{deployment}/cancel", null), HttpStatusCode.Conflict, "deployment_finished");
    }

    [Fact]
    public async Task A_scheduled_deployment_waits_for_its_start_time()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var start = DateTimeOffset.UtcNow.AddHours(3);
        var deployment = await ApprovedAsync(s, SoftwareTestKit.Deployment(package.Id, s.DeviceId) with { ScheduledStartUtc = start });
        Assert.Equal(DeploymentStatus.Scheduled, (await s.Owner.GetDeploymentAsync(deployment)).Deployment.Status);

        await fixture.RunDeploymentsAsync();
        Assert.Empty(await s.Agent.FetchAsync());

        await fixture.RunDeploymentsAsync(start.AddMinutes(1));
        var detail = await s.Owner.GetDeploymentAsync(deployment);
        Assert.Equal(DeploymentStatus.Running, detail.Deployment.Status);
        Assert.Equal(TargetStatus.Dispatched, Assert.Single(detail.Targets).Status);
    }

    [Fact]
    public async Task An_attempt_claimed_but_never_sent_is_sent_once_with_the_same_command_id()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var deployment = await ApprovedAsync(s, SoftwareTestKit.Deployment(package.Id, s.DeviceId));
        await fixture.ExecuteAsOwnerAsync("UPDATE software_deployments SET status = 'Running', started_at_utc = now() WHERE id = @d", ("d", deployment));

        // Simulate a crash after the target was claimed (command id recorded) and before the broker was called.
        var executionId = Guid.CreateVersion7();
        await fixture.ExecuteAsOwnerAsync(
            "UPDATE software_deployment_targets SET status = 'Dispatched', attempts = 1, current_execution_id = @e WHERE deployment_id = @d",
            ("e", executionId), ("d", deployment));

        await fixture.RunDeploymentsAsync();
        await fixture.RunDeploymentsAsync();
        var (envelope, _) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(executionId, envelope.ExecutionId);
        Assert.Equal(1, await fixture.ScalarAsOwnerAsync<long>("SELECT count(*) FROM remote_command_executions WHERE id = @e", ("e", executionId)));
    }

    [Fact]
    public async Task A_requester_who_loses_the_permission_after_approval_cannot_have_the_command_delivered()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var (requester, client) = await fixture.TechnicianAsync(s.Tenant, Permissions.Software.Deploy);
        using (client)
        {
            var created = await client.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));
            await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(created.Deployment.Id, true));
            await HttpAssert.OkAsync<DeploymentDetailDto>(await s.SecondApproverClient.DecideAsync(created.Deployment.Id, true));

            // The technician is moved to a view-only role before the engine sends anything.
            var viewOnly = await fixture.AddMemberWithPermissionsAsync(s.Tenant, Permissions.Devices.View);
            var roles = await HttpAssert.OkAsync<TenantUserDto>(await s.Owner.GetAsync($"/api/v1/users/{viewOnly.UserId}"));
            await HttpAssert.OkAsync<TenantUserDto>(await s.Owner.PutAsJsonAsync($"/api/v1/users/{requester.UserId}/roles", new SetUserRolesRequest([.. roles.Roles.Select(r => r.Id)]), ApiFixture.Json));
            await fixture.RunDeploymentsAsync();

            Assert.Empty(await s.Agent.FetchAsync());
            var target = Assert.Single((await s.Owner.GetDeploymentAsync(created.Deployment.Id)).Targets);
            Assert.Equal(TargetStatus.Failed, target.Status);
            Assert.Equal("permission_denied", target.ErrorCode);
        }
    }

    [Fact]
    public async Task Uninstalling_an_msi_uses_its_product_code_without_a_package_and_is_sensitive()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var request = SoftwareTestKit.Deployment(package.Id, s.DeviceId) with { Action = DeploymentAction.Uninstall };
        var preview = await HttpAssert.OkAsync<DeploymentPreviewDto>(await s.Owner.PostAsJsonAsync("/api/v1/software/deployments/preview", request, ApiFixture.Json));
        Assert.Contains("uninstall", preview.SensitivityReasons);
        Assert.Equal(CommandTypes.SoftwareUninstall, preview.CommandType);
        Assert.Empty(preview.Arguments);

        var deployment = await ApprovedAsync(s, request);
        await fixture.RunDeploymentsAsync();
        var (envelope, operation) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(CommandTypes.SoftwareUninstall, envelope.CommandType);
        Assert.False(operation.RequiresPackage);
        Assert.Null(operation.Sha256);
        Assert.Equal(SoftwareTestKit.ProductCode, operation.Detection!.ProductCode);

        // There is nothing to download for an MSI removal.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(envelope.ExecutionId)).StatusCode);
        await HttpAssert.ProblemAsync(await s.Agent.DownloadAsync(envelope.ExecutionId), HttpStatusCode.NotFound);
        await s.Agent.SoftwareResultAsync(envelope.ExecutionId, CommandOutcome.Succeeded, new SoftwareOperationResult { Outcome = SoftwareOutcome.Uninstalled, InstallerExitCode = 0 });
        Assert.Equal(SoftwareOutcome.Uninstalled, Assert.Single((await s.Owner.GetDeploymentAsync(deployment)).Targets).Outcome);
    }

    /// <summary>Creates a deployment and gives it every approval it needs.</summary>
    private static async Task<Guid> ApprovedAsync(SoftwareSetup s, CreateDeploymentRequest request)
    {
        var created = await s.Owner.CreateDeploymentAsync(request);
        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(created.Deployment.Id, true));
        if (created.Deployment.RequiredApprovals > 1)
        {
            await HttpAssert.OkAsync<DeploymentDetailDto>(await s.SecondApproverClient.DecideAsync(created.Deployment.Id, true));
        }

        return created.Deployment.Id;
    }

    /// <summary>Runs <paramref name="action"/> with the API clock moved to <paramref name="at"/>.</summary>
    private async Task<T> AtAsync<T>(DateTimeOffset at, Func<Task<T>> action)
    {
        fixture.Factory.Clock.Advance(at - DateTimeOffset.UtcNow);
        try
        {
            return await action();
        }
        finally
        {
            fixture.Factory.Clock.Reset();
        }
    }

    private Task AtAsync(DateTimeOffset at, Func<Task> action) => AtAsync(at, async () =>
    {
        await action();
        return true;
    });
}
