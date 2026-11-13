namespace Solvix.IntegrationTests.Fixtures;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.SharedKernel.Abstractions;

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
            ["Serilog__MinimumLevel__Default"] = "Warning",

            // Tests drive the deployment state machine explicitly (with a controllable clock).
            ["Solvix__EndpointManagement__WorkerEnabled"] = "false",
        };
        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>System time plus an adjustable offset; tests move it forward only around agent calls.</summary>
    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }
}

public sealed class TestClock : IClock
{
    private long _offsetTicks;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>Shifts server time until the returned handle is disposed.</summary>
    public IDisposable Advance(TimeSpan by)
    {
        Interlocked.Add(ref _offsetTicks, by.Ticks);
        return new Reset(this, by);
    }

    private sealed class Reset(TestClock clock, TimeSpan by) : IDisposable
    {
        public void Dispose() => Interlocked.Add(ref clock._offsetTicks, -by.Ticks);
    }
}
