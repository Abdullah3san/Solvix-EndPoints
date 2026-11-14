namespace Solvix.Modules.Assistant.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Chat;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Knowledge;
using Solvix.Modules.Assistant.Remediation;
using Solvix.Modules.Assistant.Reports;
using Solvix.Modules.Assistant.Tickets;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/assistant")]
public sealed class AssistantController(IAssistantChatService chat) : ApiControllerBase
{
    [HttpGet("status")]
    public ActionResult<AssistantStatusDto> Status() => Ok(chat.GetStatus());

    /// <summary>Send a natural-language message. Tool calls run as the signed-in user; actions are only ever proposed.</summary>
    [HttpPost("chat")]
    [EnableRateLimiting(AssistantModule.ChatRateLimitPolicy)]
    [ProducesResponseType<ChatReplyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatReplyDto>> Send([FromBody] SendMessageRequest request, CancellationToken cancellationToken) =>
        Ok(await chat.SendAsync(request, cancellationToken));

    [HttpGet("conversations")]
    public async Task<ActionResult<PagedResult<ConversationDto>>> Conversations([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await chat.ListConversationsAsync(new PageRequest(page, pageSize), cancellationToken));

    [HttpGet("conversations/{id:guid}")]
    public async Task<ActionResult<ConversationDetailDto>> Conversation(Guid id, CancellationToken cancellationToken) =>
        Ok(await chat.GetConversationAsync(id, cancellationToken));

    [HttpDelete("conversations/{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        await chat.ArchiveConversationAsync(id, cancellationToken);
        return NoContent();
    }
}

/// <summary>
/// Human approval of assistant proposals. This is deliberately a REST endpoint for signed-in people only:
/// the model has no tool that can reach it.
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/assistant/proposals")]
public sealed class ProposalsController(IRemediationService proposals) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<ProposalDto>>> List([FromQuery] ProposalStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await proposals.ListAsync(status, new PageRequest(page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProposalDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await proposals.GetAsync(id, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<ProposalDto>> Approve(Guid id, [FromBody] ApproveProposalRequest request, CancellationToken cancellationToken) =>
        Ok(await proposals.ApproveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<ProposalDto>> Reject(Guid id, [FromBody] RejectProposalRequest request, CancellationToken cancellationToken) =>
        Ok(await proposals.RejectAsync(id, request, cancellationToken));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/assistant/tickets")]
public sealed class AssistantTicketsController(ITicketService tickets) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<TicketDto>>> List([FromQuery] TicketListQuery query, CancellationToken cancellationToken) =>
        Ok(await tickets.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TicketDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await tickets.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<TicketDto>> Create([FromBody] CreateTicketRequest request, CancellationToken cancellationToken)
    {
        var created = await tickets.CreateAsync(request, viaAssistant: false, null, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TicketDto>> SetStatus(Guid id, [FromBody] UpdateTicketStatusRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.SetStatusAsync(id, request.Status, cancellationToken));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/assistant/reports")]
public sealed class AssistantReportsController(IReportService reports) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<ReportSummaryDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await reports.ListAsync(new PageRequest(page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ReportDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await reports.GetAsync(id, cancellationToken));

    [HttpPost]
    [EnableRateLimiting(AssistantModule.ChatRateLimitPolicy)]
    public async Task<ActionResult<ReportDto>> Generate([FromBody] GenerateReportRequest request, CancellationToken cancellationToken)
    {
        var report = await reports.GenerateAsync(request, null, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, report);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await reports.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/assistant/knowledge")]
public sealed class KnowledgeController(IKnowledgeService knowledge) : ApiControllerBase
{
    private const long RequestCeiling = 12L * 1024 * 1024;

    [HttpGet("documents")]
    public async Task<ActionResult<PagedResult<KnowledgeDocumentDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await knowledge.ListAsync(new PageRequest(page, pageSize), cancellationToken));

    [HttpGet("documents/{id:guid}")]
    public async Task<ActionResult<KnowledgeDocumentDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await knowledge.GetAsync(id, cancellationToken));

    [HttpPost("documents")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RequestCeiling)]
    [RequestFormLimits(MultipartBodyLengthLimit = RequestCeiling)]
    public async Task<ActionResult<KnowledgeDocumentDto>> Upload(IFormFile file, [FromForm] string? title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = file.OpenReadStream();
        var document = await knowledge.UploadAsync(stream, file.FileName, title, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, document);
    }

    [HttpPost("documents/{id:guid}/reindex")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<KnowledgeDocumentDto>> Reindex(Guid id, CancellationToken cancellationToken) =>
        Ok(await knowledge.ReindexAsync(id, cancellationToken));

    [HttpDelete("documents/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await knowledge.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("search")]
    public async Task<ActionResult<IReadOnlyList<KnowledgeHit>>> Search([FromBody] KnowledgeSearchRequest request, CancellationToken cancellationToken) =>
        Ok(await knowledge.SearchAsync(request.Query, request.Limit, cancellationToken));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/assistant/audit")]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class AssistantAuditController(IAssistantAuditQuery audit) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditEntryDto>>> List([FromQuery] AuditQuery query, CancellationToken cancellationToken) =>
        Ok(await audit.ListAsync(query, cancellationToken));
}
