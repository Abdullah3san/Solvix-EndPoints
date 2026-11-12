// Applies pending EF Core migrations and exits. Used as a one-shot container in
// Docker Compose (and in CI/CD) so the API never needs schema-owner credentials.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;
using Solvix.Infrastructure.DependencyInjection;
using Solvix.Infrastructure.Persistence;
using Solvix.SharedKernel.Tenancy;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    // The migrator connects with the schema-owner role. Fall back to the app
    // connection string so a single-role local setup still works.
    var migratorConnection = builder.Configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.PostgresMigratorConnectionName)
        ?? builder.Configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.PostgresConnectionName)
        ?? throw new InvalidOperationException("ConnectionStrings:PostgresMigrator (or ConnectionStrings:Postgres) must be configured.");

    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = migratorConnection,
        ["ConnectionStrings:Redis"] = builder.Configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.RedisConnectionName) ?? "localhost:6379",
        ["Solvix:Jwt:SigningKey"] = builder.Configuration["Solvix:Jwt:SigningKey"] ?? new string('x', 32),
    });
    builder.Services.AddSolvixInfrastructure(builder.Configuration);

    using var host = builder.Build();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");

    await using var scope = host.Services.CreateAsyncScope();
    using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
    var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();

    // Only query the history table when it exists; EF logs a (harmless) command error otherwise.
    if (await db.GetService<IHistoryRepository>().ExistsAsync())
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        logger.LogInformation("Pending migrations: {Count} {Migrations}", pending.Count, pending);
    }
    else
    {
        logger.LogInformation("Empty database: applying all migrations");
    }

    var attempts = 0;
    while (true)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Npgsql.NpgsqlException ex) when (++attempts < 10)
        {
            logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in 3s", attempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
    logger.LogInformation("Database is up to date. Applied migrations: {Count}", applied.Count);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Migration failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
