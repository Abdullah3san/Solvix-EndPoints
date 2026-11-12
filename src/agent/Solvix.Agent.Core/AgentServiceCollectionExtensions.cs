namespace Solvix.Agent.Core;

using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Enrollment;
using Solvix.Agent.Core.Health;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;
using Solvix.Agent.Core.Updates;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-neutral agent. The host adds the platform pieces:
    /// <see cref="IDeviceKeyStore"/>, <see cref="ISecretProtector"/>,
    /// <see cref="IPackageSignatureVerifier"/>, <see cref="IPackageInstaller"/> and
    /// additional <see cref="IInventoryCollector"/>s.
    /// </summary>
    public static IServiceCollection AddSolvixAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAgentStateStore, FileAgentStateStore>();
        services.TryAddSingleton<DeviceCredentialStore>();
        services.TryAddSingleton<BootstrapTokenStore>();
        services.TryAddSingleton<IAgentTransport, MutualTlsTransport>();
        services.TryAddSingleton<IAgentApiClient, AgentApiClient>();
        services.TryAddSingleton<ConnectionMonitor>();
        services.TryAddSingleton<AgentHealthMonitor>();
        services.TryAddSingleton<IDeviceIdentityProvider, DeviceIdentityProvider>();
        services.TryAddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IInventoryCollector, NetworkInventoryCollector>();
        services.AddSingleton<IInventoryCollector, VolumeInventoryCollector>();
        services.TryAddSingleton<EnrollmentCoordinator>();
        services.TryAddSingleton<UpdateManager>();
        services.TryAddSingleton<IPackageSignatureVerifier, UnsupportedSignatureVerifier>();
        services.TryAddSingleton<AgentRuntime>();
        services.AddHttpClient(UpdateManager.DownloadClientName, c => c.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
                MaxAutomaticRedirections = 5,
            });
        return services;
    }
}
