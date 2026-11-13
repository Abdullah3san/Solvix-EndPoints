namespace Solvix.Modules.Mdm.Providers.Apple;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Wakes a device so it contacts the MDM server and collects queued commands.</summary>
public interface IApplePushClient
{
    Task SendMdmPushAsync(string pushTokenHex, string pushMagic, string topic, CancellationToken cancellationToken);
}

/// <summary>APNs HTTP/2 provider API using the MDM push certificate as client certificate.</summary>
internal sealed class ApnsPushClient(IHttpClientFactory httpClientFactory, IOptions<MdmOptions> options, ILogger<ApnsPushClient> logger) : IApplePushClient
{
    public const string HttpClientName = "apns";

    public async Task SendMdmPushAsync(string pushTokenHex, string pushMagic, string topic, CancellationToken cancellationToken)
    {
        var apple = options.Value.Apple;
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(apple.ApnsHost), $"/3/device/{pushTokenHex}"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = JsonContent.Create(new Dictionary<string, string> { ["mdm"] = pushMagic }),
        };
        request.Headers.TryAddWithoutValidation("apns-push-type", "mdm");
        request.Headers.TryAddWithoutValidation("apns-topic", topic);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"APNs rejected the MDM push ({(int)response.StatusCode}): {body}");
        }

        logger.LogDebug("MDM push accepted by APNs");
    }

    /// <summary>Attaches the MDM push certificate to the named APNs client.</summary>
    public static HttpMessageHandler CreateHandler(MdmOptions options)
    {
        var handler = new SocketsHttpHandler();
        var path = options.Apple.PushCertificatePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, options.Apple.PushCertificatePassword);
            handler.SslOptions.ClientCertificates = [certificate];
        }

        return handler;
    }
}
