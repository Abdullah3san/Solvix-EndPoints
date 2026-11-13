namespace Solvix.Infrastructure.DependencyInjection;

using System.Reflection;
using Solvix.Modules.Assets;
using Solvix.Modules.Devices;
using Solvix.Modules.Identity;
using Solvix.Modules.Organization;
using Solvix.Modules.RemoteManagement;
using Solvix.Modules.Tenancy;
using Solvix.SharedKernel.Modules;

/// <summary>Registry of the feature modules composed into this deployment.</summary>
public static class SolvixModules
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new TenancyModule(),
        new IdentityModule(),
        new OrganizationModule(),
        new DevicesModule(),
        new AssetsModule(),
        new RemoteManagementModule(),
    ];

    public static IReadOnlyList<Assembly> ConfigurationAssemblies { get; } =
        All.Select(m => m.Assembly).Distinct().ToList();
}
