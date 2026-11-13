namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.Agent.Core.Api;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.EndpointManagement.Application.Deployments;
using Solvix.Modules.EndpointManagement.Application.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Application.Patches;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class PatchManagementTests(ApiFixture fixture)
{
    private sealed record Fleet(TenantSession Tenant, HttpClient Admin, List<SimulatedDevice> Devices, PatchDto Patch, string UpdateId) : IDisposable
    {
        public void Dispose() => Admin.Dispose();
    }

    /// <summary>Enrols devices that all report one missing critical update, optionally approved.</summary>
    private async Task<Fleet> FleetAsync(int count, bool approve = true, UpdateClassification classification = UpdateClassification.SecurityUpdates)
    {
        var tenant = await fixture.RegisterTenantAsync();
        var admin = fixture.CreateClient(tenant.Token);
        var token = await fixture.CreateEnrollmentTokenAsync(tenant);
        var updateId = Guid.NewGuid().ToString();
        var devices = new List<SimulatedDevice>();
        for (var i = 0; i < count; i++)
        {
            var device = await fixture.EnrollDeviceAsync(token, $"PC-{i:D2}");
            await device.Api.UploadInventoryAsync(
                EndpointTestKit.Inventory([EndpointTestKit.Update(updateId, "KB5040001 Cumulative Update", classification: classification)]),
                CancellationToken.None);
            devices.Add(device);
        }

        var patch = Assert.Single((await HttpAssert.OkAsync<PagedResult<PatchDto>>(await admin.GetAsync("/api/v1/patches"))).Items);
        if (approve)
        {
            patch = Assert.Single(await HttpAssert.OkAsync<List<PatchDto>>(
                await admin.PostAsJsonAsync("/api/v1/patches/approve", new PatchDecisionRequest([patch.Id], "Tested in lab"), ApiFixture.Json)));
        }

        return new Fleet(tenant, admin, devices, patch, updateId);
    }

    private static SaveDeploymentRequest Plan(
        Fleet fleet,
        Guid? window = null,
        Guid? pilotGroup = null,
        int maxAttempts = 3,
        DateTimeOffset? deadline = null,
        RestartBehavior restart = RestartBehavior.WithinMaintenanceWindow,
        int failureThreshold = 20) =>
        new(
            "Patch Tuesday",
            null,
            [fleet.Patch.Id],
            [new TargetDto(TargetType.AllDevices, null)],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            deadline ?? DateTimeOffset.UtcNow.AddDays(7),
            window,
            restart,
            300,
            pilotGroup,
            null,
            100,
            0,
            false,
            failureThreshold,
            maxAttempts);

    private static async Task<DeploymentDto> CreateAsync(HttpClient admin, SaveDeploymentRequest plan, bool schedule = true)
    {
        var created = await HttpAssert.OkAsync<DeploymentDto>(await admin.PostAsJsonAsync("/api/v1/patch-deployments", plan, ApiFixture.Json), HttpStatusCode.Created);
        return schedule
            ? await HttpAssert.OkAsync<DeploymentDto>(await admin.PostAsync($"/api/v1/patch-deployments/{created.Id}/schedule", null))
            : created;
    }

    private static async Task<DeploymentDto> GetAsync(HttpClient admin, Guid id) =>
        await HttpAssert.OkAsync<DeploymentDto>(await admin.GetAsync($"/api/v1/patch-deployments/{id}"));

    private static async Task<List<DeploymentJobDto>> JobsAsync(HttpClient admin, Guid id) =>
        [.. (await HttpAssert.OkAsync<PagedResult<DeploymentJobDto>>(await admin.GetAsync($"/api/v1/patch-deployments/{id}/devices"))).Items];

    private static async Task<MaintenanceWindowDto> AlwaysOpenWindowAsync(HttpClient admin) =>
        await HttpAssert.OkAsync<MaintenanceWindowDto>(
            await admin.PostAsJsonAsync(
                "/api/v1/maintenance-windows",
                new SaveMaintenanceWindowRequest("Always", null, MaintenanceWindowRecurrence.Weekly, "UTC", WeekDays.Everyday, new TimeOnly(0, 0), 1440, null, null),
                ApiFixture.Json),
            HttpStatusCode.Created);

    private static PatchJobResult Success(PatchJobDto job, bool reboot = false) =>
        new(job.Attempt, PatchJobOutcome.Succeeded, reboot, job.Updates.Select(u => new PatchUpdateResult(u.UpdateId, OperationResultCode.Succeeded, 0, reboot)).ToList(), null, null);

    private static PatchJobResult Failure(PatchJobDto job) =>
        new(job.Attempt, PatchJobOutcome.Failed, false, job.Updates.Select(u => new PatchUpdateResult(u.UpdateId, OperationResultCode.Failed, unchecked((int)0x80070643), false)).ToList(), "Fatal error during installation.", unchecked((int)0x80070643));

    [Fact]
    public async Task Inventory_detects_missing_installed_failed_and_hidden_updates_and_auto_approves_by_rule()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        await HttpAssert.OkAsync<AutoApprovalRuleDto>(
            await admin.PostAsJsonAsync("/api/v1/patch-auto-approval-rules", new SaveAutoApprovalRuleRequest("Defender definitions", [UpdateClassification.DefinitionUpdates], null), ApiFixture.Json),
            HttpStatusCode.Created);
        var device = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(tenant), "SCAN-PC");

        string missing = Guid.NewGuid().ToString(), installed = Guid.NewGuid().ToString(), failing = Guid.NewGuid().ToString(),
            definitions = Guid.NewGuid().ToString(), hidden = Guid.NewGuid().ToString();
        var report = EndpointTestKit.Inventory(
            [
                EndpointTestKit.Update(missing, "KB5040001 Cumulative Update"),
                EndpointTestKit.Update(installed, "KB5039999 Servicing Stack", installed: true, severity: MsrcSeverity.Unspecified),
                EndpointTestKit.Update(failing, "KB5040123 .NET Update", classification: UpdateClassification.Updates, severity: MsrcSeverity.Important),
                EndpointTestKit.Update(definitions, "KB2267602 Defender Definitions", classification: UpdateClassification.DefinitionUpdates, severity: MsrcSeverity.Unspecified),
                EndpointTestKit.Update(hidden, "KB5011111 Driver", classification: UpdateClassification.Drivers, severity: MsrcSeverity.Unspecified) with { IsHidden = true },
            ],
            [new UpdateHistoryEntry(failing, 1, "KB5040123 .NET Update", UpdateOperation.Installation, OperationResultCode.Failed, unchecked((int)0x80070643), DateTimeOffset.UtcNow.AddHours(-3))]);
        await device.Api.UploadInventoryAsync(report, CancellationToken.None);

        var patches = (await HttpAssert.OkAsync<PagedResult<PatchDto>>(await admin.GetAsync("/api/v1/patches?pageSize=50"))).Items.ToDictionary(p => p.UpdateId);
        Assert.Equal(5, patches.Count);
        Assert.Equal(1, patches[missing].DevicesMissing);
        Assert.Equal(1, patches[installed].DevicesInstalled);
        Assert.Equal(1, patches[failing].DevicesFailed);
        Assert.Equal(PatchApprovalState.Approved, patches[definitions].ApprovalState);
        Assert.NotNull(patches[definitions].AutoApprovalRuleId);
        Assert.Equal(PatchApprovalState.NotReviewed, patches[missing].ApprovalState);

        var needed = await HttpAssert.OkAsync<PagedResult<PatchDto>>(await admin.GetAsync("/api/v1/patches?onlyNeeded=true"));
        Assert.Equal(new[] { definitions, failing, missing }.Order(), needed.Items.Select(p => p.UpdateId).Order());

        var failures = await HttpAssert.OkAsync<PagedResult<PatchFailureDto>>(await admin.GetAsync("/api/v1/patches/failures"));
        var failure = Assert.Single(failures.Items);
        Assert.Equal("0x80070643", failure.LastFailureHex);
        Assert.Equal(device.DeviceId, failure.DeviceId);

        var status = await HttpAssert.OkAsync<DeviceUpdateStatusDto>(await admin.GetAsync($"/api/v1/patches/devices/{device.DeviceId}"));
        Assert.Equal("Running", status.ServiceState);
        Assert.Equal(OperationResultCode.Succeeded, status.LastScanResult);
        Assert.Contains(status.Patches, p => p.PatchId == patches[hidden].Id && p.Status == DevicePatchStatus.Hidden);

        // The auto-approved definition update is missing, so the device is not compliant.
        var compliance = await HttpAssert.OkAsync<PatchComplianceReportDto>(await admin.GetAsync("/api/v1/patches/compliance"));
        var row = Assert.Single(compliance.DeviceRows);
        Assert.Equal(DeviceComplianceStatus.NonCompliant, row.Status);
        Assert.Equal(1, row.MissingApproved);
        Assert.Equal(1, row.Failed);

        // A later scan shows the failing update installed: the failure clears.
        await device.Api.UploadInventoryAsync(
            EndpointTestKit.Inventory(
                [EndpointTestKit.Update(failing, "KB5040123 .NET Update", installed: true, classification: UpdateClassification.Updates)],
                [new UpdateHistoryEntry(failing, 1, "KB5040123 .NET Update", UpdateOperation.Installation, OperationResultCode.Succeeded, 0, DateTimeOffset.UtcNow)]),
            CancellationToken.None);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<PatchFailureDto>>(await admin.GetAsync("/api/v1/patches/failures"))).Items);

        // Administrators can ask for a rescan.
        await admin.PostAsync($"/api/v1/patches/devices/{device.DeviceId}/scan", null);
        Assert.True((await device.HeartbeatAsync()).InventoryRequested);
    }

    [Fact]
    public async Task Scheduling_requires_approved_patches_and_owner_sign_off_for_high_impact_rollouts()
    {
        using var fleet = await FleetAsync(1, approve: false);
        var draft = await CreateAsync(fleet.Admin, Plan(fleet), schedule: false);
        await HttpAssert.ProblemAsync(await fleet.Admin.PostAsync($"/api/v1/patch-deployments/{draft.Id}/schedule", null), HttpStatusCode.Conflict, "conflict");

        await fleet.Admin.PostAsJsonAsync("/api/v1/patches/approve", new PatchDecisionRequest([fleet.Patch.Id], null), ApiFixture.Json);
        var adminToken = await fixture.AddUserAsync(fleet.Tenant, TenantRole.Admin);
        using var admin = fixture.CreateClient(adminToken);

        // All devices without a pilot ring needs the Owner.
        await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/patch-deployments/{draft.Id}/schedule", null), HttpStatusCode.Forbidden, "forbidden");
        var scheduled = await HttpAssert.OkAsync<DeploymentDto>(await fleet.Admin.PostAsync($"/api/v1/patch-deployments/{draft.Id}/schedule", null));
        Assert.Equal(DeploymentStatus.Scheduled, scheduled.Status);
        Assert.NotNull(scheduled.AuthorizedByUserId);

        // A patch in an active deployment cannot be declined; a declined patch cannot be deployed.
        await HttpAssert.ProblemAsync(
            await fleet.Admin.PostAsJsonAsync("/api/v1/patches/decline", new PatchDecisionRequest([fleet.Patch.Id], null), ApiFixture.Json), HttpStatusCode.Conflict, "conflict");
        await HttpAssert.OkAsync<DeploymentDto>(await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{draft.Id}/cancel", new DeploymentActionRequest("test"), ApiFixture.Json));
        await HttpAssert.OkAsync<List<PatchDto>>(await fleet.Admin.PostAsJsonAsync("/api/v1/patches/decline", new PatchDecisionRequest([fleet.Patch.Id], "Known issue"), ApiFixture.Json));
        await HttpAssert.ProblemAsync(await fleet.Admin.PostAsJsonAsync("/api/v1/patch-deployments", Plan(fleet), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Feature_upgrades_need_the_owner_to_schedule()
    {
        using var fleet = await FleetAsync(1, classification: UpdateClassification.Upgrades);
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(await fleet.Admin.PostAsJsonAsync("/api/v1/device-groups", new SaveDeviceGroupRequest("Ring 0", null), ApiFixture.Json), HttpStatusCode.Created);
        var draft = await CreateAsync(fleet.Admin, Plan(fleet, pilotGroup: group.Id), schedule: false);
        using var admin = fixture.CreateClient(await fixture.AddUserAsync(fleet.Tenant, TenantRole.Admin));
        var problem = await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/patch-deployments/{draft.Id}/schedule", null), HttpStatusCode.Forbidden);
        Assert.Contains("upgrades", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pilot_gate_promotion_restart_and_post_restart_verification()
    {
        using var fleet = await FleetAsync(3);
        var (pilot, broad1, broad2) = (fleet.Devices[0], fleet.Devices[1], fleet.Devices[2]);
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(await fleet.Admin.PostAsJsonAsync("/api/v1/device-groups", new SaveDeviceGroupRequest("Pilot", null), ApiFixture.Json), HttpStatusCode.Created);
        await fleet.Admin.PostAsJsonAsync($"/api/v1/device-groups/{group.Id}/members", new DeviceGroupMembersRequest([pilot.DeviceId]), ApiFixture.Json);
        var window = await AlwaysOpenWindowAsync(fleet.Admin);
        var deployment = await CreateAsync(fleet.Admin, Plan(fleet, window.Id, group.Id));

        await fixture.TickAsync(fleet.Tenant.TenantId);
        deployment = await GetAsync(fleet.Admin, deployment.Id);
        Assert.Equal((DeploymentStatus.InProgress, DeploymentPhase.Pilot), (deployment.Status, deployment.Phase));
        Assert.Equal(1, deployment.Progress.PilotDevices);

        // Only the pilot ring receives work.
        Assert.Empty((await broad1.HeartbeatAsync()).PatchJobs);
        var job = Assert.Single((await pilot.HeartbeatAsync()).PatchJobs);
        Assert.Equal(fleet.UpdateId, Assert.Single(job.Updates).UpdateId);
        Assert.Equal(window.CurrentOccurrenceEndsUtc, job.InstallWindowEndsUtc);

        await pilot.Api.ReportProgressAsync(job.JobId, new PatchJobProgress(job.Attempt, PatchJobStage.Installing, 40, "Installing"), CancellationToken.None);
        Assert.Equal(PatchJobStatus.InProgress, (await JobsAsync(fleet.Admin, deployment.Id)).Single(j => j.DeviceId == pilot.DeviceId).Status);
        await pilot.Api.ReportResultAsync(job.JobId, Success(job, reboot: true), CancellationToken.None);

        // Installed, restart pending: the restart is offered inside the window.
        var work = await pilot.HeartbeatAsync();
        Assert.Empty(work.PatchJobs);
        var restart = Assert.Single(work.Restarts);
        Assert.Equal(300, restart.GracePeriodSeconds);
        await pilot.Api.AcknowledgeRestartAsync(restart.RestartId, new RestartAck(RestartAckStatus.Initiated, null), CancellationToken.None);

        // Pilot settled at 100% → waits for an administrator (no auto-promote).
        await fixture.TickAsync(fleet.Tenant.TenantId);
        deployment = await GetAsync(fleet.Admin, deployment.Id);
        Assert.Equal(DeploymentStatus.AwaitingPromotion, deployment.Status);
        Assert.Empty((await broad1.HeartbeatAsync()).PatchJobs);

        // The device comes back with a new boot time; the restart completes and a rescan is requested.
        pilot.LastBootUtc = DateTimeOffset.UtcNow;
        Assert.True((await pilot.HeartbeatAsync()).InventoryRequested);
        var restarts = await HttpAssert.OkAsync<PagedResult<DeviceRestartDto>>(await fleet.Admin.GetAsync($"/api/v1/restart-requests?deviceId={pilot.DeviceId}&openOnly=false"));
        Assert.Equal(RestartRequestStatus.Completed, Assert.Single(restarts.Items).Status);

        // The rescan proves the update survived the restart.
        await pilot.Api.UploadInventoryAsync(
            EndpointTestKit.Inventory([EndpointTestKit.Update(fleet.UpdateId, "KB5040001 Cumulative Update", installed: true)]), CancellationToken.None);
        Assert.Equal(PatchJobStatus.Succeeded, (await JobsAsync(fleet.Admin, deployment.Id)).Single(j => j.DeviceId == pilot.DeviceId).Status);

        await HttpAssert.OkAsync<DeploymentDto>(await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{deployment.Id}/promote", new DeploymentActionRequest(null), ApiFixture.Json));
        foreach (var device in new[] { broad1, broad2 })
        {
            var broadJob = Assert.Single((await device.HeartbeatAsync()).PatchJobs);
            await device.Api.ReportResultAsync(broadJob.JobId, Success(broadJob), CancellationToken.None);
        }

        await fixture.TickAsync(fleet.Tenant.TenantId);
        deployment = await GetAsync(fleet.Admin, deployment.Id);
        Assert.Equal(DeploymentStatus.Completed, deployment.Status);
        Assert.Equal(3, deployment.Progress.Succeeded);

        var compliance = await HttpAssert.OkAsync<PatchComplianceReportDto>(await fleet.Admin.GetAsync("/api/v1/patches/compliance"));
        Assert.Equal(100, compliance.CompliancePercent);
        Assert.All(compliance.DeviceRows, r => Assert.Equal(DeviceComplianceStatus.Compliant, r.Status));

        var csv = await fleet.Admin.GetStringAsync("/api/v1/patches/compliance/report.csv");
        Assert.StartsWith("device_id,hostname,status", csv, StringComparison.Ordinal);
        Assert.Equal(4, csv.Trim().Split('\n').Length);
    }

    [Fact]
    public async Task Failed_pilot_halts_the_rollout_and_only_an_owner_can_override_it_with_a_reason()
    {
        using var fleet = await FleetAsync(2);
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(await fleet.Admin.PostAsJsonAsync("/api/v1/device-groups", new SaveDeviceGroupRequest("Pilot", null), ApiFixture.Json), HttpStatusCode.Created);
        await fleet.Admin.PostAsJsonAsync($"/api/v1/device-groups/{group.Id}/members", new DeviceGroupMembersRequest([fleet.Devices[0].DeviceId]), ApiFixture.Json);
        var deployment = await CreateAsync(fleet.Admin, Plan(fleet, pilotGroup: group.Id, maxAttempts: 1));
        await fixture.TickAsync(fleet.Tenant.TenantId);

        var job = Assert.Single((await fleet.Devices[0].HeartbeatAsync()).PatchJobs);
        await fleet.Devices[0].Api.ReportResultAsync(job.JobId, Failure(job), CancellationToken.None);
        await fixture.TickAsync(fleet.Tenant.TenantId);

        deployment = await GetAsync(fleet.Admin, deployment.Id);
        Assert.Equal(DeploymentStatus.Halted, deployment.Status);
        Assert.Contains("below the required 100%", deployment.HaltReason, StringComparison.Ordinal);
        Assert.Empty((await fleet.Devices[1].HeartbeatAsync()).PatchJobs);

        using var admin = fixture.CreateClient(await fixture.AddUserAsync(fleet.Tenant, TenantRole.Admin));
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/patch-deployments/{deployment.Id}/promote", new DeploymentActionRequest("go", Force: true), ApiFixture.Json), HttpStatusCode.Forbidden);
        await HttpAssert.ProblemAsync(await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{deployment.Id}/promote", new DeploymentActionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict);
        await HttpAssert.ProblemAsync(await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{deployment.Id}/promote", new DeploymentActionRequest(null, Force: true), ApiFixture.Json), HttpStatusCode.BadRequest);
        var promoted = await HttpAssert.OkAsync<DeploymentDto>(
            await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{deployment.Id}/promote", new DeploymentActionRequest("Failure is a known WSUS sync issue on the pilot only", Force: true), ApiFixture.Json));
        Assert.Equal(DeploymentPhase.Broad, promoted.Phase);
        Assert.Single((await fleet.Devices[1].HeartbeatAsync()).PatchJobs);
    }

    [Fact]
    public async Task Interrupted_installation_is_retried_late_results_are_rejected_and_the_job_fails_after_max_attempts()
    {
        using var fleet = await FleetAsync(1);
        var device = fleet.Devices[0];
        var deployment = await CreateAsync(fleet.Admin, Plan(fleet, maxAttempts: 2));
        await fixture.TickAsync(fleet.Tenant.TenantId);
        var first = Assert.Single((await device.HeartbeatAsync()).PatchJobs);
        var start = DateTimeOffset.UtcNow;

        // The device goes silent (power loss mid-install): the lease expires.
        await fixture.TickAsync(fleet.Tenant.TenantId, start.AddHours(1));
        var job = Assert.Single(await JobsAsync(fleet.Admin, deployment.Id));
        Assert.Equal(PatchJobStatus.Pending, job.Status);
        Assert.Equal(1, job.FailureCount);
        Assert.Contains("Interrupted", job.LastError, StringComparison.Ordinal);
        Assert.Empty((await device.HeartbeatAsync()).PatchJobs); // back-off still running

        PatchJobDto second;
        using (fixture.Factory.Clock.Advance(TimeSpan.FromHours(2)))
        {
            second = Assert.Single((await device.HeartbeatAsync()).PatchJobs);
        }

        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(first.Attempt + 1, second.Attempt);

        // The agent of the abandoned attempt reports late: rejected.
        await Assert.ThrowsAsync<AgentConflictException>(() => device.Api.ReportResultAsync(first.JobId, Success(first), CancellationToken.None));

        // Second attempt also never reports → no attempts left.
        await fixture.TickAsync(fleet.Tenant.TenantId, start.AddHours(4));
        job = Assert.Single(await JobsAsync(fleet.Admin, deployment.Id));
        Assert.Equal(PatchJobStatus.Failed, job.Status);
        Assert.Equal(2, job.FailureCount);

        await fixture.TickAsync(fleet.Tenant.TenantId, start.AddHours(4));
        Assert.Equal(DeploymentStatus.CompletedWithFailures, (await GetAsync(fleet.Admin, deployment.Id)).Status);

        var audit = await fleet.Admin.GetStringAsync($"/api/v1/audit?action=patch_deployment&entityId={deployment.Id}&pageSize=100");
        Assert.Contains("patch_deployment.device_interrupted", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aborted_runs_and_resumed_jobs_do_not_consume_attempts()
    {
        using var fleet = await FleetAsync(1);
        var device = fleet.Devices[0];
        var deployment = await CreateAsync(fleet.Admin, Plan(fleet, maxAttempts: 1));
        await fixture.TickAsync(fleet.Tenant.TenantId);
        var job = Assert.Single((await device.HeartbeatAsync()).PatchJobs);

        // The agent restarts mid-job: the same attempt is handed out again (resume), not a new one.
        var resumed = Assert.Single((await device.HeartbeatAsync()).PatchJobs);
        Assert.Equal((job.JobId, job.Attempt), (resumed.JobId, resumed.Attempt));

        // Progress for a wrong attempt is rejected.
        await Assert.ThrowsAsync<AgentConflictException>(() => device.Api.ReportProgressAsync(job.JobId, new PatchJobProgress(job.Attempt + 1, PatchJobStage.Installing, 10, null), CancellationToken.None));

        // The window is closing: the agent stops cleanly. This must not count as a failure.
        await device.Api.ReportResultAsync(job.JobId, new PatchJobResult(job.Attempt, PatchJobOutcome.Aborted, false, [], "window closing", null), CancellationToken.None);
        var row = Assert.Single(await JobsAsync(fleet.Admin, deployment.Id));
        Assert.Equal((PatchJobStatus.Pending, 0), (row.Status, row.FailureCount));

        using (fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(5)))
        {
            var retry = Assert.Single((await device.HeartbeatAsync()).PatchJobs);
            Assert.Equal(job.Attempt + 1, retry.Attempt);
            await device.Api.ReportResultAsync(retry.JobId, Success(retry), CancellationToken.None);
        }

        Assert.Equal(PatchJobStatus.Succeeded, Assert.Single(await JobsAsync(fleet.Admin, deployment.Id)).Status);
    }

    [Fact]
    public async Task Nothing_is_installed_or_restarted_outside_the_maintenance_window()
    {
        using var fleet = await FleetAsync(1);
        var device = fleet.Devices[0];
        var opens = DateTimeOffset.UtcNow.AddDays(1);
        var window = await HttpAssert.OkAsync<MaintenanceWindowDto>(
            await fleet.Admin.PostAsJsonAsync(
                "/api/v1/maintenance-windows",
                new SaveMaintenanceWindowRequest("Change 7", null, MaintenanceWindowRecurrence.Once, null, WeekDays.None, null, null, opens, opens.AddHours(2)),
                ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.False(window.IsOpenNow);
        await CreateAsync(fleet.Admin, Plan(fleet, window.Id));
        await fixture.TickAsync(fleet.Tenant.TenantId);

        Assert.Empty((await device.HeartbeatAsync()).PatchJobs);

        using (fixture.Factory.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(30))))
        {
            var job = Assert.Single((await device.HeartbeatAsync()).PatchJobs);
            Assert.Equal(opens.AddHours(2).ToUnixTimeSeconds(), job.InstallWindowEndsUtc!.Value.ToUnixTimeSeconds());
            await device.Api.ReportResultAsync(job.JobId, Success(job, reboot: true), CancellationToken.None);
        }

        // The device only checks in after the window closed: the restart it needs is withheld.
        using (fixture.Factory.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromHours(3))))
        {
            Assert.Empty((await device.HeartbeatAsync()).Restarts);
        }

        var restarts = await HttpAssert.OkAsync<PagedResult<DeviceRestartDto>>(await fleet.Admin.GetAsync($"/api/v1/restart-requests?deviceId={device.DeviceId}"));
        Assert.Equal(RestartRequestStatus.Pending, Assert.Single(restarts.Items).Status);
        var compliance = await HttpAssert.OkAsync<PatchComplianceReportDto>(await fleet.Admin.GetAsync("/api/v1/patches/compliance"));
        Assert.Equal(DeviceComplianceStatus.PendingRestart, Assert.Single(compliance.DeviceRows).Status);
    }

    [Fact]
    public async Task Broad_rollout_halts_when_the_failure_rate_reaches_the_threshold()
    {
        using var fleet = await FleetAsync(6);
        var deployment = await CreateAsync(fleet.Admin, Plan(fleet, maxAttempts: 1, failureThreshold: 30));
        await fixture.TickAsync(fleet.Tenant.TenantId);

        var outcomes = new[] { true, true, true, false, false };
        for (var i = 0; i < outcomes.Length; i++)
        {
            var job = Assert.Single((await fleet.Devices[i].HeartbeatAsync()).PatchJobs);
            await fleet.Devices[i].Api.ReportResultAsync(job.JobId, outcomes[i] ? Success(job) : Failure(job), CancellationToken.None);
        }

        await fixture.TickAsync(fleet.Tenant.TenantId);
        deployment = await GetAsync(fleet.Admin, deployment.Id);
        Assert.Equal(DeploymentStatus.Halted, deployment.Status);
        Assert.Contains("40%", deployment.HaltReason, StringComparison.Ordinal);
        Assert.Empty((await fleet.Devices[5].HeartbeatAsync()).PatchJobs);

        await HttpAssert.OkAsync<DeploymentDto>(await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{deployment.Id}/resume", new DeploymentActionRequest("Root cause fixed"), ApiFixture.Json));
        Assert.Single((await fleet.Devices[5].HeartbeatAsync()).PatchJobs);
    }

    [Fact]
    public async Task Deadline_times_out_devices_that_never_installed_and_cancel_stops_pending_work()
    {
        using var fleet = await FleetAsync(2);
        var deployment = await CreateAsync(fleet.Admin, Plan(fleet, deadline: DateTimeOffset.UtcNow.AddHours(1)));
        await fixture.TickAsync(fleet.Tenant.TenantId);
        var job = Assert.Single((await fleet.Devices[0].HeartbeatAsync()).PatchJobs);
        await fleet.Devices[0].Api.ReportResultAsync(job.JobId, Success(job), CancellationToken.None);

        await fixture.TickAsync(fleet.Tenant.TenantId, DateTimeOffset.UtcNow.AddHours(2));
        var jobs = await JobsAsync(fleet.Admin, deployment.Id);
        Assert.Contains(jobs, j => j.DeviceId == fleet.Devices[1].DeviceId && j.Status == PatchJobStatus.TimedOut);
        await fixture.TickAsync(fleet.Tenant.TenantId, DateTimeOffset.UtcNow.AddHours(2));
        Assert.Equal(DeploymentStatus.CompletedWithFailures, (await GetAsync(fleet.Admin, deployment.Id)).Status);

        // Cancellation of a second deployment cancels pending jobs.
        var second = await CreateAsync(fleet.Admin, Plan(fleet) with { Name = "Out of band" });
        await fixture.TickAsync(fleet.Tenant.TenantId);
        var cancelled = await HttpAssert.OkAsync<DeploymentDto>(await fleet.Admin.PostAsJsonAsync($"/api/v1/patch-deployments/{second.Id}/cancel", new DeploymentActionRequest("Superseded"), ApiFixture.Json));
        Assert.Equal(DeploymentStatus.Cancelled, cancelled.Status);
        Assert.All(await JobsAsync(fleet.Admin, second.Id), j => Assert.True(j.Status is PatchJobStatus.Cancelled or PatchJobStatus.NotApplicable, j.Status.ToString()));
        Assert.Empty((await fleet.Devices[1].HeartbeatAsync()).PatchJobs);
    }

    [Fact]
    public async Task Manual_restarts_follow_the_window_and_forced_restarts_need_an_owner()
    {
        using var fleet = await FleetAsync(1, approve: false);
        var device = fleet.Devices[0];
        using var admin = fixture.CreateClient(await fixture.AddUserAsync(fleet.Tenant, TenantRole.Admin));
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/restart-requests", new CreateRestartRequest(device.DeviceId, "Driver", null, DateTimeOffset.UtcNow.AddHours(1), null, Force: true), ApiFixture.Json),
            HttpStatusCode.Forbidden);

        var created = await HttpAssert.OkAsync<DeviceRestartDto>(
            await admin.PostAsJsonAsync("/api/v1/restart-requests", new CreateRestartRequest(device.DeviceId, "Apply driver update", null, null, null), ApiFixture.Json),
            HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/restart-requests", new CreateRestartRequest(device.DeviceId, "again", null, null, null), ApiFixture.Json), HttpStatusCode.Conflict);

        var restart = Assert.Single((await device.HeartbeatAsync()).Restarts);
        Assert.Equal(created.Id, restart.RestartId);
        await device.Api.AcknowledgeRestartAsync(restart.RestartId, new RestartAck(RestartAckStatus.Initiated, null), CancellationToken.None);
        device.LastBootUtc = DateTimeOffset.UtcNow;
        await device.HeartbeatAsync();
        var after = await HttpAssert.OkAsync<PagedResult<DeviceRestartDto>>(await admin.GetAsync($"/api/v1/restart-requests?deviceId={device.DeviceId}&openOnly=false"));
        Assert.Equal(RestartRequestStatus.Completed, Assert.Single(after.Items).Status);
    }

    [Fact]
    public async Task Devices_cannot_see_or_report_on_jobs_of_other_devices_or_tenants()
    {
        using var fleet = await FleetAsync(2);
        await CreateAsync(fleet.Admin, Plan(fleet));
        await fixture.TickAsync(fleet.Tenant.TenantId);
        var job = Assert.Single((await fleet.Devices[0].HeartbeatAsync()).PatchJobs);

        var sameTenant = await Assert.ThrowsAsync<HttpRequestException>(() => fleet.Devices[1].Api.ReportResultAsync(job.JobId, Success(job), CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, sameTenant.StatusCode);

        var other = await fixture.RegisterTenantAsync();
        var foreign = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(other), "FOREIGN");
        var crossTenant = await Assert.ThrowsAsync<HttpRequestException>(() => foreign.Api.ReportResultAsync(job.JobId, Success(job), CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Empty((await foreign.HeartbeatAsync()).PatchJobs);

        using var otherAdmin = fixture.CreateClient(other.Token);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<PatchDto>>(await otherAdmin.GetAsync("/api/v1/patches"))).Items);
        await HttpAssert.ProblemAsync(await otherAdmin.GetAsync($"/api/v1/patches/{fleet.Patch.Id}"), HttpStatusCode.NotFound);
    }
}
