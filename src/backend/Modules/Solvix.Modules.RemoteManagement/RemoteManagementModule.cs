namespace Solvix.Modules.RemoteManagement;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        services.AddHostedService<CommandSignerWarmup>();
        services.AddSingleton<CommandSignal>();

        services.AddScoped<RemoteAuditTrail>();
        services.AddScoped<RemoteAccessPolicyProvider>();
        services.AddScoped<RemoteAuthorizer>();
        services.AddScoped<CurrentAgentDevice>();
        services.AddScoped<CommandBroker.CommandBroker>();
        services.AddScoped<ICommandBroker>(sp => sp.GetRequiredService<CommandBroker.CommandBroker>());
        services.AddScoped<IRemoteCommandDispatcher, DelegatedCommandDispatcher>();
        services.AddScoped<RemoteCommandObserverPublisher>();
        services.AddScoped<AgentCommandService>();
        services.AddScoped<IAgentCommandService>(sp => sp.GetRequiredService<AgentCommandService>());
        services.AddScoped<IRemoteCommandQueue>(sp => sp.GetRequiredService<AgentCommandService>());
        services.AddScoped<IRemoteAccessAdministration, RemoteAccessAdministration>();
        services.AddScoped<IRemoteAuditQueries, RemoteAuditQueries>();
        services.AddScoped<CommandSweeper>();
        services.AddHostedService<CommandSweeperService>();

        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }

    /// <summary>Loads the command signing key at start-up so a missing or invalid key fails fast instead of on the first command.</summary>
    private sealed class CommandSignerWarmup(ICommandSigner signer) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = signer.KeyId;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
