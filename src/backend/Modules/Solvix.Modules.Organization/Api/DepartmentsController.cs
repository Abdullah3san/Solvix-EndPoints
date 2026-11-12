namespace Solvix.Modules.Organization.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class DepartmentsController(IDepartmentService service) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<DepartmentDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DepartmentDto>>> List([FromQuery] DepartmentListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("tree/{branchId:guid}")]
    [ProducesResponseType<IReadOnlyList<DepartmentTreeNode>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DepartmentTreeNode>>> Tree(Guid branchId, CancellationToken cancellationToken) =>
        Ok(await service.TreeAsync(branchId, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DepartmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DepartmentDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<DepartmentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DepartmentDto>> Create([FromBody] CreateDepartmentRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<DepartmentDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DepartmentDto>> Update(Guid id, [FromBody] UpdateDepartmentRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DepartmentDto>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetActiveAsync(id, true, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DepartmentDto>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetActiveAsync(id, false, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
