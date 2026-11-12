namespace Solvix.Modules.Organization.Application.Provisioning;

using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Persistence;

internal sealed class OrganizationProvisioningService(ISolvixDbContext db) : IOrganizationProvisioningService
{
    public Task<CompanyProfile> CreateCompanyProfileAsync(Guid tenantId, string legalName, CancellationToken cancellationToken)
    {
        var profile = CompanyProfile.Create(tenantId, legalName);
        db.Set<CompanyProfile>().Add(profile);
        return Task.FromResult(profile);
    }

    public Task<Branch> CreateHeadquartersAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var branch = Branch.Create(tenantId, name, "HQ");
        branch.MarkAsHeadquarters();
        db.Set<Branch>().Add(branch);
        return Task.FromResult(branch);
    }
}
