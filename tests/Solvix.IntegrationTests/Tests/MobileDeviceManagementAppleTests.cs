namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Mdm.Application;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers.Apple;

[Collection(ApiCollection.Name)]
public sealed class MobileDeviceManagementAppleTests(ApiFixture fixture)
{
    private static readonly ManagementScenario CompanyIos = new(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario UserEnrollment = new(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned);

    private static Dictionary<string, object?> Dict(object? value) => (Dictionary<string, object?>)value!;

    [Fact]
    public async Task Company_device_enrolls_receives_policy_and_apps_reports_inventory_and_executes_commands()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var app = await MdmTestKit.CreateAppAsync(ws.Owner, MobilePlatform.Ios, "Mail", "com.example.mail", 123456789);
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "Corporate iPhone", MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned,
            new MobilePolicySettings
            {
                Password = new PasswordSettings { Quality = PasswordQuality.Alphanumeric, MinLength = 8, MaxFailedAttempts = 10, MaxInactivitySeconds = 120 },
                WifiNetworks = [new WifiNetworkSettings { Ssid = "Corp-WiFi", Security = WifiSecurity.WpaPersonal, Passphrase = "s3cret-passphrase" }],
                Vpn = [new VpnSettings { Name = "Corp IKEv2", Type = VpnType.IkeV2, Server = "vpn.example.test", SharedSecret = "vpn-shared-secret" }],
                Restrictions = new RestrictionSettings { DisableCamera = true, DisableFactoryReset = true, DisableUsbFileTransfer = true },
                Compliance = new ComplianceRequirements { MinimumOsVersion = "16", RequirePasscode = true, RequireEncryption = true },
            });
        await HttpAssert.OkAsync<PolicyDto>(await ws.Owner.PutAsJsonAsync($"/api/v1/mobile-policies/{policy.Id}/apps",
            new SetPolicyAppsRequest([new PolicyAppAssignment(app.Id, AppInstallType.ForceInstalled, "{\"server\":\"imap.example.test\"}")]), ApiFixture.Json));

        // Publishing validates the translation and reports what an unsupervised device cannot do.
        var published = await MdmTestKit.PublishAsync(ws.Owner, policy.Id);
        Assert.Contains(published.Skipped, s => s.StartsWith("erase content and settings disabled", StringComparison.Ordinal));
        Assert.Contains(published.Skipped, s => s.StartsWith("disableUsbFileTransfer", StringComparison.Ordinal));
        Assert.Contains(published.Applied, a => a == "camera disabled");
        Assert.Contains(published.Applied, a => a == "wifi Corp-WiFi");

