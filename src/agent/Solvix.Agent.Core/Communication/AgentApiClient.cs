namespace Solvix.Agent.Core.Communication;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Configuration;

public interface IAgentApiClient
{
    Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken);

    Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken);

    Task SubmitInventoryAsync(InventoryReport report, CancellationToken cancellationToken);

    Task<CertificateRenewalResponse> RenewCertificateAsync(CertificateRenewalRequest request, CancellationToken cancellationToken);

    Task<UpdateCheckResponse> CheckForUpdateAsync(string channel, string currentVersion, CancellationToken cancellationToken);
}

/// <summary>The signed command channel (Phase 06). Outbound only, like everything else.</summary>
public interface IAgentCommandClient
{
    /// <summary>Pending signed commands; the server holds the request up to <paramref name="waitSeconds"/> when there are none.</summary>
    Task<AgentCommandBatch> FetchCommandsAsync(int waitSeconds, CancellationToken cancellationToken);

    Task AcknowledgeCommandAsync(Guid executionId, CommandAcknowledgement acknowledgement, CancellationToken cancellationToken);

    Task ReportCommandResultAsync(Guid executionId, CommandResultReport report, CancellationToken cancellationToken);

    Task<CommandKeySet> GetCommandKeysAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Typed client for the agent API. All traffic is outbound; responses are
/// deserialized into fixed contracts only (source-generated, no polymorphism).
/// </summary>
public sealed class AgentApiClient : IAgentApiClient, IAgentCommandClient, IDisposable
{
    private readonly Lock _gate = new();
    private readonly IAgentTransport? _transport;
    private readonly Uri? _baseAddress;
    private readonly TimeSpan _timeout;
    private HttpClient _http;
    private string? _credentialVersion;

    public AgentApiClient(IOptions<AgentOptions> options, IAgentTransport transport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        var server = options.Value.ServerUrl ?? throw new InvalidOperationException("SolvixAgent:ServerUrl is not configured.");
        if (server.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("SolvixAgent:ServerUrl must use https.");
        }

        _transport = transport;
        _baseAddress = server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
        _timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 5, 600));
        _credentialVersion = transport.CredentialVersion;
        _http = CreateClient(transport);
    }

    /// <summary>Test hook: use a pre-built client (e.g. against an in-process server).</summary>
    internal AgentApiClient(HttpClient http) => _http = http;

    public Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, AgentRoutes.Enroll, request, AgentJsonContext.Default.EnrollmentRequest, AgentJsonContext.Default.EnrollmentResponse, cancellationToken);

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, AgentRoutes.Heartbeat, request, AgentJsonContext.Default.HeartbeatRequest, AgentJsonContext.Default.HeartbeatResponse, cancellationToken);

    public async Task SubmitInventoryAsync(InventoryReport report, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(report, AgentJsonContext.Default.InventoryReport);
        using var request = new HttpRequestMessage(HttpMethod.Put, AgentRoutes.Inventory) { Content = content };
        using var response = await SendRawAsync(request, cancellationToken);
    }

    public Task<CertificateRenewalResponse> RenewCertificateAsync(CertificateRenewalRequest request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, AgentRoutes.RenewCertificate, request, AgentJsonContext.Default.CertificateRenewalRequest, AgentJsonContext.Default.CertificateRenewalResponse, cancellationToken);

    public async Task<UpdateCheckResponse> CheckForUpdateAsync(string channel, string currentVersion, CancellationToken cancellationToken)
    {
        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"{AgentRoutes.Updates}?channel={Uri.EscapeDataString(channel)}&currentVersion={Uri.EscapeDataString(currentVersion)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendRawAsync(request, cancellationToken);
        return await ReadAsync(response, AgentJsonContext.Default.UpdateCheckResponse, cancellationToken);
    }

    public async Task<AgentCommandBatch> FetchCommandsAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        var uri = string.Create(CultureInfo.InvariantCulture, $"{AgentRoutes.Commands}?waitSeconds={Math.Clamp(waitSeconds, 0, 30)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendRawAsync(request, cancellationToken);
        return await ReadAsync(response, AgentJsonContext.Default.AgentCommandBatch, cancellationToken);
    }

    public async Task AcknowledgeCommandAsync(Guid executionId, CommandAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(acknowledgement, AgentJsonContext.Default.CommandAcknowledgement);
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentRoutes.CommandAcknowledgement(executionId)) { Content = content };
        using var response = await SendRawAsync(request, cancellationToken);
    }

    public async Task ReportCommandResultAsync(Guid executionId, CommandResultReport report, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(report, AgentJsonContext.Default.CommandResultReport);
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentRoutes.CommandResult(executionId)) { Content = content };
        using var response = await SendRawAsync(request, cancellationToken);
    }

    public async Task<CommandKeySet> GetCommandKeysAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AgentRoutes.CommandKeys);
        using var response = await SendRawAsync(request, cancellationToken);
        return await ReadAsync(response, AgentJsonContext.Default.CommandKeySet, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private HttpClient CreateClient(IAgentTransport transport)
    {
        var client = new HttpClient(transport.CreateHandler(), disposeHandler: true) { BaseAddress = _baseAddress, Timeout = _timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SolvixAgent/{AgentInfo.Version}");
        return client;
    }

    /// <summary>Returns a client whose connections were opened with the current device credential.</summary>
    private HttpClient CurrentClient()
    {
        if (_transport is null)
        {
            return _http;
        }

        lock (_gate)
        {
            var version = _transport.CredentialVersion;
            if (!string.Equals(version, _credentialVersion, StringComparison.Ordinal))
            {
                var previous = _http;
                _http = CreateClient(_transport);
                _credentialVersion = version;
                previous.Dispose();
            }

            return _http;
        }
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string route,
        TRequest body,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(body, requestType);
        using var request = new HttpRequestMessage(method, route) { Content = content };
        using var response = await SendRawAsync(request, cancellationToken);
        return await ReadAsync(response, responseType, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await CurrentClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AgentApiException(AgentApiFailure.Transient, null, null, $"Server unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentApiException(AgentApiFailure.Transient, HttpStatusCode.RequestTimeout, null, "The request timed out.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            AgentProblem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.AgentProblem, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                // Non-JSON error body (e.g. from a proxy).
            }

            throw new AgentApiException(
                AgentApiException.Classify(response.StatusCode),
                response.StatusCode,
                problem?.Code,
                $"{(int)response.StatusCode} {problem?.Title ?? response.ReasonPhrase}: {problem?.Detail}");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync(type, cancellationToken)
        ?? throw new AgentApiException(AgentApiFailure.Transient, response.StatusCode, null, "Empty response from server.");
}

/// <summary>Version and identity of the running agent build.</summary>
public static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
