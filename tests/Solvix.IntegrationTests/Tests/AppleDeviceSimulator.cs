namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Mdm.Providers.Apple;

/// <summary>
/// Plays the part of an iPhone: owns an identity certificate, signs every message with it (Mdm-Signature) and
/// speaks the check-in / command protocol against the real API host.
/// </summary>
internal sealed class AppleDeviceSimulator : IDisposable
{
    private readonly X509Certificate2 _identity;
    private readonly HttpClient _http;

    public AppleDeviceSimulator(HttpClient anonymousClient)
    {
        _http = anonymousClient;
        _identity = NewIdentity();
    }

    public string? Handle { get; private set; }

    public string Udid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public static X509Certificate2 NewIdentity()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=device", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Downloads the enrollment profile and extracts the per-device URLs it contains.</summary>
    public async Task<Dictionary<string, object?>> DownloadProfileAsync(string enrollmentUrl)
    {
        var response = await _http.GetAsync(new Uri(enrollmentUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-apple-aspen-config", response.Content.Headers.ContentType?.MediaType);
        var profile = ApplePlist.ParseDictionary(await response.Content.ReadAsByteArrayAsync());
        var mdm = ((List<object?>)profile["PayloadContent"]!).OfType<Dictionary<string, object?>>().Single(p => (string?)p["PayloadType"] == "com.apple.mdm");
        var checkIn = (string)mdm["CheckInURL"]!;
        Handle = checkIn.Split('/')[^2];
        return profile;
    }

    public Task<HttpResponseMessage> AuthenticateAsync(Dictionary<string, object?>? extra = null, X509Certificate2? identity = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["MessageType"] = "Authenticate",
            ["Topic"] = SolvixApiFactory.AppleTopic,
            ["UDID"] = Udid,
            ["SerialNumber"] = "C02TESTSERIAL",
            ["DeviceName"] = "Test iPhone",
            ["Model"] = "iPhone15,2",
            ["ModelName"] = "iPhone 14 Pro",
            ["OSVersion"] = "17.4",
        };
        foreach (var (key, value) in extra ?? [])
        {
            if (value is null)
            {
                message.Remove(key);
            }
            else
            {
                message[key] = value;
            }
        }

        return CheckInAsync(message, identity);
    }

    public Task<HttpResponseMessage> TokenUpdateAsync(bool withUnlockToken = true, string identifierKey = "UDID")
    {
        var message = new Dictionary<string, object?>
        {
            ["MessageType"] = "TokenUpdate",
            ["Topic"] = SolvixApiFactory.AppleTopic,
            [identifierKey] = identifierKey == "UDID" ? Udid : EnrollmentId,
            ["Token"] = new byte[] { 0xde, 0xad, 0xbe, 0xef, 1, 2, 3, 4 },
            ["PushMagic"] = "magic-" + Handle,
        };
        if (withUnlockToken)
        {
            message["UnlockToken"] = new byte[] { 9, 9, 9, 9 };
        }

        return CheckInAsync(message);
    }

    public string EnrollmentId { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public Task<HttpResponseMessage> CheckOutAsync(string identifierKey = "UDID") =>
        CheckInAsync(new Dictionary<string, object?>
        {
            ["MessageType"] = "CheckOut",
            ["Topic"] = SolvixApiFactory.AppleTopic,
            [identifierKey] = identifierKey == "UDID" ? Udid : EnrollmentId,
        });

    public Task<HttpResponseMessage> CheckInAsync(Dictionary<string, object?> message, X509Certificate2? identity = null) =>
        SendAsync("checkin", message, identity);

    /// <summary>Sends a status message to the server endpoint and returns the command the server answers with (if any).</summary>
    public async Task<(HttpStatusCode Status, Dictionary<string, object?>? Command)> ReportAsync(Dictionary<string, object?> status, string identifierKey = "UDID")
    {
        status[identifierKey] = identifierKey == "UDID" ? Udid : EnrollmentId;
        var response = await SendAsync("server", status, null);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Only a 200 carries a command; anything else must have an empty body (devices ignore it either way).
            Assert.True(bytes.Length == 0, $"{(int)response.StatusCode} response had a body: {System.Text.Encoding.UTF8.GetString(bytes)}");
            return (response.StatusCode, null);
        }

        return (response.StatusCode, bytes.Length == 0 ? null : ApplePlist.ParseDictionary(bytes));
    }

    public Task<(HttpStatusCode Status, Dictionary<string, object?>? Command)> IdleAsync(string identifierKey = "UDID") =>
        ReportAsync(new Dictionary<string, object?> { ["Status"] = "Idle" }, identifierKey);

    public Task<(HttpStatusCode Status, Dictionary<string, object?>? Command)> AcknowledgeAsync(
        Dictionary<string, object?> command, Dictionary<string, object?>? response = null, string identifierKey = "UDID")
    {
        var message = new Dictionary<string, object?> { ["Status"] = "Acknowledged", ["CommandUUID"] = command["CommandUUID"] };
        foreach (var (k, v) in response ?? [])
        {
            message[k] = v;
        }

        return ReportAsync(message, identifierKey);
    }

    public static string RequestType(Dictionary<string, object?> command) => (string)((Dictionary<string, object?>)command["Command"]!)["RequestType"]!;

    public static Dictionary<string, object?> Arguments(Dictionary<string, object?> command) => (Dictionary<string, object?>)command["Command"]!;

    public async Task<HttpResponseMessage> SendAsync(string endpoint, Dictionary<string, object?> message, X509Certificate2? identity, bool sign = true, byte[]? bodyOverride = null) =>
        await SendRawAsync(Handle!, endpoint, ApplePlist.Serialize(message), sign ? Sign(ApplePlist.Serialize(message), identity ?? _identity) : null, bodyOverride);

    public async Task<HttpResponseMessage> SendRawAsync(string handle, string endpoint, byte[] body, string? signature, byte[]? bodyOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/mdm/apple/{handle}/{endpoint}")
        {
            Content = new ByteArrayContent(bodyOverride ?? body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-apple-plist");
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("Mdm-Signature", signature);
        }

        return await _http.SendAsync(request);
    }

    public static string Sign(byte[] body, X509Certificate2 identity)
    {
        var cms = new SignedCms(new ContentInfo(body), detached: true);
        cms.ComputeSignature(new CmsSigner(identity) { IncludeOption = X509IncludeOption.EndCertOnly });
        return Convert.ToBase64String(cms.Encode());
    }

    public void Dispose() => _identity.Dispose();
}
