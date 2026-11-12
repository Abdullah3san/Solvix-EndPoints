namespace Solvix.Modules.Tenancy.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Tenancy.Application.Management;
using Solvix.Modules.Tenancy.Application.Registration;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/tenant")]
public sealed class TenantController(ITenantRegistrationService registration, ITenantService tenants) : ApiControllerBase
{
    /// <summary>Self-service registration of a new workspace and its owner.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<RegisterTenantResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegisterTenantResponse>> Register([FromBody] RegisterTenantRequest request, CancellationToken cancellationToken)
    {
        var result = await registration.RegisterAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpGet("slug-availability")]
    [AllowAnonymous]
    [ProducesResponseType<SlugAvailabilityResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SlugAvailabilityResponse>> SlugAvailability([FromQuery] string slug, CancellationToken cancellationToken) =>
        Ok(await registration.CheckSlugAsync(slug ?? string.Empty, cancellationToken));

    /// <summary>The tenant resolved from the caller's token.</summary>
    [HttpGet]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantDto>> GetCurrent(CancellationToken cancellationToken) =>
        Ok(await tenants.GetCurrentAsync(cancellationToken));

    [HttpGet("overview")]
    [ProducesResponseType<TenantOverviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantOverviewDto>> Overview(CancellationToken cancellationToken) =>
        Ok(await tenants.GetOverviewAsync(cancellationToken));

    [HttpPut]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantDto>> Update([FromBody] UpdateTenantRequest request, CancellationToken cancellationToken) =>
        Ok(await tenants.UpdateCurrentAsync(request, cancellationToken));
}
