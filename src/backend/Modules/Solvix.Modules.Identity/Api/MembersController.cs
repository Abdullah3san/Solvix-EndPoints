namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Identity.Application.Members;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/tenant/members")]
public sealed class MembersController(IMemberService members) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<MemberDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MemberDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await members.ListAsync(new PageRequest(page, pageSize), cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<MemberDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MemberDto>> Add([FromBody] AddMemberRequest request, CancellationToken cancellationToken)
    {
        var created = await members.AddAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("{membershipId:guid}/role")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<MemberDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MemberDto>> ChangeRole(Guid membershipId, [FromBody] ChangeMemberRoleRequest request, CancellationToken cancellationToken) =>
        Ok(await members.ChangeRoleAsync(membershipId, request.Role, cancellationToken));

    [HttpDelete("{membershipId:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid membershipId, CancellationToken cancellationToken)
    {
        await members.RemoveAsync(membershipId, cancellationToken);
        return NoContent();
    }
}
