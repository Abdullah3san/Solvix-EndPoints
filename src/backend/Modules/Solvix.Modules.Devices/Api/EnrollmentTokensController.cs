namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Short-lived enrollment tokens (admins only). The token value is returned exactly once.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class EnrollmentTokensController(IEnrollmentTokenService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<EnrollmentTokenDto>>> List([FromQuery] EnrollmentTokenListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpPost]
    [ProducesResponseType<CreatedEnrollmentTokenDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreatedEnrollmentTokenDto>> Create([FromBody] CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>
    /// Downloads the agent package for a token the caller has just created: the unmodified agent exe plus an
    /// enrollment.json holding that token, so the administrator never pastes a command line.
    /// </summary>
    [HttpPost("package")]
    [Produces("application/zip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Package([FromBody] AgentPackageRequest request, [FromServices] IAgentPackageService packages, CancellationToken cancellationToken)
    {
        var package = await packages.BuildAsync(request, cancellationToken);
        Response.Headers.CacheControl = "no-store"; // the zip contains the token
        Response.RegisterForDisposeAsync(package);
        return File(package.OpenRead(), "application/zip", package.FileName);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<EnrollmentTokenDto>> Revoke(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(id, cancellationToken));
}
