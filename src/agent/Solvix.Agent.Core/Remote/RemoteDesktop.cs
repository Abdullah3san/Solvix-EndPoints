namespace Solvix.Agent.Core.Remote;

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Configuration;

/// <summary>What the service needs from the signed-in person's desktop: a picture, and (when allowed) input.</summary>
public interface IRemoteDesktop
{
    /// <summary>True when a per-user helper is connected, i.e. someone is signed in and the helper is running.</summary>
    bool IsAvailable { get; }

    Task<ScreenFrame?> CaptureAsync(CancellationToken cancellationToken);

    Task InjectAsync(ScreenInputEvent input, CancellationToken cancellationToken);
}

/// <summary>Helper side: takes the picture / performs the input inside the user's own session.</summary>
public interface IScreenCapture
{
    ScreenFrame? Capture();
}

public interface IInputInjector
{
    void Inject(ScreenInputEvent input);
}

/// <summary>Decides whether a process that connected to the helper pipe is the signed-in person's helper.</summary>
public interface IHelperAuthorizer
{
    bool IsAuthorized(NamedPipeServerStream pipe);
}

public sealed class AllowAnyHelper : IHelperAuthorizer
{
    public bool IsAuthorized(NamedPipeServerStream pipe) => true;
}

/// <summary>
/// Wire format between the service and the helper: one request at a time, each frame is
/// <c>[type or status: 1 byte][length: 4 bytes big endian][payload]</c>.
/// </summary>
internal static class HelperProtocol
{
    public const byte Capture = 1;
    public const byte Input = 2;
    public const byte Ok = 0;
    public const byte Error = 1;
    public const int MaxPayload = 4 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<(byte Type, byte[] Payload)> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 0 or > MaxPayload)
        {
            throw new InvalidDataException("The helper sent an oversized message.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return (header[0], payload);
    }

    public static byte[] EncodeFrame(ScreenFrame frame)
    {
        var data = new byte[8 + frame.Jpeg.Length];
        BinaryPrimitives.WriteInt32BigEndian(data, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), frame.Height);
        frame.Jpeg.CopyTo(data, 8);
        return data;
    }

    public static ScreenFrame DecodeFrame(byte[] data) =>
        data.Length <= 8
            ? throw new InvalidDataException("The helper sent an empty frame.")
            : new ScreenFrame(data[8..], BinaryPrimitives.ReadInt32BigEndian(data), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)));
}

/// <summary>
/// Service side of the helper bridge. The Windows service runs in session 0 and cannot see a user's
/// desktop, so a helper started at sign-in (as that user) connects to the service and answers
/// "capture" and "input" requests. At most one helper is used at a time; a new one replaces the old.
/// </summary>
public sealed partial class ScreenHelperBroker(ILogger<ScreenHelperBroker> logger) : IRemoteDesktop
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _helper;

    public bool IsAvailable => Volatile.Read(ref _helper) is not null;

    public void Attach(Stream helper)
    {
        var previous = Interlocked.Exchange(ref _helper, helper);
        previous?.Dispose();
    }

    public void Detach(Stream helper) => Interlocked.CompareExchange(ref _helper, null, helper);

    public async Task<ScreenFrame?> CaptureAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(HelperProtocol.Capture, ReadOnlyMemory<byte>.Empty, cancellationToken);
        return reply is { Length: > 8 } ? HelperProtocol.DecodeFrame(reply) : null;
    }

    public async Task InjectAsync(ScreenInputEvent input, CancellationToken cancellationToken) =>
        await RequestAsync(HelperProtocol.Input, JsonSerializer.SerializeToUtf8Bytes(input, AgentJsonContext.Default.ScreenInputEvent), cancellationToken);

    private async Task<byte[]?> RequestAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var helper = Volatile.Read(ref _helper);
            if (helper is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                await HelperProtocol.WriteAsync(helper, type, payload, timeout.Token);
                var (status, data) = await HelperProtocol.ReadAsync(helper, timeout.Token);
                return status == HelperProtocol.Ok ? data : null;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or EndOfStreamException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogHelperLost(logger, ex.Message);
                Detach(helper);
                helper.Dispose();
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen helper connection lost: {Reason}")]
    private static partial void LogHelperLost(ILogger logger, string reason);
}

/// <summary>Accepts the per-user helper on a local pipe (never from the network) and hands it to the broker.</summary>
public sealed partial class ScreenHelperServer(
    ScreenHelperBroker broker,
    IHelperAuthorizer authorizer,
    IOptions<AgentOptions> options,
    ILogger<ScreenHelperServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RemoteAccess.Features.HasFlag(RemoteFeatures.Screen) || options.Value.RemoteAccess.Mode == RemoteAccessMode.Disabled)
        {
            return; // nothing to bridge unless screen sharing is enabled on this computer
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(options.Value.RemoteAccess.ScreenHelperPipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                if (!authorizer.IsAuthorized(pipe))
                {
                    LogRejected(logger);
                    pipe.Dispose();
                    continue;
                }

                broker.Attach(pipe);
                LogAttached(logger);
                pipe = null; // owned by the broker now
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogListenFailed(logger, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsPipe(name);
        }

        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsPipe(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A screen helper connected")]
    private static partial void LogAttached(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A process that is not the signed-in user's helper tried to connect and was refused")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen helper pipe could not listen: {Reason}")]
    private static partial void LogListenFailed(ILogger logger, string reason);
}

/// <summary>
/// The helper process (<c>Solvix.Agent.exe screen-helper</c>): runs as the signed-in user, connects to
/// the service and answers capture / input requests until the user signs out.
/// </summary>
public sealed class ScreenHelperLoop(IScreenCapture capture, IInputInjector injector, string pipeName, TimeSpan? retryDelay = null)
{
    private readonly TimeSpan _retry = retryDelay ?? TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, cancellationToken);
                await ServeAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
            {
                // The service is not up (yet) or went away: try again.
            }

            await Task.Delay(_retry, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Answers requests on a connected stream. Exposed for tests.</summary>
    internal async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (type, payload) = await HelperProtocol.ReadAsync(stream, cancellationToken);
            try
            {
                switch (type)
                {
                    case HelperProtocol.Capture:
                        var frame = capture.Capture();
                        await HelperProtocol.WriteAsync(stream, frame is null ? HelperProtocol.Error : HelperProtocol.Ok, frame is null ? ReadOnlyMemory<byte>.Empty : HelperProtocol.EncodeFrame(frame), cancellationToken);
                        break;
                    case HelperProtocol.Input:
                        var input = JsonSerializer.Deserialize(payload, AgentJsonContext.Default.ScreenInputEvent);
                        if (input is not null)
                        {
                            injector.Inject(input);
                        }

                        await HelperProtocol.WriteAsync(stream, HelperProtocol.Ok, ReadOnlyMemory<byte>.Empty, cancellationToken);
                        break;
                    default:
                        await HelperProtocol.WriteAsync(stream, HelperProtocol.Error, ReadOnlyMemory<byte>.Empty, cancellationToken);
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                await HelperProtocol.WriteAsync(stream, HelperProtocol.Error, ReadOnlyMemory<byte>.Empty, cancellationToken);
            }
        }
    }
}
