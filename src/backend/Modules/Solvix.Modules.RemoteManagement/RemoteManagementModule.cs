namespace Solvix.Modules.RemoteManagement;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Authorization;
using Solvix.Modules.RemoteManagement.CommandBroker;
using Solvix.Modules.RemoteManagement.Configuration;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Modules;

/// <summary>
/// Secure remote endpoint administration (Phase 06): authorization and scope, the signed
/// command broker, approvals and the tamper-evident remote audit trail.
/// </summary>
public sealed class RemoteManagementModule : IModule
{
    public string Name => "RemoteManagement";

    public Assembly Assembly => typeof(RemoteManagementModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RemoteManagementOptions>().Bind(configuration.GetSection(RemoteManagementOptions.SectionName));
        services.AddHttpContextAccessor();

        services.AddSingleton<ICommandSigner, EcdsaCommandSigner>();
        services.AddSingleton<CommandSignal>();

        services.AddScoped<RemoteAuditTrail>();
        services.AddScoped<RemoteAccessPolicyProvider>();
        services.AddScoped<RemoteAuthorizer>();
        services.AddScoped<CurrentAgentDevice>();
        services.AddScoped<ICommandBroker, CommandBroker.CommandBroker>();
        services.AddScoped<AgentCommandService>();
        services.AddScoped<IAgentCommandService>(sp => sp.GetRequiredService<AgentCommandService>());
        services.AddScoped<IRemoteCommandQueue>(sp => sp.GetRequiredService<AgentCommandService>());
        services.AddScoped<IRemoteAccessAdministration, RemoteAccessAdministration>();
        services.AddScoped<IRemoteAuditQueries, RemoteAuditQueries>();
        services.AddScoped<CommandSweeper>();
        services.AddHostedService<CommandSweeperService>();

        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
