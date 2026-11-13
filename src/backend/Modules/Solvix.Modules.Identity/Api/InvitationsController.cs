namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Modules.Identity.Application.Invitations;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class InvitationsController(IInvitationService invitations) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<PagedResult<InvitationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<InvitationDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await invitations.ListAsync(new PageRequest(page, pageSize), cancellationToken));

    /// <summary>Invites a person by e-mail with the given roles. The link is sent by e-mail and never returned here.</summary>
    [HttpPost]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<InvitationDto>> Create([FromBody] CreateInvitationRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await invitations.CreateAsync(request, cancellationToken));

    [HttpPost("{id:guid}/resend")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InvitationDto>> Resend(Guid id, CancellationToken cancellationToken) =>
        Ok(await invitations.ResendAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await invitations.RevokeAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Shows the invitee what they are about to accept. The token from the e-mail is the credential.</summary>
    [HttpPost("/api/v{version:apiVersion}/auth/invitations/preview")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Credentials)]
    [ProducesResponseType<InvitationPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InvitationPreviewDto>> Preview([FromBody] InvitationTokenRequest request, CancellationToken cancellationToken) =>
        Ok(await invitations.PreviewAsync(request, cancellationToken));

    [HttpPost("/api/v{version:apiVersion}/auth/invitations/accept")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Credentials)]
    [ProducesResponseType<InvitationAcceptedDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InvitationAcceptedDto>> Accept([FromBody] AcceptInvitationRequest request, CancellationToken cancellationToken) =>
        Ok(await invitations.AcceptAsync(request, cancellationToken));
}
