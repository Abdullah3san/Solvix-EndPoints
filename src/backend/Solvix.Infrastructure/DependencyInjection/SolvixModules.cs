namespace Solvix.Infrastructure.DependencyInjection;

using System.Reflection;
using Solvix.Modules.Identity;
using Solvix.Modules.Mdm;
using Solvix.Modules.Organization;
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
        new MdmModule(),
    ];

    public static IReadOnlyList<Assembly> ConfigurationAssemblies { get; } =
        All.Select(m => m.Assembly).Distinct().ToList();
}
