namespace Solvix.Infrastructure.DependencyInjection;

using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvix.Infrastructure.Caching;
using Solvix.Infrastructure.Identity;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Persistence.Interceptors;
using Solvix.Infrastructure.Tenancy;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public static class InfrastructureServiceCollectionExtensions
{
    public const string PostgresConnectionName = "Postgres";
    public const string PostgresMigratorConnectionName = "PostgresMigrator";
    public const string RedisConnectionName = "Redis";

    /// <summary>Registers persistence, caching, tenancy and the feature modules.</summary>
    public static IServiceCollection AddSolvixInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection(RedisOptions.SectionName));

        services.AddSingleton<IClock, SystemClock>();
        services.AddHttpContextAccessor();

        // Tenant context: one mutable instance per scope, exposed through two interfaces.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextMutator>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();
        services.AddSingleton<ITenantJobRunner, TenantJobRunner>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddScoped<TenantEnforcementInterceptor>();
        services.AddScoped<TenantSessionConnectionInterceptor>();
        services.AddSingleton<IEnumerable<Assembly>>(SolvixModules.ConfigurationAssemblies);

        services.AddDbContext<SolvixDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connectionString = configuration.GetConnectionString(PostgresConnectionName)
                ?? throw new InvalidOperationException($"Connection string '{PostgresConnectionName}' is not configured.");

            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(SolvixDbContext).Assembly.GetName().Name);
                npgsql.CommandTimeout(db.CommandTimeoutSeconds);
                npgsql.EnableRetryOnFailure(3);
            });
            options.AddInterceptors(
                sp.GetRequiredService<TenantEnforcementInterceptor>(),
                sp.GetRequiredService<TenantSessionConnectionInterceptor>());
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
            if (db.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }
        });
        services.AddScoped<ISolvixDbContext>(sp => sp.GetRequiredService<SolvixDbContext>());

        var redis = configuration.GetConnectionString(RedisConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{RedisConnectionName}' is not configured.");
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redis;
            options.InstanceName = configuration.GetSection(RedisOptions.SectionName).GetValue<string>(nameof(RedisOptions.InstanceName)) ?? "solvix:";
        });

        // Wrap the Redis cache: an outage must degrade to "no cache", never to failed requests.
        var redisCacheDescriptor = services.Last(d => d.ServiceType == typeof(IDistributedCache));
        services.Remove(redisCacheDescriptor);
        services.AddSingleton<IDistributedCache>(sp =>
        {
            var inner = redisCacheDescriptor.ImplementationFactory is { } factory
                ? (IDistributedCache)factory(sp)
                : (IDistributedCache)ActivatorUtilities.CreateInstance(sp, redisCacheDescriptor.ImplementationType!);
            return new ResilientDistributedCache(inner, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientDistributedCache>>());
        });
        services.AddScoped<ITenantCache, TenantCache>();

        services.AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString(PostgresConnectionName)!, name: "postgres", tags: ["ready"])
            .AddRedis(redis, name: "redis", tags: ["ready"]);

        foreach (var module in SolvixModules.All)
        {
            module.ConfigureServices(services, configuration);
        }

        return services;
    }
}
