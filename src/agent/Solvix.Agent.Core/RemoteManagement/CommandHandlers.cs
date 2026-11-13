namespace Solvix.Agent.Core.RemoteManagement;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Monitoring;

/// <summary>What a handler produced. Output is JSON for structured results.</summary>
public sealed record CommandHandlerResult(CommandOutcome Outcome, string? Output = null, int? ExitCode = null, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static CommandHandlerResult Json(JsonNode node) => new(CommandOutcome.Succeeded, node.ToJsonString());

    public static CommandHandlerResult Fail(string code, string message) => new(CommandOutcome.Failed, ErrorCode: code, ErrorMessage: message);
}

/// <summary>
/// Implements exactly one allow-listed command type. Handlers receive an already verified
/// envelope; they never interpret strings as code, paths or shell input.
/// </summary>
public interface ICommandHandler
{
    string CommandType { get; }

    Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// A handler that needs the exact signed bytes (to hand them to a privileged component that verifies
/// them itself) and may run longer than the default execution limit.
/// </summary>
public interface ISignedCommandHandler
{
    /// <summary>Upper bound for this command; the processor cancels the handler after it.</summary>
    TimeSpan MaxDuration(CommandEnvelope envelope);

    Task<CommandHandlerResult> ExecuteAsync(SignedCommandContext command, CancellationToken cancellationToken);
}

public sealed class CommandHandlerRegistry(IEnumerable<ICommandHandler> handlers)
{
    private readonly Dictionary<string, ICommandHandler> _handlers =
        handlers.Where(h => CommandTypes.All.Contains(h.CommandType)).ToDictionary(h => h.CommandType, StringComparer.Ordinal);

    public bool CanHandle(string commandType) => commandType == CommandTypes.Cancel || _handlers.ContainsKey(commandType);

    public ICommandHandler? Find(string commandType) => _handlers.GetValueOrDefault(commandType);
}

/// <summary>
/// Operations the unprivileged agent service cannot perform itself (restart, shutdown, sign-off,
/// lock the interactive session). On Windows they are handed to the separate Solvix Remote
/// Executor service, which re-verifies the signed envelope before acting.
/// </summary>
public interface IPrivilegedOperationHost
{
    Task<CommandHandlerResult> ExecuteAsync(SignedCommandContext command, CancellationToken cancellationToken);
}

/// <summary>A verified command together with the exact signed bytes, so a privileged host can verify it independently.</summary>
public sealed record SignedCommandContext(SignedCommand Signed, CommandEnvelope Envelope);

/// <summary>Used where no privileged executor is installed (and on non-Windows development builds).</summary>
public sealed class UnavailablePrivilegedOperationHost : IPrivilegedOperationHost
{
    public const string ErrorCode = "privileged_executor_unavailable";

    public Task<CommandHandlerResult> ExecuteAsync(SignedCommandContext command, CancellationToken cancellationToken) =>
        Task.FromResult(CommandHandlerResult.Fail(ErrorCode, "The Solvix Remote Executor service is not installed on this device."));
}

/// <summary>Forwards one privileged command type to <see cref="IPrivilegedOperationHost"/>.</summary>
public sealed class PrivilegedCommandHandler(string commandType) : ICommandHandler
{
    public static readonly string[] Types = [CommandTypes.Restart, CommandTypes.Shutdown, CommandTypes.Logoff, CommandTypes.Lock];

    public string CommandType { get; } = commandType;

    // Execution goes through CommandProcessor, which has the signed bytes; see ExecutePrivilegedAsync.
    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Privileged commands are executed with their signed bytes.");
}

public sealed class UptimeHandler(ISystemHealthProbe probe, TimeProvider time) : ICommandHandler
{
    public string CommandType => CommandTypes.Uptime;

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var booted = probe.GetBootTimeUtc();
        var now = time.GetUtcNow();
        return Task.FromResult(CommandHandlerResult.Json(new JsonObject
        {
            ["bootedAtUtc"] = booted.ToString("O", CultureInfo.InvariantCulture),
            ["uptimeSeconds"] = (long)(now - booted).TotalSeconds,
        }));
    }
}

public sealed class SystemInfoHandler(ISystemHealthProbe probe) : ICommandHandler
{
    public string CommandType => CommandTypes.SystemInfo;

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var health = probe.Capture();
        return Task.FromResult(CommandHandlerResult.Json(new JsonObject
        {
            ["hostname"] = Environment.MachineName,
            ["domain"] = Environment.UserDomainName,
            ["os"] = RuntimeInformation.OSDescription,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["totalMemoryBytes"] = health.TotalMemoryBytes,
            ["availableMemoryBytes"] = health.AvailableMemoryBytes,
            ["systemDrive"] = health.SystemDrive,
            ["systemDriveFreeBytes"] = health.SystemDriveFreeBytes,
            ["systemDriveTotalBytes"] = health.SystemDriveTotalBytes,
            ["rebootPending"] = health.RebootPending,
            ["bootedAtUtc"] = probe.GetBootTimeUtc().ToString("O", CultureInfo.InvariantCulture),
        }));
    }
}

/// <summary>Running processes: name, id, memory and start time. Command lines are deliberately not collected (they often contain secrets).</summary>
public sealed class ProcessesHandler : ICommandHandler
{
    public const int MaxProcesses = 2000;

    public string CommandType => CommandTypes.Processes;

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var list = new JsonArray();
        foreach (var process in Process.GetProcesses().OrderByDescending(SafeWorkingSet).Take(MaxProcesses))
        {
            using (process)
            {
                var entry = new JsonObject
                {
                    ["id"] = process.Id,
                    ["name"] = process.ProcessName,
                    ["workingSetBytes"] = SafeWorkingSet(process),
                };
                try
                {
                    entry["sessionId"] = process.SessionId;
                    entry["startedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Not visible to this account or already exited.
                }

                list.Add(entry);
            }
        }

        return Task.FromResult(CommandHandlerResult.Json(new JsonObject { ["count"] = list.Count, ["processes"] = list }));
    }

    private static long SafeWorkingSet(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}
