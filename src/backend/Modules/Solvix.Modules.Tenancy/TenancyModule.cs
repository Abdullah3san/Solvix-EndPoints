namespace Solvix.Modules.Tenancy;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Tenancy.Application.Directory;
using Solvix.Modules.Tenancy.Application.Management;
using Solvix.Modules.Tenancy.Application.Platform;
using Solvix.Modules.Tenancy.Configuration;
using Solvix.Modules.Tenancy.Application.Registration;
using Solvix.SharedKernel.Modules;
using Solvix.SharedKernel.Tenancy;

public sealed class TenancyModule : IModule
{
    public string Name => "Tenancy";

    public Assembly Assembly => typeof(TenancyModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CachedTenantDirectory>();
        services.AddScoped<ITenantDirectory>(sp => sp.GetRequiredService<CachedTenantDirectory>());
        services.AddScoped<ITenantDirectoryCache>(sp => sp.GetRequiredService<CachedTenantDirectory>());
        services.AddScoped<TenantRegistrationService>();
        services.AddScoped<ITenantRegistrationService>(sp => sp.GetRequiredService<TenantRegistrationService>());
        services.AddScoped<ITenantProvisioner>(sp => sp.GetRequiredService<TenantRegistrationService>());
        services.AddScoped<ITenantService, TenantService>();
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.SectionName));
        services.AddScoped<IPlatformTenantService, PlatformTenantService>();
        services.AddScoped<IPlatformOperatorSeeder, PlatformOperatorSeeder>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
