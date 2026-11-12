namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Email;

/// <summary>
/// Hosts the real API (Program.cs) in-process. Configuration is injected
/// through environment variables, which WebApplication.CreateBuilder reads
/// before any service registration runs, so the values reliably override
/// appsettings.json. Everything is production behaviour except the last hop of
/// e-mail delivery, which is captured instead of sent.
/// </summary>
public class SolvixApiFactory : WebApplicationFactory<Program>
{
    /// <summary>In-process tests have no TLS; the client certificate travels the way a trusted proxy forwards it.</summary>
    public const string ForwardedCertificateHeader = "X-Client-Cert";

    public const string LatestAgentVersion = "1.2.0";

    public const string JwtSigningKey = "integration-tests-signing-key-integration-tests-signing-key";
    public const string JwtIssuer = "solvix-endpoints";
    public const string JwtAudience = "solvix-endpoints-web";
    public const string WebBaseUrl = "https://app.solvix.test";

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
        });
    }
}
