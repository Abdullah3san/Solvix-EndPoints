namespace Solvix.Modules.Devices.Application.Devices;

using FluentValidation;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Domain;

public sealed class SaveDeviceGroupRequestValidator : AbstractValidator<SaveDeviceGroupRequest>
{
    public SaveDeviceGroupRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceGroup.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(DeviceGroup.DescriptionMaxLength);
    }
}

public sealed class DeviceGroupMembersRequestValidator : AbstractValidator<DeviceGroupMembersRequest>
{
    public DeviceGroupMembersRequestValidator()
    {
        RuleFor(x => x.DeviceIds).NotNull().Must(ids => ids.Count is > 0 and <= 1000)
            .WithMessage("Provide between 1 and 1000 device ids.");
    }
}

public sealed class CreateEnrollmentTokenRequestValidator : AbstractValidator<CreateEnrollmentTokenRequest>
{
    public CreateEnrollmentTokenRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(EnrollmentToken.NameMaxLength);
        RuleFor(x => x.ValidForHours).InclusiveBetween(1, 24 * 90);
        RuleFor(x => x.MaxUses).InclusiveBetween(1, 100_000).When(x => x.MaxUses is not null);
        RuleFor(x => x.DefaultBranchId).NotNull().When(x => x.DefaultDepartmentId is not null)
            .WithMessage("A default department requires its branch.");
    }
}

public sealed class EnrollDeviceRequestValidator : AbstractValidator<EnrollDeviceRequest>
{
    public EnrollDeviceRequestValidator()
    {
        RuleFor(x => x.EnrollmentToken).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(Device.HostnameMaxLength);
        RuleFor(x => x.HardwareId).MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.DnsDomain).MaximumLength(Device.HostnameMaxLength);
        RuleFor(x => x.OsName).NotEmpty().MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.OsVersion).MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.OsBuild).MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.AgentVersion).NotEmpty().MaximumLength(Device.AgentVersionMaxLength);
    }
}

public sealed class AgentHeartbeatRequestValidator : AbstractValidator<AgentHeartbeatRequest>
{
    public AgentHeartbeatRequestValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(Device.HostnameMaxLength);
        RuleFor(x => x.DnsDomain).MaximumLength(Device.HostnameMaxLength);
        RuleFor(x => x.OsName).MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.OsVersion).MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.OsBuild).MaximumLength(Device.ShortTextMaxLength);
        RuleFor(x => x.AgentVersion).MaximumLength(Device.AgentVersionMaxLength);
    }
}
