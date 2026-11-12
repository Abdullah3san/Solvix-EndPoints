namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Short-lived enrollment tokens (admins only). The token value is returned exactly once.</summary>
[ApiVersion(1.0)]
public sealed class EnrollmentTokensController(IEnrollmentTokenService service) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<PagedResult<EnrollmentTokenDto>>> List([FromQuery] EnrollmentTokenListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpPost]
    [RequirePermission(Permissions.Devices.Enroll)]
    [ProducesResponseType<CreatedEnrollmentTokenDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreatedEnrollmentTokenDto>> Create([FromBody] CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPost("{id:guid}/revoke")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<EnrollmentTokenDto>> Revoke(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(id, cancellationToken));
}
