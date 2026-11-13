namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Mdm.Application;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers.Android;

[Collection(ApiCollection.Name)]
public sealed class MobileDeviceManagementAndroidTests(ApiFixture fixture)
{
    private IReadOnlyList<(string Device, string[] Flags)> DeletionsOf(MdmWorkspace ws) =>
        Google.Deletions.Where(d => d.Device.StartsWith(ws.EnterpriseName + "/", StringComparison.Ordinal)).ToList();

    private FakeAndroidManagementApi Google => fixture.Factory.AndroidApi;

    private static readonly ManagementScenario FullyManaged = new(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario Byod = new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned);
    private static readonly ManagementScenario CompanyWorkProfile = new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.CompanyOwned);

    [Fact]
    public async Task Fully_managed_device_goes_through_enrollment_policy_compliance_and_approved_wipe()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        await MdmTestKit.BindAndroidAsync(ws);
        var enterprise = ws.EnterpriseName!;

        // --- catalogue + policy (Wi-Fi, VPN, password policy, restrictions, compliance rules, app control)
        var mail = await MdmTestKit.CreateAppAsync(ws.Owner, MobilePlatform.Android, "Mail", "com.example.mail");
        var vpnApp = await MdmTestKit.CreateAppAsync(ws.Owner, MobilePlatform.Android, "VPN Client", "org.example.vpn");
        var settings = new MobilePolicySettings
        {
            Password = new PasswordSettings { Quality = PasswordQuality.Alphanumeric, MinLength = 8, MaxFailedAttempts = 10, MaxInactivitySeconds = 300, ExpirationDays = 90 },
            WifiNetworks = [new WifiNetworkSettings { Ssid = "Corp-WiFi", Security = WifiSecurity.WpaPersonal, Passphrase = "s3cret-passphrase" }],
            Vpn = [new VpnSettings { Name = "Corp VPN", Type = VpnType.AlwaysOnApp, PackageName = "org.example.vpn", Lockdown = true }],
            Restrictions = new RestrictionSettings { DisableCamera = true, DisableUsbFileTransfer = true, DisableUnknownSources = true, DisableFactoryReset = true },
            Compliance = new ComplianceRequirements { MinimumOsVersion = "12", RequirePasscode = true, RequireEncryption = true, BlockCompromisedDevices = true },
            AppControl = AppControlMode.ApprovedAppsOnly,
        };
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "Corporate Android", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned, settings);

        // Secrets are write-only: never echoed back, only flagged as stored.
        var rawPolicy = await (await ws.Owner.GetAsync($"/api/v1/mobile-policies/{policy.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-passphrase", rawPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("enc:v1", rawPolicy, StringComparison.Ordinal);
        Assert.True(policy.Settings.WifiNetworks[0].PassphraseSet);
        Assert.Null(policy.Settings.WifiNetworks[0].Passphrase);

        await HttpAssert.OkAsync<PolicyDto>(await ws.Owner.PutAsJsonAsync($"/api/v1/mobile-policies/{policy.Id}/apps",
            new SetPolicyAppsRequest([new PolicyAppAssignment(mail.Id, AppInstallType.ForceInstalled, "{\"server\":\"imap.example.test\",\"port\":993}")]), ApiFixture.Json));

        // --- publish: translated and pushed to the (fake) Android Management API
        var published = await MdmTestKit.PublishAsync(ws.Owner, policy.Id);
        Assert.Equal(published.Policy.Version, published.Policy.PublishedVersion);
        Assert.Contains(published.Applied, a => a.StartsWith("wifi Corp-WiFi", StringComparison.Ordinal));
        Assert.Empty(published.Skipped);
        var googlePolicy = Google.Policies.Single(p => p.Key.StartsWith(enterprise, StringComparison.Ordinal)).Value;
        Assert.Equal("WHITELIST", (string?)googlePolicy["playStoreMode"]);
        Assert.Equal(8, (int?)googlePolicy["passwordPolicies"]![0]!["passwordMinimumLength"]);
        Assert.Equal("ALPHANUMERIC", (string?)googlePolicy["passwordPolicies"]![0]!["passwordQuality"]);
        Assert.Equal("s3cret-passphrase", (string?)googlePolicy["openNetworkConfiguration"]!["NetworkConfigurations"]![0]!["WiFi"]!["Passphrase"]);
        Assert.Equal("org.example.vpn", (string?)googlePolicy["alwaysOnVpnPackage"]!["packageName"]);
        Assert.True((bool?)googlePolicy["cameraDisabled"]);
        Assert.Equal("imap.example.test", (string?)googlePolicy["applications"]!.AsArray().Single(a => (string?)a!["packageName"] == "com.example.mail")!["managedConfiguration"]!["server"]);
        Assert.Equal("FORCE_INSTALLED", (string?)googlePolicy["applications"]!.AsArray().Single(a => (string?)a!["packageName"] == "org.example.vpn")!["installType"]);
        Assert.Equal(31, (int?)googlePolicy["minimumApiLevel"]);

        // --- enrollment: single-use token carrying the policy and our token id
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "Warehouse phones", FullyManaged, policy.Id);
        Assert.NotNull(enrollment.QrPayload);
        Assert.NotEmpty(enrollment.Instructions);
        var tokenBody = Google.EnrollmentTokens.Single(t => t.Key.StartsWith(enterprise, StringComparison.Ordinal)).Value;
        Assert.EndsWith(policy.Id.ToString("N"), (string?)tokenBody["policyName"], StringComparison.Ordinal);
        Assert.Equal(enrollment.Enrollment.Id.ToString("N"), (string?)tokenBody["additionalData"]);
        Assert.True((bool?)tokenBody["oneTimeOnly"]);
        Assert.Equal("PERSONAL_USAGE_DISALLOWED", (string?)tokenBody["allowPersonalUsage"]);

        // --- the device enrols on Google's side; Solvix discovers it
        var device = MdmTestKit.AndroidDevice(enterprise, "dev1", enrollment.Enrollment.Id.ToString("N"),
            policyName: (string?)Google.Policies.Keys.Single(k => k.StartsWith(enterprise, StringComparison.Ordinal)).ToString(),
            apps: [new AndroidAppReport("com.example.mail", "Mail", "1.0", "PLAY_STORE"), new AndroidAppReport("org.example.vpn", "VPN", "2.0", "PLAY_STORE")]);
        Google.Devices[device.Name] = device;
        var sync = await MdmTestKit.SyncAsync(ws.Owner);
        Assert.Equal(1, sync.Discovered);

        var listed = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));
        Assert.Equal(DeviceState.Enrolled, listed.State);
        Assert.Equal(ManagementMode.FullyManaged, listed.Mode);
        Assert.Equal(DeviceOwnership.CompanyOwned, listed.Ownership);
        Assert.Equal("SER-dev1", listed.SerialNumber);
        Assert.Equal(policy.Id, listed.PolicyId);
        Assert.Equal(published.Policy.PublishedVersion, listed.AppliedPolicyVersion);
        Assert.Equal(ComplianceState.Compliant, listed.Compliance);
        var used = Assert.Single((await HttpAssert.OkAsync<Solvix.SharedKernel.Results.PagedResult<EnrollmentDto>>(await ws.Owner.GetAsync("/api/v1/mobile-enrollments?includeInactive=true"))).Items);
        Assert.Equal(1, used.UseCount);
        Assert.False(used.IsUsable);

        // --- compliance monitoring: the device falls out of compliance and the fleet report shows why
        Google.Devices[device.Name] = device with { AndroidVersion = "11", DevicePosture = "AT_RISK" };
        await MdmTestKit.SyncAsync(ws.Owner);
        var detail = await MdmTestKit.GetDetailAsync(ws.Owner, listed.Id);
        Assert.Equal(ComplianceState.NonCompliant, detail.Device.Compliance);
        Assert.Contains(detail.Violations, v => v.Rule == "minimum_os_version");
        Assert.Contains(detail.Violations, v => v.Rule == "compromised_device");
        Assert.Contains(detail.Events, e => e.Type == DeviceEventType.ComplianceChanged);
        var summary = await HttpAssert.OkAsync<ComplianceSummaryDto>(await ws.Owner.GetAsync("/api/v1/mobile-compliance/summary"));
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.NonCompliant);
        Assert.Contains(summary.TopViolations, v => v.Rule == "compromised_device" && v.Devices == 1);
        Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner, "&compliance=NonCompliant"));
        Assert.Empty(await MdmTestKit.ListDevicesAsync(ws.Owner, "&compliance=Compliant"));

        // --- non-destructive command runs straight away
        var lockResponse = await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Lock);
        var lockCommand = await HttpAssert.OkAsync<CommandDto>(lockResponse, HttpStatusCode.Created);
        Assert.Equal(CommandState.Sent, lockCommand.State);
        Assert.Contains(Google.Commands, c => c.Device == device.Name && (string?)c.Command["type"] == "LOCK");
        await MdmTestKit.SyncAsync(ws.Owner);
        Assert.Equal(CommandState.Acknowledged, (await HttpAssert.OkAsync<CommandDto>(await ws.Owner.GetAsync($"/api/v1/mobile-commands/{lockCommand.Id}"))).State);

        // --- destructive command: needs a reason, waits for a different administrator
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Wipe), HttpStatusCode.BadRequest, "validation_failed");
        var wipe = await HttpAssert.OkAsync<CommandDto>(
            await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Wipe, "Device reported stolen"), HttpStatusCode.Created);
        Assert.Equal(CommandState.PendingApproval, wipe.State);
        Assert.True(wipe.RequiresApproval);
        Assert.Empty(DeletionsOf(ws));
        Assert.Equal(DeviceState.Enrolled, (await MdmTestKit.GetDeviceAsync(ws.Owner, listed.Id)).State);

        await HttpAssert.ProblemAsync(await ws.Owner.PostAsJsonAsync($"/api/v1/mobile-commands/{wipe.Id}/approve", new CommandDecisionRequest("self"), ApiFixture.Json), HttpStatusCode.Forbidden, "forbidden");
        Assert.Empty(DeletionsOf(ws));

        var approved = await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{wipe.Id}/approve", new CommandDecisionRequest("confirmed"), ApiFixture.Json));
        Assert.Equal(CommandState.Acknowledged, approved.State);
        Assert.Equal(ws.SecondAdminUserId, approved.DecidedByUserId);
        var deletion = Assert.Single(DeletionsOf(ws));
        Assert.Equal(device.Name, deletion.Device);

        var wiped = await MdmTestKit.GetDetailAsync(ws.Owner, listed.Id);
        Assert.Equal(DeviceState.Wiped, wiped.Device.State);
        Assert.Contains(wiped.Events, e => e.Type == DeviceEventType.Unenrolled && e.Summary.Contains("wiped", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wiped.Events, e => e.Type == DeviceEventType.CommandApproved);
        // A wiped device is out of the active fleet and accepts no further commands.
        Assert.Equal(0, (await HttpAssert.OkAsync<ComplianceSummaryDto>(await ws.Owner.GetAsync("/api/v1/mobile-compliance/summary"))).Total);
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Lock), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Byod_work_profile_separates_personal_data_and_only_allows_selective_removal()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        await MdmTestKit.BindAndroidAsync(ws);
        var enterprise = ws.EnterpriseName!;

        // A kiosk makes no sense on a personal device and is rejected.
        await HttpAssert.ProblemAsync(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-policies",
                new CreatePolicyRequest("Bad BYOD", null, MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned,
                    new MobilePolicySettings { Kiosk = new KioskSettings { AllowedPackages = ["com.example.kiosk"] } }), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // Invalid scenario combinations are rejected too.
        await HttpAssert.ProblemAsync(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-policies",
                new CreatePolicyRequest("Bad scenario", null, MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.PersonallyOwned, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "BYOD", MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned,
            new MobilePolicySettings
            {
                Password = new PasswordSettings { Quality = PasswordQuality.NumericComplex, MinLength = 6 },
                Restrictions = new RestrictionSettings { DisableCamera = true, DisableUsbFileTransfer = true, DisableFactoryReset = true },
            });
        var published = await MdmTestKit.PublishAsync(ws.Owner, policy.Id);
        Assert.Contains(published.Skipped, s => s.StartsWith("disableUsbFileTransfer", StringComparison.Ordinal));
        Assert.Contains(published.Skipped, s => s.StartsWith("disableFactoryReset", StringComparison.Ordinal));
        Assert.Contains(published.Applied, a => a.Contains("always enforced on personally owned", StringComparison.Ordinal));
        Assert.Contains(published.Applied, a => a.Contains("work profile challenge", StringComparison.Ordinal));
        var googlePolicy = Google.Policies.Single(p => p.Key.StartsWith(enterprise, StringComparison.Ordinal)).Value;
        Assert.Equal("SCOPE_PROFILE", (string?)googlePolicy["passwordPolicies"]![0]!["passwordScope"]);
        Assert.Equal("DATA_SHARING_FROM_WORK_TO_PERSONAL_DISALLOWED", (string?)googlePolicy["crossProfilePolicies"]!["crossProfileDataSharing"]);
        Assert.Null(googlePolicy["deviceConnectivityManagement"]);
        Assert.Null(googlePolicy["factoryResetDisabled"]);

        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "BYOD users", Byod, policy.Id);
        var tokenBody = Google.EnrollmentTokens.Single(t => t.Key.StartsWith(enterprise, StringComparison.Ordinal)).Value;
        Assert.Equal("PERSONAL_USAGE_ALLOWED", (string?)tokenBody["allowPersonalUsage"]);
        Assert.Contains(enrollment.Instructions, i => i.Contains("Personal apps and data stay private", StringComparison.Ordinal));

        var device = MdmTestKit.AndroidDevice(enterprise, "byod1", enrollment.Enrollment.Id.ToString("N"), "PROFILE_OWNER", "PERSONALLY_OWNED", apps:
            [new AndroidAppReport("com.example.work", "Work", "1", "PLAY_STORE")]);
        Google.Devices[device.Name] = device;
        await MdmTestKit.SyncAsync(ws.Owner);

        var listed = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));
        Assert.Equal(DeviceOwnership.PersonallyOwned, listed.Ownership);
        Assert.Equal(ManagementMode.WorkProfile, listed.Mode);
        // Even though the provider reported a serial number, it is not stored for a personal device.
        Assert.Null(listed.SerialNumber);

        var detail = await MdmTestKit.GetDetailAsync(ws.Owner, listed.Id);
        Assert.False(detail.Capabilities.CanFactoryReset);
        Assert.False(detail.Capabilities.CanReadDeviceIdentifiers);
        Assert.True(detail.Capabilities.CanRemoveCorporateDataOnly);
        Assert.DoesNotContain(DeviceCommandType.Wipe, detail.Capabilities.Commands);

        // Operations that would reach personal data are refused outright.
        var wipe = await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Wipe, "no"), HttpStatusCode.UnprocessableEntity, "unsupported_operation");
        Assert.Contains("personally owned", wipe.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.ResetPasscode), HttpStatusCode.UnprocessableEntity, "unsupported_operation");
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Reboot), HttpStatusCode.UnprocessableEntity, "unsupported_operation");
        Assert.DoesNotContain(Google.Commands, c => c.Device == device.Name);

        // Selective removal: retire, with approval.
        var retire = await HttpAssert.OkAsync<CommandDto>(
            await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Retire, "Employee left"), HttpStatusCode.Created);
        Assert.Equal(CommandState.PendingApproval, retire.State);
        await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{retire.Id}/approve", new CommandDecisionRequest(null), ApiFixture.Json));
        var retired = await MdmTestKit.GetDetailAsync(ws.Owner, listed.Id);
        Assert.Equal(DeviceState.Retired, retired.Device.State);
        Assert.Contains(retired.Events, e => e.Type == DeviceEventType.Unenrolled && e.Summary.Contains("personal data untouched", StringComparison.Ordinal));
        Assert.Single(DeletionsOf(ws));
    }

    [Fact]
    public async Task Rejected_destructive_command_never_reaches_the_platform_and_unsent_work_can_be_cancelled()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        await MdmTestKit.BindAndroidAsync(ws);
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "P", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "E", FullyManaged, policy.Id);
        Google.Devices[$"{ws.EnterpriseName}/devices/rej1"] = MdmTestKit.AndroidDevice(ws.EnterpriseName!, "rej1", enrollment.Enrollment.Id.ToString("N"));
        await MdmTestKit.SyncAsync(ws.Owner);
        var device = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));

        var wipe = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Wipe, "test"), HttpStatusCode.Created);
        // A second destructive request while one is pending is a conflict.
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Retire, "again"), HttpStatusCode.Conflict);

        var rejected = await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{wipe.Id}/reject", new CommandDecisionRequest("not justified"), ApiFixture.Json));
        Assert.Equal(CommandState.Rejected, rejected.State);
        Assert.Empty(DeletionsOf(ws));
        Assert.Equal(DeviceState.Enrolled, (await MdmTestKit.GetDeviceAsync(ws.Owner, device.Id)).State);

        // A rejected command cannot be approved afterwards.
        await HttpAssert.ProblemAsync(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{wipe.Id}/approve", new CommandDecisionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict);

        var next = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Wipe, "again"), HttpStatusCode.Created);
        var cancelled = await HttpAssert.OkAsync<CommandDto>(await ws.Owner.PostAsync($"/api/v1/mobile-commands/{next.Id}/cancel", null));
        Assert.Equal(CommandState.Cancelled, cancelled.State);
        Assert.Empty(DeletionsOf(ws));
    }

    [Fact]
    public async Task Company_owned_work_profile_retire_relinquishes_ownership_and_completes_when_the_operation_finishes()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        await MdmTestKit.BindAndroidAsync(ws);
        var enterprise = ws.EnterpriseName!;
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "COPE", MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.CompanyOwned);
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "COPE", CompanyWorkProfile, policy.Id);
        var device = MdmTestKit.AndroidDevice(enterprise, "cope1", enrollment.Enrollment.Id.ToString("N"), "PROFILE_OWNER", "COMPANY_OWNED");
        Google.Devices[device.Name] = device;
        await MdmTestKit.SyncAsync(ws.Owner);
        var listed = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));
        Assert.Equal("SER-cope1", listed.SerialNumber); // company hardware: identifiers are available

        // Full wipe is not offered on a work profile, even for company hardware.
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Wipe, "x"), HttpStatusCode.UnprocessableEntity, "unsupported_operation");

        Google.CompleteOperationsImmediately = false;
        try
        {
            var retire = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Retire, "Handing device back"), HttpStatusCode.Created);
            await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{retire.Id}/approve", new CommandDecisionRequest(null), ApiFixture.Json));
            Assert.Contains(Google.Commands, c => c.Device == device.Name && (string?)c.Command["type"] == "RELINQUISH_OWNERSHIP");
            Assert.Empty(DeletionsOf(ws)); // personal profile must not be deleted
            Assert.Equal(DeviceState.RemovalPending, (await MdmTestKit.GetDeviceAsync(ws.Owner, listed.Id)).State);

            // Google finishes the operation: the next sync completes the removal.
            foreach (var key in Google.Operations.Keys.ToList())
            {
                Google.Operations[key] = new AndroidOperationInfo(true, null);
            }

            await MdmTestKit.SyncAsync(ws.Owner);
            Assert.Equal(DeviceState.Retired, (await MdmTestKit.GetDeviceAsync(ws.Owner, listed.Id)).State);
        }
        finally
        {
            Google.CompleteOperationsImmediately = true;
        }
    }

    [Fact]
    public async Task Device_removed_on_the_platform_is_unenrolled_by_sync_and_failed_operations_are_recorded()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        await MdmTestKit.BindAndroidAsync(ws);
        var enterprise = ws.EnterpriseName!;
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "P", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "E", FullyManaged, policy.Id);
        var device = MdmTestKit.AndroidDevice(enterprise, "gone1", enrollment.Enrollment.Id.ToString("N"));
        Google.Devices[device.Name] = device;
        await MdmTestKit.SyncAsync(ws.Owner);
        var listed = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));

        // A command whose operation fails on Google's side is marked failed.
        Google.CompleteOperationsImmediately = false;
        try
        {
            var reboot = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, listed.Id, DeviceCommandType.Reboot), HttpStatusCode.Created);
            foreach (var key in Google.Operations.Keys.ToList())
            {
                Google.Operations[key] = new AndroidOperationInfo(true, "Device is offline");
            }

            await MdmTestKit.SyncAsync(ws.Owner);
            var failed = await HttpAssert.OkAsync<CommandDto>(await ws.Owner.GetAsync($"/api/v1/mobile-commands/{reboot.Id}"));
            Assert.Equal(CommandState.Failed, failed.State);
            Assert.Equal("Device is offline", failed.Error);
        }
        finally
        {
            Google.CompleteOperationsImmediately = true;
        }

        // The device is deleted outside Solvix (for example from the Google admin console).
        Google.Devices[device.Name] = device with { State = "DELETED" };
        var result = await MdmTestKit.SyncAsync(ws.Owner);
        Assert.Equal(1, result.Removed);
        Assert.Equal(DeviceState.Wiped, (await MdmTestKit.GetDeviceAsync(ws.Owner, listed.Id)).State);
    }

    [Fact]
    public async Task Enrollment_and_policy_rules_are_enforced_and_unbound_workspaces_are_told_so()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "P", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);

        // No enterprise bound yet.
        await HttpAssert.ProblemAsync(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enrollments", new CreateEnrollmentRequest("E", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned, policy.Id), ApiFixture.Json),
            HttpStatusCode.Conflict);
        await MdmTestKit.BindAndroidAsync(ws);
        // Binding twice is a conflict.
        await HttpAssert.ProblemAsync(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enterprise/android/signup", new StartSignupRequest("https://app.example.test/cb"), ApiFixture.Json), HttpStatusCode.Conflict);

        // Android tokens are single-use.
        await HttpAssert.ProblemAsync(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enrollments", new CreateEnrollmentRequest("E", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned, policy.Id, 24, 5), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        // A policy only fits the scenario it was written for.
        await HttpAssert.ProblemAsync(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enrollments", new CreateEnrollmentRequest("E", MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned, policy.Id), ApiFixture.Json),
            HttpStatusCode.Conflict);

        // Revoked tokens are no longer usable.
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "E", FullyManaged, policy.Id);
        var revoked = await HttpAssert.OkAsync<EnrollmentDto>(await ws.Owner.PostAsync($"/api/v1/mobile-enrollments/{enrollment.Enrollment.Id}/revoke", null));
        Assert.False(revoked.IsUsable);
        Assert.NotNull(revoked.RevokedAtUtc);

        // Policies used for enrollment are kept; unused ones can be deleted.
        await HttpAssert.ProblemAsync(await ws.Owner.DeleteAsync($"/api/v1/mobile-policies/{policy.Id}"), HttpStatusCode.Conflict);
        var spare = await MdmTestKit.CreatePolicyAsync(ws.Owner, "Spare", MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.CompanyOwned);
        Assert.Equal(HttpStatusCode.NoContent, (await ws.Owner.DeleteAsync($"/api/v1/mobile-policies/{spare.Id}")).StatusCode);
    }

    [Fact]
    public async Task Dedicated_kiosk_policy_translates_to_kiosk_configuration()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        await MdmTestKit.BindAndroidAsync(ws);
        var kioskApp = await MdmTestKit.CreateAppAsync(ws.Owner, MobilePlatform.Android, "POS", "com.example.pos");
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "Kiosk", MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.CompanyOwned,
            new MobilePolicySettings { Kiosk = new KioskSettings { AllowedPackages = ["com.example.pos"] } });
        await HttpAssert.OkAsync<PolicyDto>(await ws.Owner.PutAsJsonAsync($"/api/v1/mobile-policies/{policy.Id}/apps",
            new SetPolicyAppsRequest([new PolicyAppAssignment(kioskApp.Id, AppInstallType.Kiosk, null)]), ApiFixture.Json));
        var published = await MdmTestKit.PublishAsync(ws.Owner, policy.Id);
        Assert.Contains("kiosk mode", published.Applied);
        var google = Google.Policies.Single(p => p.Key.StartsWith(ws.EnterpriseName!, StringComparison.Ordinal)).Value;
        Assert.Equal("KIOSK", (string?)google["applications"]!.AsArray().Single()!["installType"]);
        Assert.NotNull(google["kioskCustomization"]);

        // Kiosk apps cannot be assigned to a non-dedicated policy.
        var fm = await MdmTestKit.CreatePolicyAsync(ws.Owner, "FM", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
        await HttpAssert.ProblemAsync(await ws.Owner.PutAsJsonAsync($"/api/v1/mobile-policies/{fm.Id}/apps",
            new SetPolicyAppsRequest([new PolicyAppAssignment(kioskApp.Id, AppInstallType.Kiosk, null)]), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Mobile_data_is_isolated_between_tenants_and_members_cannot_administer()
    {
        var a = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var b = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var policy = await MdmTestKit.CreatePolicyAsync(a.Owner, "A-only", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
        var app = await MdmTestKit.CreateAppAsync(a.Owner, MobilePlatform.Android, "A app", "com.a.app");

        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/mobile-policies/{policy.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await b.Owner.PostAsync($"/api/v1/mobile-policies/{policy.Id}/publish", null), HttpStatusCode.NotFound);
        Assert.Empty((await HttpAssert.OkAsync<Solvix.SharedKernel.Results.PagedResult<PolicyDto>>(await b.Owner.GetAsync("/api/v1/mobile-policies"))).Items);
        Assert.Empty((await HttpAssert.OkAsync<Solvix.SharedKernel.Results.PagedResult<AppDto>>(await b.Owner.GetAsync("/api/v1/mobile-apps"))).Items);
        // The same package id can exist in another tenant's catalogue.
        await MdmTestKit.CreateAppAsync(b.Owner, MobilePlatform.Android, "B app", app.Identifier);
        await HttpAssert.ProblemAsync(await b.Owner.PutAsJsonAsync($"/api/v1/mobile-policies/{policy.Id}/apps", new SetPolicyAppsRequest([]), ApiFixture.Json), HttpStatusCode.NotFound);

        // A plain member can read but not administer.
        var memberEmail = $"member-{Guid.NewGuid():N}@example.test";
        await HttpAssert.OkAsync<Solvix.Modules.Identity.Application.Members.MemberDto>(
            await a.Owner.PostAsJsonAsync("/api/v1/tenant/members",
                new Solvix.Modules.Identity.Application.Members.AddMemberRequest(memberEmail, "Member", MdmTestKit.Password, Solvix.SharedKernel.Identity.TenantRole.Member), ApiFixture.Json),
            HttpStatusCode.Created);
        using var member = fixture.CreateClient((await fixture.LoginAsync(memberEmail, MdmTestKit.Password, a.Session.Slug)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/mobile-policies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/v1/mobile-policies/{policy.Id}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/mobile-enrollments",
            new CreateEnrollmentRequest("E", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned, policy.Id), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/v1/mobile-devices/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Capability_matrix_is_published_and_never_offers_wipe_for_personal_devices()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var matrix = await HttpAssert.OkAsync<List<CapabilitiesDto>>(await ws.Owner.GetAsync("/api/v1/mobile-capabilities"));
        Assert.Equal(7, matrix.Count);
        Assert.All(matrix.Where(c => c.Ownership == DeviceOwnership.PersonallyOwned), c =>
        {
            Assert.DoesNotContain(DeviceCommandType.Wipe, c.Commands);
            Assert.DoesNotContain(DeviceCommandType.ResetPasscode, c.Commands);
            Assert.False(c.CanReadDeviceIdentifiers);
            Assert.NotEmpty(c.Limitations);
        });
        Assert.All(matrix, c => Assert.Equal(new[] { DeviceCommandType.Retire }.Concat(c.Commands.Contains(DeviceCommandType.Wipe) ? [DeviceCommandType.Wipe] : []).Order().ToList(), c.CommandsRequiringApproval.Order().ToList()));
    }

    [Fact]
    public async Task Background_worker_syncs_every_tenant_inside_its_own_tenant_scope()
    {
        var a = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var b = await MdmTestKit.CreateWorkspaceAsync(fixture);
        foreach (var ws in new[] { a, b })
        {
            await MdmTestKit.BindAndroidAsync(ws);
            var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "P", MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
            var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "E", FullyManaged, policy.Id);
            Google.Devices[$"{ws.EnterpriseName}/devices/w1"] = MdmTestKit.AndroidDevice(ws.EnterpriseName!, "w1", enrollment.Enrollment.Id.ToString("N"));
        }

        var worker = fixture.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Solvix.Modules.Mdm.Application.Maintenance.MdmSyncWorker>().Single();
        await worker.RunOnceAsync(CancellationToken.None);

        // Each tenant discovered exactly its own device: no sync pass leaked across tenants.
        var devicesA = await MdmTestKit.ListDevicesAsync(a.Owner);
        var devicesB = await MdmTestKit.ListDevicesAsync(b.Owner);
        Assert.Single(devicesA);
        Assert.Single(devicesB);
        Assert.NotEqual(devicesA[0].Id, devicesB[0].Id);
        // The worker also ran compliance evaluation for both tenants.
        Assert.NotNull(devicesA[0].ComplianceEvaluatedAtUtc);
        Assert.NotNull(devicesB[0].ComplianceEvaluatedAtUtc);
    }
}
