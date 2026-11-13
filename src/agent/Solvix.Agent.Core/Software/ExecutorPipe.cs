namespace Solvix.Agent.Core.Software;

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Configuration;

/// <summary>Length-prefixed (4 bytes, little endian) UTF-8 JSON messages, at most <see cref="MaxMessageBytes"/>.</summary>
public static class ExecutorProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, type);
        if (body.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("Message too large.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException("Invalid message length.");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize(body, type);
    }
}

/// <summary>The agent's side: one pipe connection per installation; closing it cancels the installation.</summary>
public sealed class PipeSoftwareExecutorClient(IOptions<AgentOptions> options) : ISoftwareExecutor
{
    public async Task<ExecutorResponse> ExecuteAsync(ExecutorRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value.Software;

        // Identification only: the executor may learn who is calling but can never act as the agent.
        await using var pipe = new NamedPipeClientStream(".", settings.ExecutorPipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ExecutorConnectTimeoutSeconds, 1, 120)));
            await pipe.ConnectAsync(connect.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException && !cancellationToken.IsCancellationRequested)
        {
            return ExecutorResponse.Refuse(SoftwareErrorCodes.ExecutorUnavailable, "The Solvix Software Executor service is not running or not installed on this device.");
        }

        try
        {
            await ExecutorProtocol.WriteAsync(pipe, request, SoftwareJsonContext.Default.ExecutorRequest, cancellationToken);
            return await ExecutorProtocol.ReadAsync(pipe, SoftwareJsonContext.Default.ExecutorResponse, cancellationToken)
                ?? ExecutorResponse.Refuse(SoftwareErrorCodes.ExecutorUnavailable, "The executor returned no answer.");
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return ExecutorResponse.Refuse(SoftwareErrorCodes.ExecutorUnavailable, $"The connection to the executor failed: {ex.Message}");
        }
    }
}

/// <summary>Creates the listening pipe. Windows replaces it with a pipe that only SYSTEM and the agent's service SID may open.</summary>
public interface IExecutorPipeFactory
{
    NamedPipeServerStream Create(string name);
}

public sealed class DefaultExecutorPipeFactory : IExecutorPipeFactory
{
    public NamedPipeServerStream Create(string name) =>
        new(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
}

/// <summary>
/// Hosts <see cref="SoftwareExecutor"/> on a named pipe (the <c>software-executor</c> mode of the agent
/// binary, installed as its own LocalSystem service). Each connection carries one request; if the
/// agent disconnects before the answer, the running installer is stopped.
/// </summary>
public sealed partial class SoftwareExecutorHost(
    SoftwareExecutor executor,
    IExecutorPipeFactory pipes,
    IOptions<AgentOptions> options,
    ILogger<SoftwareExecutorHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = options.Value.Software.ExecutorPipeName;
        var connections = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = pipes.Create(name);
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                // All instances busy (or the pipe name is taken): try again shortly.
                LogPipeFailed(logger, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            connections.RemoveAll(t => t.IsCompleted);
            connections.Add(Task.Run(() => ServeAsync(pipe, stoppingToken), CancellationToken.None));
        }

        await Task.WhenAll(connections);
    }

    internal async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                var request = await ExecutorProtocol.ReadAsync(pipe, SoftwareJsonContext.Default.ExecutorRequest, stoppingToken);
                if (request is null)
                {
                    return;
                }

                using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                // The client sends nothing more; a completed read means it went away: stop the installer.
                var disconnected = WatchDisconnectAsync(pipe, run);
                var response = await executor.ExecuteAsync(request, run.Token);
                if (!run.IsCancellationRequested)
                {
                    await ExecutorProtocol.WriteAsync(pipe, response, SoftwareJsonContext.Default.ExecutorResponse, stoppingToken);
                }

                await run.CancelAsync();
                await disconnected;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or JsonException or OperationCanceledException)
            {
                LogConnectionFailed(logger, ex.Message);
            }
        }
    }

    private static async Task WatchDisconnectAsync(NamedPipeServerStream pipe, CancellationTokenSource run)
    {
        try
        {
            // Any outcome of this read (end of stream or an unexpected byte) means the request is over.
            var probe = new byte[1];
            _ = await pipe.ReadAsync(probe, run.Token);
            await run.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            await run.CancelAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Software executor pipe unavailable: {Message}")]
    private static partial void LogPipeFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Software executor connection ended abnormally: {Message}")]
    private static partial void LogConnectionFailed(ILogger logger, string message);
}
