namespace Solvix.Agent.Windows;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Core.Api;
using Solvix.Agent.Core.Runtime;

/// <summary>Runs the agent loop with exponential back-off on errors.</summary>
internal sealed partial class AgentWorker(AgentRuntime runtime, ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(15);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await runtime.RunOnceAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(15);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (AgentUnauthorizedException ex)
            {
                // Retired or re-enrolled elsewhere: keep running quietly so an administrator can re-enrol.
                LogUnauthorized(logger, ex.Message);
                delay = TimeSpan.FromHours(1);
            }
            catch (Exception ex)
            {
                LogCycleFailed(logger, ex, backoff);
                delay = backoff;
                backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Device credential rejected: {Message}. Re-enrol the device with 'Solvix.Agent.exe enroll'.")]
    private static partial void LogUnauthorized(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent cycle failed; retrying in {Backoff}")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception, TimeSpan backoff);
}
