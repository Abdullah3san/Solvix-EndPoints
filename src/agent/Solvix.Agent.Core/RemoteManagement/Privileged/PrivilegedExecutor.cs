namespace Solvix.Agent.Core.RemoteManagement.Privileged;

using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.State;

/// <summary>Power and session operations that need privileges the agent service deliberately does not have.</summary>
public interface IPowerOperations
{
    /// <summary>Restarts the computer after <paramref name="delay"/>, showing <paramref name="message"/> to signed-in users.</summary>
    Task RestartAsync(TimeSpan delay, string? message, CancellationToken cancellationToken);

    Task ShutdownAsync(TimeSpan delay, string? message, CancellationToken cancellationToken);

    /// <summary>Signs the interactive (console) user off.</summary>
    Task LogOffAsync(CancellationToken cancellationToken);

    /// <summary>Locks the interactive (console) session.</summary>
    Task LockAsync(CancellationToken cancellationToken);
}

public sealed class PowerOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Executes one of the four privileged command types through <see cref="IPowerOperations"/>.
/// Parameters are validated again here: the executor never relies on the server or the agent to have done it.
/// </summary>
public sealed class PowerCommandHandler(string commandType, IPowerOperations operations) : ICommandHandler
{
    public const int MaxDelaySeconds = 600;
    public const int MaxMessageLength = 200;

    public string CommandType { get; } = commandType;

    public async Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var delaySeconds = 0;
        string? message = null;
        foreach (var (key, value) in envelope.Parameters)
        {
            switch (key)
            {
                case "delaySeconds" when CommandType is CommandTypes.Restart or CommandTypes.Shutdown:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out delaySeconds) || delaySeconds > MaxDelaySeconds)
                    {
                        return CommandHandlerResult.Fail("invalid_parameter", "delaySeconds is out of range.");
                    }

                    break;
                case "message" when CommandType is CommandTypes.Restart or CommandTypes.Shutdown or CommandTypes.Logoff:
                    if (value.Length > MaxMessageLength || value.Any(char.IsControl))
                    {
                        return CommandHandlerResult.Fail("invalid_parameter", "message is not valid.");
                    }

                    message = value;
                    break;
                default:
                    return CommandHandlerResult.Fail("invalid_parameter", $"Unexpected parameter '{key}'.");
            }
        }

        try
        {
            switch (CommandType)
            {
                case CommandTypes.Restart:
                    await operations.RestartAsync(TimeSpan.FromSeconds(delaySeconds), message, cancellationToken);
                    break;
                case CommandTypes.Shutdown:
                    await operations.ShutdownAsync(TimeSpan.FromSeconds(delaySeconds), message, cancellationToken);
                    break;
                case CommandTypes.Logoff:
                    await operations.LogOffAsync(cancellationToken);
                    break;
                case CommandTypes.Lock:
                    await operations.LockAsync(cancellationToken);
                    break;
                default:
                    return CommandHandlerResult.Fail("not_implemented", CommandType);
            }
        }
        catch (PowerOperationException ex)
        {
            return CommandHandlerResult.Fail(ex.Code, ex.Message);
        }

        return CommandHandlerResult.Json(new JsonObject { ["accepted"] = true, ["delaySeconds"] = delaySeconds });
    }
}

public sealed record PrivilegedRequest
{
    public required SignedCommand Command { get; init; }
}

public sealed record PrivilegedResponse
{
    /// <summary>Set when the executor refused the command (see <see cref="CommandRejectionCodes"/>).</summary>
    public string? RejectionCode { get; init; }

    public string? RejectionDetail { get; init; }

    public CommandOutcome Outcome { get; init; }

    public string? Output { get; init; }

    public int? ExitCode { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PrivilegedRequest))]
[JsonSerializable(typeof(PrivilegedResponse))]
[JsonSerializable(typeof(SignedCommand))]
[JsonSerializable(typeof(PinnedIdentity))]
internal sealed partial class PrivilegedJsonContext : JsonSerializerContext
{
}

public sealed record PinnedIdentity
{
    public Guid TenantId { get; init; }

    public Guid DeviceId { get; init; }
}

