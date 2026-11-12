namespace Solvix.Modules.Organization.Application.Provisioning;

using Solvix.Modules.Organization.Domain;

/// <summary>Creates the initial organisation records for a freshly registered tenant.</summary>
public interface IOrganizationProvisioningService
{
    Task<CompanyProfile> CreateCompanyProfileAsync(Guid tenantId, string legalName, CancellationToken cancellationToken);

    Task<Branch> CreateHeadquartersAsync(Guid tenantId, string name, CancellationToken cancellationToken);
}
