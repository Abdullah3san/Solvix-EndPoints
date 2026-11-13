namespace Solvix.Modules.EndpointManagement;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.EndpointManagement.Application;
using Solvix.Modules.EndpointManagement.Application.Agent;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Application.Deployments;
using Solvix.Modules.EndpointManagement.Application.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Application.Patches;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Application.Worker;
using Solvix.SharedKernel.Modules;

/// <summary>Phase 09: patch management and configuration management.</summary>
public sealed class EndpointManagementModule : IModule
{
    public string Name => "EndpointManagement";

    public Assembly Assembly => typeof(EndpointManagementModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EndpointManagementOptions>().Bind(configuration.GetSection(EndpointManagementOptions.SectionName));

        services.AddScoped<ITargetingService, TargetingService>();
        services.AddScoped<IMaintenanceWindowService, MaintenanceWindowService>();

        services.AddScoped<IPatchInventoryIngestion, PatchInventoryIngestion>();
        services.AddScoped<IPatchCatalogService, PatchCatalogService>();
        services.AddScoped<IPatchComplianceService, PatchComplianceService>();
        services.AddScoped<PatchDeploymentService>();
        services.AddScoped<IPatchDeploymentService>(sp => sp.GetRequiredService<PatchDeploymentService>());
        services.AddScoped<IDeviceGroupUsageCheck>(sp => sp.GetRequiredService<PatchDeploymentService>());
        services.AddScoped<IDeploymentOrchestrator, DeploymentOrchestrator>();
        services.AddScoped<IRestartService, RestartService>();
        services.AddScoped<AgentPatchingService>();
        services.AddScoped<IAgentPatchingService>(sp => sp.GetRequiredService<AgentPatchingService>());
        services.AddScoped<IAgentWorkProvider>(sp => sp.GetRequiredService<AgentPatchingService>());

        services.AddScoped<IConfigurationEngine, ConfigurationEngine>();
        services.AddScoped<IConfigurationPolicyService, ConfigurationPolicyService>();
        services.AddScoped<IConfigurationReportingService, ConfigurationReportingService>();

        services.AddHostedService<EndpointManagementWorker>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
