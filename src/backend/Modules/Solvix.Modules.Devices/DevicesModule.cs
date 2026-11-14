namespace Solvix.Modules.Devices;

using System.Reflection;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Solvix.Modules.Devices.Api;
using Solvix.Modules.Devices.Application.Agent;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Modules;

/// <summary>Windows endpoint management: enrollment, device identity, inventory, groups and agent tracking.</summary>
public sealed class DevicesModule : IModule
{
    public string Name => "Devices";

    public Assembly Assembly => typeof(DevicesModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DevicesOptions.SectionName);
        services.AddOptions<DevicesOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DevicesOptions>, DevicesOptionsValidator>();

        services.AddSingleton<IDeviceCertificateAuthority, DeviceCertificateAuthority>();
        services.AddHostedService<DeviceCaWarmup>();
        services.AddSingleton<IAgentReleaseCatalog, AgentReleaseCatalog>();
        services.AddScoped<DeviceCredentialValidator>();
        services.AddScoped<ICurrentDevice, HttpCurrentDevice>();

        services.AddScoped<IEnrollmentTokenService, EnrollmentTokenService>();
        services.AddScoped<IAgentEnrollmentService, AgentEnrollmentService>();
        services.AddScoped<IAgentPackageService, AgentPackageService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IDeviceGroupService, DeviceGroupService>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);

        // Anonymous enrollment is the only unauthenticated device endpoint: throttle it per client address.
        var permits = section.GetSection(nameof(DevicesOptions.Enrollment)).GetValue<int?>(nameof(EnrollmentOptions.RateLimitPerMinute)) ?? 30;
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AgentController.EnrollmentRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    /// <summary>Loads the device CA at start-up so a misconfiguration fails fast instead of on first enrollment.</summary>
    private sealed class DeviceCaWarmup(IDeviceCertificateAuthority ca) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = ca.Certificate;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
