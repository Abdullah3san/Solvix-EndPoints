namespace Solvix.Agent.Core.Remote;

using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Health;

/// <summary>
/// Remote command sessions. Does nothing unless the local policy allows them. Waits (outbound long
/// poll) for a technician's request, applies the policy (ask the person, or unattended), and relays
/// a shell until either side ends the session. The server never sends anything the agent executes
/// except text typed into the shell the local policy allowed.
/// </summary>
public sealed partial class RemoteAccessWorker(
    IAgentApiClient api,
    IOptions<AgentOptions> options,
    IConsentPrompter prompter,
    IRemoteShellFactory shells,
    AgentHealthMonitor health,
    ILogger<RemoteAccessWorker> logger) : BackgroundService
{
    private const int PollSeconds = 20;
    private const int FlushChars = 8 * 1024;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(4);

    /// <summary>Test hook: wait between polls after a failure.</summary>
    internal TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = options.Value.RemoteAccess.Mode;
        health.RemoteAccess = mode;
        if (mode == RemoteAccessMode.Disabled)
        {
            LogDisabled(logger);
            return;
        }

        LogEnabled(logger, mode);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await api.PollRemotePendingAsync(PollSeconds, stoppingToken);
                if (pending.Request is { } request)
                {
                    await HandleAsync(request, mode, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is AgentApiException or InvalidOperationException)
            {
                LogPollFailed(logger, ex.Message);
                await Task.Delay(ErrorDelay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Answers one request and, when accepted, runs the session to its end. Exposed for tests.</summary>
    internal async Task HandleAsync(AgentRemoteRequest request, RemoteAccessMode mode, CancellationToken cancellationToken)
    {
        RemoteConsent consent;
        if (mode == RemoteAccessMode.Unattended)
        {
            consent = RemoteConsent.Unattended;
        }
        else
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RemoteAccess.ConsentTimeoutSeconds, 10, 110));
            var allowed = await prompter.AskAsync(request.RequestedBy, request.Reason, timeout, cancellationToken);
            if (!allowed)
            {
                LogDenied(logger, request.SessionId, request.RequestedBy);
                await api.DenyRemoteAsync(request.SessionId, new DenyRemoteRequest { Reason = "The person at the computer declined or did not answer." }, cancellationToken);
                return;
            }

            consent = RemoteConsent.User;
        }

        IRemoteShell shell;
        try
        {
            shell = shells.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogShellFailed(logger, ex.Message);
            await api.DenyRemoteAsync(request.SessionId, new DenyRemoteRequest { Reason = "The shell could not be started on the computer." }, cancellationToken);
            return;
        }

        await using (shell)
        {
            await api.AcceptRemoteAsync(request.SessionId, new AcceptRemoteRequest { Consent = consent }, cancellationToken);
            LogStarted(logger, request.SessionId, request.RequestedBy, consent);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session.CancelAfter(MaxSession);
            var output = PumpOutputAsync(request.SessionId, shell, session);
            var input = PumpInputAsync(request.SessionId, shell, session);
            await Task.WhenAny(output, input);
            shell.Kill();
            await session.CancelAsync();
            await Task.WhenAll(output, input).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            try
            {
                await api.EndRemoteAsync(request.SessionId, new EndRemoteRequest { Reason = "The session closed." }, CancellationToken.None);
            }
            catch (AgentApiException)
            {
                // Already ended on the server side.
            }

            LogEnded(logger, request.SessionId);
        }
    }

    private async Task PumpOutputAsync(Guid sessionId, IRemoteShell shell, CancellationTokenSource session)
    {
        var buffer = new StringBuilder();
        try
        {
            while (await shell.Output.WaitToReadAsync(session.Token))
            {
                buffer.Clear();
                while (buffer.Length < FlushChars && shell.Output.TryRead(out var text))
                {
                    buffer.Append(text);
                }

                if (buffer.Length < FlushChars)
                {
                    // Let a burst of output (e.g. a long listing) arrive in one request.
                    await Task.Delay(FlushDelay, session.Token);
                    while (buffer.Length < FlushChars && shell.Output.TryRead(out var more))
                    {
                        buffer.Append(more);
                    }
                }

                foreach (var piece in Split(buffer.ToString(), RemoteChunk.MaxTextLength))
                {
                    await api.PostRemoteOutputAsync(sessionId, new RemoteOutputRequest { Text = piece }, session.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session over.
        }
        catch (AgentApiException ex)
        {
            LogRelayStopped(logger, sessionId, ex.Message); // e.g. ended by the technician: stop the shell
        }
    }

    private async Task PumpInputAsync(Guid sessionId, IRemoteShell shell, CancellationTokenSource session)
    {
        long after = 0;
        try
        {
            while (!session.IsCancellationRequested)
            {
                var input = await api.PollRemoteInputAsync(sessionId, after, PollSeconds, session.Token);
                foreach (var chunk in input.Chunks)
                {
                    after = Math.Max(after, chunk.Sequence);
                    await shell.WriteAsync(chunk.Text, session.Token);
                }

                if (input.Ended)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session over.
        }
        catch (AgentApiException ex)
        {
            LogRelayStopped(logger, sessionId, ex.Message);
        }
    }

    private static IEnumerable<string> Split(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote access is disabled on this computer")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote access is enabled on this computer ({Mode})")]
    private static partial void LogEnabled(ILogger logger, RemoteAccessMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} requested by {User} was declined")]
    private static partial void LogDenied(ILogger logger, Guid sessionId, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} started for {User} ({Consent})")]
    private static partial void LogStarted(ILogger logger, Guid sessionId, string user, RemoteConsent consent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} ended")]
    private static partial void LogEnded(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote session {SessionId} relay stopped: {Reason}")]
    private static partial void LogRelayStopped(ILogger logger, Guid sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the remote shell: {Reason}")]
    private static partial void LogShellFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote session poll failed: {Reason}")]
    private static partial void LogPollFailed(ILogger logger, string reason);
}
