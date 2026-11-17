namespace Solvix.Modules.Support.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Agent.Contracts;
using Solvix.Modules.Support.Application;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Tickets, for technicians in the web console. Any member may raise a ticket; handling them needs an administrator.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/tickets")]
public sealed class TicketsController(ITicketService service) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PagedResult<TicketSummaryDto>>> List([FromQuery] TicketListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDetailsDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<TicketDetailsDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TicketDetailsDto>> Create([FromBody] CreateTicketRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Summary.Id, version = "1" }, created);
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDetailsDto>> Update(Guid id, [FromBody] UpdateTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/comments")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDetailsDto>> AddComment(Guid id, [FromBody] AddTicketCommentRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AddCommentAsync(id, request, cancellationToken));
}

/// <summary>Live chat with the person using a device.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class ChatController(IChatService service) : ApiControllerBase
{
    [HttpGet("~/api/v{version:apiVersion}/chat/conversations")]
    public async Task<ActionResult<IReadOnlyList<ChatConversationDto>>> Conversations(CancellationToken cancellationToken) =>
        Ok(await service.ListConversationsAsync(cancellationToken));

    [HttpGet("~/api/v{version:apiVersion}/devices/{deviceId:guid}/chat")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> List(Guid deviceId, [FromQuery] long after, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(deviceId, after, cancellationToken));

    [HttpPost("~/api/v{version:apiVersion}/devices/{deviceId:guid}/chat")]
    [ProducesResponseType<ChatMessageDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ChatMessageDto>> Send(Guid deviceId, [FromBody] SendChatRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.SendAsync(deviceId, request, cancellationToken));
}

/// <summary>Called by the agent on behalf of the person using the computer.</summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent")]
[Authorize(Policy = Policies.Device)]
public sealed class AgentSupportController(IAgentSupportService service) : ControllerBase
{
    [HttpPost("tickets")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<AgentTicket>> SubmitTicket([FromBody] SubmitTicketRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.SubmitTicketAsync(request, cancellationToken));

    [HttpGet("tickets")]
    public async Task<ActionResult<AgentTicketList>> Tickets(CancellationToken cancellationToken) =>
        Ok(await service.ListTicketsAsync(cancellationToken));

    [HttpGet("chat")]
    public async Task<ActionResult<ChatMessageList>> PollChat([FromQuery] long after, [FromQuery] int wait, CancellationToken cancellationToken) =>
        Ok(await service.PollChatAsync(after, wait, cancellationToken));

    [HttpPost("chat")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<ChatMessage>> SendChat([FromBody] SendChatMessageRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.SendChatAsync(request, cancellationToken));
}
