namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Staged;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Computers the organisation expects to manage that have not installed the agent yet. Writes: admins.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/staged-computers")]
public sealed class StagedComputersController(IStagedComputerService service) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<StagedComputerDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<StagedComputerDto>>> List([FromQuery] StagedComputerListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<StagedComputerSummaryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StagedComputerSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await service.GetSummaryAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<StagedComputerDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StagedComputerDto>> Create([FromBody] CreateStagedComputerRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateAsync(request, cancellationToken));

    [HttpPost("import")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<ImportStagedComputersResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImportStagedComputersResult>> Import([FromBody] ImportStagedComputersRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ImportAsync(request, cancellationToken));

    /// <summary>Removes entries from the list. Enrolled devices are not affected.</summary>
    [HttpPost("remove")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove([FromBody] RemoveStagedComputersRequest request, CancellationToken cancellationToken)
    {
        await service.RemoveAsync(request.Ids, cancellationToken);
        return NoContent();
    }
}