/// <summary>Length-prefixed JSON messages (4 bytes little endian, at most 128 KiB).</summary>
internal static class PipeMessages
{
    public const int MaxLength = 128 * 1024;

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaxLength)
        {
            throw new InvalidDataException("Invalid message length.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}

/// <summary>
/// The privileged side. It trusts neither the agent nor the files the agent can write: it keeps its
/// own pinned command keys, its own pinned tenant/device identity and its own replay store in a
/// separate directory (readable by SYSTEM and administrators only on Windows), seeded once from the
/// agent's state the first time a command arrives, and verifies every envelope independently.
/// </summary>
public sealed partial class PrivilegedExecutor
{
    private readonly CommandVerifier _verifier;
    private readonly CommandReplayStore _replays;
    private readonly CommandHandlerRegistry _handlers;
    private readonly Func<CommandKeySet> _agentKeys;
    private readonly Func<AgentState> _agentState;
    private readonly string _directory;
    private readonly CommandKeyStore _keys;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    public PrivilegedExecutor(
        IPowerOperations operations,
        CommandKeyStore agentKeyStore,
        IAgentStateStore agentState,
        IOptions<AgentOptions> agentOptions,
        TimeProvider time,
        ILogger<PrivilegedExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(agentOptions);
        _logger = logger;
        _directory = Path.Combine(agentOptions.Value.ResolveDataDirectory(), "executor");
        var executorOptions = Options.Create(new AgentOptions
        {
            DataDirectory = _directory,
            RemoteManagement = agentOptions.Value.RemoteManagement,
        });
        _agentKeys = agentKeyStore.Export;
        _agentState = agentState.Load;
        _keys = new CommandKeyStore(executorOptions);
        _replays = new CommandReplayStore(executorOptions, time);
        _handlers = new CommandHandlerRegistry(PrivilegedCommandHandler.Types.Select(t => (ICommandHandler)new PowerCommandHandler(t, operations)));
        _verifier = new CommandVerifier(_keys, _replays, new PinnedIdentityStore(this), _handlers, executorOptions, time);
    }

    public async Task<PrivilegedResponse> ExecuteAsync(SignedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!EnsureTrust())
        {
            return Reject(CommandRejectionCodes.NotAuthorized, "The executor has no trusted identity or signing key yet.");
        }

        var verification = _verifier.Verify(command);
        if (!verification.Accepted)
        {
            LogRejected(_logger, verification.RejectionCode!);
            return Reject(verification.RejectionCode!, verification.Detail);
        }

        var envelope = verification.Envelope!;
        if (!PrivilegedCommandHandler.Types.Contains(envelope.CommandType, StringComparer.Ordinal))
        {
            return Reject(CommandRejectionCodes.NotAllowed, "Not a privileged command.");
        }

        if (!_replays.TryRecord(envelope))
        {
            return Reject(CommandRejectionCodes.Replayed, "This command was already executed.");
        }

        var result = await _handlers.Find(envelope.CommandType)!.ExecuteAsync(envelope, cancellationToken);
        LogExecuted(_logger, envelope.ExecutionId, envelope.CommandType, result.Outcome);
        return new PrivilegedResponse
        {
            Outcome = result.Outcome,
            Output = result.Output,
            ExitCode = result.ExitCode,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
        };
    }

    /// <summary>
    /// First use: adopt the agent's enrolled identity and pinned keys. After that the executor's own
    /// copies are authoritative, so a later change to the agent's files cannot redirect it.
    /// </summary>
    private bool EnsureTrust()
    {
        lock (_gate)
        {
            if (!_keys.IsPinned && !_keys.PinIfEmpty(_agentKeys()))
            {
                return false;
            }

            if (ReadIdentity() is null)
            {
                var state = _agentState();
                if (state.TenantId is not { } tenantId || state.DeviceId is not { } deviceId)
                {
                    return false;
                }

                Directory.CreateDirectory(_directory);
                AtomicFile.WriteAllText(IdentityPath, JsonSerializer.Serialize(new PinnedIdentity { TenantId = tenantId, DeviceId = deviceId }, PrivilegedJsonContext.Default.PinnedIdentity));
            }

            return true;
        }
    }

    private string IdentityPath => Path.Combine(_directory, "identity.json");

    private PinnedIdentity? ReadIdentity() =>
        File.Exists(IdentityPath) ? JsonSerializer.Deserialize(File.ReadAllText(IdentityPath), PrivilegedJsonContext.Default.PinnedIdentity) : null;

    private static PrivilegedResponse Reject(string code, string? detail) => new() { RejectionCode = code, RejectionDetail = detail, Outcome = CommandOutcome.Failed };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Privileged command refused: {Code}")]
    private static partial void LogRejected(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Privileged command {ExecutionId} ({CommandType}) finished: {Outcome}")]
    private static partial void LogExecuted(ILogger logger, Guid executionId, string commandType, CommandOutcome outcome);

    private sealed class PinnedIdentityStore(PrivilegedExecutor owner) : IAgentStateStore
    {
        public AgentState Load()
        {
            var identity = owner.ReadIdentity();
            return new AgentState { TenantId = identity?.TenantId, DeviceId = identity?.DeviceId, KeyName = "executor" };
        }

        public void Save(AgentState state) => throw new NotSupportedException();
    }
}

/// <summary>Creates the server side of the executor pipe. The Windows implementation restricts who may connect.</summary>
public interface IExecutorPipeFactory
{
    NamedPipeServerStream Create(string name);
}

/// <summary>Portable implementation: current user only. Used by tests and non-Windows development.</summary>
public sealed class CurrentUserPipeFactory : IExecutorPipeFactory
{
    public NamedPipeServerStream Create(string name) =>
        new(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}

/// <summary>Listens on the local pipe and passes each request to <see cref="PrivilegedExecutor"/>.</summary>
public sealed partial class PrivilegedExecutorServer(
    PrivilegedExecutor executor,
    IExecutorPipeFactory pipes,
    IOptions<AgentOptions> options,
    ILogger<PrivilegedExecutorServer> logger) : BackgroundService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = options.Value.RemoteManagement.ExecutorPipeName;
        LogListening(logger, name);
        var connections = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = pipes.Create(name);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException ex)
            {
                await pipe.DisposeAsync();
                LogPipeFailed(logger, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            connections.Add(HandleAsync(pipe, stoppingToken));
            connections.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(connections);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var bytes = await PipeMessages.ReadAsync(pipe, timeout.Token);
                var request = JsonSerializer.Deserialize(bytes, PrivilegedJsonContext.Default.PrivilegedRequest);
                var response = request is null
                    ? new PrivilegedResponse { RejectionCode = CommandRejectionCodes.Malformed, Outcome = CommandOutcome.Failed }
                    : await executor.ExecuteAsync(request.Command, timeout.Token);
                await PipeMessages.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response, PrivilegedJsonContext.Default.PrivilegedResponse), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                LogRequestFailed(logger, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote Executor listening on pipe {Name}")]
    private static partial void LogListening(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Executor pipe error: {Message}")]
    private static partial void LogPipeFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Executor request failed: {Message}")]
    private static partial void LogRequestFailed(ILogger logger, string message);
}

/// <summary>Agent side: sends the signed command to the Remote Executor over the local pipe.</summary>
public sealed class PipePrivilegedOperationHost(IOptions<AgentOptions> options) : IPrivilegedOperationHost
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<CommandHandlerResult> ExecuteAsync(SignedCommandContext command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var pipe = new NamedPipeClientStream(".", options.Value.RemoteManagement.ExecutorPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connect.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return CommandHandlerResult.Fail(UnavailablePrivilegedOperationHost.ErrorCode, "The Solvix Remote Executor service is not running on this device.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PrivilegedExecutorServer.RequestTimeout);
        await PipeMessages.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new PrivilegedRequest { Command = command.Signed }, PrivilegedJsonContext.Default.PrivilegedRequest), timeout.Token);
        var response = JsonSerializer.Deserialize(await PipeMessages.ReadAsync(pipe, timeout.Token), PrivilegedJsonContext.Default.PrivilegedResponse)
            ?? throw new InvalidDataException("Empty executor response.");
        return response.RejectionCode is { } code
            ? CommandHandlerResult.Fail("executor_rejected_" + code, response.RejectionDetail ?? "The Remote Executor refused the command.")
            : new CommandHandlerResult(response.Outcome, response.Output, response.ExitCode, response.ErrorCode, response.ErrorMessage);
    }
}

public static class RemoteExecutorServiceCollectionExtensions
{
    /// <summary>Registers the privileged executor (only in the executor process). <typeparamref name="TOperations"/> performs the privileged work.</summary>
    public static IServiceCollection AddSolvixRemoteExecutor<TOperations>(this IServiceCollection services)
        where TOperations : class, IPowerOperations
    {
        services.AddSingleton<IPowerOperations, TOperations>();
        services.TryAddSingleton<IExecutorPipeFactory, CurrentUserPipeFactory>();
        services.AddSingleton<PrivilegedExecutor>();
        services.AddHostedService<PrivilegedExecutorServer>();
        return services;
    }
}
