namespace Solvix.Agent.Core.RemoteManagement;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;

/// <summary>
/// Runs delivered commands: verify → acknowledge → record (replay store) → execute → report.
/// A command is executed only after the server accepted the acknowledgement, so a command that
/// was cancelled or expired on the server in the meantime never runs, and only once.
/// </summary>
public sealed partial class CommandProcessor(
    IAgentCommandClient client,
    CommandVerifier verifier,
    CommandKeyStore keys,
    CommandReplayStore replays,
    CommandHandlerRegistry handlers,
    IPrivilegedOperationHost privileged,
    IAgentStateStore state,
    DeviceCredentialStore credentials,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<CommandProcessor> logger)
{
    public const int MaxConcurrentCommands = 4;
    public static readonly TimeSpan MaxExecutionTime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentCommands);
    private readonly ConcurrentBag<Task> _work = [];

    /// <summary>Executions currently running (for tests and diagnostics).</summary>
    public IReadOnlyCollection<Guid> Running => [.. _running.Keys];

    /// <summary>One poll. Returns how long to wait before the next one.</summary>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.RemoteManagement;
        if (!settings.Enabled || !state.Load().IsEnrolled || credentials.GetClientCertificate() is null)
        {
            return TimeSpan.FromSeconds(30);
        }

        if (!keys.IsPinned && !keys.PinIfEmpty(await client.GetCommandKeysAsync(cancellationToken)))
        {
            LogNoKeys(logger);
            return TimeSpan.FromMinutes(5);
        }

        var wait = Math.Clamp(settings.PollWaitSeconds, 0, Math.Max(0, Math.Min(30, options.Value.RequestTimeoutSeconds - 10)));
        var batch = await client.FetchCommandsAsync(wait, cancellationToken);
        foreach (var command in batch.Commands)
        {
            await ProcessAsync(command, cancellationToken);
        }

        return batch.Commands.Count > 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
    }

    /// <summary>Waits for started executions (tests and shutdown).</summary>
    public Task DrainAsync() => Task.WhenAll(_work.ToArray());

    internal async Task ProcessAsync(SignedCommand signed, CancellationToken cancellationToken)
    {
        var verification = verifier.Verify(signed);
        if (!verification.Accepted)
        {
            // The execution id is read from the unverified bytes only to tell the server which command
            // was refused; the server accepts that report only for a command of this very device.
            if (TryPeekExecutionId(signed) is { } unverifiedId)
            {
                LogRejected(logger, unverifiedId, verification.RejectionCode!);
                await TryAcknowledgeAsync(unverifiedId, new CommandAcknowledgement { Accepted = false, RejectionCode = verification.RejectionCode, Detail = verification.Detail }, cancellationToken);
            }

            return;
        }

        var envelope = verification.Envelope!;
        if (!await TryAcknowledgeAsync(envelope.ExecutionId, new CommandAcknowledgement { Accepted = true }, cancellationToken))
        {
            return; // cancelled / expired on the server, or not reachable: do not run
        }

        if (!replays.TryRecord(envelope))
        {
            return;
        }

        if (envelope.CommandType == CommandTypes.Cancel)
        {
            await ReportAsync(envelope.ExecutionId, Cancel(envelope), time.GetUtcNow(), cancellationToken);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(MaxExecutionTime);
        _running[envelope.ExecutionId] = cts;
        _work.Add(Task.Run(() => ExecuteAsync(signed, envelope, cts), CancellationToken.None));
    }

    private async Task ExecuteAsync(SignedCommand signed, CommandEnvelope envelope, CancellationTokenSource cts)
    {
        var started = time.GetUtcNow();
        CommandHandlerResult result;
        await _slots.WaitAsync(CancellationToken.None);
        try
        {
            var handler = handlers.Find(envelope.CommandType);
            result = handler switch
            {
                null => CommandHandlerResult.Fail("not_implemented", $"No handler for {envelope.CommandType}."),
                PrivilegedCommandHandler => await privileged.ExecuteAsync(new SignedCommandContext(signed, envelope), cts.Token),
                _ => await handler.ExecuteAsync(envelope, cts.Token),
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            result = new CommandHandlerResult(CommandOutcome.Cancelled, ErrorCode: "cancelled", ErrorMessage: "The command was cancelled.");
        }
        catch (Exception ex)
        {
            LogHandlerFailed(logger, ex, envelope.ExecutionId, envelope.CommandType);
            result = CommandHandlerResult.Fail("handler_error", ex.Message);
        }
        finally
        {
            _slots.Release();
            _running.TryRemove(envelope.ExecutionId, out _);
        }

        try
        {
            await ReportAsync(envelope.ExecutionId, result, started, CancellationToken.None);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private CommandHandlerResult Cancel(CommandEnvelope envelope)
    {
        var cancelled = false;
        if (Guid.TryParse(envelope.Parameters.GetValueOrDefault("executionId"), out var target) && _running.TryGetValue(target, out var running))
        {
            running.Cancel();
            cancelled = true;
        }

        return CommandHandlerResult.Json(new JsonObject { ["cancelled"] = cancelled });
    }

    private async Task<bool> TryAcknowledgeAsync(Guid executionId, CommandAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        try
        {
            await client.AcknowledgeCommandAsync(executionId, acknowledgement, cancellationToken);
            return true;
        }
        catch (AgentApiException ex) when (ex.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            LogNotExecutable(logger, executionId, ex.Code);
            return false;
        }
    }

    private async Task ReportAsync(Guid executionId, CommandHandlerResult result, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var output = result.Output;
        var truncated = false;
        if (output is { Length: > CommandResultReport.MaxOutputLength })
        {
            output = output[..CommandResultReport.MaxOutputLength];
            truncated = true;
        }

        var report = new CommandResultReport
        {
            Outcome = result.Outcome,
            ExitCode = result.ExitCode,
            Output = output,
            OutputTruncated = truncated,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            StartedAtUtc = started,
            CompletedAtUtc = time.GetUtcNow(),
        };

        // A result must survive short outages; the server keeps the command Acknowledged until its deadline.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.ReportCommandResultAsync(executionId, report, cancellationToken);
                return;
            }
            catch (AgentApiException ex) when (ex.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                return; // already recorded or timed out on the server
            }
            catch (AgentApiException ex) when (ex.Failure == AgentApiFailure.Transient && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << attempt)), time, cancellationToken);
            }
        }
    }

    private static Guid? TryPeekExecutionId(SignedCommand signed)
    {
        try
        {
            return CommandSignature.Deserialize(System.Buffers.Text.Base64Url.DecodeFromChars(signed.Envelope))?.ExecutionId;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {ExecutionId} refused: {Code}")]
    private static partial void LogRejected(ILogger logger, Guid executionId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {ExecutionId} is no longer executable ({Code})")]
    private static partial void LogNotExecutable(ILogger logger, Guid executionId, string? code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {ExecutionId} ({CommandType}) failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, Guid executionId, string commandType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server published no usable command signing key; remote commands are disabled")]
    private static partial void LogNoKeys(ILogger logger);
}

/// <summary>Keeps one outbound command poll open while the agent is enrolled.</summary>
public sealed partial class CommandChannelWorker(CommandProcessor processor, TimeProvider time, ILogger<CommandChannelWorker> logger) : BackgroundService
{
    private readonly ExponentialBackoff _backoff = new(new BackoffOptions { InitialSeconds = 5, MaxSeconds = 120 });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await processor.PollOnceAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentApiException ex)
            {
                delay = _backoff.Delay(++failures);
                LogPollFailed(logger, ex.Message);
            }
            catch (Exception ex)
            {
                LogUnexpected(logger, ex);
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

        await processor.DrainAsync();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Command poll failed: {Message}")]
    private static partial void LogPollFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command channel failed unexpectedly")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
