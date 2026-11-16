namespace Solvix.Modules.Helpdesk.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Configuration;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>
/// Tickets. Any workspace member can raise tickets and follow their own; technicians and administrators see and
/// work all of them. Visibility is enforced in the service (another member's ticket answers 404), not just here.
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/helpdesk/tickets")]
public sealed class TicketsController(ITicketService tickets, IOptions<HelpdeskOptions> options) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<TicketSummaryDto>>> List([FromQuery] TicketListQuery query, CancellationToken cancellationToken) =>
        Ok(await tickets.ListAsync(query, cancellationToken));

    [HttpGet("stats")]
    public async Task<ActionResult<TicketStatsDto>> Stats(CancellationToken cancellationToken) =>
        Ok(await tickets.StatsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TicketDetailsDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await tickets.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<TicketDetailsDto>> Create([FromBody] CreateTicketRequest request, CancellationToken cancellationToken)
    {
        var created = await tickets.CreateAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TicketDetailsDto>> Update(Guid id, [FromBody] UpdateTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/status")]
    public async Task<ActionResult<TicketDetailsDto>> ChangeStatus(Guid id, [FromBody] ChangeStatusRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.ChangeStatusAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<TicketDetailsDto>> Assign(Guid id, [FromBody] AssignTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.AssignAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/category")]
    public async Task<ActionResult<TicketDetailsDto>> Categorize(Guid id, [FromBody] CategorizeTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.CategorizeAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/priority")]
    public async Task<ActionResult<TicketDetailsDto>> ChangePriority(Guid id, [FromBody] ChangePriorityRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.ChangePriorityAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/device")]
    public async Task<ActionResult<TicketDetailsDto>> LinkDevice(Guid id, [FromBody] LinkDeviceRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.LinkDeviceAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/comments")]
    public async Task<ActionResult<TicketDetailsDto>> AddComment(Guid id, [FromBody] AddCommentRequest request, CancellationToken cancellationToken) =>
        Ok(await tickets.AddCommentAsync(id, request, cancellationToken));

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<TicketEventDto>>> History(Guid id, CancellationToken cancellationToken) =>
        Ok(await tickets.HistoryAsync(id, cancellationToken));

    [HttpPost("{id:guid}/summary")]
    public async Task<ActionResult<TicketDetailsDto>> Summarize(Guid id, CancellationToken cancellationToken) =>
        Ok(await tickets.GenerateSummaryAsync(id, cancellationToken));

    [HttpPost("{id:guid}/attachments")]
    [RequestSizeLimit(26 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<AttachmentDto>> Upload(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var max = options.Value.Attachments.MaxBytes;
        if (file.Length > max)
        {
            throw new Solvix.SharedKernel.Exceptions.ValidationException("file", $"Files can be at most {max / (1024 * 1024)} MiB.");
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        var created = await tickets.AddAttachmentAsync(id, file.FileName, buffer.ToArray(), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>Always a download (never rendered in our origin), with sniffing and scripting disabled.</summary>
    [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid attachmentId, CancellationToken cancellationToken)
    {
        var (meta, data) = await tickets.GetAttachmentAsync(id, attachmentId, cancellationToken);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        Response.Headers.CacheControl = "private, no-store";
        return File(data, meta.ContentType, meta.FileName);
    }
}

/// <summary>Categories, technicians, SLA policies, escalation rules and inbound e-mail channels.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/helpdesk")]
public sealed class HelpdeskAdminController(ICatalogService catalog) : ApiControllerBase
{
    [HttpGet("categories")]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories([FromQuery] bool includeInactive, CancellationToken cancellationToken) =>
        Ok(await catalog.ListCategoriesAsync(includeInactive, cancellationToken));

    [HttpPost("categories")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CategoryDto>> CreateCategory([FromBody] SaveCategoryRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await catalog.SaveCategoryAsync(null, request, cancellationToken));

    [HttpPut("categories/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CategoryDto>> UpdateCategory(Guid id, [FromBody] SaveCategoryRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.SaveCategoryAsync(id, request, cancellationToken));

    [HttpGet("technicians")]
    public async Task<ActionResult<IReadOnlyList<TechnicianDto>>> Technicians(CancellationToken cancellationToken) =>
        Ok(await catalog.ListTechniciansAsync(cancellationToken));

    [HttpPost("technicians")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TechnicianDto>> CreateTechnician([FromBody] CreateTechnicianRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await catalog.CreateTechnicianAsync(request, cancellationToken));

    [HttpPut("technicians/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<TechnicianDto>> UpdateTechnician(Guid id, [FromBody] UpdateTechnicianRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.UpdateTechnicianAsync(id, request, cancellationToken));

    [HttpGet("sla-policies")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDto>>> SlaPolicies(CancellationToken cancellationToken) =>
        Ok(await catalog.ListSlaPoliciesAsync(cancellationToken));

    [HttpPost("sla-policies")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<SlaPolicyDto>> CreateSlaPolicy([FromBody] SaveSlaPolicyRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await catalog.SaveSlaPolicyAsync(null, request, cancellationToken));

    [HttpPut("sla-policies/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<SlaPolicyDto>> UpdateSlaPolicy(Guid id, [FromBody] SaveSlaPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.SaveSlaPolicyAsync(id, request, cancellationToken));

    [HttpDelete("sla-policies/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<IActionResult> DeleteSlaPolicy(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteSlaPolicyAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("escalation-rules")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<IReadOnlyList<EscalationRuleDto>>> EscalationRules(CancellationToken cancellationToken) =>
        Ok(await catalog.ListEscalationRulesAsync(cancellationToken));

    [HttpPost("escalation-rules")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<EscalationRuleDto>> CreateEscalationRule([FromBody] SaveEscalationRuleRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await catalog.SaveEscalationRuleAsync(null, request, cancellationToken));

    [HttpPut("escalation-rules/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<EscalationRuleDto>> UpdateEscalationRule(Guid id, [FromBody] SaveEscalationRuleRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.SaveEscalationRuleAsync(id, request, cancellationToken));

    [HttpDelete("escalation-rules/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<IActionResult> DeleteEscalationRule(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteEscalationRuleAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("email-channels")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<IReadOnlyList<InboundChannelDto>>> Channels(CancellationToken cancellationToken) =>
        Ok(await catalog.ListChannelsAsync(cancellationToken));

    /// <summary>The returned token is shown once; only its hash is stored.</summary>
    [HttpPost("email-channels")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CreatedInboundChannelDto>> CreateChannel([FromBody] SaveInboundChannelRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, await catalog.CreateChannelAsync(request, cancellationToken));
    }

    [HttpPut("email-channels/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<InboundChannelDto>> UpdateChannel(Guid id, [FromBody] SaveInboundChannelRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.UpdateChannelAsync(id, request, cancellationToken));

    [HttpPost("email-channels/{id:guid}/rotate")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CreatedInboundChannelDto>> RotateChannel(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await catalog.RotateChannelSecretAsync(id, cancellationToken));
    }

    [HttpDelete("email-channels/{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<IActionResult> DeleteChannel(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteChannelAsync(id, cancellationToken);
        return NoContent();
    }
}

/// <summary>
/// Mail provider webhook. Anonymous; authenticated by the channel secret in <c>X-Channel-Token</c> (a header, never
/// the URL, so it cannot end up in access logs). Point the provider (or a small relay such as an Azure Function /
/// Power Automate flow) directly at the API.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/helpdesk/inbound-email")]
public sealed class InboundEmailController(IEmailIntakeService intake) : ControllerBase
{
    public const string RateLimitPolicy = "helpdesk-inbound-email";

    public const string TokenHeader = "X-Channel-Token";

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult<InboundEmailResult>> Receive([FromBody] InboundEmailRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await intake.ProcessAsync(Request.Headers[TokenHeader].ToString(), request, cancellationToken));
    }
}
