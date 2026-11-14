namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;

/// <summary>
/// Runs the API on real Kestrel with the dedicated "Agents" HTTPS endpoint, so
/// the TLS handshake, client-certificate request and certificate authentication
/// are exercised exactly as in production (no forwarded header).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AgentMutualTlsTests(ApiFixture fixture) : IAsyncLifetime
{
    private readonly string _certificateDirectory = Path.Combine(Path.GetTempPath(), "solvix-tls-" + Guid.NewGuid().ToString("N"));
    private SolvixApiFactory? _factory;
    private Uri _agentBase = null!;
    private X509Certificate2 _serverCertificate = null!;

    private static readonly string[] OverriddenKeys =
    [
        "Kestrel__Endpoints__Http__Url",
        "Kestrel__Endpoints__Agents__Url",
        "Kestrel__Endpoints__Agents__Certificate__Path",
        "Kestrel__Endpoints__Agents__Certificate__Password",
        "Solvix__Devices__GenerateDevelopmentTlsCertificate",
        "Solvix__Devices__DevelopmentTlsHostNames",
        "Solvix__Devices__ForwardedClientCertificateHeader",
    ];

    public async Task InitializeAsync()
    {
        var httpPort = FreePort();
        var tlsPort = FreePort();
        var certificatePath = Path.Combine(_certificateDirectory, "agents-tls.pfx");
        _factory = new SolvixApiFactory(
            fixture.Database,
            fixture.RedisConnection,
            $"solvix-test-{Guid.NewGuid():N}:",
            new Dictionary<string, string>
            {
                ["Kestrel__Endpoints__Http__Url"] = $"http://127.0.0.1:{httpPort}",
                ["Kestrel__Endpoints__Agents__Url"] = $"https://127.0.0.1:{tlsPort}",
                ["Kestrel__Endpoints__Agents__Certificate__Path"] = certificatePath,
                ["Kestrel__Endpoints__Agents__Certificate__Password"] = "test-tls",
                ["Solvix__Devices__GenerateDevelopmentTlsCertificate"] = "true",
                ["Solvix__Devices__DevelopmentTlsHostNames"] = "localhost,127.0.0.1",

                // Real mTLS only: header forwarding is off on this host.
                ["Solvix__Devices__ForwardedClientCertificateHeader"] = string.Empty,
            });
        _factory.UseKestrel();
        _factory.StartServer();

        // Do not leak the Kestrel endpoints into hosts built later in this process.
        foreach (var key in OverriddenKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        _agentBase = new Uri($"https://127.0.0.1:{tlsPort}/");
        _serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, "test-tls");
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        _serverCertificate.Dispose();
        if (Directory.Exists(_certificateDirectory))
        {
            Directory.Delete(_certificateDirectory, true);
        }
    }

    [Fact]
    public async Task Agent_enrolls_and_authenticates_over_real_mutual_tls()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant);

        // 1. Enrollment: TLS without client certificate.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var anonymous = CreateClient(null);
        var enrollment = await HttpAssert.OkAsync<EnrollmentResponse>(await anonymous.PostAsJsonAsync(
            AgentRoutes.Enroll,
            new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = DeviceTestKit.CreateCsr(key), Device = DeviceTestKit.Identity() },
            ApiFixture.Json));

        // 2. Device certificate presented in the TLS handshake.
        using var certificate = X509Certificate2.CreateFromPem(enrollment.CertificatePem).CopyWithPrivateKey(key);
        using var device = CreateClient(certificate);
        var heartbeat = await HttpAssert.OkAsync<HeartbeatResponse>(await device.HeartbeatAsync());
        Assert.False(heartbeat.InventoryRequested);

        // 3. No certificate, or a self-signed one: rejected by the authentication handler.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.HeartbeatAsync()).StatusCode);
        using var rogueKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rogueRequest = new CertificateRequest($"CN={enrollment.DeviceId}", rogueKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(DeviceIdentityUri.Format(enrollment.TenantId, enrollment.DeviceId)));
        rogueRequest.CertificateExtensions.Add(san.Build());
        using var rogue = rogueRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var rogueClient = CreateClient(rogue);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rogueClient.HeartbeatAsync()).StatusCode);

        // 4. A forwarded-certificate header is ignored on a host that terminates TLS itself.
        using var spoof = CreateClient(null);
        spoof.DefaultRequestHeaders.Add(SolvixApiFactory.ForwardedCertificateHeader, Convert.ToBase64String(certificate.RawData));
        Assert.Equal(HttpStatusCode.Unauthorized, (await spoof.HeartbeatAsync()).StatusCode);
    }

    private HttpClient CreateClient(X509Certificate2? clientCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = clientCertificate is null ? null : new X509CertificateCollection { clientCertificate },
                LocalCertificateSelectionCallback = clientCertificate is null ? null : (_, _, _, _, _) => clientCertificate,

                // Pin the generated development server certificate.
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is not null && presented.GetCertHashString() == _serverCertificate.GetCertHashString(),
            },
        };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = _agentBase };
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
