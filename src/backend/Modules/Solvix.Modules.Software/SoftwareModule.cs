namespace Solvix.Modules.Software;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Configuration;
using Solvix.Modules.Software.Deployments;
using Solvix.Modules.Software.Inventory;
using Solvix.Modules.Software.Packages;
using Solvix.Modules.Software.Policies;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Modules;

/// <summary>
/// Enterprise software management (Phase 07): software inventory, the package repository,
/// deployment groups, approved and scheduled MSI/EXE deployments executed through the signed
/// remote command broker, deployment reports and software restriction rules.
/// </summary>
public sealed class SoftwareModule : IModule
{
    public string Name => "Software";

    public Assembly Assembly => typeof(SoftwareModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SoftwareOptions>().Bind(configuration.GetSection(SoftwareOptions.SectionName));
        services.AddHttpContextAccessor();

        services.AddSingleton<IPackageStorage, FileSystemPackageStorage>();
        services.AddScoped<SoftwareAuditTrail>();
        services.AddScoped<ISoftwareAuditQueries, SoftwareAuditQueries>();
        services.AddScoped<DeploymentPolicyProvider>();
        services.AddScoped<TargetResolver>();
        services.AddScoped<ISoftwarePolicyService, SoftwarePolicyService>();
        services.AddScoped<PackageService>();
        services.AddScoped<IPackageService>(sp => sp.GetRequiredService<PackageService>());
        services.AddScoped<IAgentPackageService, AgentPackageService>();
        services.AddScoped<ISoftwareInventoryQueries, SoftwareInventoryQueries>();
        services.AddScoped<ISoftwareInventoryObserver, SoftwareInventoryProjector>();
        services.AddScoped<DeploymentEngine>();
        services.AddScoped<IDeploymentService, DeploymentService>();
        services.AddScoped<IRemoteCommandObserver, DeploymentCommandObserver>();
        services.AddScoped<DeploymentProcessor>();
        services.AddHostedService<DeploymentProcessorService>();
    }
}
