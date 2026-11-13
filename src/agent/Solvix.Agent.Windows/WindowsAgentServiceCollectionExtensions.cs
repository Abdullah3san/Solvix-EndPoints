namespace Solvix.Agent.Windows;

using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Monitoring;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.RemoteManagement.Privileged;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.Updates;
using Solvix.Agent.Windows.Inventory;
using Solvix.Agent.Windows.Monitoring;
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
        services.Replace(ServiceDescriptor.Singleton<ISystemHealthProbe, WindowsSystemHealthProbe>());
        // The agent forwards privileged operations to the separate Remote Executor service.
        services.Replace(ServiceDescriptor.Singleton<IPrivilegedOperationHost, PipePrivilegedOperationHost>());
        services.AddSingleton<ICommandHandler, Solvix.Agent.Windows.RemoteManagement.ServicesHandler>();
        services.AddSingleton<ICommandHandler, Solvix.Agent.Windows.RemoteManagement.LoggedInUsersHandler>();
        services.AddSingleton<IInventoryCollector, SystemCollector>();
        services.AddSingleton<IInventoryCollector, BiosCollector>();
        services.AddSingleton<IInventoryCollector, OperatingSystemCollector>();
        services.AddSingleton<IInventoryCollector, ProcessorCollector>();
        services.AddSingleton<IInventoryCollector, MemoryCollector>();
        services.AddSingleton<IInventoryCollector, DiskCollector>();
        services.AddSingleton<IInventoryCollector, SoftwareCollector>();
        return services;
    }

    /// <summary>Registers what only the Remote Executor process needs: the privileged Win32 operations and the restricted pipe.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddSolvixRemoteExecutorWindows(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IExecutorPipeFactory, Solvix.Agent.Windows.RemoteManagement.SecuredExecutorPipeFactory>());
        return services.AddSolvixRemoteExecutor<Solvix.Agent.Windows.RemoteManagement.WindowsPowerOperations>();
    }
}
