namespace Solvix.UnitTests.EndpointManagement;

using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Targeting;

/// <summary>Policy assignment and conflict resolution rules.</summary>
public sealed class EffectiveConfigurationResolverTests
{
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid ParentDept = Guid.NewGuid();
    private static readonly Guid ChildDept = Guid.NewGuid();
    private static readonly Guid OtherDept = Guid.NewGuid();
    private static readonly Guid Group = Guid.NewGuid();

    private static readonly DeviceTargetInfo Laptop = new(Guid.NewGuid(), "laptop", Branch, ChildDept, new HashSet<Guid> { Group });
    private static readonly DeviceTargetInfo Kiosk = new(Guid.NewGuid(), "kiosk", Branch, OtherDept, new HashSet<Guid>());

    private static readonly TargetingSnapshot Snapshot = new(
        [Laptop, Kiosk],
        new Dictionary<Guid, Guid?> { [ParentDept] = null, [ChildDept] = ParentDept, [OtherDept] = null });

    private static ScreenLockSetting Lock(int seconds) => new(ScreenLockControl.InactivityTimeoutSeconds, seconds);

    private static AssignmentInput Assign(TargetType type, Guid? id, int priority = 100, bool exclude = false, EnforcementMode mode = EnforcementMode.Enforce) =>
        new(Guid.NewGuid(), new TargetRef(type, id, exclude), mode, priority, null, RemovalBehavior.Revert);

    private static PolicyInput Policy(string name, IReadOnlyList<ConfigurationSetting> settings, params AssignmentInput[] assignments) =>
        new(Guid.NewGuid(), name, Guid.NewGuid(), 1, SettingRisk.Low, settings, assignments);

    [Fact]
    public void Department_assignment_includes_sub_departments_but_not_siblings()
    {
        var policy = Policy("Lock", [Lock(900)], Assign(TargetType.Department, ParentDept));
        Assert.Single(EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [policy]).Settings);
        Assert.Empty(EffectiveConfigurationResolver.Resolve(Kiosk, Snapshot, [policy]).Settings);
    }

    [Fact]
    public void Exclusion_wins_over_any_inclusion_of_the_same_policy()
    {
        var policy = Policy("Lock", [Lock(900)], Assign(TargetType.Branch, Branch), Assign(TargetType.DeviceGroup, Group, exclude: true));
        Assert.Empty(EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [policy]).Settings);
        Assert.Single(EffectiveConfigurationResolver.Resolve(Kiosk, Snapshot, [policy]).Settings);
    }

    [Fact]
    public void Identical_values_from_several_policies_are_not_a_conflict()
    {
        var a = Policy("A", [Lock(900)], Assign(TargetType.AllDevices, null));
        var b = Policy("B", [Lock(900)], Assign(TargetType.Branch, Branch));
        var result = EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [a, b]);
        Assert.Single(result.Settings);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Lower_priority_number_wins_and_the_conflict_is_reported_as_resolved()
    {
        var baseline = Policy("Baseline", [Lock(900)], Assign(TargetType.AllDevices, null, priority: 100));
        var strict = Policy("Strict", [Lock(300)], Assign(TargetType.AllDevices, null, priority: 10));
        var result = EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [baseline, strict]);

        var setting = Assert.Single(result.Settings);
        Assert.Equal(300, ((ScreenLockSetting)setting.Setting).Value);
        var conflict = Assert.Single(result.Conflicts);
        Assert.True(conflict.IsResolved);
        Assert.Equal(strict.PolicyId, conflict.Winner!.PolicyId);
    }

    [Fact]
    public void On_equal_priority_the_more_specific_target_wins()
    {
        var branch = Policy("Branch", [Lock(900)], Assign(TargetType.Branch, Branch));
        var device = Policy("Device", [Lock(60)], Assign(TargetType.Device, Laptop.Id));
        var group = Policy("Group", [Lock(120)], Assign(TargetType.DeviceGroup, Group));
        var result = EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [branch, group, device]);

        Assert.Equal(60, ((ScreenLockSetting)Assert.Single(result.Settings).Setting).Value);
        Assert.True(Assert.Single(result.Conflicts).IsResolved);
    }

    [Fact]
    public void Same_priority_and_specificity_with_different_values_is_unresolved_and_withheld()
    {
        var a = Policy("A", [Lock(900), new UsbStorageSetting(UsbDeviceClass.RemovableDisks, UsbAccess.ReadOnly)], Assign(TargetType.Branch, Branch));
        var b = Policy("B", [Lock(300)], Assign(TargetType.Branch, Branch));
        var result = EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [a, b]);

        // The USB setting is unaffected; the lock timeout is not applied at all.
        var setting = Assert.Single(result.Settings);
        Assert.IsType<UsbStorageSetting>(setting.Setting);
        var conflict = Assert.Single(result.Conflicts);
        Assert.False(conflict.IsResolved);
        Assert.Null(conflict.Winner);
        Assert.Equal(Lock(900).Key, conflict.Key);
        Assert.Contains(Lock(900).Key, result.UnresolvedKeys);
    }

    [Fact]
    public void Best_assignment_of_a_policy_supplies_mode_and_priority()
    {
        var policy = Policy(
            "Lock",
            [Lock(900)],
            Assign(TargetType.AllDevices, null, priority: 500, mode: EnforcementMode.Monitor),
            Assign(TargetType.DeviceGroup, Group, priority: 20, mode: EnforcementMode.Enforce));
        var laptop = Assert.Single(EffectiveConfigurationResolver.Resolve(Laptop, Snapshot, [policy]).Settings);
        var kiosk = Assert.Single(EffectiveConfigurationResolver.Resolve(Kiosk, Snapshot, [policy]).Settings);
        Assert.Equal(EnforcementMode.Enforce, laptop.Mode);
        Assert.Equal(20, laptop.Priority);
        Assert.Equal(EnforcementMode.Monitor, kiosk.Mode);
    }

    [Fact]
    public void Snapshot_resolves_target_sets_with_exclusions()
    {
        var devices = Snapshot.Resolve([new TargetRef(TargetType.AllDevices, null, false), new TargetRef(TargetType.Device, Kiosk.Id, true)]);
        Assert.Equal([Laptop.Id], devices.Select(d => d.Id));
    }
}
