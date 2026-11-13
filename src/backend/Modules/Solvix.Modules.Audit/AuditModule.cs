namespace Solvix.Modules.Audit;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Audit.Application;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Modules;

public sealed class AuditModule : IModule
{
    public string Name => "Audit";

    public Assembly Assembly => typeof(AuditModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
    }
}
