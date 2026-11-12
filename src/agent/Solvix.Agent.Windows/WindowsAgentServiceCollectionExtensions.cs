namespace Solvix.Agent.Windows;

using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.Updates;
using Solvix.Agent.Windows.Inventory;
using Solvix.Agent.Windows.Security;
using Solvix.Agent.Windows.Updates;

public static class WindowsAgentServiceCollectionExtensions
{
    /// <summary>Windows key storage, secret protection, signature verification and inventory modules.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddSolvixAgentWindows(this IServiceCollection services, string dataDirectory)
    {
        services.Replace(ServiceDescriptor.Singleton<IDeviceKeyStore, CngDeviceKeyStore>());
        services.Replace(ServiceDescriptor.Singleton<ISecretProtector, DpapiSecretProtector>());
        services.Replace(ServiceDescriptor.Singleton<IPackageSignatureVerifier, AuthenticodeVerifier>());
        services.Replace(ServiceDescriptor.Singleton<IPackageInstaller>(_ => new MsiPackageInstaller(Path.Combine(dataDirectory, "logs"))));
        services.AddSingleton<IInventoryCollector, SystemCollector>();
        services.AddSingleton<IInventoryCollector, BiosCollector>();
        services.AddSingleton<IInventoryCollector, OperatingSystemCollector>();
        services.AddSingleton<IInventoryCollector, ProcessorCollector>();
        services.AddSingleton<IInventoryCollector, MemoryCollector>();
        services.AddSingleton<IInventoryCollector, DiskCollector>();
        services.AddSingleton<IInventoryCollector, SoftwareCollector>();
        return services;
    }
}
