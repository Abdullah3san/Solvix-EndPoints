namespace Solvix.Agent.Core.Runtime;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Enrollment;
using Solvix.Agent.Core.Health;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;
using Solvix.Agent.Core.Updates;

/// <summary>
/// The agent's control loop, one cycle at a time. Strictly outbound: it
/// enrolls, sends heartbeats, uploads inventory, renews its certificate and
/// checks for updates on its own schedule. Server responses can only adjust
/// schedules (clamped to local bounds) or set well-known boolean hints mapped
/// to the built-in actions above — there is no code path that executes
/// anything received from the server.
/// </summary>
public sealed partial class AgentRuntime(
    IAgentApiClient api,
    EnrollmentCoordinator enrollment,
    BootstrapTokenStore bootstrap,
    DeviceCredentialStore credentials,
    IAgentStateStore stateStore,
    IInventoryService inventory,
    UpdateManager updates,
    AgentHealthMonitor health,
    ConnectionMonitor connection,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<AgentRuntime> logger)
{
    public const string CommunicationComponent = "communication";
    public const string CredentialComponent = "credential";
    public const string EnrollmentComponent = "enrollment";
    public const string InventoryComponent = "inventory";
    public const string UpdatesComponent = "updates";

    private static readonly TimeSpan NoTokenPollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RejectedTokenPollInterval = TimeSpan.FromMinutes(5);

    private readonly ExponentialBackoff _backoff = new(options.Value.Backoff);
    private DateTimeOffset _nextHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _nextUpdateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _inventoryRetryAfter = DateTimeOffset.MinValue;
    private bool _inventoryRequested;
    private bool _renewalRecommended;
    private int _enrollmentFailures;

    /// <summary>Runs every operation that is due and returns how long to wait before the next cycle.</summary>
    public async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Load();
        if (!state.IsEnrolled || credentials.GetClientCertificate() is null)
        {
            return await EnrollAsync(cancellationToken);
        }

        try
        {
            if (time.GetUtcNow() >= _nextHeartbeat)
            {
                await HeartbeatAsync(cancellationToken);
            }

            if (_inventoryRequested || IsInventoryDue(stateStore.Load()))
            {
                await InventoryAsync(cancellationToken);
            }

            if (_renewalRecommended || IsLocalRenewalDue(stateStore.Load()))
            {
                await RenewCertificateAsync(cancellationToken);
            }

            if (time.GetUtcNow() >= _nextUpdateCheck)
            {
                await CheckForUpdatesAsync(cancellationToken);
            }
        }
        catch (AgentApiException ex)
        {
            return HandleFailure(ex);
        }

        var wait = _nextHeartbeat - time.GetUtcNow();
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    internal static AgentConfiguration Clamp(AgentConfiguration configuration) => new()
    {
        HeartbeatIntervalSeconds = Math.Clamp(configuration.HeartbeatIntervalSeconds, 15, 3600),
        InventoryIntervalMinutes = Math.Clamp(configuration.InventoryIntervalMinutes, 15, 7 * 24 * 60),
        UpdateCheckIntervalMinutes = Math.Clamp(configuration.UpdateCheckIntervalMinutes, 15, 7 * 24 * 60),
    };

    private async Task<TimeSpan> EnrollAsync(CancellationToken cancellationToken)
    {
        var result = await enrollment.EnrollAsync(cancellationToken);
        switch (result)
        {
            case EnrollmentResult.Enrolled:
                _enrollmentFailures = 0;
                _nextHeartbeat = DateTimeOffset.MinValue;
                health.Clear(EnrollmentComponent);
                health.Clear(CredentialComponent);
                connection.RecordSuccess();
                return TimeSpan.Zero;
            case EnrollmentResult.NoToken:
                health.Report(EnrollmentComponent, HealthIssueSeverity.Error, "Not enrolled: no enrollment token has been supplied.");
                return NoTokenPollInterval;
            case EnrollmentResult.TokenRejected:
                health.Report(EnrollmentComponent, HealthIssueSeverity.Error, "Enrollment token rejected; a new token is required.");
                return RejectedTokenPollInterval;
            default:
                return _backoff.Delay(++_enrollmentFailures);
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Load();
        var report = health.Build(state.LastInventoryAtUtc, state.CertificateNotAfterUtc, Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture), connection.ConsecutiveFailures);
        var response = await api.HeartbeatAsync(new HeartbeatRequest { AgentVersion = AgentInfo.Version, Health = report }, cancellationToken);

        var recoveredAfter = connection.RecordSuccess();
        if (recoveredAfter > 0)
        {
            LogReconnected(logger, recoveredAfter);
        }

        health.Clear(CommunicationComponent);
        health.Clear(CredentialComponent);
        var configuration = Clamp(response.Configuration);
        _inventoryRequested |= response.InventoryRequested;
        _renewalRecommended = response.CertificateRenewalRecommended;
        _nextHeartbeat = time.GetUtcNow().AddSeconds(configuration.HeartbeatIntervalSeconds);

        // The new certificate was accepted: the previous key is no longer needed.
        if (state.RetiredKeyName is not null)
        {
            credentials.DeleteKey(state.RetiredKeyName);
        }

        stateStore.Save(stateStore.Load() with
        {
            Configuration = configuration,
            LastSuccessfulContactAtUtc = time.GetUtcNow(),
            RetiredKeyName = null,
        });
    }

    private bool IsInventoryDue(AgentState state)
    {
        var now = time.GetUtcNow();
        if (now < _inventoryRetryAfter)
        {
            return false;
        }

        return state.LastInventoryAtUtc is not { } last
            || now >= last.AddMinutes(Clamp(state.Configuration).InventoryIntervalMinutes);
    }

    private async Task InventoryAsync(CancellationToken cancellationToken)
    {
        InventoryReport report;
        try
        {
            report = await inventory.CollectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            health.Report(InventoryComponent, HealthIssueSeverity.Error, "Inventory collection failed: " + ex.Message);
            _inventoryRetryAfter = time.GetUtcNow().AddMinutes(15);
            return;
        }

        var state = stateStore.Load();
        var now = time.GetUtcNow();
        var unchanged = report.ContentHash == state.LastInventoryHash
            && state.LastInventoryAtUtc is { } last
            && now < last.AddHours(options.Value.InventoryMaxAgeHours);
        if (unchanged && !_inventoryRequested)
        {
            // Nothing changed: just restart the interval.
            stateStore.Save(state with { LastInventoryAtUtc = now });
            return;
        }

        await api.SubmitInventoryAsync(report, cancellationToken);
        _inventoryRequested = false;
        health.Clear(InventoryComponent);
        if (report.CollectionIssues.Count > 0)
        {
            health.Report(InventoryComponent, HealthIssueSeverity.Warning, $"{report.CollectionIssues.Count} inventory module(s) reported problems.");
        }

        stateStore.Save(stateStore.Load() with { LastInventoryHash = report.ContentHash, LastInventoryAtUtc = now });
        LogInventorySent(logger, report.Software.Count, report.ContentHash);
    }

    private bool IsLocalRenewalDue(AgentState state)
    {
        if (state.CertificateNotAfterUtc is not { } notAfter || state.CertificateIssuedAtUtc is not { } issued)
        {
            return false;
        }

        // Renew when less than 30% of the lifetime remains, even if the server never asked.
        var lifetime = notAfter - issued;
        return time.GetUtcNow() >= notAfter - (lifetime * 0.3);
    }

    private async Task RenewCertificateAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Load();
        var (keyName, csr) = credentials.CreateSigningRequest(Environment.MachineName);
        CertificateRenewalResponse response;
        try
        {
            response = await api.RenewCertificateAsync(new CertificateRenewalRequest { CertificateSigningRequestPem = csr }, cancellationToken);
        }
        catch
        {
            credentials.DeleteKey(keyName);
            throw;
        }

        credentials.Install(keyName, response.CertificatePem, response.CaCertificatesPem);
        stateStore.Save(state with
        {
            KeyName = keyName,
            RetiredKeyName = state.KeyName,
            CertificateIssuedAtUtc = time.GetUtcNow(),
            CertificateNotAfterUtc = response.CertificateNotAfterUtc,
        });
        _renewalRecommended = false;
        _nextHeartbeat = DateTimeOffset.MinValue; // use the new certificate right away
        LogRenewed(logger, response.CertificateNotAfterUtc);
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Updates;
        var interval = Clamp(stateStore.Load().Configuration).UpdateCheckIntervalMinutes;
        _nextUpdateCheck = time.GetUtcNow().AddMinutes(interval);
        if (!settings.Enabled)
        {
            return;
        }

        var check = await api.CheckForUpdateAsync(settings.Channel, AgentInfo.Version, cancellationToken);
        try
        {
            var outcome = await updates.ProcessAsync(check, AgentInfo.Version, cancellationToken);
            if (outcome == UpdateOutcome.Refused)
            {
                health.Report(UpdatesComponent, HealthIssueSeverity.Warning, $"Update {check.Release?.Version} was refused by local policy.");
            }
            else
            {
                health.Clear(UpdatesComponent);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            health.Report(UpdatesComponent, HealthIssueSeverity.Warning, "Update download failed: " + ex.Message);
        }
    }

    private TimeSpan HandleFailure(AgentApiException ex)
    {
        connection.RecordFailure(ex);
        health.Report(CommunicationComponent, HealthIssueSeverity.Warning, ex.Message);
        LogCallFailed(logger, ex.Failure, connection.ConsecutiveFailures, ex.Message);

        if (ex.Failure == AgentApiFailure.Authentication
            && connection.ConsecutiveAuthenticationFailures >= Math.Max(1, options.Value.MaxAuthenticationFailures))
        {
            // The server no longer accepts this identity (revoked, retired or deleted device).
            if (bootstrap.HasToken)
            {
                LogReEnrolling(logger);
                credentials.Reset();
                stateStore.Save(stateStore.Load() with { KeyName = null, DeviceId = null, TenantId = null, RetiredKeyName = null });
                return TimeSpan.Zero;
            }

            health.Report(CredentialComponent, HealthIssueSeverity.Error, "The server rejects this device's credential. Re-enroll with a new enrollment token.");
        }

        // Re-send the heartbeat first once connectivity returns.
        _nextHeartbeat = DateTimeOffset.MinValue;
        return _backoff.Delay(connection.ConsecutiveFailures);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to the server after {Failures} failed attempt(s)")]
    private static partial void LogReconnected(ILogger logger, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server call failed ({Failure}, {Failures} consecutive): {Message}")]
    private static partial void LogCallFailed(ILogger logger, AgentApiFailure failure, int failures, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device credential rejected repeatedly; re-enrolling with the supplied enrollment token")]
    private static partial void LogReEnrolling(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inventory uploaded ({Applications} applications, hash {Hash})")]
    private static partial void LogInventorySent(ILogger logger, int applications, string? hash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device certificate renewed; valid until {NotAfter:O}")]
    private static partial void LogRenewed(ILogger logger, DateTimeOffset notAfter);
}
