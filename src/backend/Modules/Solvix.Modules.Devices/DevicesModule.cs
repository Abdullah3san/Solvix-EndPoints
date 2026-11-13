namespace Solvix.Modules.Devices;

using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Application.Credentials;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.SharedKernel.Modules;

public sealed class DevicesModule : IModule
{
    public string Name => "Devices";

    public Assembly Assembly => typeof(DevicesModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IDeviceGroupService, DeviceGroupService>();
        services.AddScoped<IEnrollmentTokenService, EnrollmentTokenService>();
        services.AddScoped<IAgentEnrollmentService, AgentEnrollmentService>();
        services.AddScoped<IAgentHeartbeatService, AgentHeartbeatService>();
        services.AddScoped<IDeviceCredentialCache, DeviceCredentialCache>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }

    /// <summary>Registers the <c>Device</c> authentication scheme used by endpoint agents.</summary>
    public static AuthenticationBuilder AddDeviceCredentialScheme(AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddScheme<AuthenticationSchemeOptions, DeviceCredentialAuthenticationHandler>(AgentAuth.Scheme, displayName: "Solvix device credential", _ => { });
    }
}
