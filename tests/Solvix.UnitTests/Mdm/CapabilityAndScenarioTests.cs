namespace Solvix.UnitTests.Mdm;

using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;

public sealed class CapabilityAndScenarioTests
{
    [Fact]
    public void Every_valid_scenario_has_a_capability_set_and_the_matrix_covers_exactly_the_valid_scenarios()
    {
        Assert.Equal(7, ManagementScenario.All.Count);
        Assert.Equal(ManagementScenario.All.Count, MdmCapabilities.All.Count);
        Assert.All(ManagementScenario.All, s => Assert.Equal(s, MdmCapabilities.For(s).Scenario));
    }

    [Theory]
    [InlineData(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.PersonallyOwned)]
    [InlineData(MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.PersonallyOwned)]
    [InlineData(MobilePlatform.Android, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned)]
    [InlineData(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.PersonallyOwned)]
    [InlineData(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.CompanyOwned)]
    [InlineData(MobilePlatform.Ios, ManagementMode.AutomatedDeviceEnrollment, DeviceOwnership.PersonallyOwned)]
    [InlineData(MobilePlatform.Ios, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned)]
    public void Invalid_scenarios_are_rejected_with_an_explanation(MobilePlatform platform, ManagementMode mode, DeviceOwnership ownership)
    {
        var scenario = new ManagementScenario(platform, mode, ownership);
        Assert.False(scenario.IsValid);
        var ex = Assert.Throws<ValidationException>(() => scenario.EnsureValid());
        Assert.NotEmpty(ex.Errors["scenario"][0]);
        Assert.Throws<ValidationException>(() => MdmCapabilities.For(scenario));
    }

    [Fact]
    public void Personally_owned_devices_never_offer_destructive_or_device_wide_operations()
    {
        foreach (var scenario in ManagementScenario.All.Where(s => s.IsPersonalBoundary))
        {
            var set = MdmCapabilities.For(scenario);
            Assert.False(set.CanFactoryReset);
            Assert.False(set.CanReadDeviceIdentifiers);
            Assert.False(set.CanEnforceDeviceWidePolicy);
            Assert.True(set.CanRemoveCorporateDataOnly);
            Assert.DoesNotContain(DeviceCommandType.Wipe, set.Commands);
            Assert.DoesNotContain(DeviceCommandType.ResetPasscode, set.Commands);
            Assert.DoesNotContain(DeviceCommandType.Reboot, set.Commands);
            Assert.Contains(DeviceCommandType.Retire, set.Commands);
            var ex = Assert.Throws<UnsupportedOperationException>(() => MdmCapabilities.EnsureSupported(scenario, DeviceCommandType.Wipe));
            Assert.Contains("personally owned", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Ios_user_enrollment_cannot_lock_or_erase()
    {
        var ue = new ManagementScenario(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned);
        Assert.Throws<UnsupportedOperationException>(() => MdmCapabilities.EnsureSupported(ue, DeviceCommandType.Lock));
        Assert.Throws<UnsupportedOperationException>(() => MdmCapabilities.EnsureSupported(ue, DeviceCommandType.Wipe));
        MdmCapabilities.EnsureSupported(ue, DeviceCommandType.InstallApp);
        MdmCapabilities.EnsureSupported(ue, DeviceCommandType.Retire);
    }

    [Fact]
    public void Android_applications_are_policy_driven_and_company_devices_can_be_wiped_only_when_there_is_no_personal_profile()
    {
        foreach (var scenario in ManagementScenario.All.Where(s => s.Platform == MobilePlatform.Android))
        {
            var set = MdmCapabilities.For(scenario);
            Assert.DoesNotContain(DeviceCommandType.InstallApp, set.Commands);
            Assert.Equal(scenario.Mode is ManagementMode.FullyManaged or ManagementMode.Dedicated, set.CanFactoryReset);
            Assert.Equal(set.CanFactoryReset, set.Supports(DeviceCommandType.Wipe));
        }
    }

    [Fact]
    public void Only_retire_and_wipe_need_approval()
    {
        foreach (var type in Enum.GetValues<DeviceCommandType>())
        {
            Assert.Equal(type is DeviceCommandType.Retire or DeviceCommandType.Wipe, MdmCapabilities.RequiresApproval(type));
        }
    }

    [Fact]
    public void Default_enrollment_method_follows_the_scenario()
    {
        Assert.Equal(EnrollmentMethod.AndroidEnrollmentToken, new ManagementScenario(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned).DefaultEnrollmentMethod);
        Assert.Equal(EnrollmentMethod.AppleUserEnrollment, new ManagementScenario(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned).DefaultEnrollmentMethod);
        Assert.Equal(EnrollmentMethod.AppleAutomated, new ManagementScenario(MobilePlatform.Ios, ManagementMode.AutomatedDeviceEnrollment, DeviceOwnership.CompanyOwned).DefaultEnrollmentMethod);
        Assert.Equal(EnrollmentMethod.AppleProfile, new ManagementScenario(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned).DefaultEnrollmentMethod);
    }
}
