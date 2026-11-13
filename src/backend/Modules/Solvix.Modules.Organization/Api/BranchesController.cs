namespace Solvix.Modules.Organization.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class BranchesController(IBranchService service) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Organization.View)]
    [ProducesResponseType<PagedResult<BranchDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BranchDto>>> List([FromQuery] BranchListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Organization.View)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BranchDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [RequirePermission(Permissions.Organization.Manage)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BranchDto>> Create([FromBody] CreateBranchRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Organization.Manage)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> Update(Guid id, [FromBody] UpdateBranchRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [RequirePermission(Permissions.Organization.Manage)]
    public async Task<ActionResult<BranchDto>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetActiveAsync(id, true, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [RequirePermission(Permissions.Organization.Manage)]
    public async Task<ActionResult<BranchDto>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetActiveAsync(id, false, cancellationToken));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Organization.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
