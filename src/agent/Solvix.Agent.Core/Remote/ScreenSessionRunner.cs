namespace Solvix.Agent.Core.Remote;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;

/// <summary>
/// Shares the signed-in person's screen for the length of a session: frames out (changed frames, plus a
/// periodic keep-alive), and — only when control was granted — the technician's mouse and keyboard in.
/// </summary>
public sealed partial class ScreenSessionRunner(IAgentApiClient api, IRemoteDesktop desktop, ILogger<ScreenSessionRunner> logger)
{
    private const int PollSeconds = 20;

    /// <summary>Time between captures (about 5 frames per second at most).</summary>
    internal TimeSpan FrameInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>An unchanged screen is still re-sent this often so the viewer knows the share is alive.</summary>
    internal TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(2);

    public async Task RunAsync(Guid sessionId, bool controlGranted, CancellationTokenSource session)
    {
        var frames = SendFramesAsync(sessionId, session);
        var input = HandleInputAsync(sessionId, controlGranted, session);
        await Task.WhenAny(frames, input);
        await session.CancelAsync();
        await Task.WhenAll(frames, input).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task SendFramesAsync(Guid sessionId, CancellationTokenSource session)
    {
        string? lastHash = null;
        var lastSent = DateTimeOffset.MinValue;
        try
        {
            while (!session.IsCancellationRequested)
            {
                var started = DateTimeOffset.UtcNow;
                var frame = await desktop.CaptureAsync(session.Token);
                if (frame is not null)
                {
                    var hash = Convert.ToHexString(SHA256.HashData(frame.Jpeg));
                    if (hash != lastHash || started - lastSent >= KeepAlive)
                    {
                        await api.PostScreenFrameAsync(sessionId, frame, session.Token);
                        lastHash = hash;
                        lastSent = started;
                    }
                }
                else if (!desktop.IsAvailable)
                {
                    LogDesktopGone(logger, sessionId);
                    return; // the person signed out: end the share
                }

                var wait = FrameInterval - (DateTimeOffset.UtcNow - started);
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(5), session.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Session over.
        }
        catch (AgentApiException ex)
        {
            LogRelayStopped(logger, sessionId, ex.Message); // e.g. ended by the technician
        }
    }

    private async Task HandleInputAsync(Guid sessionId, bool controlGranted, CancellationTokenSource session)
    {
        long after = 0;
        try
        {
            while (!session.IsCancellationRequested)
            {
                var response = await api.PollRemoteInputAsync(sessionId, after, PollSeconds, session.Token);
                foreach (var chunk in response.Chunks)
                {
                    after = Math.Max(after, chunk.Sequence);
                    if (!controlGranted)
                    {
                        continue; // view only: input is never applied, whatever arrives
                    }

                    ScreenInputEvent? input = null;
                    try
                    {
                        input = JsonSerializer.Deserialize(chunk.Text, AgentJsonContext.Default.ScreenInputEvent);
                    }
                    catch (JsonException)
                    {
                        // Ignore anything that is not a well-formed event.
                    }

                    if (input is not null)
                    {
                        await desktop.InjectAsync(input, session.Token);
                    }
                }

                if (response.Ended)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen session {SessionId}: the desktop is no longer available")]
    private static partial void LogDesktopGone(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen session {SessionId} relay stopped: {Reason}")]
    private static partial void LogRelayStopped(ILogger logger, Guid sessionId, string reason);
}
