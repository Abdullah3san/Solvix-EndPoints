namespace Solvix.UnitTests.EndpointManagement;

using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.SharedKernel.Exceptions;

/// <summary>Approval workflow, versioning and deployment state-machine rules.</summary>
public sealed class DomainRulesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Author = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ConfigurationPolicyVersion Submitted(params ConfigurationSetting[] settings)
    {
        var policy = ConfigurationPolicy.Create(Tenant, "P", null);
        var version = ConfigurationPolicyVersion.CreateDraft(Tenant, policy, settings, null, Author);
        version.Submit(Author, Now);
        return version;
    }

    [Fact]
    public void Low_risk_versions_can_be_self_approved()
    {
        var version = Submitted(new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 900));
        version.Approve(Author, reviewerIsOwner: false, acknowledgeDestructive: false, null, Now);
        Assert.Equal(PolicyVersionStatus.Approved, version.Status);
        Assert.Equal(SettingRisk.Low, version.AuthorizedRisk);
    }

    [Fact]
    public void Elevated_risk_needs_a_second_person()
    {
        var version = Submitted(new ServiceSetting("Spooler", ServiceStartupType.Disabled, null));
        Assert.Throws<ForbiddenException>(() => version.Approve(Author, true, false, null, Now));
        version.Approve(Reviewer, reviewerIsOwner: false, acknowledgeDestructive: false, null, Now);
        Assert.Equal(SettingRisk.Elevated, version.AuthorizedRisk);
    }

    [Fact]
    public void Destructive_risk_needs_an_owner_other_than_the_author_and_explicit_acknowledgement()
    {
        var version = Submitted(new FirewallProfileSetting(FirewallProfile.Public, false, null, null));
        Assert.Throws<ForbiddenException>(() => version.Approve(Author, reviewerIsOwner: true, acknowledgeDestructive: true, null, Now));
        Assert.Throws<ForbiddenException>(() => version.Approve(Reviewer, reviewerIsOwner: false, acknowledgeDestructive: true, null, Now));
        Assert.Throws<ValidationException>(() => version.Approve(Reviewer, reviewerIsOwner: true, acknowledgeDestructive: false, null, Now));
        version.Approve(Reviewer, reviewerIsOwner: true, acknowledgeDestructive: true, "CAB-123", Now);
        Assert.True(version.DestructiveAcknowledged);
        Assert.Equal(SettingRisk.Destructive, version.AuthorizedRisk);
    }

    [Fact]
    public void Blocked_and_duplicate_settings_cannot_be_saved()
    {
        var policy = ConfigurationPolicy.Create(Tenant, "P", null);
        var blocked = Assert.Throws<ValidationException>(() =>
            ConfigurationPolicyVersion.CreateDraft(Tenant, policy, [new ServiceSetting("RpcSs", ServiceStartupType.Disabled, null)], null, Author));
        Assert.Contains(blocked.Errors, e => e.Key == "settings[0]");

        var duplicate = Assert.Throws<ValidationException>(() => ConfigurationPolicyVersion.CreateDraft(
            Tenant, policy, [new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 60), new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 120)], null, Author));
        Assert.Contains(duplicate.Errors, e => e.Key == "settings[1]");
    }

    [Fact]
    public void Versions_are_immutable_after_submission_and_content_hash_ignores_order()
    {
        var policy = ConfigurationPolicy.Create(Tenant, "P", null);
        var a = new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 60);
        var b = new UsbStorageSetting(UsbDeviceClass.CdDvd, UsbAccess.ReadOnly);
        var v1 = ConfigurationPolicyVersion.CreateDraft(Tenant, policy, [a, b], null, Author);
        var v2 = ConfigurationPolicyVersion.CreateDraft(Tenant, policy, [b, a], null, Author);
        Assert.Equal(v1.ContentHash, v2.ContentHash);
        Assert.Equal([1, 2], new[] { v1.Number, v2.Number });

        v1.Submit(Author, Now);
        Assert.Throws<ConflictException>(() => v1.ReplaceSettings([a], null));
    }

    [Fact]
    public void Superseded_versions_can_be_published_again_as_a_rollback()
    {
        var v1 = Submitted(new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 60));
        v1.Approve(Author, false, false, null, Now);
        v1.Publish(Author, Now);
        v1.Supersede(Now.AddDays(1));
        Assert.Equal(PolicyVersionStatus.Superseded, v1.Status);
        v1.Publish(Author, Now.AddDays(2));
        Assert.Equal(PolicyVersionStatus.Published, v1.Status);
        Assert.Null(v1.SupersededAtUtc);
    }

    private static PatchDeployment Deployment(bool pilot = true)
    {
        var d = PatchDeployment.Create(Tenant, "Nov", Author);
        d.Configure("Nov", null, Now, Now.AddDays(7), null, RestartBehavior.WithinMaintenanceWindow, 600,
            pilot ? Guid.NewGuid() : null, null, 90, 0, false, 20, 3);
        d.SetPatches([Guid.NewGuid()]);
        d.SetTargets([new TargetRef(TargetType.AllDevices, null, false)]);
        return d;
    }

    [Fact]
    public void Deployment_needs_explicit_authorisation_before_it_can_start()
    {
        var d = Deployment();
        Assert.Throws<ConflictException>(() => d.Start(Now, withPilot: true));
        d.Authorize(Reviewer, Now);
        d.Start(Now, withPilot: true);
        Assert.Equal((DeploymentStatus.InProgress, DeploymentPhase.Pilot), (d.Status, d.Phase));
        Assert.Throws<ConflictException>(() => d.Configure("x", null, Now, null, null, RestartBehavior.Never, 0, null, null, 90, 0, false, 20, 3));
    }

    [Fact]
    public void Deployment_without_patches_or_targets_cannot_be_authorised()
    {
        var d = PatchDeployment.Create(Tenant, "Empty", Author);
        d.Configure("Empty", null, Now, null, null, RestartBehavior.Never, 0, null, null, 90, 0, false, 20, 3);
        Assert.Throws<ValidationException>(() => d.Authorize(Author, Now));
    }

    [Fact]
    public void Halted_pilot_can_only_be_promoted_with_force()
    {
        var d = Deployment();
        d.Authorize(Reviewer, Now);
        d.Start(Now, withPilot: true);
        d.Halt("Pilot success rate 50% is below the required 90%.");
        Assert.Throws<ConflictException>(() => d.Promote(Reviewer, Now, force: false));
        Assert.Throws<ConflictException>(() => d.Resume());
        d.Promote(Reviewer, Now, force: true);
        Assert.Equal((DeploymentStatus.InProgress, DeploymentPhase.Broad), (d.Status, d.Phase));
        Assert.Null(d.HaltReason);
    }

    [Fact]
    public void Broad_phase_halt_can_be_resumed_and_terminal_states_are_final()
    {
        var d = Deployment(pilot: false);
        d.Authorize(Reviewer, Now);
        d.Start(Now, withPilot: false);
        d.Halt("Failure rate 40% reached the 20% threshold.");
        d.Resume();
        Assert.Equal(DeploymentStatus.InProgress, d.Status);
        d.Cancel(Now, null);
        Assert.Throws<ConflictException>(() => d.Cancel(Now, null));
        Assert.Throws<ConflictException>(() => d.Resume());
    }

    [Fact]
    public void Job_retries_with_back_off_until_max_attempts()
    {
        var job = new PatchDeploymentJob(Tenant, Guid.NewGuid(), Guid.NewGuid(), DeploymentRing.Broad);
        var delay = TimeSpan.FromMinutes(15);

        job.Dispatch(Now, TimeSpan.FromMinutes(30));
        job.FailAttempt("0x80240022", unchecked((int)0x80240022), null, maxAttempts: 2, Now, delay);
        Assert.Equal(PatchJobStatus.Pending, job.Status);
        Assert.Equal(Now + delay, job.NextAttemptAfterUtc);
        Assert.False(job.IsEligible(Now));
        Assert.True(job.IsEligible(Now + delay));

        job.Dispatch(Now + delay, TimeSpan.FromMinutes(30));
        Assert.Equal(2, job.Attempt);
        job.FailAttempt("again", null, null, maxAttempts: 2, Now + delay, delay);
        Assert.Equal(PatchJobStatus.Failed, job.Status);
        Assert.True(job.CountsAsFailure);
    }

    [Fact]
    public void Aborted_runs_are_requeued_without_counting_as_failures()
    {
        var job = new PatchDeploymentJob(Tenant, Guid.NewGuid(), Guid.NewGuid(), DeploymentRing.Broad);
        job.Dispatch(Now, TimeSpan.FromMinutes(30));
        job.Requeue("window closing", Now.AddMinutes(1));
        Assert.Equal(PatchJobStatus.Pending, job.Status);
        Assert.Equal(0, job.FailureCount);
    }

    [Fact]
    public void Restart_pending_jobs_settle_only_after_verification()
    {
        var job = new PatchDeploymentJob(Tenant, Guid.NewGuid(), Guid.NewGuid(), DeploymentRing.Pilot);
        job.Dispatch(Now, TimeSpan.FromMinutes(30));
        job.Succeed(rebootRequired: true, null, Now);
        Assert.Equal(PatchJobStatus.PendingRestart, job.Status);
        Assert.True(job.IsSettled);
        Assert.False(job.IsTerminal);
        job.ConfirmRestart(Now.AddHours(1));
        Assert.Equal(PatchJobStatus.Succeeded, job.Status);
    }
}
