namespace Solvix.Modules.Support;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Support.Application;
using Solvix.SharedKernel.Modules;

/// <summary>Help desk: tickets (web console and agent) and live chat with the person using a device.</summary>
public sealed class SupportModule : IModule
{
    public string Name => "Support";

    public Assembly Assembly => typeof(SupportModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IAgentSupportService, AgentSupportService>();
        services.AddScoped<IRemoteSessionService, RemoteSessionService>();
        services.AddScoped<IAgentRemoteService, AgentRemoteService>();
        services.AddSingleton<IScreenFrameStore, DistributedScreenFrameStore>();
        services.AddScoped<IRemoteScreenService, RemoteScreenService>();
        services.AddScoped<IAgentScreenService, AgentScreenService>();
        services.AddScoped<IRemoteFileService, RemoteFileService>();
        services.AddScoped<IAgentRemoteFileService, AgentRemoteFileService>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
