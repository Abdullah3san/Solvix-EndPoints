namespace Solvix.Infrastructure.DependencyInjection;

using System.Reflection;
using Solvix.Modules.Assets;
using Solvix.Modules.Devices;
using Solvix.Modules.Assistant;
using Solvix.Modules.Billing;
using Solvix.Modules.Helpdesk;
using Solvix.Modules.Identity;
using Solvix.Modules.Mdm;
using Solvix.Modules.Organization;
using Solvix.Modules.RemoteManagement;
using Solvix.Modules.Software;
using Solvix.Modules.Security;
using Solvix.Modules.Reporting;
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
        new SoftwareModule(),
        new AssistantModule(),
        new SecurityModule(),
        new MdmModule(),
        new BillingModule(),
        new HelpdeskModule(),
        new ReportingModule(),
    ];

    public static IReadOnlyList<Assembly> ConfigurationAssemblies { get; } =
        All.Select(m => m.Assembly).Distinct().ToList();
}
