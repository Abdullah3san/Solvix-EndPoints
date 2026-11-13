namespace Solvix.Modules.Organization;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.CompanyProfiles;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.Modules.Organization.Application.Directory;
using Solvix.Modules.Organization.Application.Provisioning;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Modules;

public sealed class OrganizationModule : IModule
{
    public string Name => "Organization";

    public Assembly Assembly => typeof(OrganizationModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ICompanyProfileService, CompanyProfileService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<IOrganizationProvisioningService, OrganizationProvisioningService>();
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
