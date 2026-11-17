namespace Solvix.Modules.Tenancy.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Tenancy.Application.Platform;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>
/// Provider console: manage the companies (workspaces) that subscribe to the
/// platform. Platform operators only; tenant owners and admins get 403.
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/platform")]
[Authorize(Policy = Policies.PlatformOperator)]
public sealed class PlatformController(IPlatformTenantService tenants) : ApiControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType<PlatformSummaryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PlatformSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await tenants.GetSummaryAsync(cancellationToken));

    [HttpGet("tenants")]
    [ProducesResponseType<PagedResult<PlatformTenantDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<PlatformTenantDto>>> List([FromQuery] PlatformTenantListQuery query, CancellationToken cancellationToken) =>
        Ok(await tenants.ListAsync(query, cancellationToken));

    /// <summary>Creates a workspace and its first owner account for a subscribed company.</summary>
    [HttpPost("tenants")]
    [ProducesResponseType<PlatformTenantDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PlatformTenantDto>> Create([FromBody] CreatePlatformTenantRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await tenants.CreateAsync(request, cancellationToken));

    /// <summary>Blocks every member and device of the workspace until it is reactivated. No data is deleted.</summary>
    [HttpPost("tenants/{id:guid}/suspend")]
    [ProducesResponseType<PlatformTenantDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PlatformTenantDto>> Suspend(Guid id, CancellationToken cancellationToken) =>
        Ok(await tenants.SuspendAsync(id, cancellationToken));

    [HttpPost("tenants/{id:guid}/reactivate")]
    [ProducesResponseType<PlatformTenantDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PlatformTenantDto>> Reactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(await tenants.ReactivateAsync(id, cancellationToken));
}
