namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// Hosts the real API (Program.cs) in-process. Configuration is injected
/// through environment variables, which WebApplication.CreateBuilder reads
/// before any service registration runs, so the values reliably override
/// appsettings.json.
/// </summary>
public sealed class SolvixApiFactory : WebApplicationFactory<Program>
{
    public const string JwtSigningKey = "integration-tests-signing-key-integration-tests-signing-key";

    public SolvixApiFactory(PostgresTestDatabase database, string redisConnection, string redisInstance)
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
            ["Solvix__Security__MinReportIntervalSeconds"] = "1",
            ["Serilog__MinimumLevel__Default"] = "Warning",
        };
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
