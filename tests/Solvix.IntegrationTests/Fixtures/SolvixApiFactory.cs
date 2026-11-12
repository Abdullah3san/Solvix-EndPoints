namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// Hosts the real API (Program.cs) in-process. Configuration is injected
/// through environment variables, which WebApplication.CreateBuilder reads
/// before any service registration runs, so the values reliably override
/// appsettings.json.
/// </summary>
public class SolvixApiFactory : WebApplicationFactory<Program>
{
    /// <summary>In-process tests have no TLS; the client certificate travels the way a trusted proxy forwards it.</summary>
    public const string ForwardedCertificateHeader = "X-Client-Cert";

    public const string LatestAgentVersion = "1.2.0";

    public const string JwtSigningKey = "integration-tests-signing-key-integration-tests-signing-key";

    public SolvixApiFactory(PostgresTestDatabase database, string redisConnection, string redisInstance, IReadOnlyDictionary<string, string>? extraSettings = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings__Postgres"] = database.AppConnectionString,
            ["ConnectionStrings__PostgresMigrator"] = database.MigratorConnectionString,
            ["ConnectionStrings__Redis"] = redisConnection,
            ["Solvix__Redis__InstanceName"] = redisInstance,
            ["Solvix__Database__MigrateOnStartup"] = "true",
            ["Solvix__Jwt__SigningKey"] = JwtSigningKey,
            ["Solvix__Jwt__AccessTokenMinutes"] = "30",
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
