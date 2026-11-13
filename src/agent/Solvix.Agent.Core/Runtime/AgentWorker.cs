namespace Solvix.Agent.Core.Runtime;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Core.Communication;

/// <summary>Hosted service driving <see cref="AgentRuntime"/> until the service stops.</summary>
public sealed partial class AgentWorker(AgentRuntime runtime, TimeProvider time, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, AgentInfo.Version, Environment.OSVersion.VersionString);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await runtime.RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bug or local fault must never stop the service; retry later.
                LogCycleFailed(logger, ex);
                delay = TimeSpan.FromMinutes(1);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Solvix agent {Version} started on {Os}")]
    private static partial void LogStarted(ILogger logger, string version, string os);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent cycle failed unexpectedly")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Solvix agent stopped")]
    private static partial void LogStopped(ILogger logger);
}
