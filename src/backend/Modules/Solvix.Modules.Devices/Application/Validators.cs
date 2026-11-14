namespace Solvix.Modules.Devices.Application;

using FluentValidation;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;

internal static class Limits
{
    public const int Text = 512;
    public const int LongText = 2048;
}

public sealed class EnrollmentRequestValidator : AbstractValidator<EnrollmentRequest>
{
    public EnrollmentRequestValidator()
    {
        RuleFor(x => x.EnrollmentToken).NotEmpty().MaximumLength(256);
        RuleFor(x => x.CertificateSigningRequestPem).NotEmpty().MaximumLength(CertificateSigningRequests.MaxPemLength);
        RuleFor(x => x.Device).NotNull().SetValidator(new DeviceIdentityInfoValidator());
    }
}

public sealed class DeviceIdentityInfoValidator : AbstractValidator<DeviceIdentityInfo>
{
    public DeviceIdentityInfoValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(Device.HostnameMaxLength);
        RuleFor(x => x.AgentVersion).NotEmpty().Must(v => AgentVersion.TryParse(v, out _)).WithMessage("Must be a semantic version.");
        RuleFor(x => x.HardwareId).MaximumLength(64);
        RuleFor(x => x.SerialNumber).MaximumLength(Limits.Text);
        RuleFor(x => x.DomainName).MaximumLength(Limits.Text);
        RuleFor(x => x.Manufacturer).MaximumLength(Limits.Text);
        RuleFor(x => x.Model).MaximumLength(Limits.Text);
        RuleFor(x => x.OsName).MaximumLength(Limits.Text);
        RuleFor(x => x.OsVersion).MaximumLength(64);
        RuleFor(x => x.OsBuild).MaximumLength(64);
        RuleFor(x => x.Architecture).MaximumLength(32);
    }
}

public sealed class CertificateRenewalRequestValidator : AbstractValidator<CertificateRenewalRequest>
{
    public CertificateRenewalRequestValidator()
    {
        RuleFor(x => x.CertificateSigningRequestPem).NotEmpty().MaximumLength(CertificateSigningRequests.MaxPemLength);
    }
}

public sealed class HeartbeatRequestValidator : AbstractValidator<HeartbeatRequest>
{
    public HeartbeatRequestValidator()
    {
        RuleFor(x => x.AgentVersion).NotEmpty().Must(v => AgentVersion.TryParse(v, out _)).WithMessage("Must be a semantic version.");
        RuleFor(x => x.Health).NotNull();
        RuleFor(x => x.Health.Status).IsInEnum().When(x => x.Health is not null);
        RuleFor(x => x.Health.OsBuild).MaximumLength(64).When(x => x.Health is not null);
        RuleFor(x => x.Health.Issues).Must(i => i.Count <= 50).WithMessage("At most 50 issues may be reported.").When(x => x.Health is not null);
        RuleForEach(x => x.Health.Issues).ChildRules(issue =>
        {
            issue.RuleFor(i => i.Component).NotEmpty().MaximumLength(64);
            issue.RuleFor(i => i.Message).NotEmpty().MaximumLength(Limits.LongText);
        }).When(x => x.Health is not null);
    }
}

