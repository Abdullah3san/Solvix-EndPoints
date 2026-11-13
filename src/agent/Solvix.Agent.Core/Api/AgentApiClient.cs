namespace Solvix.Agent.Core.Api;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;

/// <summary>The server rejected a report because the job attempt or restart is no longer current.</summary>
public sealed class AgentConflictException(string message) : Exception(message)
{
    public AgentConflictException()
        : this("The server rejected the request as stale.")
    {
    }

    public AgentConflictException(string message, Exception innerException)
        : this(message + " " + innerException?.Message)
    {
    }
}

/// <summary>The device credential was rejected (device retired or re-enrolled elsewhere).</summary>
public sealed class AgentUnauthorizedException(string message) : Exception(message)
{
    public AgentUnauthorizedException()
        : this("The device credential was rejected.")
    {
    }

    public AgentUnauthorizedException(string message, Exception innerException)
        : this(message + " " + innerException?.Message)
    {
    }
}

public interface IAgentApi
{
    Task<EnrollDeviceResponse> EnrollAsync(EnrollDeviceRequest request, CancellationToken cancellationToken);

    Task<AgentWorkResponse> HeartbeatAsync(AgentHeartbeatRequest request, CancellationToken cancellationToken);

    Task UploadInventoryAsync(PatchInventoryReport report, CancellationToken cancellationToken);

    Task ReportProgressAsync(Guid jobId, PatchJobProgress progress, CancellationToken cancellationToken);

    Task ReportResultAsync(Guid jobId, PatchJobResult result, CancellationToken cancellationToken);

    Task AcknowledgeRestartAsync(Guid restartId, RestartAck ack, CancellationToken cancellationToken);

    /// <summary>Returns null when the document is unchanged since <paramref name="knownRevision"/>.</summary>
    Task<ConfigurationDocument?> GetConfigurationAsync(string? knownRevision, CancellationToken cancellationToken);

    Task ReportConfigurationAsync(ConfigurationReport report, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP client for the agent endpoints (<c>/api/v1/agent/*</c>). Authenticates
/// with <c>Authorization: Device &lt;credential&gt;</c>; the server URL must be HTTPS
/// outside development.
/// </summary>
public sealed class AgentApiClient(HttpClient http, Func<string?> credential) : IAgentApi
{
    private const string Base = "api/v1/agent/";

    public async Task<EnrollDeviceResponse> EnrollAsync(EnrollDeviceRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Base + "enroll") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<EnrollDeviceResponse>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<AgentWorkResponse> HeartbeatAsync(AgentHeartbeatRequest request, CancellationToken cancellationToken) =>
        PostAsync<AgentHeartbeatRequest, AgentWorkResponse>("heartbeat", request, cancellationToken);

    public Task UploadInventoryAsync(PatchInventoryReport report, CancellationToken cancellationToken) =>
        PostAsync("patches/inventory", report, cancellationToken);

    public Task ReportProgressAsync(Guid jobId, PatchJobProgress progress, CancellationToken cancellationToken) =>
        PostAsync($"jobs/{jobId}/progress", progress, cancellationToken);

    public Task ReportResultAsync(Guid jobId, PatchJobResult result, CancellationToken cancellationToken) =>
        PostAsync($"jobs/{jobId}/result", result, cancellationToken);

    public Task AcknowledgeRestartAsync(Guid restartId, RestartAck ack, CancellationToken cancellationToken) =>
        PostAsync($"restarts/{restartId}/ack", ack, cancellationToken);

    public async Task<ConfigurationDocument?> GetConfigurationAsync(string? knownRevision, CancellationToken cancellationToken)
    {
        using var message = Authorized(HttpMethod.Get, "configuration");
        if (!string.IsNullOrEmpty(knownRevision))
        {
            message.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{knownRevision}\""));
        }

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ConfigurationDocument>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    public Task ReportConfigurationAsync(ConfigurationReport report, CancellationToken cancellationToken) =>
        PostAsync("configuration/report", report, cancellationToken);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken cancellationToken)
    {
        using var message = Authorized(HttpMethod.Post, path);
        message.Content = JsonContent.Create(body, options: ContractJson.Options);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<TResponse>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task PostAsync<TRequest>(string path, TRequest body, CancellationToken cancellationToken)
    {
        using var message = Authorized(HttpMethod.Post, path);
        message.Content = JsonContent.Create(body, options: ContractJson.Options);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, Base + path);
        var secret = credential() ?? throw new AgentUnauthorizedException("The agent is not enrolled.");
        message.Headers.Authorization = new AuthenticationHeaderValue(AgentAuth.Scheme, secret);
        return message;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw response.StatusCode switch
        {
            HttpStatusCode.Conflict => new AgentConflictException(body),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AgentUnauthorizedException($"{(int)response.StatusCode}: {body}"),
            _ => new HttpRequestException($"Server returned {(int)response.StatusCode}: {body}", null, response.StatusCode),
        };
    }
}
