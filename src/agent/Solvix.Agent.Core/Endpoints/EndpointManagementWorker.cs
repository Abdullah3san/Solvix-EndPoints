namespace Solvix.Agent.Core.Endpoints;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Patching;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;
using Solvix.Endpoint.Contracts.Agent;

/// <summary>
/// Read-only side of endpoint management (Phase 09) in the unprivileged agent: reports restarts, uploads the
/// Windows Update inventory when the server asks (or daily) and checks the desired configuration, reporting
/// compliance and drift. Installing, enforcing and restarting arrive as signed commands for the executor.
/// </summary>
public sealed partial class EndpointManagementWorker(
    IEndpointManagementClient client,
    IAgentStateStore state,
    DeviceCredentialStore credentials,
    IRebootManager reboot,
    IWindowsUpdateClient windowsUpdate,
    IEnumerable<ISettingProvider> providers,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan InventoryMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan ConfigurationRecheck = TimeSpan.FromMinutes(30);

    private readonly ILogger<EndpointManagementWorker> _logger = loggers.CreateLogger<EndpointManagementWorker>();
    private readonly ExponentialBackoff _backoff = new(new BackoffOptions { InitialSeconds = 30, MaxSeconds = 900 });
    private readonly ConfigurationEvaluator _evaluator = new(providers, new InMemorySnapshotStore(), new InMemoryAppliedSettingsLedger(), time, loggers.CreateLogger<ConfigurationEvaluator>())
    {
        ReadOnly = true,
    };

    private DateTimeOffset? _lastScan;
    private DateTimeOffset? _lastEvaluation;
    private ConfigurationDocument? _document;
    private string? _evaluatedRevision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                delay = _backoff.Delay(++failures);
                LogFailed(_logger, ex.Message);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!state.Load().IsEnrolled || credentials.GetClientCertificate() is null)
        {
            return TimeSpan.FromMinutes(1);
        }

        var work = await client.GetEndpointWorkAsync(new AgentWorkRequest(reboot.LastBootUtc), cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (work.InventoryRequested || _lastScan is not { } scanned || now - scanned > InventoryMaxAge)
        {
            var report = await windowsUpdate.ScanAsync(cancellationToken).ConfigureAwait(false);
            await client.SubmitPatchInventoryAsync(report, cancellationToken).ConfigureAwait(false);
            _lastScan = now;
        }

        if (!string.Equals(work.ConfigurationRevision, _evaluatedRevision, StringComparison.Ordinal)
            || _lastEvaluation is not { } evaluated || now - evaluated > ConfigurationRecheck)
        {
            _document = await client.GetConfigurationAsync(_document?.Revision, cancellationToken).ConfigureAwait(false) ?? _document;
            if (_document is not null)
            {
                var report = _evaluator.Evaluate(_document);
                await client.SubmitConfigurationReportAsync(report with { Results = report.Results.Take(2000).ToList() }, cancellationToken).ConfigureAwait(false);
                _evaluatedRevision = _document.Revision;
                _lastEvaluation = now;
            }
        }

        return TimeSpan.FromSeconds(Math.Clamp(work.PollIntervalSeconds, 30, 3600));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint management cycle failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