/// <summary>Bounds the size of every collection and string so a report cannot bloat the database.</summary>
public sealed class InventoryReportValidator : AbstractValidator<InventoryReport>
{
    public InventoryReportValidator()
    {
        RuleFor(x => x.SchemaVersion).InclusiveBetween(1, InventoryReport.CurrentSchemaVersion);
        RuleFor(x => x.AgentVersion).NotEmpty().Must(v => AgentVersion.TryParse(v, out _)).WithMessage("Must be a semantic version.");
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(Device.HostnameMaxLength);
        RuleFor(x => x.ContentHash).MaximumLength(DeviceInventory.ContentHashMaxLength);
        RuleFor(x => x.Software.Count).LessThanOrEqualTo(InventoryReport.MaxSoftwareEntries).OverridePropertyName("software");
        RuleFor(x => x.NetworkAdapters.Count).LessThanOrEqualTo(InventoryReport.MaxNetworkAdapters).OverridePropertyName("networkAdapters");
        RuleFor(x => x.Disks.Count).LessThanOrEqualTo(InventoryReport.MaxDisks).OverridePropertyName("disks");
        RuleFor(x => x.Volumes.Count).LessThanOrEqualTo(InventoryReport.MaxVolumes).OverridePropertyName("volumes");
        RuleFor(x => x.Processors.Count).LessThanOrEqualTo(InventoryReport.MaxProcessors).OverridePropertyName("processors");
        RuleFor(x => x.CollectionIssues.Count).LessThanOrEqualTo(50).OverridePropertyName("collectionIssues");
        RuleFor(x => x.Memory!.Modules.Count).LessThanOrEqualTo(InventoryReport.MaxMemoryModules).OverridePropertyName("memory.modules").When(x => x.Memory is not null);
        RuleForEach(x => x.Software).ChildRules(app =>
        {
            app.RuleFor(a => a.Name).NotEmpty().MaximumLength(Limits.Text);
            app.RuleFor(a => a.Version).MaximumLength(128);
            app.RuleFor(a => a.Publisher).MaximumLength(Limits.Text);
            app.RuleFor(a => a.Scope).MaximumLength(16);
            app.RuleFor(a => a.InstallLocation).MaximumLength(Limits.LongText);
        });
        RuleForEach(x => x.NetworkAdapters).ChildRules(adapter =>
        {
            adapter.RuleFor(a => a.Name).MaximumLength(Limits.Text);
            adapter.RuleFor(a => a.Description).MaximumLength(Limits.Text);
            adapter.RuleFor(a => a.MacAddress).MaximumLength(32);
            adapter.RuleFor(a => a.IPv4Addresses.Count).LessThanOrEqualTo(64).OverridePropertyName("iPv4Addresses");
            adapter.RuleFor(a => a.IPv6Addresses.Count).LessThanOrEqualTo(64).OverridePropertyName("iPv6Addresses");
            adapter.RuleForEach(a => a.IPv4Addresses).MaximumLength(64);
            adapter.RuleForEach(a => a.IPv6Addresses).MaximumLength(64);
        });
    }
}

public sealed class CreateEnrollmentTokenRequestValidator : AbstractValidator<CreateEnrollmentTokenRequest>
{
    public CreateEnrollmentTokenRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(EnrollmentToken.NameMaxLength);
        RuleFor(x => x.LifetimeHours).GreaterThan(0).When(x => x.LifetimeHours is not null);
        RuleFor(x => x.MaxUses).GreaterThan(0).When(x => x.MaxUses is not null);
    }
}

public sealed class CreateDeviceGroupRequestValidator : AbstractValidator<CreateDeviceGroupRequest>
{
    public CreateDeviceGroupRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceGroup.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(DeviceGroup.DescriptionMaxLength);
    }
}

public sealed class UpdateDeviceGroupRequestValidator : AbstractValidator<UpdateDeviceGroupRequest>
{
    public UpdateDeviceGroupRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceGroup.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(DeviceGroup.DescriptionMaxLength);
    }
}

public sealed class AddGroupDevicesRequestValidator : AbstractValidator<AddGroupDevicesRequest>
{
    public AddGroupDevicesRequestValidator()
    {
        RuleFor(x => x.DeviceIds).NotEmpty().Must(ids => ids.Count <= 1000).WithMessage("At most 1000 devices per request.");
    }
}

public sealed class SetDeviceGroupsRequestValidator : AbstractValidator<SetDeviceGroupsRequest>
{
    public SetDeviceGroupsRequestValidator()
    {
        RuleFor(x => x.GroupIds).NotNull().Must(ids => ids is null || ids.Count <= 100).WithMessage("At most 100 groups.");
    }
}

public sealed class UpdateDeviceRequestValidator : AbstractValidator<UpdateDeviceRequest>
{
    public UpdateDeviceRequestValidator()
    {
        RuleFor(x => x.DisplayName).MaximumLength(Device.DisplayNameMaxLength);
        RuleFor(x => x.Notes).MaximumLength(Device.NotesMaxLength);
    }
}

public sealed class AgentPackageRequestValidator : AbstractValidator<AgentPackageRequest>
{
    public AgentPackageRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(256);
    }
}
