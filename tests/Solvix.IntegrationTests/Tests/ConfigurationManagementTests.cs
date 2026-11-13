namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Application.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class ConfigurationManagementTests(ApiFixture fixture)
{
    private static readonly ScreenLockSetting Lock900 = new(ScreenLockControl.InactivityTimeoutSeconds, 900);

    /// <summary>Creates a policy, submits, approves (as the same owner: low risk) and publishes v1.</summary>
    private static async Task<ConfigurationPolicyDto> PublishedAsync(HttpClient owner, string name, params ConfigurationSetting[] settings)
    {
        var policy = await HttpAssert.OkAsync<ConfigurationPolicyDto>(
            await owner.PostAsJsonAsync("/api/v1/configuration-policies", new CreateConfigurationPolicyRequest(name, null, settings, "Initial"), ApiFixture.Json),
            HttpStatusCode.Created);
        await ApproveAndPublishAsync(owner, owner, policy.Id, 1);
        return policy;
    }

    private static async Task ApproveAndPublishAsync(HttpClient author, HttpClient approver, Guid policyId, int number, bool acknowledge = false)
    {
        await HttpAssert.OkAsync<PolicyVersionDto>(await author.PostAsync($"/api/v1/configuration-policies/{policyId}/versions/{number}/submit", null));
        await HttpAssert.OkAsync<PolicyVersionDto>(await approver.PostAsJsonAsync($"/api/v1/configuration-policies/{policyId}/versions/{number}/approve", new ReviewPolicyVersionRequest("ok", acknowledge), ApiFixture.Json));
        await HttpAssert.OkAsync<PolicyVersionDto>(await approver.PostAsync($"/api/v1/configuration-policies/{policyId}/versions/{number}/publish", null));
    }

    private static async Task<AssignmentDto> AssignAsync(HttpClient admin, Guid policyId, SaveAssignmentRequest request) =>
        await HttpAssert.OkAsync<AssignmentDto>(await admin.PostAsJsonAsync($"/api/v1/configuration-policies/{policyId}/assignments", request, ApiFixture.Json), HttpStatusCode.Created);

    private static SaveAssignmentRequest ToAll(int priority = 100, EnforcementMode mode = EnforcementMode.Enforce, Guid? window = null, RemovalBehavior removal = RemovalBehavior.Revert) =>
        new(TargetType.AllDevices, null, false, mode, priority, window, removal);

    private async Task<(TenantSession Tenant, HttpClient Owner, SimulatedDevice Device)> SingleDeviceAsync()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(tenant), "CFG-PC");
        return (tenant, fixture.CreateClient(tenant.Token), device);
    }

    [Fact]
    public async Task Approval_workflow_enforces_four_eyes_and_owner_authorisation_by_risk()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        using var admin = fixture.CreateClient(await fixture.AddUserAsync(tenant, TenantRole.Admin));
        using var admin2 = fixture.CreateClient(await fixture.AddUserAsync(tenant, TenantRole.Admin));
        using var member = fixture.CreateClient(await fixture.AddUserAsync(tenant, TenantRole.Member));

        // Members can read but not author.
        await HttpAssert.ProblemAsync(
            await member.PostAsJsonAsync("/api/v1/configuration-policies", new CreateConfigurationPolicyRequest("x", null, [Lock900], null), ApiFixture.Json), HttpStatusCode.Forbidden);

        // Low risk: the author may approve.
        await PublishedAsync(owner, "Screen lock", Lock900);

        // Elevated risk: needs someone other than the author.
        var spooler = await HttpAssert.OkAsync<ConfigurationPolicyDto>(
            await admin.PostAsJsonAsync("/api/v1/configuration-policies", new CreateConfigurationPolicyRequest("Print spooler off", null, [new ServiceSetting("Spooler", ServiceStartupType.Disabled, ServiceRunState.Stopped)], null), ApiFixture.Json),
            HttpStatusCode.Created);
        var v1 = Assert.Single(spooler.Versions);
        Assert.Equal(SettingRisk.Elevated, v1.MaxRisk);
        Assert.Contains("second administrator", v1.ApprovalRequirement, StringComparison.Ordinal);
        await HttpAssert.OkAsync<PolicyVersionDto>(await admin.PostAsync($"/api/v1/configuration-policies/{spooler.Id}/versions/1/submit", null));
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/configuration-policies/{spooler.Id}/versions/1/approve", new ReviewPolicyVersionRequest(null), ApiFixture.Json), HttpStatusCode.Forbidden);
        Assert.Equal(SettingRisk.Elevated, (await HttpAssert.OkAsync<PolicyVersionDto>(
            await admin2.PostAsJsonAsync($"/api/v1/configuration-policies/{spooler.Id}/versions/1/approve", new ReviewPolicyVersionRequest(null), ApiFixture.Json))).AuthorizedRisk);

        // Destructive: Owner only, explicit acknowledgement, Owner publishes.
        var firewallOff = await HttpAssert.OkAsync<ConfigurationPolicyDto>(
            await admin.PostAsJsonAsync("/api/v1/configuration-policies", new CreateConfigurationPolicyRequest("Lab firewall off", null, [new FirewallProfileSetting(FirewallProfile.Public, false, null, null)], null), ApiFixture.Json),
            HttpStatusCode.Created);
        await admin.PostAsync($"/api/v1/configuration-policies/{firewallOff.Id}/versions/1/submit", null);
        await HttpAssert.ProblemAsync(await admin2.PostAsJsonAsync($"/api/v1/configuration-policies/{firewallOff.Id}/versions/1/approve", new ReviewPolicyVersionRequest(null, true), ApiFixture.Json), HttpStatusCode.Forbidden);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/configuration-policies/{firewallOff.Id}/versions/1/approve", new ReviewPolicyVersionRequest(null), ApiFixture.Json), HttpStatusCode.BadRequest);
        var approved = await HttpAssert.OkAsync<PolicyVersionDto>(await owner.PostAsJsonAsync($"/api/v1/configuration-policies/{firewallOff.Id}/versions/1/approve", new ReviewPolicyVersionRequest("CAB-7781", true), ApiFixture.Json));
        Assert.True(approved.DestructiveAcknowledged);
        await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/configuration-policies/{firewallOff.Id}/versions/1/publish", null), HttpStatusCode.Forbidden);
        await HttpAssert.OkAsync<PolicyVersionDto>(await owner.PostAsync($"/api/v1/configuration-policies/{firewallOff.Id}/versions/1/publish", null));

        // Enforcing it on more devices is again Owner-only.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/configuration-policies/{firewallOff.Id}/assignments", ToAll(), ApiFixture.Json), HttpStatusCode.Forbidden);
        await AssignAsync(admin, firewallOff.Id, ToAll(mode: EnforcementMode.Monitor));
        var hq = (await HttpAssert.OkAsync<PagedResult<BranchDto>>(await owner.GetAsync("/api/v1/branches"))).Items[0];
        await AssignAsync(owner, firewallOff.Id, new SaveAssignmentRequest(TargetType.Branch, hq.Id, false, EnforcementMode.Enforce, 60));

        // Blocked settings never get saved.
        var blocked = await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/configuration-policies", new CreateConfigurationPolicyRequest("Break it", null, [new ServiceSetting("RpcSs", ServiceStartupType.Disabled, null)], null), ApiFixture.Json),
            HttpStatusCode.BadRequest,
            "validation_failed");
        Assert.True(blocked.GetProperty("errors").TryGetProperty("settings[0]", out _));
    }

    [Fact]
    public async Task Assignments_target_branches_departments_with_sub_departments_groups_and_exclusions()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var hq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await owner.GetAsync("/api/v1/branches"))).Items);
        async Task<DepartmentDto> Department(string code, Guid? parent) => await HttpAssert.OkAsync<DepartmentDto>(
            await owner.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hq.Id, parent, code, code, null), ApiFixture.Json), HttpStatusCode.Created);
        var it = await Department("IT", null);
        var helpdesk = await Department("HELPDESK", it.Id);
        var sales = await Department("SALES", null);
        var kiosks = await HttpAssert.OkAsync<DeviceGroupDto>(await owner.PostAsJsonAsync("/api/v1/device-groups", new SaveDeviceGroupRequest("Kiosks", null), ApiFixture.Json), HttpStatusCode.Created);

        var token = await fixture.CreateEnrollmentTokenAsync(tenant);
        var helpdeskPc = await fixture.EnrollDeviceAsync(token, "HD-01");
        var salesPc = await fixture.EnrollDeviceAsync(token, "SALES-01");
        var kiosk = await fixture.EnrollDeviceAsync(token, "KIOSK-01");
        await HttpAssert.OkAsync<DeviceDto>(await owner.PutAsJsonAsync($"/api/v1/devices/{helpdeskPc.DeviceId}/placement", new PlaceDeviceRequest(null, helpdesk.Id), ApiFixture.Json));
        await HttpAssert.OkAsync<DeviceDto>(await owner.PutAsJsonAsync($"/api/v1/devices/{salesPc.DeviceId}/placement", new PlaceDeviceRequest(hq.Id, sales.Id), ApiFixture.Json));
        await HttpAssert.OkAsync<DeviceDto>(await owner.PutAsJsonAsync($"/api/v1/devices/{kiosk.DeviceId}/placement", new PlaceDeviceRequest(hq.Id, null), ApiFixture.Json));
        await owner.PostAsJsonAsync($"/api/v1/device-groups/{kiosks.Id}/members", new DeviceGroupMembersRequest([kiosk.DeviceId]), ApiFixture.Json);

        var lockPolicy = await PublishedAsync(owner, "IT lock", Lock900);
        await AssignAsync(owner, lockPolicy.Id, new SaveAssignmentRequest(TargetType.Department, it.Id, false));
        var usb = new UsbStorageSetting(UsbDeviceClass.AllRemovableStorage, UsbAccess.NoAccess);
        var usbPolicy = await PublishedAsync(owner, "Kiosk USB", usb);
        await AssignAsync(owner, usbPolicy.Id, new SaveAssignmentRequest(TargetType.DeviceGroup, kiosks.Id, false));
        var sleep = new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 1800);
        var powerPolicy = await PublishedAsync(owner, "HQ power", sleep);
        await AssignAsync(owner, powerPolicy.Id, new SaveAssignmentRequest(TargetType.Branch, hq.Id, false));
        await AssignAsync(owner, powerPolicy.Id, new SaveAssignmentRequest(TargetType.Device, salesPc.DeviceId, true));

        async Task<string[]> Keys(SimulatedDevice d) =>
            [.. (await d.Api.GetConfigurationAsync(null, CancellationToken.None))!.Settings.Select(s => s.Key).Order()];

        Assert.Equal(new[] { Lock900.Key, sleep.Key }.Order(), await Keys(helpdeskPc));
        Assert.Empty(await Keys(salesPc));
        Assert.Equal(new[] { sleep.Key, usb.Key }.Order(), await Keys(kiosk));

        // Targets must exist in the tenant.
        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync($"/api/v1/configuration-policies/{lockPolicy.Id}/assignments", new SaveAssignmentRequest(TargetType.DeviceGroup, Guid.NewGuid(), false), ApiFixture.Json),
            HttpStatusCode.BadRequest);

        // The group cannot be deleted while a policy targets it.
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/device-groups/{kiosks.Id}"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Conflicting_policies_are_reported_and_unresolved_settings_are_withheld_until_priorities_differ()
    {
        var (_, owner, device) = await SingleDeviceAsync();
        using var _owner = owner;
        var strict = await PublishedAsync(owner, "Strict lock", new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 300));
        var relaxed = await PublishedAsync(owner, "Relaxed lock", Lock900, new PowerSetting(PowerSettingType.DisplayTimeout, PowerSource.Ac, 600));
        var strictAssignment = await AssignAsync(owner, strict.Id, ToAll(priority: 100));
        await AssignAsync(owner, relaxed.Id, ToAll(priority: 100));

        var conflicts = await HttpAssert.OkAsync<List<DeviceConflictsDto>>(await owner.GetAsync("/api/v1/configuration/conflicts"));
        var conflict = Assert.Single(Assert.Single(conflicts).Conflicts);
        Assert.False(conflict.IsResolved);
        Assert.Equal(Lock900.Key, conflict.Key);
        Assert.Equal(2, conflict.Candidates.Count);

        var document = (await device.Api.GetConfigurationAsync(null, CancellationToken.None))!;
        Assert.Equal([Lock900.Key], document.ConflictedKeys);
        Assert.DoesNotContain(document.Settings, s => s.Key == Lock900.Key);
        Assert.Contains(document.Settings, s => s.Setting is PowerSetting);

        // Give the strict policy precedence: the conflict becomes resolved and its value is delivered.
        await HttpAssert.OkAsync<AssignmentDto>(await owner.PutAsJsonAsync(
            $"/api/v1/configuration/assignments/{strictAssignment.Id}", new UpdateAssignmentRequest(EnforcementMode.Enforce, 10, null, RemovalBehavior.Revert), ApiFixture.Json));
        Assert.Empty(await HttpAssert.OkAsync<List<DeviceConflictsDto>>(await owner.GetAsync("/api/v1/configuration/conflicts")));
        var resolved = Assert.Single(Assert.Single(await HttpAssert.OkAsync<List<DeviceConflictsDto>>(await owner.GetAsync("/api/v1/configuration/conflicts?includeResolved=true"))).Conflicts);
        Assert.True(resolved.IsResolved);
        Assert.Equal(strict.Id, resolved.WinningPolicyId);

        document = (await device.Api.GetConfigurationAsync(null, CancellationToken.None))!;
        Assert.Equal(300, ((ScreenLockSetting)document.Settings.Single(s => s.Key == Lock900.Key).Setting).Value);
        Assert.Empty(document.ConflictedKeys);

        var view = await HttpAssert.OkAsync<DeviceConfigurationDto>(await owner.GetAsync($"/api/v1/configuration/devices/{device.DeviceId}"));
        Assert.Equal(strict.Id, view.Settings.Single(s => s.Key == Lock900.Key).PolicyId);
    }

    [Fact]
    public async Task Agent_applies_detects_drift_remediates_and_reports_compliance()
    {
        var (_, owner, device) = await SingleDeviceAsync();
        using var _owner = owner;
        device.Settings.Values[Lock900.Key] = "60";
        var enforced = await PublishedAsync(owner, "Lock", Lock900);
        await AssignAsync(owner, enforced.Id, ToAll());
        var usb = new UsbStorageSetting(UsbDeviceClass.RemovableDisks, UsbAccess.ReadOnly);
        var monitored = await PublishedAsync(owner, "USB audit", usb);
        await AssignAsync(owner, monitored.Id, ToAll(mode: EnforcementMode.Monitor));

        var work = await device.HeartbeatAsync();
        var report = (await device.SyncConfigurationAsync())!;
        Assert.Equal(work.ConfigurationRevision, report.Revision);
        Assert.Equal(SettingComplianceStatus.Applied, report.Results.Single(r => r.Key == Lock900.Key).Status);
        Assert.Equal(SettingComplianceStatus.Drifted, report.Results.Single(r => r.Key == usb.Key).Status);
        Assert.Equal("900", device.Settings.Values[Lock900.Key]);
        Assert.False(device.Settings.Values.ContainsKey(usb.Key), "Monitor mode must not change the device.");

        // Unchanged document → 304.
        Assert.Null(await device.Api.GetConfigurationAsync(report.Revision, CancellationToken.None));

        // A local administrator changes the value: detected and corrected on the next evaluation.
        device.Settings.Values[Lock900.Key] = "0";
        var second = (await device.SyncConfigurationAsync())!;
        Assert.Equal(SettingComplianceStatus.Remediated, second.Results.Single(r => r.Key == Lock900.Key).Status);
        Assert.Equal("900", device.Settings.Values[Lock900.Key]);

        var drift = await HttpAssert.OkAsync<PagedResult<DriftEventDto>>(await owner.GetAsync($"/api/v1/configuration/drift?deviceId={device.DeviceId}"));
        Assert.Contains(drift.Items, e => e.SettingKey == Lock900.Key && e.Status == SettingComplianceStatus.Remediated && e.ActualValue == "900");
        Assert.Contains(drift.Items, e => e.SettingKey == usb.Key && e.Status == SettingComplianceStatus.Drifted);

        var compliance = await HttpAssert.OkAsync<ConfigurationComplianceDto>(await owner.GetAsync("/api/v1/configuration/compliance"));
        Assert.Equal(1, compliance.DevicesEvaluated);
        Assert.Equal(1, compliance.DevicesWithDrift);
        Assert.Equal(0, compliance.CompliancePercent);
        Assert.Equal(1, compliance.Policies.Single(p => p.PolicyId == enforced.Id).Compliant);
        Assert.Equal(1, compliance.Policies.Single(p => p.PolicyId == monitored.Id).Drifted);

        var audit = await owner.GetStringAsync($"/api/v1/audit?action=configuration&entityId={device.DeviceId}");
        Assert.Contains("configuration.setting_remediated", audit, StringComparison.Ordinal);
        Assert.Contains("\"actorType\":\"Agent\"", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maintenance_window_gates_changes_but_not_evaluation()
    {
        var (_, owner, device) = await SingleDeviceAsync();
        using var _owner = owner;
        var opens = DateTimeOffset.UtcNow.AddDays(2);
        var window = await HttpAssert.OkAsync<MaintenanceWindowDto>(
            await owner.PostAsJsonAsync("/api/v1/maintenance-windows", new SaveMaintenanceWindowRequest("Weekend", null, MaintenanceWindowRecurrence.Once, null, WeekDays.None, null, null, opens, opens.AddHours(4)), ApiFixture.Json),
            HttpStatusCode.Created);
        var policy = await PublishedAsync(owner, "Lock", Lock900);
        await AssignAsync(owner, policy.Id, ToAll(window: window.Id));
        device.Settings.Values[Lock900.Key] = "60";

        var outside = (await device.SyncConfigurationAsync())!.Results.Single();
        Assert.Equal(SettingComplianceStatus.Drifted, outside.Status);
        Assert.Equal("60", device.Settings.Values[Lock900.Key]);

        using (fixture.Factory.Clock.Advance(TimeSpan.FromDays(2).Add(TimeSpan.FromHours(1))))
        {
            Assert.Equal(SettingComplianceStatus.Applied, (await device.SyncConfigurationAsync())!.Results.Single().Status);
        }

        Assert.Equal("900", device.Settings.Values[Lock900.Key]);
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/maintenance-windows/{window.Id}"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Versions_roll_back_and_removing_an_assignment_restores_the_original_value()
    {
        var (_, owner, device) = await SingleDeviceAsync();
        using var _owner = owner;
        device.Settings.Values[Lock900.Key] = "60";
        var policy = await PublishedAsync(owner, "Lock", Lock900);
        var assignment = await AssignAsync(owner, policy.Id, ToAll(removal: RemovalBehavior.Revert));
        await device.SyncConfigurationAsync();
        Assert.Equal("900", device.Settings.Values[Lock900.Key]);

        // v2 tightens the timeout.
        await HttpAssert.OkAsync<PolicyVersionDto>(await owner.PostAsJsonAsync(
            $"/api/v1/configuration-policies/{policy.Id}/versions", new CreatePolicyVersionRequest([new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 300)], "Tighten"), ApiFixture.Json),
            HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/configuration-policies/{policy.Id}/versions", new CreatePolicyVersionRequest(null, null), ApiFixture.Json), HttpStatusCode.Conflict);
        await ApproveAndPublishAsync(owner, owner, policy.Id, 2);
        await device.SyncConfigurationAsync();
        Assert.Equal("300", device.Settings.Values[Lock900.Key]);

        // Fast rollback: re-activate v1.
        var rolledBack = await HttpAssert.OkAsync<PolicyVersionDto>(await owner.PostAsync($"/api/v1/configuration-policies/{policy.Id}/versions/1/publish", null));
        Assert.Equal(PolicyVersionStatus.Published, rolledBack.Status);
        var detail = await HttpAssert.OkAsync<ConfigurationPolicyDto>(await owner.GetAsync($"/api/v1/configuration-policies/{policy.Id}"));
        Assert.Equal(PolicyVersionStatus.Superseded, detail.Versions.Single(v => v.Number == 2).Status);
        await device.SyncConfigurationAsync();
        Assert.Equal("900", device.Settings.Values[Lock900.Key]);

        // Restore copies an old version into a reviewable draft.
        var restored = await HttpAssert.OkAsync<PolicyVersionDto>(await owner.PostAsync($"/api/v1/configuration-policies/{policy.Id}/versions/2/restore", null), HttpStatusCode.Created);
        Assert.Equal((3, 2, PolicyVersionStatus.Draft), (restored.Number, restored.RestoredFromNumber!.Value, restored.Status));

        // Removing the assignment reverts the device to the value captured before Solvix first changed it.
        await owner.DeleteAsync($"/api/v1/configuration/assignments/{assignment.Id}");
        var document = (await device.Api.GetConfigurationAsync(null, CancellationToken.None))!;
        Assert.Equal([Lock900.Key], document.RevertKeys);
        var revert = (await device.SyncConfigurationAsync())!.Results.Single();
        Assert.Equal(SettingComplianceStatus.Reverted, revert.Status);
        Assert.Equal("60", device.Settings.Values[Lock900.Key]);
        Assert.Empty((await device.Api.GetConfigurationAsync(null, CancellationToken.None))!.RevertKeys);

        var view = await HttpAssert.OkAsync<DeviceConfigurationDto>(await owner.GetAsync($"/api/v1/configuration/devices/{device.DeviceId}"));
        Assert.Equal(SettingComplianceStatus.Reverted, Assert.Single(view.RemovedSettings).Status);
    }

    [Fact]
    public async Task Leave_behaviour_and_archiving_do_not_revert_unless_asked()
    {
        var (_, owner, device) = await SingleDeviceAsync();
        using var _owner = owner;
        device.Settings.Values[Lock900.Key] = "60";
        var keep = await PublishedAsync(owner, "Keep", Lock900);
        await AssignAsync(owner, keep.Id, ToAll(removal: RemovalBehavior.Leave));
        await device.SyncConfigurationAsync();

        await HttpAssert.OkAsync<ConfigurationPolicyDto>(await owner.PostAsync($"/api/v1/configuration-policies/{keep.Id}/archive", null));
        var document = (await device.Api.GetConfigurationAsync(null, CancellationToken.None))!;
        Assert.Empty(document.Settings);
        Assert.Empty(document.RevertKeys);
        await device.SyncConfigurationAsync();
        Assert.Equal("900", device.Settings.Values[Lock900.Key]);
    }

    [Fact]
    public async Task Baselines_create_reviewable_drafts()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var baselines = await HttpAssert.OkAsync<List<BaselineDto>>(await owner.GetAsync("/api/v1/configuration/baselines"));
        var security = baselines.Single(b => b.Id == "workstation-security");
        Assert.All(security.Settings, s => Assert.NotEqual(SettingRisk.Blocked, s.Risk));

        var policy = await HttpAssert.OkAsync<ConfigurationPolicyDto>(
            await owner.PostAsJsonAsync("/api/v1/configuration/baselines/policies", new CreateFromBaselineRequest("workstation-security", "Workstations", null), ApiFixture.Json),
            HttpStatusCode.Created);
        var draft = Assert.Single(policy.Versions);
        Assert.Equal(PolicyVersionStatus.Draft, draft.Status);
        Assert.Equal(security.Settings.Count, draft.Settings.Count);
        Assert.Equal(SettingRisk.Elevated, draft.MaxRisk);
    }
}
