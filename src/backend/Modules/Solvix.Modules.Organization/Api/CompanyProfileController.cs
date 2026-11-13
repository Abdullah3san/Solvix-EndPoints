namespace Solvix.Modules.Organization.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Organization.Application.CompanyProfiles;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/company-profile")]
public sealed class CompanyProfileController(ICompanyProfileService service) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Organization.View)]
    [ProducesResponseType<CompanyProfileDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CompanyProfileDto>> Get(CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(cancellationToken));

    [HttpPut]
    [RequirePermission(Permissions.Organization.Manage)]
    [ProducesResponseType<CompanyProfileDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CompanyProfileDto>> Update([FromBody] UpdateCompanyProfileRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(request, cancellationToken));
}
