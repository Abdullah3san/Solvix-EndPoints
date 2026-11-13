namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.Modules.Mdm.Providers.Apple;

/// <summary>
/// Hosts the real API (Program.cs) in-process. Configuration is injected
/// through environment variables, which WebApplication.CreateBuilder reads
/// before any service registration runs, so the values reliably override
/// appsettings.json.
/// </summary>
public sealed class SolvixApiFactory : WebApplicationFactory<Program>
{
    public const string AppleTopic = "com.apple.mgmt.External.00000000-test";

    public const string JwtSigningKey = "integration-tests-signing-key-integration-tests-signing-key";

    public FakeAndroidManagementApi AndroidApi { get; } = new();

    public RecordingApplePushClient ApplePush { get; } = new();

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
            ["Serilog__MinimumLevel__Default"] = "Warning",
            ["Solvix__Mdm__PublicBaseUrl"] = "https://mdm.example.test",
            ["Solvix__Mdm__Android__ProjectId"] = "solvix-test-project",
            ["Solvix__Mdm__Android__ServiceAccountJson"] = "{}",
            ["Solvix__Mdm__Apple__PushTopic"] = AppleTopic,
            ["Solvix__Mdm__Apple__ScepUrl"] = "https://scep.example.test/scep",
            ["Solvix__Mdm__Apple__ScepChallenge"] = "challenge",
            ["Solvix__Mdm__Apple__AllowUntrustedDeviceIdentity"] = "true",
        };
        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
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
