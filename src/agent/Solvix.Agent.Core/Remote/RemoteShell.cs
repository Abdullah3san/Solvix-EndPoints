namespace Solvix.Agent.Core.Remote;

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;

/// <summary>A command shell attached to a remote session: text in, text out.</summary>
public interface IRemoteShell : IAsyncDisposable
{
    /// <summary>Shell output (stdout and stderr merged). Completes when the shell has exited.</summary>
    ChannelReader<string> Output { get; }

    Task WriteAsync(string text, CancellationToken cancellationToken);

    void Kill();
}

public interface IRemoteShellFactory
{
    IRemoteShell Start();
}

/// <summary>
/// Starts the configured shell as the agent's own identity with redirected streams (line-oriented,
/// not a full terminal emulator). The identity is whatever the agent service runs as; the agent
/// never elevates itself for a remote session.
/// </summary>
public sealed class ProcessShellFactory(IOptions<AgentOptions> options) : IRemoteShellFactory
{
    public IRemoteShell Start()
    {
        var settings = options.Value.RemoteAccess;
        var windows = OperatingSystem.IsWindows();
        var file = !string.IsNullOrWhiteSpace(settings.Shell) ? settings.Shell : windows ? "powershell.exe" : "/bin/sh";
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var arguments = settings.ShellArguments ?? (windows ? ["-NoLogo", "-NoProfile", "-Command", "-"] : []);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return new ProcessShell(Process.Start(info) ?? throw new InvalidOperationException("The shell could not be started."));
    }
}

internal sealed class ProcessShell : IRemoteShell
{
    private readonly Process _process;
    private readonly Channel<string> _output = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public ProcessShell(Process process)
    {
        _process = process;
        var pumps = new[] { Pump(process.StandardOutput), Pump(process.StandardError) };
        _ = Task.WhenAll(pumps).ContinueWith(_ => _output.Writer.TryComplete(), TaskScheduler.Default);
    }

    public ChannelReader<string> Output => _output.Reader;

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // The shell already exited; the output pump reports the end.
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task Pump(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                await _output.Writer.WriteAsync(new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process torn down.
        }
    }
}

/// <summary>Asks the person at the computer whether a technician may open a remote session.</summary>
public interface IConsentPrompter
{
    Task<bool> AskAsync(string requestedBy, string? reason, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Safe default: with no way to ask, the answer is no.</summary>
public sealed class DenyingConsentPrompter : IConsentPrompter
{
    public Task<bool> AskAsync(string requestedBy, string? reason, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(false);
}
