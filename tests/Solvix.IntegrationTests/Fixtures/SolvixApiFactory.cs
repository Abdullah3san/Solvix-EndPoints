namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Email;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.Modules.Mdm.Providers.Apple;

/// <summary>
/// Hosts the real API (Program.cs) in-process. Configuration is injected
/// through environment variables, which WebApplication.CreateBuilder reads
/// before any service registration runs, so the values reliably override
/// appsettings.json. Everything is production behaviour except the last hop of
/// e-mail delivery, which is captured instead of sent.
/// </summary>
public class SolvixApiFactory : WebApplicationFactory<Program>
{
    public const string AppleTopic = "com.apple.mgmt.External.00000000-test";

    public FakeAndroidManagementApi AndroidApi { get; } = new();

    public RecordingApplePushClient ApplePush { get; } = new();

    /// <summary>In-process tests have no TLS; the client certificate travels the way a trusted proxy forwards it.</summary>
    public const string ForwardedCertificateHeader = "X-Client-Cert";

    public const string LatestAgentVersion = "1.2.0";

    public const string JwtSigningKey = "integration-tests-signing-key-integration-tests-signing-key";
    public const string JwtIssuer = "solvix-endpoints";
    public const string JwtAudience = "solvix-endpoints-web";
    public const string WebBaseUrl = "https://app.solvix.test";

    /// <summary>Command signing key of the API under test (P-256, generated per test run).</summary>
    public static readonly string TestCommandKeyPem = CreateCommandKey();

    private static string CreateCommandKey()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    public SolvixApiFactory(
        PostgresTestDatabase database,
        string redisConnection,
        string redisInstance,
        CapturingEmailSender? emails = null,
        IReadOnlyDictionary<string, string>? extraSettings = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        Emails = emails ?? new CapturingEmailSender();
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings__Postgres"] = database.AppConnectionString,
            ["ConnectionStrings__PostgresMigrator"] = database.MigratorConnectionString,
            ["ConnectionStrings__Redis"] = redisConnection,
            ["Solvix__Redis__InstanceName"] = redisInstance,
            ["Solvix__Database__MigrateOnStartup"] = "true",
            ["Solvix__Jwt__SigningKey"] = JwtSigningKey,
            ["Solvix__Jwt__AccessTokenMinutes"] = "15",
            ["Solvix__Identity__DataProtectionKey"] = "integration-tests-data-protection-key-0123456789",
            ["Solvix__Identity__WebBaseUrl"] = WebBaseUrl,
            ["Solvix__Identity__RequireEmailVerification"] = "true",

            // The suite sends many e-mails to the same accounts back to back.
            ["Solvix__Identity__EmailCooldownSeconds"] = "0",

            // Every test request comes from the same (absent) client address, so the shared
            // limits are lifted here; RateLimitingTests starts a host with strict ones.
            ["Solvix__RateLimiting__Credentials__PermitLimit"] = "100000",
            ["Solvix__RateLimiting__Email__PermitLimit"] = "100000",
            ["Solvix__RateLimiting__Refresh__PermitLimit"] = "100000",
            ["Solvix__RateLimiting__Registration__PermitLimit"] = "100000",
            ["Solvix__Logging__Format"] = "text",
            ["Solvix__Cors__AllowedOrigins__0"] = "http://localhost:3000",
            // The real fleet adapter is used everywhere; AssistantHost swaps in the demo fleet for chat tests.
            ["Solvix__Assistant__UseDemoFleet"] = "false",
            ["Solvix__Assistant__ChatPermitsPerMinute"] = "10000",
            ["Solvix__Security__MinReportIntervalSeconds"] = "1",
            ["Serilog__MinimumLevel__Default"] = "Warning",
            ["Solvix__Devices__CertificateAuthority__AutoGenerate"] = "true",
            ["Solvix__Devices__ForwardedClientCertificateHeader"] = ForwardedCertificateHeader,
            ["Solvix__Devices__Enrollment__RateLimitPerMinute"] = "100000",
            ["Solvix__Devices__Releases__0__Version"] = LatestAgentVersion,
            ["Solvix__Devices__Releases__0__Channel"] = "stable",
            ["Solvix__Devices__Releases__0__DownloadUrl"] = "https://downloads.solvix.test/agent/SolvixAgent-" + LatestAgentVersion + ".msi",
            ["Solvix__Devices__Releases__0__Sha256"] = new string('a', 64),
            ["Solvix__Devices__Releases__0__SizeBytes"] = "1048576",
            // Tests drive the monitor explicitly (MonitoringTestKit.SweepAsync).
            ["Solvix__Devices__Monitoring__RunMonitor"] = "false",

            // Remote management: a fresh command signing key per test run; tests drive the sweeper.
            ["Solvix__RemoteManagement__CommandSigning__KeyPem"] = TestCommandKeyPem,
            ["Solvix__RemoteManagement__RunSweeper"] = "false",
            ["Solvix__RemoteManagement__LongPollSeconds"] = "2",

            // Software management: packages in a private temporary directory; tests drive the deployment engine.
            ["Solvix__Software__Storage__RootPath"] = PackageStoragePath,
            ["Solvix__Software__RunProcessor"] = "false",
            ["Solvix__Mdm__PublicBaseUrl"] = "https://mdm.example.test",
            ["Solvix__Mdm__Android__ProjectId"] = "solvix-test-project",
            ["Solvix__Mdm__Android__ServiceAccountJson"] = "{}",
            ["Solvix__Mdm__Apple__PushTopic"] = AppleTopic,
            ["Solvix__Mdm__Apple__ScepUrl"] = "https://scep.example.test/scep",
            ["Solvix__Mdm__Apple__ScepChallenge"] = "challenge",
            ["Solvix__Mdm__Apple__AllowUntrustedDeviceIdentity"] = "true",
        };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string>())
        {
            settings[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>Package storage of this test run (deleted with the process's temporary files).</summary>
    public static string PackageStoragePath { get; } = Path.Combine(Path.GetTempPath(), "solvix-test-packages-" + Guid.NewGuid().ToString("N"));

    /// <summary>The API's clock (real time unless a test advances it).</summary>
    public TestClock Clock { get; } = new();

    public CapturingEmailSender Emails { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            // Provider network calls are replaced by in-memory fakes that tests can inspect and drive.
            services.RemoveAll<IAndroidManagementApi>();
            services.RemoveAll<IApplePushClient>();
            services.AddSingleton(AndroidApi);
            services.AddSingleton<IAndroidManagementApi>(AndroidApi);
            services.AddSingleton(ApplePush);
            services.AddSingleton<IApplePushClient>(ApplePush);
        });
    }
}
