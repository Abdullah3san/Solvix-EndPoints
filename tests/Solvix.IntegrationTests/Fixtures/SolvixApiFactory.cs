namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.SharedKernel.Email;

/// <summary>
/// Hosts the real API (Program.cs) in-process. Configuration is injected
/// through environment variables, which WebApplication.CreateBuilder reads
/// before any service registration runs, so the values reliably override
/// appsettings.json. Everything is production behaviour except the last hop of
/// e-mail delivery, which is captured instead of sent.
/// </summary>
public sealed class SolvixApiFactory : WebApplicationFactory<Program>
{
    public const string JwtSigningKey = "integration-tests-signing-key-integration-tests-signing-key";
    public const string JwtIssuer = "solvix-endpoints";
    public const string JwtAudience = "solvix-endpoints-web";
    public const string WebBaseUrl = "https://app.solvix.test";

    public SolvixApiFactory(PostgresTestDatabase database, string redisConnection, string redisInstance, CapturingEmailSender emails)
    {
        ArgumentNullException.ThrowIfNull(database);
        Emails = emails;
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
        };
        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public CapturingEmailSender Emails { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }
}
