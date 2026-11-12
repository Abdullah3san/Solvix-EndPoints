namespace Solvix.Infrastructure.Persistence;

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Solvix.Infrastructure.DependencyInjection;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c>. It does not need a reachable
/// database to add migrations; the connection string only matters for
/// <c>database update</c>.
/// </summary>
public sealed class SolvixDbContextFactory : IDesignTimeDbContextFactory<SolvixDbContext>
{
    public SolvixDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresMigrator")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=solvix;Username=solvix_migrator;Password=solvix_migrator_dev";

        var options = new DbContextOptionsBuilder<SolvixDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(SolvixDbContext).Assembly.GetName().Name))
            .Options;

        return new SolvixDbContext(options, new TenantContext(), SolvixModules.ConfigurationAssemblies);
    }
}
