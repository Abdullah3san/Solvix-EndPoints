namespace Solvix.Modules.Assets;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Configuration;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Modules;

/// <summary>IT asset management: register, lifecycle, maintenance, audits, depreciation and the link to managed endpoints.</summary>
public sealed class AssetsModule : IModule
{
    public string Name => "Assets";

    public Assembly Assembly => typeof(AssetsModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AssetsOptions>().Bind(configuration.GetSection(AssetsOptions.SectionName));
        services.AddScoped<AssetAuditTrail>();
        services.AddScoped<AssetReferences>();
        services.AddScoped<AssetNumbering>();
        services.AddScoped<IAssetSettingsService, AssetSettingsService>();
        services.AddScoped<IAssetMatchingService, AssetMatchingService>();
        services.AddScoped<IAssetService, AssetService>();
        services.AddScoped<IMaintenanceService, MaintenanceService>();
        services.AddScoped<IVendorService, VendorService>();
        services.AddScoped<IPhysicalAuditService, PhysicalAuditService>();
        services.AddScoped<IAssetReportService, AssetReportService>();
        services.AddScoped<IEndpointRegistrationService, EndpointRegistrationService>();

        // Integration: match physical assets whenever an endpoint enrolls or reports new hardware identity.
        services.AddScoped<IEndpointIdentityObserver, AssetEndpointObserver>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
