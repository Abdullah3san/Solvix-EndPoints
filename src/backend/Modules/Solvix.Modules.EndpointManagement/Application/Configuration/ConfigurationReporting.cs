namespace Solvix.Modules.EndpointManagement.Application.Configuration;

using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public sealed record DriftQuery(Guid? DeviceId, string? SettingKey, DateTimeOffset? SinceUtc, int Page = 1, int PageSize = 50);

public interface IConfigurationReportingService
{
    Task<ConfigurationComplianceDto> GetComplianceAsync(CancellationToken cancellationToken);

    Task<PagedResult<DriftEventDto>> ListDriftAsync(DriftQuery query, CancellationToken cancellationToken);

    IReadOnlyList<BaselineDto> ListBaselines();

    Task<ConfigurationPolicyDto> CreateFromBaselineAsync(CreateFromBaselineRequest request, CancellationToken cancellationToken);
}

internal sealed class ConfigurationReportingService(ISolvixDbContext db, IConfigurationPolicyService policies) : IConfigurationReportingService
{
    private static readonly SettingComplianceStatus[] OkStatuses =
        [SettingComplianceStatus.Compliant, SettingComplianceStatus.Applied, SettingComplianceStatus.Remediated, SettingComplianceStatus.NotApplicable];

    private static readonly SettingComplianceStatus[] ErrorStatuses =
        [SettingComplianceStatus.Failed, SettingComplianceStatus.Refused];

