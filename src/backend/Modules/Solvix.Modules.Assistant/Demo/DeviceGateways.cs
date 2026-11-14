namespace Solvix.Modules.Assistant.Demo;

using System.Collections.Concurrent;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;

/// <summary>Used until a device module provides real fleet data: every call reports that clearly instead of fabricating data.</summary>
internal sealed class NotConnectedFleet : IFleetQueryService
{
    public bool IsAvailable => false;

    public Task<FleetSummary> GetSummaryAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken) => throw Unavailable();

    public Task<(IReadOnlyList<DeviceSummary> Items, long Total)> SearchAsync(FleetAccess access, DeviceSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken) => throw Unavailable();

    public Task<DeviceResolution> ResolveAsync(FleetAccess access, IReadOnlyList<string> references, CancellationToken cancellationToken) => throw Unavailable();

    public Task<DeviceDetail?> GetDetailAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken) => throw Unavailable();

    public Task<PerformanceReport?> GetPerformanceAsync(FleetAccess access, Guid deviceId, TimeSpan window, CancellationToken cancellationToken) => throw Unavailable();

    public Task<HardwareDiagnostics?> GetHardwareDiagnosticsAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken) => throw Unavailable();

    public Task<IReadOnlyList<InstalledSoftware>?> GetInstalledSoftwareAsync(FleetAccess access, Guid deviceId, string? search, CancellationToken cancellationToken) => throw Unavailable();

    public Task<IReadOnlyList<FleetSoftwareEntry>> FindSoftwareAsync(FleetAccess access, SoftwareSearchCriteria criteria, int take, CancellationToken cancellationToken) => throw Unavailable();

    public Task<SecurityPosture> GetSecurityPostureAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken) => throw Unavailable();

    private static ServiceUnavailableException Unavailable() => new("No fleet data source is connected to this workspace yet.", "fleet_unavailable");
}

internal sealed class NotConnectedCommandGateway : IDeviceCommandGateway
{
    public bool IsAvailable => false;

    public Task<DeviceCommandValidation> ValidateAsync(FleetAccess access, DeviceCommandRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(DeviceCommandValidation.Denied("No device command service is connected."));

    public Task<IReadOnlyList<DeviceCommandOutcome>> ExecuteAsync(FleetAccess access, DeviceCommandRequest request, CancellationToken cancellationToken) =>
        throw new ServiceUnavailableException("No device command service is connected.", "fleet_unavailable");
}

/// <summary>
/// Demo stand-in for the command module. Shows what a real gateway's semantic validation looks like
/// (service deny-list, approved package catalog) and records what it was asked to run; it touches no device.
/// </summary>
internal sealed class DemoCommandGateway(IFleetQueryService fleet) : IDeviceCommandGateway
{
    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase) { "WinDefend", "EventLog", "RpcSs", "SolvixAgent", "mpssvc", "wuauserv" };
    private static readonly HashSet<string> ApprovedPackages = new(StringComparer.OrdinalIgnoreCase) { "7zip", "chrome", "vlc", "acrobat-reader", "notepadplusplus", "slack" };

    /// <summary>Executed demo commands (process-wide); used by tests to prove nothing ran without approval.</summary>
    public static ConcurrentQueue<DeviceCommandRequest> Executed { get; } = new();

    public static int GetExecutedCount() => Executed.Count;

    public bool IsAvailable => true;

    public async Task<DeviceCommandValidation> ValidateAsync(FleetAccess access, DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        var resolution = await fleet.ResolveAsync(access, request.DeviceIds.Select(i => i.ToString()).ToList(), cancellationToken);
        if (resolution.Devices.Count != request.DeviceIds.Count)
        {
            problems.Add("One or more devices do not exist or are not accessible.");
        }

        if (request.Parameters.TryGetValue("serviceName", out var service) && ProtectedServices.Contains(service))
        {
            problems.Add($"Service '{service}' is protected and cannot be restarted remotely.");
        }

        if (request.Parameters.TryGetValue("packageId", out var package) && !ApprovedPackages.Contains(package))
        {
            problems.Add($"Package '{package}' is not in the approved software catalog.");
        }

        return problems.Count == 0 ? DeviceCommandValidation.Allowed : new DeviceCommandValidation(false, problems);
    }

    public async Task<IReadOnlyList<DeviceCommandOutcome>> ExecuteAsync(FleetAccess access, DeviceCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Executed.Enqueue(request);
        var resolution = await fleet.ResolveAsync(access, request.DeviceIds.Select(i => i.ToString()).ToList(), cancellationToken);
        return resolution.Devices.Select(d => d.Name == "POS-013"
            ? new DeviceCommandOutcome(d.Id, DeviceCommandStatus.Failed, "Simulated failure.")
            : d.Status == DeviceConnectivity.Online
                ? new DeviceCommandOutcome(d.Id, DeviceCommandStatus.Succeeded, "Completed (demo; no real device was touched).")
                : new DeviceCommandOutcome(d.Id, DeviceCommandStatus.Queued, "Queued until the device reconnects (demo).")).ToList();
    }
}
