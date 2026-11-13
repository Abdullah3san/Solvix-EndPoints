namespace Solvix.UnitTests.Mdm;

using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly ManagementScenario Company = new(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario Personal = new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned);

    private static DeviceCommand Command(DeviceCommandType type, Guid requester) =>
        DeviceCommand.Create(Tenant, Guid.NewGuid(), type, requester, "reason", null, Now, TimeSpan.FromHours(24));

    // ---------------------------------------------------------------- commands

    [Fact]
    public void Destructive_commands_start_pending_and_others_start_queued()
    {
        Assert.Equal(CommandState.PendingApproval, Command(DeviceCommandType.Wipe, Guid.NewGuid()).State);
        Assert.Equal(CommandState.PendingApproval, Command(DeviceCommandType.Retire, Guid.NewGuid()).State);
        Assert.Equal(CommandState.Queued, Command(DeviceCommandType.Lock, Guid.NewGuid()).State);
    }

    [Fact]
    public void Requester_cannot_approve_their_own_destructive_command_unless_self_approval_is_enabled()
    {
        var requester = Guid.NewGuid();
        var command = Command(DeviceCommandType.Wipe, requester);
        Assert.Throws<ForbiddenException>(() => command.Approve(requester, null, Now, allowSelfApproval: false));
        Assert.Equal(CommandState.PendingApproval, command.State);

        var other = Guid.NewGuid();
        command.Approve(other, "ok", Now, allowSelfApproval: false);
        Assert.Equal(CommandState.Queued, command.State);
        Assert.Equal(other, command.DecidedByUserId);

        var solo = Command(DeviceCommandType.Retire, requester);
        solo.Approve(requester, null, Now, allowSelfApproval: true);
        Assert.Equal(CommandState.Queued, solo.State);
    }

    [Fact]
    public void Approval_window_expires_and_decisions_are_final()
    {
        var command = Command(DeviceCommandType.Wipe, Guid.NewGuid());
        Assert.Throws<ConflictException>(() => command.Approve(Guid.NewGuid(), null, Now.AddHours(25), false));
        Assert.True(command.ExpireIfDue(Now.AddHours(25)));
        Assert.Equal(CommandState.Expired, command.State);
        Assert.False(command.ExpireIfDue(Now.AddHours(26)));

        var rejected = Command(DeviceCommandType.Wipe, Guid.NewGuid());
        rejected.Reject(Guid.NewGuid(), "no", Now);
        Assert.Equal(CommandState.Rejected, rejected.State);
        Assert.Throws<ConflictException>(() => rejected.Approve(Guid.NewGuid(), null, Now, false));
        Assert.Throws<ConflictException>(() => rejected.Cancel(Now));
    }

    [Fact]
    public void Command_lifecycle_moves_forward_only()
    {
        var command = Command(DeviceCommandType.Lock, Guid.NewGuid());
        command.MarkSent("ref", Now);
        Assert.Equal(CommandState.Sent, command.State);
        command.Requeue();
        Assert.Equal(CommandState.Queued, command.State);
        command.MarkSent(null, Now);
        command.MarkAcknowledged(Now);
        Assert.Equal(CommandState.Acknowledged, command.State);
        command.MarkFailed("late", Now);
        Assert.Equal(CommandState.Acknowledged, command.State); // terminal states stay put
        Assert.Throws<ConflictException>(() => command.MarkSent(null, Now));
    }

    // ----------------------------------------------------------------- devices

    private static MobileDevice Device(ManagementScenario scenario, string id = "enterprises/e/devices/d")
    {
        var device = MobileDevice.CreatePending(Tenant, scenario, id, null, null);
        device.CompleteEnrollment(Now);
        return device;
    }

    [Fact]
    public void Personal_devices_never_store_identifiers_or_unmanaged_apps()
    {
        var device = Device(Personal);
        device.ApplyInventory(new DeviceInventory
        {
            SerialNumber = "SERIAL-123",
            Model = "Pixel",
            OsVersion = "14",
            Apps = [new InstalledApp("com.bank", "Bank", "1", IsManaged: false), new InstalledApp("com.work", "Work", "1", IsManaged: true)],
        }, Now);
        Assert.Null(device.SerialNumber);
        Assert.Equal("Pixel", device.Model);
        var app = Assert.Single(device.InstalledApps);
        Assert.Equal("com.work", app.Identifier);

        var company = Device(Company);
        company.ApplyInventory(new DeviceInventory
        {
            SerialNumber = "SERIAL-123",
            Apps = [new InstalledApp("com.bank", "Bank", "1", false), new InstalledApp("com.work", "Work", "1", true)],
        }, Now);
        Assert.Equal("SERIAL-123", company.SerialNumber);
        Assert.Equal(2, company.InstalledApps.Count);
    }

    [Fact]
    public void Partial_inventory_updates_keep_previous_values()
    {
        var device = Device(Company);
        device.ApplyInventory(new DeviceInventory { OsVersion = "14", IsEncrypted = true, Model = "Pixel" }, Now);
        device.ApplyInventory(new DeviceInventory { HasPasscode = true }, Now.AddMinutes(1));
        Assert.Equal("14", device.OsVersion);
        Assert.True(device.IsEncrypted);
        Assert.True(device.HasPasscode);
        Assert.Equal(Now.AddMinutes(1), device.LastSeenAtUtc);
    }

    [Fact]
    public void Policy_scenario_must_match_the_device_and_identity_cannot_change()
    {
        var device = Device(Company);
        var other = MobilePolicy.Create(Tenant, "BYOD", null, Personal, new MobilePolicySettings());
        Assert.Throws<ConflictException>(() => device.AssignPolicy(other));
        var fitting = MobilePolicy.Create(Tenant, "Corp", null, Company, new MobilePolicySettings());
        device.AssignPolicy(fitting);
        Assert.Equal(fitting.Id, device.PolicyId);

        device.BindIdentity("AAAA");
        device.BindIdentity("AAAA");
        Assert.Throws<ForbiddenException>(() => device.BindIdentity("BBBB"));
    }

    [Fact]
    public void Removal_clears_transport_secrets_and_distinguishes_wipe_from_retire()
    {
        var device = Device(new ManagementScenario(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned));
        device.BindAppleTransport("abcd", "magic", "topic", "enc:v1:xyz");
        device.BeginRemoval();
        Assert.Equal(DeviceState.RemovalPending, device.State);
        Assert.True(device.IsActive);
        device.CancelRemoval();
        Assert.Equal(DeviceState.Enrolled, device.State);

        device.BeginRemoval();
        device.CompleteRemoval(Now, factoryReset: false);
        Assert.Equal(DeviceState.Retired, device.State);
        Assert.Null(device.PushToken);
        Assert.Null(device.ProtectedUnlockToken);
        Assert.Throws<ConflictException>(() => device.CompleteEnrollment(Now));
        Assert.Throws<ConflictException>(() => device.BeginRemoval());
    }

    // ----------------------------------------------------------------- tokens

    [Fact]
    public void Enrollment_tokens_expire_run_out_and_can_be_revoked()
    {
        var policyId = Guid.NewGuid();
        var token = EnrollmentToken.Create(Tenant, Company, "t", policyId, Guid.NewGuid(), Now.AddHours(1), 1, "hash");
        Assert.True(token.IsUsable(Now));
        Assert.False(token.IsUsable(Now.AddHours(2)));
        token.RegisterUse(Now);
        Assert.False(token.IsUsable(Now));
        Assert.Throws<ConflictException>(() => token.RegisterUse(Now));

        var revoked = EnrollmentToken.Create(Tenant, Company, "t", policyId, Guid.NewGuid(), Now.AddHours(1), 5, null);
        revoked.Revoke(Now);
        Assert.False(revoked.IsUsable(Now));
        Assert.Throws<ValidationException>(() => EnrollmentToken.Create(Tenant, Company, "t", policyId, Guid.NewGuid(), Now, 0, null));
        Assert.Throws<ValidationException>(() => EnrollmentToken.Create(Tenant, new ManagementScenario(MobilePlatform.Ios, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned), "t", policyId, Guid.NewGuid(), Now, 1, null));
    }

    [Fact]
    public void Policy_updates_bump_the_version_but_publication_is_tracked_separately()
    {
        var policy = MobilePolicy.Create(Tenant, "P", null, Company, new MobilePolicySettings());
        Assert.Equal(1, policy.Version);
        Assert.Equal(0, policy.PublishedVersion);
        policy.MarkPublished(Now);
        policy.Update("P2", "d", new MobilePolicySettings());
        Assert.Equal(2, policy.Version);
        Assert.Equal(1, policy.PublishedVersion);
        Assert.Equal(Company, policy.Scenario);
    }

    [Fact]
    public void Apps_are_platform_scoped_and_store_ids_are_ios_only()
    {
        Assert.Null(MobileApp.Create(Tenant, MobilePlatform.Android, "A", "com.a", 123).StoreId);
        Assert.Equal(123, MobileApp.Create(Tenant, MobilePlatform.Ios, "A", "com.a", 123).StoreId);
        Assert.Equal(AppStoreSource.ManagedGooglePlay, MobileApp.Create(Tenant, MobilePlatform.Android, "A", "com.a", null).Source);
        Assert.Throws<ValidationException>(() => MobileApp.Create(Tenant, MobilePlatform.Android, " ", "com.a", null));
    }
}