    public async Task<ConfigurationComplianceDto> GetComplianceAsync(CancellationToken cancellationToken)
    {
        var states = await (
                from s in db.Set<DeviceConfigurationState>().AsNoTracking()
                join d in db.Set<Device>().AsNoTracking() on s.DeviceId equals d.Id
                where s.IsDesired && d.Status == DeviceStatus.Active
                select new { s.DeviceId, s.PolicyId, s.Status })
            .ToListAsync(cancellationToken);
        var policyNames = await db.Set<ConfigurationPolicy>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        bool IsOk(SettingComplianceStatus? s) => s is { } v && OkStatuses.Contains(v);
        bool IsError(SettingComplianceStatus? s) => s is { } v && ErrorStatuses.Contains(v);

        var byDevice = states.GroupBy(s => s.DeviceId).ToList();
        var compliant = byDevice.Count(g => g.All(s => IsOk(s.Status)));
        var drift = byDevice.Count(g => g.Any(s => s.Status == SettingComplianceStatus.Drifted));
        var errors = byDevice.Count(g => g.Any(s => IsError(s.Status)));
        var pending = byDevice.Count(g => g.Any(s => s.Status is null) && !g.Any(s => s.Status == SettingComplianceStatus.Drifted || IsError(s.Status)));

        var policiesDto = states.Where(s => s.PolicyId is not null)
            .GroupBy(s => s.PolicyId!.Value)
            .Select(g =>
            {
                var devices = g.GroupBy(s => s.DeviceId).ToList();
                return new PolicyComplianceDto(
                    g.Key,
                    policyNames.GetValueOrDefault(g.Key, "(deleted)"),
                    devices.Count,
                    devices.Count(d => d.All(s => IsOk(s.Status))),
                    devices.Count(d => d.Any(s => s.Status == SettingComplianceStatus.Drifted)),
                    devices.Count(d => d.Any(s => IsError(s.Status))),
                    devices.Count(d => d.Any(s => s.Status is null)));
            })
            .OrderBy(p => p.PolicyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ConfigurationComplianceDto(
            byDevice.Count,
            compliant,
            drift,
            errors,
            pending,
            byDevice.Count == 0 ? 100 : Math.Round(compliant * 100.0 / byDevice.Count, 1),
            states.GroupBy(s => s.Status?.ToString() ?? "Pending").ToDictionary(g => g.Key, g => g.Count()),
            policiesDto);
    }

    public async Task<PagedResult<DriftEventDto>> ListDriftAsync(DriftQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var events = db.Set<ConfigurationDriftEvent>().AsNoTracking();
        if (query.DeviceId is { } deviceId)
        {
            events = events.Where(e => e.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(query.SettingKey))
        {
            events = events.Where(e => e.SettingKey == query.SettingKey);
        }

        if (query.SinceUtc is { } since)
        {
            events = events.Where(e => e.DetectedAtUtc >= since);
        }

        var total = await events.LongCountAsync(cancellationToken);
        var rows = await events.OrderByDescending(e => e.DetectedAtUtc).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<DriftEventDto>(
            rows.Select(e => new DriftEventDto(e.Id, e.DeviceId, e.SettingKey, e.PolicyVersionId, e.Status, e.Mode, e.ExpectedValue, e.ActualValue, e.DetectedAtUtc)).ToList(),
            page.SafePage,
            page.SafePageSize,
            total);
    }

    public IReadOnlyList<BaselineDto> ListBaselines() =>
        SecurityBaselines.All.Select(b => new BaselineDto(
            b.Id,
            b.Name,
            b.Description,
            b.Settings.Select(s => new SettingView(s.Key, s.Kind, s, SettingRiskClassifier.Classify(s), SettingRiskClassifier.Explain(s), s.Describe())).ToList()))
        .ToList();

    public Task<ConfigurationPolicyDto> CreateFromBaselineAsync(CreateFromBaselineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseline = SecurityBaselines.All.FirstOrDefault(b => b.Id == request.BaselineId)
            ?? throw new ValidationException("baselineId", "Unknown baseline.");
        return policies.CreateAsync(
            new CreateConfigurationPolicyRequest(request.Name, request.Description ?? baseline.Description, baseline.Settings, $"Created from baseline '{baseline.Name}'."),
            cancellationToken);
    }
}

/// <summary>
/// Built-in, reviewed configuration baselines ("approved security
/// configurations"). Creating a policy from one still produces a draft that
/// goes through the normal approval workflow.
/// </summary>
public static class SecurityBaselines
{
    public sealed record Baseline(string Id, string Name, string Description, IReadOnlyList<ConfigurationSetting> Settings);

    public static IReadOnlyList<Baseline> All { get; } =
    [
        new(
            "workstation-security",
            "Windows workstation security baseline",
            "Firewall on with inbound blocked on every profile, 15-minute machine inactivity lock, sign-in on wake, removable storage read-only, and remote registry and legacy services disabled.",
            [
                new FirewallProfileSetting(FirewallProfile.Domain, true, FirewallAction.Block, FirewallAction.Allow),
                new FirewallProfileSetting(FirewallProfile.Private, true, FirewallAction.Block, FirewallAction.Allow),
                new FirewallProfileSetting(FirewallProfile.Public, true, FirewallAction.Block, FirewallAction.Allow),
                new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 900),
                new ScreenLockSetting(ScreenLockControl.RequirePasswordOnWake, 1),
                new ScreenLockSetting(ScreenLockControl.DisableLockWorkstation, 0),
                new UsbStorageSetting(UsbDeviceClass.RemovableDisks, UsbAccess.ReadOnly),
                new UsbStorageSetting(UsbDeviceClass.PortableDevices, UsbAccess.ReadOnly),
                new ServiceSetting("RemoteRegistry", ServiceStartupType.Disabled, ServiceRunState.Stopped),
                new ServiceSetting("SSDPSRV", ServiceStartupType.Disabled, ServiceRunState.Stopped),
                new ServiceSetting("upnphost", ServiceStartupType.Disabled, ServiceRunState.Stopped),
                new ServiceSetting("WinDefend", null, ServiceRunState.Running),
                new ServiceSetting("mpssvc", ServiceStartupType.Automatic, ServiceRunState.Running),
                new RegistrySetting("SOFTWARE\\Policies\\Microsoft\\Windows\\Explorer", "NoAutoplayfornonVolume", RegistryValueType.DWord, "1"),
                new RegistrySetting("SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services", "fDisableCdm", RegistryValueType.DWord, "1"),
            ]),
        new(
            "power-saving",
            "Power saving (laptops)",
            "Display off after 10/5 minutes and sleep after 30/15 minutes (plugged in / on battery); closing the lid sleeps.",
            [
                new PowerSetting(PowerSettingType.DisplayTimeout, PowerSource.Ac, 600),
                new PowerSetting(PowerSettingType.DisplayTimeout, PowerSource.Dc, 300),
                new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 1800),
                new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Dc, 900),
                new PowerSetting(PowerSettingType.LidCloseAction, PowerSource.Ac, 1),
                new PowerSetting(PowerSettingType.LidCloseAction, PowerSource.Dc, 1),
            ]),
        new(
            "kiosk-lockdown",
            "Kiosk lock-down",
            "No removable storage at all, never sleep while plugged in, 5-minute inactivity lock.",
            [
                new UsbStorageSetting(UsbDeviceClass.AllRemovableStorage, UsbAccess.NoAccess),
                new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 0),
                new PowerSetting(PowerSettingType.DisplayTimeout, PowerSource.Ac, 0),
                new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 300),
            ]),
    ];
}
