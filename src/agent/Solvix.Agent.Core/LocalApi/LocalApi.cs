namespace Solvix.Agent.Core.LocalApi;

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;

/// <summary>
/// Operations people on the computer can ask the agent service for. The service owns the device
/// credential (machine-protected key), so tickets and chat reach the server through it; the
/// set is fixed and every operation is a plain data call.
/// </summary>
public static class LocalOps
{
    public const string SubmitTicket = "ticket.submit";
    public const string ListTickets = "ticket.list";
    public const string PollChat = "chat.poll";
    public const string SendChat = "chat.send";
}

public sealed record LocalRequest
{
    public required string Op { get; init; }

    public SubmitTicketRequest? Ticket { get; init; }

    public string? Body { get; init; }

    public long After { get; init; }

    public int WaitSeconds { get; init; }
}

public sealed record LocalResponse
{
    public bool Ok { get; init; }

    /// <summary>User-presentable reason when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    public AgentTicket? Ticket { get; init; }

    public IReadOnlyList<AgentTicket>? Tickets { get; init; }

    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    public ChatMessage? Message { get; init; }

    public static LocalResponse Fail(string error) => new() { Ok = false, Error = error };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocalRequest))]
[JsonSerializable(typeof(LocalResponse))]
internal sealed partial class LocalApiJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Listens on a local named pipe. On Windows the pipe is open to authenticated local users only
/// (never the network) and the requester's identity comes from the operating system, not from what
/// the caller claims.
/// </summary>
public sealed partial class LocalApiServer(IAgentApiClient api, IOptions<AgentOptions> options, ILogger<LocalApiServer> logger) : BackgroundService
{
    public const int MaxRequestBytes = 64 * 1024;
    private const int MaxConcurrent = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = options.Value.LocalPipeName;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(name);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var connection = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connection, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogListenFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    /// <summary>Handles one request on an accepted connection. Exposed for tests.</summary>
    internal async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            if (!await _slots.WaitAsync(0, cancellationToken))
            {
                await WriteAsync(pipe, LocalResponse.Fail("The agent is busy. Try again in a moment."), cancellationToken);
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                var request = await ReadAsync(pipe, timeout.Token);
                if (request is null)
                {
                    await WriteAsync(pipe, LocalResponse.Fail("Invalid request."), timeout.Token);
                    return;
                }

                var who = ClientName(pipe);
                var response = await DispatchAsync(request, who, timeout.Token);
                await WriteAsync(pipe, response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The caller went away or timed out; nothing to report to.
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    internal async Task<LocalResponse> DispatchAsync(LocalRequest request, string requester, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Op)
            {
                case LocalOps.SubmitTicket when request.Ticket is { } ticket:
                    if (string.IsNullOrWhiteSpace(ticket.Subject) || ticket.Subject.Length > SubmitTicketRequest.SubjectMaxLength)
                    {
                        return LocalResponse.Fail($"Please describe the problem in a few words (up to {SubmitTicketRequest.SubjectMaxLength} characters).");
                    }

                    // The operating-system identity of the caller is authoritative for who is asking.
                    var created = await api.SubmitTicketAsync(ticket with { RequesterName = requester }, cancellationToken);
                    return new LocalResponse { Ok = true, Ticket = created };

                case LocalOps.ListTickets:
                    return new LocalResponse { Ok = true, Tickets = (await api.ListTicketsAsync(cancellationToken)).Tickets };

                case LocalOps.PollChat:
                    return new LocalResponse
                    {
                        Ok = true,
                        Messages = (await api.PollChatAsync(Math.Max(0, request.After), Math.Clamp(request.WaitSeconds, 0, 25), cancellationToken)).Messages,
                    };

                case LocalOps.SendChat when !string.IsNullOrWhiteSpace(request.Body):
                    if (request.Body.Length > ChatMessage.BodyMaxLength)
                    {
                        return LocalResponse.Fail("That message is too long.");
                    }

                    return new LocalResponse
                    {
                        Ok = true,
                        Message = await api.SendChatAsync(new SendChatMessageRequest { Body = request.Body, SenderName = requester }, cancellationToken),
                    };

                default:
                    return LocalResponse.Fail("Unsupported request.");
            }
        }
        catch (AgentApiException ex)
        {
            LogServerCallFailed(logger, request.Op, ex.Failure, ex.Code);
            return LocalResponse.Fail(ex.Failure switch
            {
                AgentApiFailure.Transient => "Solvix cannot be reached right now. Please try again shortly.",
                AgentApiFailure.Authentication => "This computer is not authorized with Solvix. Contact your IT administrator.",
                _ => ex.Code == "ticket_rate_limited" ? "You have submitted several tickets recently. Please wait a few minutes." : "Solvix rejected the request.",
            });
        }
        catch (InvalidOperationException ex)
        {
            LogServerCallFailed(logger, request.Op, AgentApiFailure.Authentication, ex.Message);
            return LocalResponse.Fail("This computer is not enrolled with Solvix yet.");
        }
    }

    private NamedPipeServerStream CreatePipe(string name)
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
        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private static string ClientName(NamedPipeServerStream pipe)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsClientName(pipe);
        }

        return Environment.UserName; // development hosts only
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsClientName(NamedPipeServerStream pipe)
    {
        try
        {
            var name = string.Empty;
            pipe.RunAsClient(() => name = WindowsIdentity.GetCurrent().Name);
            return name;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return "Unknown user";
        }
    }

    private static async Task<LocalRequest?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            length += read;
            if (buffer.AsSpan(0, length).Contains((byte)'\n'))
            {
                break;
            }
        }

        var end = buffer.AsSpan(0, length).IndexOf((byte)'\n');
        if (end < 0)
        {
            return null; // too large or unterminated
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.AsSpan(0, end), LocalApiJsonContext.Default.LocalRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(Stream stream, LocalResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, LocalApiJsonContext.Default.LocalResponse);
        await stream.WriteAsync(json, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local API could not listen; retrying")]
    private static partial void LogListenFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Local request {Op} failed against the server: {Failure} {Code}")]
    private static partial void LogServerCallFailed(ILogger logger, string op, AgentApiFailure failure, string? code);
}

/// <summary>Used by the command line (and future UI) to talk to the running agent service.</summary>
public sealed class LocalApiClient(string pipeName)
{
    public async Task<LocalResponse> CallAsync(LocalRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return LocalResponse.Fail("The Solvix Agent service is not running on this computer.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(request, LocalApiJsonContext.Default.LocalRequest);
        await pipe.WriteAsync(json, cancellationToken);
        await pipe.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await pipe.FlushAsync(cancellationToken);

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            return LocalResponse.Fail("The Solvix Agent did not answer.");
        }

        return JsonSerializer.Deserialize(line, LocalApiJsonContext.Default.LocalResponse)
            ?? LocalResponse.Fail("The Solvix Agent sent an unreadable answer.");
    }
}
