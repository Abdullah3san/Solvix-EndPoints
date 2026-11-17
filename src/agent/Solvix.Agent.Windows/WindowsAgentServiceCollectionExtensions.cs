namespace Solvix.Agent.Windows;

using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Endpoints;
using Solvix.Agent.Core.Installation;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Monitoring;
using Solvix.Agent.Core.Patching;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.RemoteManagement.Privileged;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.Software;
using Solvix.Agent.Core.Updates;
using Solvix.Agent.Windows.Inventory;
using Solvix.Agent.Windows.Monitoring;
using Solvix.Agent.Windows.Platform;
using Solvix.Agent.Windows.Providers;
using Solvix.Agent.Windows.Security;
using Solvix.Agent.Windows.Software;
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
        services.Replace(ServiceDescriptor.Singleton<IInstalledApplicationSource, UninstallRegistrySource>());
        services.Replace(ServiceDescriptor.Singleton<Solvix.Agent.Core.Software.IExecutorPipeFactory, SecureExecutorPipeFactory>());
        // The agent forwards privileged operations to the separate Remote Executor service.
        services.Replace(ServiceDescriptor.Singleton<IPrivilegedOperationHost, PipePrivilegedOperationHost>());
        services.AddSingleton<ICommandHandler, Solvix.Agent.Windows.RemoteManagement.ServicesHandler>();
        services.AddSingleton<ICommandHandler, Solvix.Agent.Windows.RemoteManagement.LoggedInUsersHandler>();
        services.Replace(ServiceDescriptor.Singleton<IElevation, WindowsElevation>());
        services.AddSingleton<IInventoryCollector, SystemCollector>();
        services.AddSingleton<IInventoryCollector, BiosCollector>();
        services.AddSingleton<IInventoryCollector, OperatingSystemCollector>();
        services.AddSingleton<IInventoryCollector, ProcessorCollector>();
        services.AddSingleton<IInventoryCollector, MemoryCollector>();
        services.AddSingleton<IInventoryCollector, DiskCollector>();
        services.AddSingleton<IInventoryCollector, SoftwareCollector>();
        return services;
    }

    /// <summary>
    /// Endpoint management (Phase 09). In the agent: Windows Update scanning and read-only configuration checks.
    /// In the software executor (LocalSystem): installing updates and enforcing settings for verified commands,
    /// with snapshots and the install journal kept in the executor's private directory.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddSolvixEndpointManagementWindows(this IServiceCollection services, bool executor)
    {
        services.TryAddSingleton<RebootManager>();
        services.TryAddSingleton<IRebootManager>(sp => sp.GetRequiredService<RebootManager>());
        services.TryAddSingleton<IWindowsUpdateClient, WindowsUpdateClient>();
        services.AddSingleton<ISettingProvider, ServiceSettingProvider>();
        services.AddSingleton<ISettingProvider, RegistrySettingProvider>();
        services.AddSingleton<ISettingProvider, StartupAppSettingProvider>();
        services.AddSingleton<ISettingProvider, PowerSettingProvider>();
        services.AddSingleton<ISettingProvider, ScreenLockSettingProvider>();
        services.AddSingleton<ISettingProvider, UsbStorageSettingProvider>();
        services.AddSingleton<ISettingProvider, FirewallProfileSettingProvider>();
        services.AddSingleton<ISettingProvider, FirewallRuleSettingProvider>();
        if (!executor)
        {
            services.AddHostedService<EndpointManagementWorker>();
            return services;
        }

        static string Private(IServiceProvider sp, string name) =>
            Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.Software.ResolveExecutorDirectory(), name);
        services.TryAddSingleton<IPatchJobJournal>(sp => new FilePatchJobJournal(Private(sp, "patch-journal.json")));
        services.TryAddSingleton<ISnapshotStore>(sp => new FileSnapshotStore(Private(sp, "configuration-snapshots")));
        services.TryAddSingleton<IAppliedSettingsLedger, InMemoryAppliedSettingsLedger>();
        services.TryAddSingleton<PatchJobRunner>();
        services.TryAddSingleton<ConfigurationEvaluator>();
        services.TryAddSingleton<IEndpointCommandExecutor, EndpointCommandExecutor>();
        return services;
    }

    /// <summary>Registers what only the Remote Executor process needs: the privileged Win32 operations and the restricted pipe.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddSolvixRemoteExecutorWindows(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<Solvix.Agent.Core.RemoteManagement.Privileged.IExecutorPipeFactory, Solvix.Agent.Windows.RemoteManagement.SecuredExecutorPipeFactory>());
        return services.AddSolvixRemoteExecutor<Solvix.Agent.Windows.RemoteManagement.WindowsPowerOperations>();
    }
}
