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
/// Remote sessions (shell, screen, files). Does nothing unless the local policy allows them, and only
/// the kinds the local policy allows. Waits (outbound long poll) for a technician's request, applies
/// the policy (ask the person, or unattended), and runs the session until either side ends it. The
/// server can ask; this computer decides.
/// </summary>
public sealed partial class RemoteAccessWorker(
    IAgentApiClient api,
    IOptions<AgentOptions> options,
    IConsentPrompter prompter,
    IRemoteShellFactory shells,
    IRemoteDesktop desktop,
    ScreenSessionRunner screens,
    FileSessionRunner files,
    AgentHealthMonitor health,
    ILogger<RemoteAccessWorker> logger) : BackgroundService
{
    private const int PollSeconds = 20;
    private const int FlushChars = 8 * 1024;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(4);

    /// <summary>Test hook: wait between polls after a failure.</summary>
    internal TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The kinds of session this computer allows (none when remote access is disabled).</summary>
    internal static RemoteFeatures EffectiveFeatures(RemoteAccessOptions settings)
    {
        if (settings.Mode == RemoteAccessMode.Disabled)
        {
            return RemoteFeatures.None;
        }

        var features = settings.Features;
        return features.HasFlag(RemoteFeatures.Screen) ? features : features & ~RemoteFeatures.Control; // control only exists with a screen
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.RemoteAccess;
        health.RemoteAccess = settings.Mode;
        health.RemoteFeatures = EffectiveFeatures(settings);
        if (settings.Mode == RemoteAccessMode.Disabled)
        {
            LogDisabled(logger);
            return;
        }

        LogEnabled(logger, settings.Mode, health.RemoteFeatures);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await api.PollRemotePendingAsync(PollSeconds, stoppingToken);
                if (pending.Request is { } request)
                {
                    await HandleAsync(request, settings.Mode, stoppingToken);
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
        var settings = options.Value.RemoteAccess;
        var features = EffectiveFeatures(settings);
        if (!features.HasFlag(request.Kind.For()))
        {
            await DenyAsync(request, $"{request.Kind} sessions are not enabled on this computer.", cancellationToken);
            return;
        }

        if (request.Kind == RemoteSessionKind.Screen && !desktop.IsAvailable)
        {
            await DenyAsync(request, "Nobody's desktop can be shared right now (no one is signed in, or the screen helper is not running).", cancellationToken);
            return;
        }

        RemoteConsent consent;
        if (mode == RemoteAccessMode.Unattended)
        {
            consent = RemoteConsent.Unattended;
        }
        else
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.ConsentTimeoutSeconds, 10, 110));
            var control = request.Kind == RemoteSessionKind.Screen && request.ControlRequested && features.HasFlag(RemoteFeatures.Control);
            var allowed = await prompter.AskAsync(new RemoteConsentRequest(request.RequestedBy, request.Reason, request.Kind, control), timeout, cancellationToken);
            if (!allowed)
            {
                LogDenied(logger, request.SessionId, request.RequestedBy);
                await DenyAsync(request, "The person at the computer declined or did not answer.", cancellationToken);
                return;
            }

            consent = RemoteConsent.User;
        }

        // Control is granted only when it was asked for, this computer allows it, and (above) the person agreed to it.
        var controlGranted = request.Kind == RemoteSessionKind.Screen && request.ControlRequested && features.HasFlag(RemoteFeatures.Control);

        IRemoteShell? shell = null;
        if (request.Kind == RemoteSessionKind.Shell)
        {
            try
            {
                shell = shells.Start();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LogShellFailed(logger, ex.Message);
                await DenyAsync(request, "The shell could not be started on the computer.", cancellationToken);
                return;
            }
        }

        try
        {
            await api.AcceptRemoteAsync(request.SessionId, new AcceptRemoteRequest { Consent = consent, ControlGranted = controlGranted }, cancellationToken);
            LogStarted(logger, request.SessionId, request.Kind, request.RequestedBy, consent, controlGranted);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session.CancelAfter(MaxSession);
            switch (request.Kind)
            {
                case RemoteSessionKind.Shell:
                    await RunShellAsync(request.SessionId, shell!, session);
                    break;
                case RemoteSessionKind.Screen:
                    await screens.RunAsync(request.SessionId, controlGranted, session);
                    break;
                case RemoteSessionKind.Files:
                    await files.RunAsync(request.SessionId, session);
                    break;
            }

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
        finally
        {
            if (shell is not null)
            {
                await shell.DisposeAsync();
            }
        }
    }

    private Task DenyAsync(AgentRemoteRequest request, string reason, CancellationToken cancellationToken) =>
        api.DenyRemoteAsync(request.SessionId, new DenyRemoteRequest { Reason = reason }, cancellationToken);

    private async Task RunShellAsync(Guid sessionId, IRemoteShell shell, CancellationTokenSource session)
    {
        var output = PumpOutputAsync(sessionId, shell, session);
        var input = PumpInputAsync(sessionId, shell, session);
        await Task.WhenAny(output, input);
        shell.Kill();
        await session.CancelAsync();
        await Task.WhenAll(output, input).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote access is enabled on this computer ({Mode}, {Features})")]
    private static partial void LogEnabled(ILogger logger, RemoteAccessMode mode, RemoteFeatures features);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} requested by {User} was declined")]
    private static partial void LogDenied(ILogger logger, Guid sessionId, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote {Kind} session {SessionId} started for {User} ({Consent}, control: {Control})")]
    private static partial void LogStarted(ILogger logger, Guid sessionId, RemoteSessionKind kind, string user, RemoteConsent consent, bool control);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} ended")]
    private static partial void LogEnded(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote session {SessionId} relay stopped: {Reason}")]
    private static partial void LogRelayStopped(ILogger logger, Guid sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the remote shell: {Reason}")]
    private static partial void LogShellFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote session poll failed: {Reason}")]
    private static partial void LogPollFailed(ILogger logger, string reason);
}
