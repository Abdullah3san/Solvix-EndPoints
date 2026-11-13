namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Deployments;
using Solvix.Modules.Software.Domain;
using Solvix.Modules.Software.Inventory;
using Solvix.Modules.Software.Packages;
using Solvix.Modules.Software.Policies;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.UnitTests.Software;

/// <summary>
/// Package repository (validation, integrity, configuration, retirement), authorization and tenant
/// isolation, software inventory, restriction rules, deployment groups, version detection and audit.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SoftwareRepositoryTests(ApiFixture fixture)
{
    [Fact]
    public async Task Invalid_corrupted_and_disallowed_uploads_are_rejected_audited_and_never_stored()
    {
        using var s = await fixture.SetUpSoftwareAsync();

        // Content type is checked after authorization.
        var text = new StringContent("x");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Owner.PostAsync("/api/v1/software/packages?fileName=a.msi", text)).StatusCode);

        // A script renamed to .exe, an executable named .msi, a signed executable modified after signing.
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("installer.exe", "@echo off\r\nformat c:\r\n"u8.ToArray()), HttpStatusCode.BadRequest, PackageValidationCodes.Malformed);
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("installer.msi", TestPackages.Executable()), HttpStatusCode.BadRequest, PackageValidationCodes.ExtensionMismatch);
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("tool.ps1", TestPackages.Executable()), HttpStatusCode.BadRequest, PackageValidationCodes.UnsupportedType);
        using var signer = TestPackages.Certificate();
        var tampered = SoftwareTestKit.SignedExe(signer);
        tampered[TestPackages.PeOffset + 500] ^= 0x20;
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("setup.exe", tampered), HttpStatusCode.BadRequest, PackageValidationCodes.SignatureMismatch);

        Assert.Empty((await HttpAssert.OkAsync<PagedResult<PackageDto>>(await s.Owner.GetAsync("/api/v1/software/packages"))).Items);
        Assert.False(Directory.Exists(Path.Combine(SolvixApiFactory.PackageStoragePath, s.Tenant.TenantId.ToString("N"))));
        Assert.Empty(Directory.GetFiles(Path.Combine(SolvixApiFactory.PackageStoragePath, "incoming")));
        var rejected = await HttpAssert.OkAsync<PagedResult<SoftwareAuditEventDto>>(await s.Owner.GetAsync($"/api/v1/software/audit?eventType={SoftwareAuditEvents.PackageRejected}"));
        Assert.Equal(4, rejected.Items.Count);
        Assert.All(rejected.Items, e => Assert.Equal(SoftwareAuditOutcome.Denied, e.Outcome));

        // The same file twice.
        var msi = SoftwareTestKit.Msi();
        await s.Owner.UploadAsync("notes.msi", msi);
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("copy-of-notes.msi", msi), HttpStatusCode.Conflict, PackageValidationCodes.Duplicate);

        // Tenant policy: MSI only, signed only.
        await s.Owner.SetPolicyAsync(new UpdateDeploymentPolicyRequest(false, true, [], true, 50, true, true, 72));
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("setup.exe", SoftwareTestKit.SignedExe(signer)), HttpStatusCode.BadRequest, PackageValidationCodes.UnsupportedType);
        await HttpAssert.ProblemAsync(await s.Owner.PostPackageAsync("other.msi", SoftwareTestKit.Msi()), HttpStatusCode.BadRequest, PackageValidationCodes.UnsignedNotAllowed);
    }

    [Fact]
    public async Task A_package_corrupted_in_storage_is_detected_never_served_and_cannot_be_deployed()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var intact = await HttpAssert.OkAsync<PackageVerification>(await s.Owner.PostAsync($"/api/v1/software/packages/{package.Id}/verify", null));
        Assert.True(intact.Intact);

        var created = await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));
        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.ApproverClient.DecideAsync(created.Deployment.Id, true));
        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.SecondApproverClient.DecideAsync(created.Deployment.Id, true));
        await fixture.RunDeploymentsAsync();
        var (envelope, _) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(envelope.ExecutionId)).StatusCode);

        // Bit rot / tampering on the storage volume.
        var stored = Directory.GetFiles(Path.Combine(SolvixApiFactory.PackageStoragePath, s.Tenant.TenantId.ToString("N"))).Single();
        var bytes = await File.ReadAllBytesAsync(stored);
        bytes[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(stored, bytes);

        // The file is re-hashed before it is served (the last check is older than the re-verification interval).
        fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(61));
        try
        {
            await HttpAssert.ProblemAsync(await s.Agent.DownloadAsync(envelope.ExecutionId), HttpStatusCode.Conflict, PackageValidationCodes.Corrupted);
        }
        finally
        {
            fixture.Factory.Clock.Reset();
        }

        var after = await HttpAssert.OkAsync<PackageDto>(await s.Owner.GetAsync($"/api/v1/software/packages/{package.Id}"));
        Assert.Equal(PackageStatus.Corrupted, after.Status);
        await HttpAssert.ProblemAsync(await s.Agent.DownloadAsync(envelope.ExecutionId), HttpStatusCode.Conflict, "package_unavailable");
        await HttpAssert.ProblemAsync(await s.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId)), HttpStatusCode.Conflict, PackageValidationCodes.Corrupted);

        // The device reports that it could not get the package; the target fails without retrying.
        await s.Agent.SoftwareResultAsync(envelope.ExecutionId, CommandOutcome.Failed, new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "download" }, SoftwareErrorCodes.PackageUnavailable);
        var target = Assert.Single((await s.Owner.GetDeploymentAsync(created.Deployment.Id)).Targets);
        Assert.Equal(TargetStatus.Failed, target.Status);
        Assert.Equal(SoftwareErrorCodes.PackageUnavailable, target.ErrorCode);

        var audit = await HttpAssert.OkAsync<PagedResult<SoftwareAuditEventDto>>(await s.Owner.GetAsync($"/api/v1/software/audit?packageId={package.Id}&eventType={SoftwareAuditEvents.PackageCorrupted}"));
        Assert.Single(audit.Items);
    }

    [Fact]
    public async Task Packages_must_be_configured_validly_and_retired_packages_disappear()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        using var signer = TestPackages.Certificate();
        var exe = await s.Owner.UploadAsync("viewer.exe", SoftwareTestKit.SignedExe(signer));

        await HttpAssert.ProblemAsync(await s.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(exe.Id, s.DeviceId)), HttpStatusCode.Conflict, "package_not_configured");

        // An EXE without silent switches would wait for user input.
        await HttpAssert.ProblemAsync(await s.Owner.PutAsJsonAsync($"/api/v1/software/packages/{exe.Id}", SoftwareTestKit.ExeConfiguration() with { SilentPreset = SilentPreset.Custom, InstallArguments = [] }, ApiFixture.Json), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(await s.Owner.PutAsJsonAsync($"/api/v1/software/packages/{exe.Id}", SoftwareTestKit.ExeConfiguration() with { SilentPreset = SilentPreset.Custom, InstallArguments = ["/S & net user x /add"] }, ApiFixture.Json), HttpStatusCode.BadRequest);
        var msi = await s.Owner.UploadAsync("notes.msi", SoftwareTestKit.Msi());
        await HttpAssert.ProblemAsync(await s.Owner.PutAsJsonAsync($"/api/v1/software/packages/{msi.Id}", SoftwareTestKit.MsiConfiguration() with { InstallArguments = ["/quiet"] }, ApiFixture.Json), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(await s.Owner.PutAsJsonAsync($"/api/v1/software/packages/{msi.Id}", SoftwareTestKit.MsiConfiguration() with { Detection = new DetectionRule { Kind = DetectionKind.MsiProductCode, ProductCode = "nope" } }, ApiFixture.Json), HttpStatusCode.BadRequest);

        // An EXE whose uninstall switches were removed cannot be uninstalled.
        var configured = await s.Owner.ConfigureAsync(exe.Id, SoftwareTestKit.ExeConfiguration() with { SilentPreset = SilentPreset.Nsis });
        Assert.False(configured.CanUninstall);
        await HttpAssert.ProblemAsync(await s.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(exe.Id, s.DeviceId) with { Action = DeploymentAction.Uninstall }), HttpStatusCode.Conflict, "package_cannot_uninstall");

        // Reconfiguring after a deployment was requested does not change what that deployment sends.
        var deployment = await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(exe.Id, s.DeviceId));
        await s.Owner.ConfigureAsync(exe.Id, SoftwareTestKit.ExeConfiguration() with { SilentPreset = SilentPreset.Custom, InstallArguments = ["/SILENT"] });
        Assert.Equal("[\"/S\"]", deployment.Parameters["arguments"]);

        // In use: cannot retire; after cancelling: retired and the file is gone.
        await HttpAssert.ProblemAsync(await s.Owner.PostAsync($"/api/v1/software/packages/{exe.Id}/retire", null), HttpStatusCode.Conflict, "package_in_use");
        await HttpAssert.OkAsync<DeploymentDetailDto>(await s.Owner.PostAsync($"/api/v1/software/deployments/{deployment.Deployment.Id}/cancel", null));
        var retired = await HttpAssert.OkAsync<PackageDto>(await s.Owner.PostAsync($"/api/v1/software/packages/{exe.Id}/retire", null));
        Assert.Equal(PackageStatus.Retired, retired.Status);
        Assert.Single(Directory.GetFiles(Path.Combine(SolvixApiFactory.PackageStoragePath, s.Tenant.TenantId.ToString("N")))); // only the MSI
        await HttpAssert.ProblemAsync(await s.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(exe.Id, s.DeviceId)), HttpStatusCode.Conflict, "package_retired");
        Assert.DoesNotContain((await HttpAssert.OkAsync<PagedResult<PackageDto>>(await s.Owner.GetAsync("/api/v1/software/packages"))).Items, p => p.Id == exe.Id);
    }

    [Fact]
    public async Task Each_step_needs_its_own_permission()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var created = await s.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId));
        var (_, viewer) = await fixture.TechnicianAsync(s.Tenant, Permissions.Software.View);
        var (_, deployer) = await fixture.TechnicianAsync(s.Tenant, Permissions.Software.Deploy);
        using (viewer)
        using (deployer)
        {
            await HttpAssert.OkAsync<PagedResult<PackageDto>>(await viewer.GetAsync("/api/v1/software/packages"));
            await HttpAssert.ProblemAsync(await viewer.PostPackageAsync("x.msi", SoftwareTestKit.Msi()), HttpStatusCode.Forbidden, "permission_denied");
            await HttpAssert.ProblemAsync(await viewer.PostDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId)), HttpStatusCode.Forbidden, "permission_denied");
            await HttpAssert.ProblemAsync(await deployer.PostPackageAsync("x.msi", SoftwareTestKit.Msi()), HttpStatusCode.Forbidden, "permission_denied");
            await HttpAssert.ProblemAsync(await deployer.DecideAsync(created.Deployment.Id, true), HttpStatusCode.Forbidden, "permission_denied");
            await HttpAssert.ProblemAsync(await deployer.PutAsJsonAsync("/api/v1/software/policy", SoftwareTestKit.DefaultPolicy(), ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");
            await HttpAssert.ProblemAsync(await s.ApproverClient.PostDeploymentAsync(SoftwareTestKit.Deployment(package.Id, s.DeviceId)), HttpStatusCode.Forbidden, "permission_denied");
            await HttpAssert.ProblemAsync(await viewer.GetAsync("/api/v1/software/audit"), HttpStatusCode.Forbidden, "permission_denied");
        }
    }

    [Fact]
    public async Task Devices_and_tenants_only_reach_their_own_packages_and_deployments()
    {
        using var a = await fixture.SetUpSoftwareAsync(devices: 2);
        using var b = await fixture.SetUpSoftwareAsync();
        var package = await a.Owner.ReadyMsiAsync();
        var created = await a.Owner.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, a.Devices[1].DeviceId));
        await HttpAssert.OkAsync<DeploymentDetailDto>(await a.ApproverClient.DecideAsync(created.Deployment.Id, true));
        await HttpAssert.OkAsync<DeploymentDetailDto>(await a.SecondApproverClient.DecideAsync(created.Deployment.Id, true));
        await fixture.RunDeploymentsAsync();
        var (envelope, _) = await a.Agents[1].NextSoftwareCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await a.Agents[1].AckAsync(envelope.ExecutionId)).StatusCode);

        // Another device of the same tenant, a device of another tenant, and a user token on the agent route.
        await HttpAssert.ProblemAsync(await a.Agents[0].DownloadAsync(envelope.ExecutionId), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Agent.DownloadAsync(envelope.ExecutionId), HttpStatusCode.NotFound);
        Assert.Contains((await a.Owner.GetAsync("/" + AgentRoutes.SoftwarePackage(envelope.ExecutionId))).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.OK, (await a.Agents[1].DownloadAsync(envelope.ExecutionId)).StatusCode);

        // Tenant B sees nothing of tenant A and cannot target A's devices.
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/software/packages/{package.Id}"), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/software/deployments/{created.Deployment.Id}"), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(package.Id, b.DeviceId)), HttpStatusCode.NotFound);
        var bPackage = await b.Owner.ReadyMsiAsync();
        await HttpAssert.ProblemAsync(await b.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(bPackage.Id, a.DeviceId)), HttpStatusCode.BadRequest);

        // Row-level security: tenant B's database session cannot read tenant A's rows.
        Assert.Equal(0, await fixture.CountInTenantAsync(b.Tenant.TenantId, "software_packages", package.Id));
        Assert.Equal(1, await fixture.CountInTenantAsync(a.Tenant.TenantId, "software_packages", package.Id));
    }

    [Fact]
    public async Task Inventory_is_kept_per_device_and_restriction_rules_report_compliance_and_block_deployments()
    {
        using var s = await fixture.SetUpSoftwareAsync(devices: 2);
        await SendInventoryAsync(s.Agents[0], s.Devices[0].Identity.Hostname,
            new InstalledApplication { Name = "Contoso Notes", Version = "5.1.0", Publisher = "Contoso Ltd", Scope = "x64" },
            new InstalledApplication { Name = "uTorrent", Version = "3.6", Publisher = "BitTorrent Inc." });
        await SendInventoryAsync(s.Agents[1], s.Devices[1].Identity.Hostname,
            new InstalledApplication { Name = "Contoso Notes", Version = "5.2.0", Publisher = "Contoso Ltd", Scope = "x64" });

        var catalog = await HttpAssert.OkAsync<PagedResult<SoftwareCatalogEntryDto>>(await s.Owner.GetAsync("/api/v1/software/inventory/catalog?search=contoso"));
        var notes = Assert.Single(catalog.Items);
        Assert.Equal(2, notes.DeviceCount);
        Assert.Equal(["5.2.0", "5.1.0"], notes.Versions);
        var installations = await HttpAssert.OkAsync<List<SoftwareInstallationDto>>(await s.Owner.GetAsync("/api/v1/software/inventory/installations?name=contoso%20notes&version=5.1.0"));
        Assert.Equal(s.DeviceId, Assert.Single(installations).DeviceId);

        // Restriction rules: blocked software and a required minimum version.
        await HttpAssert.OkAsync<RestrictionRuleDto>(await s.Owner.PostAsJsonAsync("/api/v1/software/restriction-rules",
            new SaveRestrictionRuleRequest("No torrent clients", null, RestrictionRuleType.Blocked, "*torrent*", null, null, null), ApiFixture.Json));
        await HttpAssert.OkAsync<RestrictionRuleDto>(await s.Owner.PostAsJsonAsync("/api/v1/software/restriction-rules",
            new SaveRestrictionRuleRequest("Notes 5.2+", null, RestrictionRuleType.Required, "Contoso Notes", "Contoso Ltd", "5.2", null), ApiFixture.Json));
        var compliance = await HttpAssert.OkAsync<ComplianceReport>(await s.Owner.GetAsync("/api/v1/software/compliance"));
        Assert.Equal(2, compliance.DevicesEvaluated);
        Assert.Equal(1, compliance.CompliantDevices);
        Assert.Equal(2, compliance.Violations.Count);
        Assert.All(compliance.Violations, v => Assert.Equal(s.DeviceId, v.DeviceId));
        Assert.Contains(compliance.Violations, v => v.RuleType == RestrictionRuleType.Blocked && v.ApplicationName == "uTorrent");
        Assert.Contains(compliance.Violations, v => v.RuleType == RestrictionRuleType.Required && v.InstalledVersion == "5.1.0");

        // The device removes the torrent client: the next inventory replaces the old rows.
        await SendInventoryAsync(s.Agents[0], s.Devices[0].Identity.Hostname,
            new InstalledApplication { Name = "Contoso Notes", Version = "5.2.0", Publisher = "Contoso Ltd", Scope = "x64" });
        var device = await HttpAssert.OkAsync<List<DeviceSoftwareDto>>(await s.Owner.GetAsync($"/api/v1/software/devices/{s.DeviceId}/inventory"));
        Assert.Equal("5.2.0", Assert.Single(device).Version);
        Assert.Empty((await HttpAssert.OkAsync<ComplianceReport>(await s.Owner.GetAsync("/api/v1/software/compliance"))).Violations);

        // Blocked software cannot be deployed.
        var torrent = await s.Owner.UploadAsync("utorrent.msi", SoftwareTestKit.Msi());
        var blocked = await s.Owner.ConfigureAsync(torrent.Id, SoftwareTestKit.MsiConfiguration() with { Name = "uTorrent", Publisher = "BitTorrent Inc." });
        Assert.Equal("No torrent clients", blocked.BlockedByRule);
        await HttpAssert.ProblemAsync(await s.Owner.PostDeploymentAsync(SoftwareTestKit.Deployment(torrent.Id, s.DeviceId)), HttpStatusCode.Conflict, "package_blocked");
    }

    [Fact]
    public async Task Deployment_groups_resolve_device_groups_and_explicit_devices_minus_exclusions()
    {
        using var s = await fixture.SetUpSoftwareAsync(devices: 3);
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(await s.Owner.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("Finance laptops", null), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.OkAsync<DeviceGroupDto>(await s.Owner.PostAsJsonAsync($"/api/v1/device-groups/{group.Id}/devices", new AddGroupDevicesRequest([s.Devices[0].DeviceId, s.Devices[1].DeviceId]), ApiFixture.Json));

        var saved = await HttpAssert.OkAsync<DeploymentGroupDto>(await s.Owner.PostAsJsonAsync("/api/v1/software/deployment-groups",
            new SaveDeploymentGroupRequest("Finance rollout", null, [s.Devices[2].DeviceId], [group.Id], null, [s.Devices[1].DeviceId]), ApiFixture.Json));
        Assert.Equal(2, saved.ResolvedDeviceCount);
        await HttpAssert.ProblemAsync(await s.Owner.PostAsJsonAsync("/api/v1/software/deployment-groups", new SaveDeploymentGroupRequest("Finance rollout", null, [s.DeviceId], null, null, null), ApiFixture.Json), HttpStatusCode.Conflict, "deployment_group_exists");
        await HttpAssert.ProblemAsync(await s.Owner.PostAsJsonAsync("/api/v1/software/deployment-groups", new SaveDeploymentGroupRequest("Ghosts", null, [Guid.NewGuid()], null, null, null), ApiFixture.Json), HttpStatusCode.BadRequest);

        var package = await s.Owner.ReadyMsiAsync();
        var created = await s.Owner.CreateDeploymentAsync(new CreateDeploymentRequest { Name = "Finance", PackageId = package.Id, DeploymentGroupId = saved.Id });
        Assert.Equal([s.Devices[0].DeviceId, s.Devices[2].DeviceId], created.Targets.Select(t => t.DeviceId).Order());
    }

    [Fact]
    public async Task Version_detection_runs_as_a_read_only_signed_command()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        var package = await s.Owner.ReadyMsiAsync();
        var requested = await HttpAssert.OkAsync<DetectionRequestResult>(await s.Owner.PostAsync($"/api/v1/software/packages/{package.Id}/detect/{s.DeviceId}", null), HttpStatusCode.Accepted);

        var (envelope, operation) = await s.Agent.NextSoftwareCommandAsync();
        Assert.Equal(CommandTypes.SoftwareDetect, envelope.CommandType);
        Assert.Equal(CommandApprovalStatus.NotRequired, envelope.ApprovalStatus);
        Assert.False(operation.RequiresPackage);
        Assert.Equal(SoftwareTestKit.ProductCode, operation.Detection!.ProductCode);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(envelope.ExecutionId)).StatusCode);
        await HttpAssert.ProblemAsync(await s.Agent.DownloadAsync(envelope.ExecutionId), HttpStatusCode.NotFound);
        await s.Agent.SoftwareResultAsync(envelope.ExecutionId, CommandOutcome.Succeeded,
            new SoftwareOperationResult { Outcome = SoftwareOutcome.Detected, DetectionSatisfied = false, DetectedVersion = "5.1.0" });

        var result = await HttpAssert.OkAsync<DetectionResultDto>(await s.Owner.GetAsync($"/api/v1/software/detections/{requested.ExecutionId}"));
        Assert.Equal("Succeeded", result.Phase);
        Assert.True(result.Installed);
        Assert.False(result.Satisfied);
        Assert.Equal("5.1.0", result.DetectedVersion);
    }

    [Fact]
    public async Task The_software_audit_trail_is_append_only()
    {
        using var s = await fixture.SetUpSoftwareAsync();
        await s.Owner.ReadyMsiAsync();
        var update = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsOwnerAsync("UPDATE software_audit_events SET detail = 'rewritten' WHERE tenant_id = @t", ("t", s.Tenant.TenantId)));
        Assert.Equal("42501", update.SqlState);
        var truncate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsOwnerAsync("TRUNCATE software_audit_events"));
        Assert.Equal("42501", truncate.SqlState);
        Assert.True((await HttpAssert.OkAsync<SoftwareAuditVerification>(await s.Owner.GetAsync("/api/v1/software/audit/verify"))).Valid);
    }

    private static async Task SendInventoryAsync(HttpClient agent, string hostname, params InstalledApplication[] software)
    {
        var report = new InventoryReport { CollectedAtUtc = DateTimeOffset.UtcNow, AgentVersion = DeviceTestKit.AgentVersion, Hostname = hostname, Software = software };
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, report, ApiFixture.Json)).StatusCode);
    }
}
