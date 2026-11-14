namespace Solvix.Modules.Organization.Application.CompanyProfiles;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;

public interface ICompanyProfileService
{
    Task<CompanyProfileDto> GetAsync(CancellationToken cancellationToken);

    Task<CompanyProfileDto> UpdateAsync(UpdateCompanyProfileRequest request, CancellationToken cancellationToken);
}

internal sealed class CompanyProfileService(ISolvixDbContext db) : ICompanyProfileService
{
    public async Task<CompanyProfileDto> GetAsync(CancellationToken cancellationToken)
    {
        var profile = await db.Set<CompanyProfile>().AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Company profile for the current tenant was not found.");
        return CompanyProfileDto.From(profile);
    }

    public async Task<CompanyProfileDto> UpdateAsync(UpdateCompanyProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await db.Set<CompanyProfile>().SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Company profile for the current tenant was not found.");

        profile.Update(
            request.LegalName,
            request.TradeName,
            request.TaxId,
            request.RegistrationNumber,
            request.Industry,
            request.Website,
            request.ContactEmail,
            request.ContactPhone,
            string.IsNullOrWhiteSpace(request.TimeZone) ? profile.TimeZone : request.TimeZone,
            request.Address?.ToDomain() ?? Address.Empty);

        await db.SaveChangesAsync(cancellationToken);
        return CompanyProfileDto.From(profile);
    }
}
