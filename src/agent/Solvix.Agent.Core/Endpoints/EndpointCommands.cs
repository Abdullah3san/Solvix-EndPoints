namespace Solvix.Agent.Core.Endpoints;

using Microsoft.Extensions.Logging;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Patching;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.Software;
using Solvix.Agent.Core.State;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;

/// <summary>
/// Runs a verified endpoint management command (Phase 09) inside the privileged executor and returns its
/// JSON result. Only registered in the executor process on Windows.
/// </summary>
public interface IEndpointCommandExecutor
{
    Task<string> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Installs approved Windows updates and enforces configuration documents.</summary>
public sealed class EndpointCommandExecutor(PatchJobRunner patches, ConfigurationEvaluator configuration) : IEndpointCommandExecutor
{
    /// <summary>Room left for the envelope around the result within <see cref="CommandResultReport.MaxOutputLength"/>.</summary>
    internal const int MaxOutputLength = CommandResultReport.MaxOutputLength - 8 * 1024;

    public async Task<string> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.CommandType)
        {
            case CommandTypes.PatchInstall:
            {
                var job = Read<PatchJobDto>(envelope, "job");
                var result = await patches.RunAsync(job, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The patch job produced no result.");
                return ContractJson.Serialize(result);
            }

            case CommandTypes.ConfigurationApply:
            {
                var document = Read<ConfigurationDocument>(envelope, "document");
                return Fit(configuration.Evaluate(document));
            }

            default:
                throw new NotSupportedException($"'{envelope.CommandType}' is not an endpoint management command.");
        }
    }

    /// <summary>Keeps a large report within the command output limit (messages go first, then results).</summary>
    internal static string Fit(ConfigurationReport report)
    {
        var json = ContractJson.Serialize(report);
        if (json.Length <= MaxOutputLength)
        {
            return json;
        }

        var trimmed = report with { Results = report.Results.Select(r => r with { Message = null, ExpectedValue = Clip(r.ExpectedValue), ActualValue = Clip(r.ActualValue) }).ToList() };
        json = ContractJson.Serialize(trimmed);
        var count = trimmed.Results.Count;
        while (json.Length > MaxOutputLength && count > 0)
        {
            count = count * 3 / 4;
            json = ContractJson.Serialize(trimmed with { Results = trimmed.Results.Take(count).ToList() });
        }

        return json;
    }

    private static string? Clip(string? value) => value is { Length: > 200 } ? value[..200] : value;

    private static T Read<T>(CommandEnvelope envelope, string parameter)
        where T : class
    {
        var json = envelope.Parameters.GetValueOrDefault(parameter)
            ?? throw new FormatException($"Parameter '{parameter}' is missing.");
        try
        {
            return ContractJson.Deserialize<T>(json) ?? throw new FormatException($"Parameter '{parameter}' is empty.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FormatException($"Parameter '{parameter}' is not valid: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Agent side of <c>patch.install</c> and <c>configuration.apply</c>: hands the signed bytes to the
/// privileged executor service, which verifies them itself before changing anything.
/// </summary>
public sealed partial class EndpointCommandHandler(
    string commandType,
    ISoftwareExecutor executor,
    CommandKeyStore keys,
    IAgentStateStore state,
    ILogger<EndpointCommandHandler> logger) : ICommandHandler, ISignedCommandHandler
{
    public static readonly string[] Types = [CommandTypes.PatchInstall, CommandTypes.ConfigurationApply];

    public string CommandType { get; } = commandType;

    public TimeSpan MaxDuration(CommandEnvelope envelope) =>
        CommandType == CommandTypes.PatchInstall ? TimeSpan.FromHours(4) + TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(20);

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Endpoint management commands are executed with their signed bytes.");

    public async Task<CommandHandlerResult> ExecuteAsync(SignedCommandContext command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = state.Load();
        var response = await executor.ExecuteAsync(
            new ExecutorRequest
            {
                Command = command.Signed,
                Keys = keys.Export(),
                TenantId = current.TenantId ?? Guid.Empty,
                DeviceId = current.DeviceId ?? Guid.Empty,
            },
            cancellationToken).ConfigureAwait(false);
        if (response.RefusalCode is { } refused)
        {
            LogRefused(logger, command.Envelope.ExecutionId, refused);
            return CommandHandlerResult.Fail(refused, response.RefusalMessage ?? "The executor refused the command.");
        }

        if (response.Cancelled)
        {
            return new CommandHandlerResult(CommandOutcome.Cancelled, response.Output, null, "cancelled", "The operation was cancelled.");
        }

        return response.Output is null
            ? CommandHandlerResult.Fail("no_result", "The executor returned no result.")
            : new CommandHandlerResult(CommandOutcome.Succeeded, response.Output);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Executor refused endpoint command {ExecutionId}: {Code}")]
    private static partial void LogRefused(ILogger logger, Guid executionId, string code);
}
