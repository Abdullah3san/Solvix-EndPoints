namespace Solvix.Modules.Devices.Application.Agent;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Integration;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Operations an authenticated agent may perform. All of them are reports
/// from the device or requests for its own credentials/releases; none of the
/// responses can instruct the agent to run anything.
/// </summary>
public interface IAgentService
{
    Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken);

    Task SubmitInventoryAsync(InventoryReport report, CancellationToken cancellationToken);

    Task<CertificateRenewalResponse> RenewCertificateAsync(CertificateRenewalRequest request, CancellationToken cancellationToken);

    Task<UpdateCheckResponse> CheckForUpdateAsync(string? channel, string? currentVersion, CancellationToken cancellationToken);
}

internal sealed partial class AgentService(
    ISolvixDbContext db,
    ICurrentDevice currentDevice,
    IDeviceCertificateAuthority certificateAuthority,
    IAgentReleaseCatalog releases,
    PresenceTracker presence,
    MonitoringSettingsProvider monitoringSettings,
    EndpointIdentityPublisher identityPublisher,
    IEnumerable<IRemoteCommandQueue> commandQueues,
    IOptions<DevicesOptions> options,
    IClock clock,
    ILogger<AgentService> logger) : IAgentService
{
    /// <summary>
    /// Processes a heartbeat with as few writes as possible: the device row is
    /// written only when a descriptive value changed (version, health status, OS
    /// build) or the stored health report is due for a refresh; connectivity is
    /// handled by the <see cref="PresenceTracker"/>, which buffers plain contact.
    /// </summary>
    public async Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await LoadDeviceAsync(cancellationToken);
        var now = clock.UtcNow;
        var settings = options.Value;
        var previousVersion = device.AgentVersion;
        var healthDue = device.AgentHealthUpdatedAtUtc is not { } updated
            || now - updated >= TimeSpan.FromSeconds(settings.Monitoring.SignalPersistIntervalSeconds)
            || device.HealthStatus != request.Health.Status
            || !SameIssues(device.AgentHealthJson, request.Health);
        device.RecordHeartbeat(
            request.AgentVersion,
            request.Health.Status,
            healthDue ? JsonSerializer.Serialize(request.Health with { System = null, Network = null }, DevicesJson.Options) : null,
            request.Health.OsBuild,
            now);
        await db.SaveChangesAsync(cancellationToken); // no-op unless something changed

        if (!string.Equals(previousVersion, device.AgentVersion, StringComparison.Ordinal))
        {
            LogAgentVersionChanged(logger, device.Id, previousVersion, device.AgentVersion);
        }

        await presence.ProcessHeartbeatAsync(device, request.Health, now, cancellationToken);

        var monitoring = await monitoringSettings.GetAsync(cancellationToken);
        var renewalFrom = currentDevice.CredentialNotAfterUtc.AddDays(-settings.CertificateAuthority.RenewalWindowDays);
        return new HeartbeatResponse
        {
            ServerTimeUtc = now,
            Configuration = AgentConfigurationFactory.From(settings.Agent, monitoring.Thresholds.HeartbeatIntervalSeconds),
            InventoryRequested = device.IsInventoryRequested,
            CertificateRenewalRecommended = now >= renewalFrom,
            PendingCommands = await CountPendingCommandsAsync(device.Id, cancellationToken),
        };
    }

    private async Task<int> CountPendingCommandsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var pending = 0;
        foreach (var queue in commandQueues)
        {
            pending += await queue.CountPendingAsync(deviceId, cancellationToken);
        }

        return pending;
    }

    private static bool SameIssues(string? storedJson, AgentHealthReport current)
    {
        if (storedJson is null)
        {
            return false;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<AgentHealthReport>(storedJson, DevicesJson.Options);
            return stored is not null
                && stored.Issues.Count == current.Issues.Count
                && stored.Issues.Zip(current.Issues).All(p => p.First.Component == p.Second.Component && p.First.Severity == p.Second.Severity && p.First.Message == p.Second.Message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task SubmitInventoryAsync(InventoryReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var device = await LoadDeviceAsync(cancellationToken);
        var now = clock.UtcNow;
        var identityBefore = (device.SerialNumber, device.Manufacturer, device.Model);
        device.ApplyInventory(report, now);

        var inventory = await db.Set<DeviceInventory>().FirstOrDefaultAsync(i => i.DeviceId == device.Id, cancellationToken);
        if (inventory is null)
        {
            inventory = DeviceInventory.Create(device.TenantId, device.Id);
            db.Set<DeviceInventory>().Add(inventory);
        }

        inventory.Replace(
            report.SchemaVersion,
            report.CollectedAtUtc,
            now,
            report.ContentHash,
            report.Software.Count,
            JsonSerializer.Serialize(report, DevicesJson.Options));
        await db.SaveChangesAsync(cancellationToken);

        if (identityBefore != (device.SerialNumber, device.Manufacturer, device.Model))
        {
            await identityPublisher.PublishAsync(device.Id, EndpointIdentityChangeKind.HardwareChanged, cancellationToken);
        }
    }

    public async Task<CertificateRenewalResponse> RenewCertificateAsync(CertificateRenewalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var publicKey = CertificateSigningRequests.ParsePublicKey(request.CertificateSigningRequestPem);
        var device = await LoadDeviceAsync(cancellationToken);
        var now = clock.UtcNow;

        // At most one outstanding (issued but unused) renewal per device.
        var currentCredentialId = currentDevice.CredentialId;
        var pending = await db.Set<DeviceCredential>()
            .Where(c => c.DeviceId == device.Id && c.Id != currentCredentialId && c.RevokedAtUtc == null && c.FirstUsedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var credential in pending)
        {
            credential.Revoke(CredentialRevocationReason.Superseded, now);
        }

        var issued = certificateAuthority.Issue(
            publicKey, device.TenantId, device.Id, now.AddMinutes(-5), now.AddDays(options.Value.CertificateAuthority.DeviceCertificateLifetimeDays));
        db.Set<DeviceCredential>().Add(DeviceCredential.Create(
            device.TenantId, device.Id, issued.Thumbprint, issued.SerialNumber, issued.NotBeforeUtc, issued.NotAfterUtc));
        await db.SaveChangesAsync(cancellationToken);
        LogRenewed(logger, device.Id, issued.Thumbprint);

        return new CertificateRenewalResponse
        {
            CertificatePem = issued.Pem,
            CaCertificatesPem = [certificateAuthority.CertificatePem],
            CertificateNotAfterUtc = issued.NotAfterUtc,
        };
    }

    public async Task<UpdateCheckResponse> CheckForUpdateAsync(string? channel, string? currentVersion, CancellationToken cancellationToken)
    {
        var device = await LoadDeviceAsync(cancellationToken);
        var latest = releases.GetLatest(AgentReleaseCatalog.NormalizeChannel(channel ?? options.Value.Agent.DefaultUpdateChannel));
        if (latest is null)
        {
            return new UpdateCheckResponse { UpdateAvailable = false };
        }

        var installed = AgentVersion.TryParse(currentVersion, out var reported)
            ? reported
            : AgentVersion.TryParse(device.AgentVersion, out var known) ? known : null;
        var available = installed is null || AgentVersion.Parse(latest.Version) > installed;
        return new UpdateCheckResponse { UpdateAvailable = available, Release = available ? latest : null };
    }

    private async Task<Device> LoadDeviceAsync(CancellationToken cancellationToken)
    {
        var deviceId = currentDevice.DeviceId;
        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new UnauthorizedException("The device is not enrolled.", "device_not_enrolled");
        device.EnsureActive();
        return device;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent on device {DeviceId} changed version {PreviousVersion} -> {AgentVersion}")]
    private static partial void LogAgentVersionChanged(ILogger logger, Guid deviceId, string previousVersion, string agentVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issued renewed certificate for device {DeviceId} (sha256 {Thumbprint})")]
    private static partial void LogRenewed(ILogger logger, Guid deviceId, string thumbprint);
}