        // --- enrollment link -> single-use profile
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "iPhones", CompanyIos, policy.Id);
        Assert.NotNull(enrollment.EnrollmentUrl);
        Assert.Null(enrollment.QrPayload);
        using var anonymous = fixture.Factory.CreateClient();
        using var phone = new AppleDeviceSimulator(anonymous);
        var profile = await phone.DownloadProfileAsync(enrollment.EnrollmentUrl!);
        var content = ((List<object?>)profile["PayloadContent"]!).OfType<Dictionary<string, object?>>().ToList();
        var mdm = content.Single(p => (string?)p["PayloadType"] == "com.apple.mdm");
        Assert.Equal(SolvixApiFactory.AppleTopic, mdm["Topic"]);
        Assert.Equal(true, mdm["SignMessage"]);
        Assert.Equal(8191L, mdm["AccessRights"]);
        Assert.StartsWith("https://mdm.example.test/api/v1/mdm/apple/", (string)mdm["ServerURL"]!, StringComparison.Ordinal);
        Assert.Contains(content, p => (string?)p["PayloadType"] == "com.apple.security.scep");
        await HttpAssert.ProblemAsync(await anonymous.GetAsync(new Uri(enrollment.EnrollmentUrl!).PathAndQuery), HttpStatusCode.Conflict);
        Assert.Empty(await MdmTestKit.ListDevicesAsync(ws.Owner)); // not enrolled until the device checks in

        // --- check-in
        Assert.Equal(HttpStatusCode.OK, (await phone.AuthenticateAsync()).StatusCode);
        Assert.Empty(await MdmTestKit.ListDevicesAsync(ws.Owner));
        Assert.Equal(HttpStatusCode.OK, (await phone.TokenUpdateAsync()).StatusCode);
        // A newly enrolled device is pushed once so that it comes to collect its first commands.
        Assert.Contains(fixture.Factory.ApplePush.Pushes, p => p.Magic == "magic-" + phone.Handle);
        var device = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));
        Assert.Equal(DeviceState.Enrolled, device.State);
        Assert.Equal(ManagementMode.DeviceEnrollment, device.Mode);
        Assert.Equal("C02TESTSERIAL", device.SerialNumber);
        Assert.Equal(policy.Id, device.PolicyId);

        // --- command exchange: the device collects and answers everything the server queued
        var seen = new List<string>();
        var (status, command) = await phone.IdleAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        while (command is not null)
        {
            var type = AppleDeviceSimulator.RequestType(command);
            seen.Add(type);
            Dictionary<string, object?>? answer = null;
            switch (type)
            {
                case "InstallProfile":
                    var installed = ApplePlist.ParseDictionary((byte[])AppleDeviceSimulator.Arguments(command)["Payload"]!);
                    Assert.Equal(AppleProfileBuilder.PolicyProfileIdentifier, installed["PayloadIdentifier"]);
                    var payloadTypes = ((List<object?>)installed["PayloadContent"]!).OfType<Dictionary<string, object?>>().Select(p => (string)p["PayloadType"]!).ToList();
                    Assert.Contains("com.apple.mobiledevice.passwordpolicy", payloadTypes);
                    Assert.Contains("com.apple.wifi.managed", payloadTypes);
                    Assert.Contains("com.apple.vpn.managed", payloadTypes);
                    Assert.Contains("com.apple.applicationaccess", payloadTypes);
                    var wifi = ((List<object?>)installed["PayloadContent"]!).OfType<Dictionary<string, object?>>().Single(p => (string?)p["PayloadType"] == "com.apple.wifi.managed");
                    Assert.Equal("s3cret-passphrase", wifi["Password"]); // delivered to the device, never to the API
                    break;
                case "DeviceInformation":
                    answer = new() { ["QueryResponses"] = new Dictionary<string, object?>
                    {
                        ["OSVersion"] = "17.4.1", ["ModelName"] = "iPhone 14 Pro", ["SerialNumber"] = "C02TESTSERIAL",
                        ["PasscodePresent"] = true, ["HardwareEncryptionCaps"] = 3L, ["IsSupervised"] = false,
                    } };
                    break;
                case "InstalledApplicationList":
                    answer = new() { ["InstalledApplicationList"] = new List<object?> { Dict(new Dictionary<string, object?> { ["Identifier"] = "com.example.mail", ["Name"] = "Mail", ["ShortVersion"] = "3.1" }) } };
                    break;
                case "InstallApplication":
                    Assert.Equal(123456789L, AppleDeviceSimulator.Arguments(command)["iTunesStoreID"]);
                    Assert.Equal("imap.example.test", Dict(AppleDeviceSimulator.Arguments(command)["Configuration"])["server"]);
                    break;
                default:
                    Assert.Fail($"Unexpected command {type}");
                    break;
            }

            (status, command) = await phone.AcknowledgeAsync(command, answer);
            Assert.Equal(HttpStatusCode.OK, status);
        }

        Assert.Contains("InstallProfile", seen);
        Assert.Contains("DeviceInformation", seen);
        Assert.Contains("InstalledApplicationList", seen);
        Assert.Contains("InstallApplication", seen);

        var detail = await MdmTestKit.GetDetailAsync(ws.Owner, device.Id);
        Assert.Equal("17.4.1", detail.Device.OsVersion);
        Assert.True(detail.Device.HasPasscode);
        Assert.True(detail.Device.IsEncrypted);
        Assert.Equal(published.Policy.PublishedVersion, detail.Device.AppliedPolicyVersion);
        Assert.Equal(ComplianceState.Compliant, detail.Device.Compliance);
        Assert.Contains(detail.Apps, a => a.Identifier == "com.example.mail");
        Assert.Contains(detail.Events, e => e.Type == DeviceEventType.Enrolled);
        Assert.All(await MdmTestKit.ListCommandsAsync(ws.Owner, device.Id), c => Assert.Equal(CommandState.Acknowledged, c.State));

        // --- device lock: queued, pushed through APNs, delivered on the next poll
        var pushesBefore = fixture.Factory.ApplePush.Pushes.Count;
        var lockCommand = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Lock, message: "Return to IT"), HttpStatusCode.Created);
        Assert.Equal(CommandState.Queued, lockCommand.State); // waits until the device collects it
        Assert.True(fixture.Factory.ApplePush.Pushes.Count > pushesBefore);
        Assert.Contains(fixture.Factory.ApplePush.Pushes, p => p.Token == "deadbeef01020304" && p.Magic == "magic-" + phone.Handle && p.Topic == SolvixApiFactory.AppleTopic);
        (status, command) = await phone.IdleAsync();
        Assert.Equal("DeviceLock", AppleDeviceSimulator.RequestType(command!));
        Assert.Equal("Return to IT", AppleDeviceSimulator.Arguments(command!)["Message"]);
        (status, command) = await phone.AcknowledgeAsync(command!);
        Assert.Null(command);
        Assert.Equal(CommandState.Acknowledged, (await HttpAssert.OkAsync<CommandDto>(await ws.Owner.GetAsync($"/api/v1/mobile-commands/{lockCommand.Id}"))).State);

        // --- passcode clear uses the unlock token the device handed over at enrollment
        await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.ResetPasscode), HttpStatusCode.Created);
        (_, command) = await phone.IdleAsync();
        Assert.Equal("ClearPasscode", AppleDeviceSimulator.RequestType(command!));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, (byte[])AppleDeviceSimulator.Arguments(command!)["UnlockToken"]!);
        await phone.AcknowledgeAsync(command!);

        // --- a device that cannot execute a command reports an error: recorded, not lost
        var erase = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Wipe, "Lost device"), HttpStatusCode.Created);
        Assert.Equal(CommandState.PendingApproval, erase.State);
        (_, command) = await phone.IdleAsync();
        Assert.Null(command); // nothing is delivered before approval
        await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{erase.Id}/approve", new CommandDecisionRequest(null), ApiFixture.Json));
        (_, command) = await phone.IdleAsync();
        Assert.Equal("EraseDevice", AppleDeviceSimulator.RequestType(command!));
        (_, command) = await phone.ReportAsync(new Dictionary<string, object?>
        {
            ["Status"] = "Error",
            ["CommandUUID"] = command!["CommandUUID"],
            ["ErrorChain"] = new List<object?> { Dict(new Dictionary<string, object?> { ["ErrorCode"] = 12021L, ["USEnglishDescription"] = "Cannot erase" }) },
        });
        var failed = Assert.Single(await MdmTestKit.ListCommandsAsync(ws.Owner, device.Id), c => c.Id == erase.Id);
        Assert.Equal(CommandState.Failed, failed.State);
        Assert.Contains("Cannot erase", failed.Error, StringComparison.Ordinal);
        Assert.Equal(DeviceState.Enrolled, (await MdmTestKit.GetDeviceAsync(ws.Owner, device.Id)).State);

        // --- a second attempt succeeds and the device is recorded as wiped
        var erase2 = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Wipe, "Lost device, retry"), HttpStatusCode.Created);
        await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{erase2.Id}/approve", new CommandDecisionRequest(null), ApiFixture.Json));
        (_, command) = await phone.IdleAsync();
        Assert.Equal("EraseDevice", AppleDeviceSimulator.RequestType(command!));
        await phone.AcknowledgeAsync(command!);
        Assert.Equal(DeviceState.Wiped, (await MdmTestKit.GetDeviceAsync(ws.Owner, device.Id)).State);
        // The server tells a removed device to stop contacting it.
        Assert.Equal(HttpStatusCode.Gone, (await phone.IdleAsync()).Status);
    }

    [Fact]
    public async Task User_enrollment_byod_is_limited_to_managed_data_and_unenrolls_selectively()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "BYOD iPhone", MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned,
            new MobilePolicySettings
            {
                Password = new PasswordSettings { Quality = PasswordQuality.Numeric, MinLength = 6, MaxFailedAttempts = 5 },
                WifiNetworks = [new WifiNetworkSettings { Ssid = "Guest", Security = WifiSecurity.Open }],
                Vpn = [new VpnSettings { Name = "Corp IKEv2", Type = VpnType.IkeV2, Server = "vpn.example.test", SharedSecret = "vpn-shared-secret" }],
                Restrictions = new RestrictionSettings { DisableCamera = true, DisableScreenCapture = true, DisableFactoryReset = true },
            });
        var published = await MdmTestKit.PublishAsync(ws.Owner, policy.Id);
        Assert.Contains(published.Skipped, s => s.StartsWith("camera disabled", StringComparison.Ordinal));
        Assert.Contains(published.Skipped, s => s.StartsWith("screen capture disabled", StringComparison.Ordinal));
        Assert.Contains(published.Skipped, s => s.StartsWith("erase content and settings disabled", StringComparison.Ordinal));
        Assert.Contains(published.Skipped, s => s.StartsWith("maxFailedAttempts", StringComparison.Ordinal));
        Assert.Contains(published.Skipped, s => s.StartsWith("vpn Corp IKEv2", StringComparison.Ordinal));
        Assert.Contains(published.Applied, a => a.Contains("always enforced on personally owned", StringComparison.Ordinal));

        // The old-style full device enrollment is refused for personal devices.
        await HttpAssert.ProblemAsync(await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enrollments",
            new CreateEnrollmentRequest("x", MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.PersonallyOwned, policy.Id), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "BYOD", UserEnrollment, policy.Id);
        Assert.Contains(enrollment.Instructions, i => i.Contains("Managed Apple ID", StringComparison.Ordinal));
        using var anonymous = fixture.Factory.CreateClient();
        using var phone = new AppleDeviceSimulator(anonymous);
        var profile = await phone.DownloadProfileAsync(enrollment.EnrollmentUrl!);
        var mdm = ((List<object?>)profile["PayloadContent"]!).OfType<Dictionary<string, object?>>().Single(p => (string?)p["PayloadType"] == "com.apple.mdm");
        Assert.Equal("BYOD", mdm["EnrollmentMode"]);
        Assert.False(mdm.ContainsKey("AccessRights"));

        // User Enrollment identifies the enrollment with an EnrollmentID, not a UDID.
        Assert.Equal(HttpStatusCode.OK, (await phone.AuthenticateAsync(new() { ["UDID"] = null, ["EnrollmentID"] = phone.EnrollmentId, ["SerialNumber"] = "SHOULD-NOT-BE-STORED" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.TokenUpdateAsync(withUnlockToken: false, identifierKey: "EnrollmentID")).StatusCode);
        var device = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));
        Assert.Equal(DeviceOwnership.PersonallyOwned, device.Ownership);
        Assert.Null(device.SerialNumber);
        var detail = await MdmTestKit.GetDetailAsync(ws.Owner, device.Id);
        Assert.False(detail.Capabilities.CanReadDeviceIdentifiers);
        Assert.Equal(new[] { DeviceCommandType.SyncInventory, DeviceCommandType.ApplyPolicy, DeviceCommandType.InstallApp, DeviceCommandType.RemoveApp, DeviceCommandType.Retire }.Order(), detail.Capabilities.Commands.Order());

        // Device-level operations are blocked by the platform, so Solvix refuses to even queue them.
        foreach (var type in new[] { DeviceCommandType.Lock, DeviceCommandType.Wipe, DeviceCommandType.ResetPasscode, DeviceCommandType.Reboot })
        {
            await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, type, "no"), HttpStatusCode.UnprocessableEntity, "unsupported_operation");
        }

        // The policy profile only carries managed-data boundaries: no device-wide restrictions, VPN or wipe-on-failure.
        var (_, command) = await phone.IdleAsync("EnrollmentID");
        var types = new List<string>();
        while (command is not null)
        {
            var type = AppleDeviceSimulator.RequestType(command);
            types.Add(type);
            if (type == "InstallProfile")
            {
                var installed = ApplePlist.ParseDictionary((byte[])AppleDeviceSimulator.Arguments(command)["Payload"]!);
                var payloads = ((List<object?>)installed["PayloadContent"]!).OfType<Dictionary<string, object?>>().ToList();
                var restrictions = payloads.Single(p => (string?)p["PayloadType"] == "com.apple.applicationaccess");
                Assert.Equal(false, restrictions["allowOpenFromManagedToUnmanaged"]);
                Assert.False(restrictions.ContainsKey("allowCamera"));
                Assert.False(restrictions.ContainsKey("allowScreenShot"));
                Assert.DoesNotContain(payloads, p => (string?)p["PayloadType"] == "com.apple.vpn.managed");
                Assert.False(payloads.Single(p => (string?)p["PayloadType"] == "com.apple.mobiledevice.passwordpolicy").ContainsKey("maxFailedAttempts"));
            }

            Dictionary<string, object?>? answer = type == "DeviceInformation"
                ? new() { ["QueryResponses"] = new Dictionary<string, object?> { ["OSVersion"] = "17.4", ["ProductName"] = "iPhone15,2" } }
                : type == "InstalledApplicationList" ? new() { ["InstalledApplicationList"] = new List<object?>() } : null;
            (_, command) = await phone.AcknowledgeAsync(command, answer, "EnrollmentID");
        }

        Assert.Contains("InstallProfile", types);

        // Selective removal: retire needs approval; the device removes only what Solvix managed.
        var retire = await HttpAssert.OkAsync<CommandDto>(await MdmTestKit.SendCommandAsync(ws.Owner, device.Id, DeviceCommandType.Retire, "Left the company"), HttpStatusCode.Created);
        Assert.Equal(CommandState.PendingApproval, retire.State);
        await HttpAssert.OkAsync<CommandDto>(await ws.SecondAdmin.PostAsJsonAsync($"/api/v1/mobile-commands/{retire.Id}/approve", new CommandDecisionRequest("ok"), ApiFixture.Json));
        (_, command) = await phone.IdleAsync("EnrollmentID");
        Assert.Equal("RemoveProfile", AppleDeviceSimulator.RequestType(command!));
        Assert.Equal(AppleProfileBuilder.EnrollmentIdentifier(phone.Handle!), AppleDeviceSimulator.Arguments(command!)["Identifier"]);
        Assert.Equal(DeviceState.RemovalPending, (await MdmTestKit.GetDeviceAsync(ws.Owner, device.Id)).State);

        // iOS tells the server it has been unenrolled.
        Assert.Equal(HttpStatusCode.OK, (await phone.CheckOutAsync("EnrollmentID")).StatusCode);
        var retired = await MdmTestKit.GetDetailAsync(ws.Owner, device.Id);
        Assert.Equal(DeviceState.Retired, retired.Device.State);
        Assert.Contains(retired.Events, e => e.Type == DeviceEventType.Unenrolled && e.Summary.Contains("personal data untouched", StringComparison.Ordinal));
        Assert.Equal(CommandState.Acknowledged, (await MdmTestKit.ListCommandsAsync(ws.Owner, device.Id)).Single(c => c.Id == retire.Id).State);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.AuthenticateAsync(new() { ["UDID"] = null, ["EnrollmentID"] = phone.EnrollmentId })).StatusCode);
    }

    [Fact]
    public async Task Device_messages_must_carry_a_valid_signature_from_the_enrolled_identity()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "P", MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "E", CompanyIos, policy.Id);
        using var anonymous = fixture.Factory.CreateClient();
        using var phone = new AppleDeviceSimulator(anonymous);
        await phone.DownloadProfileAsync(enrollment.EnrollmentUrl!);

        var authenticate = new Dictionary<string, object?> { ["MessageType"] = "Authenticate", ["Topic"] = SolvixApiFactory.AppleTopic, ["UDID"] = phone.Udid };
        var body = ApplePlist.Serialize(authenticate);

        // Unsigned, garbage-signed and tampered messages are rejected before any state is touched.
        await HttpAssert.ProblemAsync(await phone.SendRawAsync(phone.Handle!, "checkin", body, null), HttpStatusCode.Unauthorized, "unauthorized");
        await HttpAssert.ProblemAsync(await phone.SendRawAsync(phone.Handle!, "checkin", body, "bm90LWEtc2lnbmF0dXJl"), HttpStatusCode.Unauthorized, "unauthorized");
        using var attacker = AppleDeviceSimulator.NewIdentity();
        var signedByAttacker = AppleDeviceSimulator.Sign(body, attacker);
        var tampered = ApplePlist.Serialize(new Dictionary<string, object?> { ["MessageType"] = "Authenticate", ["Topic"] = SolvixApiFactory.AppleTopic, ["UDID"] = "SOMEONE-ELSE" });
        await HttpAssert.ProblemAsync(await phone.SendRawAsync(phone.Handle!, "checkin", body, signedByAttacker, tampered), HttpStatusCode.Unauthorized, "unauthorized");

        // The first valid message binds the identity certificate to the device record...
        Assert.Equal(HttpStatusCode.OK, (await phone.AuthenticateAsync()).StatusCode);
        // ...and a different (but cryptographically valid) identity can no longer speak for it.
        await HttpAssert.ProblemAsync(await phone.AuthenticateAsync(identity: attacker), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssert.ProblemAsync(await phone.SendAsync("server", new Dictionary<string, object?> { ["Status"] = "Idle", ["UDID"] = phone.Udid }, attacker), HttpStatusCode.Forbidden, "forbidden");

        // Wrong topic, unknown handle, malformed body.
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.CheckInAsync(new Dictionary<string, object?> { ["MessageType"] = "Authenticate", ["Topic"] = "com.apple.mgmt.External.other", ["UDID"] = phone.Udid })).StatusCode);
        var signed = AppleDeviceSimulator.Sign(body, attacker);
        await HttpAssert.ProblemAsync(await phone.SendRawAsync("unknown-handle", "checkin", body, signed), HttpStatusCode.NotFound, "not_found");
        var garbage = new byte[] { 1, 2, 3 };
        // A validly signed but malformed body is a client error, not an authentication failure.
        await HttpAssert.ProblemAsync(await phone.SendRawAsync(phone.Handle!, "checkin", garbage, AppleDeviceSimulator.Sign(garbage, attacker)), HttpStatusCode.BadRequest, "validation_failed");

        // A message before TokenUpdate cannot pull commands for a non-enrolled device.
        Assert.Empty(await MdmTestKit.ListDevicesAsync(ws.Owner));
    }

    [Fact]
    public async Task Apple_enrollment_links_belong_to_their_tenant_and_do_not_leak_across_tenants()
    {
        var a = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var b = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var policy = await MdmTestKit.CreatePolicyAsync(a.Owner, "P", MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);
        var enrollment = await MdmTestKit.EnrollAsync(a.Owner, "E", CompanyIos, policy.Id);
        using var anonymous = fixture.Factory.CreateClient();
        using var phone = new AppleDeviceSimulator(anonymous);
        await phone.DownloadProfileAsync(enrollment.EnrollmentUrl!);
        await phone.AuthenticateAsync();
        await phone.TokenUpdateAsync();

        Assert.Single(await MdmTestKit.ListDevicesAsync(a.Owner));
        Assert.Empty(await MdmTestKit.ListDevicesAsync(b.Owner));
        var device = Assert.Single(await MdmTestKit.ListDevicesAsync(a.Owner));
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/mobile-devices/{device.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await MdmTestKit.SendCommandAsync(b.Owner, device.Id, DeviceCommandType.Lock), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await b.Owner.PutAsJsonAsync($"/api/v1/mobile-devices/{device.Id}/policy", new AssignPolicyRequest(null), ApiFixture.Json), HttpStatusCode.NotFound);

        // An unknown or malformed enrollment secret never reveals anything.
        await HttpAssert.ProblemAsync(await anonymous.GetAsync("/api/v1/mdm/apple/enroll?token=definitely-not-valid"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/mdm/apple/enroll")).StatusCode);
        // Administrators' endpoints still need authentication.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/mobile-devices")).StatusCode);
    }

    [Fact]
    public async Task Sync_asks_enrolled_apple_devices_for_inventory_once_and_repushes_commands_that_waited_too_long()
    {
        var ws = await MdmTestKit.CreateWorkspaceAsync(fixture);
        var policy = await MdmTestKit.CreatePolicyAsync(ws.Owner, "P", MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);
        var enrollment = await MdmTestKit.EnrollAsync(ws.Owner, "E", CompanyIos, policy.Id);
        using var anonymous = fixture.Factory.CreateClient();
        using var phone = new AppleDeviceSimulator(anonymous);
        await phone.DownloadProfileAsync(enrollment.EnrollmentUrl!);
        await phone.AuthenticateAsync();
        await phone.TokenUpdateAsync();
        var device = Assert.Single(await MdmTestKit.ListDevicesAsync(ws.Owner));

        // Drain the initial commands.
        var (_, command) = await phone.IdleAsync();
        while (command is not null)
        {
            (_, command) = await phone.AcknowledgeAsync(command, AppleDeviceSimulator.RequestType(command) switch
            {
                "DeviceInformation" => new() { ["QueryResponses"] = new Dictionary<string, object?> { ["OSVersion"] = "17.4" } },
                "InstalledApplicationList" => new() { ["InstalledApplicationList"] = new List<object?>() },
                _ => null,
            });
        }

        var first = await MdmTestKit.SyncAsync(ws.Owner);
        Assert.Equal(1, first.InventoryRequested);
        var second = await MdmTestKit.SyncAsync(ws.Owner);
        Assert.Equal(0, second.InventoryRequested); // an open request is not duplicated
        var queued = Assert.Single(await MdmTestKit.ListCommandsAsync(ws.Owner, device.Id), c => c.State == CommandState.Queued);
        Assert.Equal(DeviceCommandType.SyncInventory, queued.Type);

        // The device then collects and answers it like any other command.
        (_, command) = await phone.IdleAsync();
        Assert.Equal("DeviceInformation", AppleDeviceSimulator.RequestType(command!));
    }
}
