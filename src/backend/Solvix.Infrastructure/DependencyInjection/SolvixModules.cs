namespace Solvix.Infrastructure.DependencyInjection;

using System.Reflection;
using Solvix.Modules.Billing;
using Solvix.Modules.Devices;
using Solvix.Modules.Helpdesk;
using Solvix.Modules.Identity;
using Solvix.Modules.Organization;
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
        new BillingModule(),
        new HelpdeskModule(),
        new ReportingModule(),
    ];

    public static IReadOnlyList<Assembly> ConfigurationAssemblies { get; } =
        All.Select(m => m.Assembly).Distinct().ToList();
}
