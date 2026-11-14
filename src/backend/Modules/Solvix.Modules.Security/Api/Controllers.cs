namespace Solvix.Modules.Security.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Modules.Security.Application;
using Solvix.Modules.Security.Application.Alerts;
using Solvix.Modules.Security.Application.Audit;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Application.Endpoints;
using Solvix.Modules.Security.Application.Findings;
using Solvix.Modules.Security.Application.Ingest;
using Solvix.Modules.Security.Application.Policies;
using Solvix.Modules.Security.Application.Remediation;
using Solvix.Modules.Security.Application.Reports;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>
/// Base for the management API. Security data is sensitive, so unlike the default <c>TenantMember</c> policy, every
/// action here requires at least <see cref="Policies.SecurityRead"/>; write actions say so explicitly.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.SecurityRead)]
[Produces("application/json")]
[ApiVersion(1.0)]
public abstract class SecurityControllerBase : ControllerBase
{
}

[Route("api/v{version:apiVersion}/security/endpoints")]
public sealed class SecurityEndpointsController(IEndpointService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<EndpointSummaryDto>>> List([FromQuery] EndpointListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EndpointDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<EndpointHistoryPointDto>>> History(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.HistoryAsync(id, cancellationToken));

    [HttpGet("{id:guid}/software")]
    public async Task<ActionResult<PagedResult<SoftwareItemDto>>> Software(Guid id, [FromQuery] SoftwareListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.SoftwareAsync(id, query, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.SecurityManage)]
    [ProducesResponseType<EnrollmentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<EnrollmentDto>> Enroll([FromBody] EnrollEndpointRequest request, CancellationToken cancellationToken)
    {
        var enrolled = await service.EnrollAsync(request, cancellationToken);
        // The response carries the one-time device key: it must never be cached by a browser or proxy.
        Response.Headers.CacheControl = "no-store";
        return CreatedAtAction(nameof(Get), new { id = enrolled.Endpoint.Id, version = "1" }, enrolled);
    }

    [HttpPost("{id:guid}/rotate-key")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<EnrollmentDto>> RotateKey(Guid id, CancellationToken cancellationToken)
    {
        var rotated = await service.RotateKeyAsync(id, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(rotated);
    }

    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<EndpointSummaryDto>> Revoke(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

[Route("api/v{version:apiVersion}/security/findings")]
public sealed class SecurityFindingsController(IFindingService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<FindingDto>>> List([FromQuery] FindingListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FindingDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost("{id:guid}/accept-risk")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<FindingDto>> AcceptRisk(Guid id, [FromBody] AcceptRiskRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AcceptRiskAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/revoke-acceptance")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<FindingDto>> RevokeAcceptance(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAcceptanceAsync(id, cancellationToken));
}

[Route("api/v{version:apiVersion}/security/alerts")]
public sealed class SecurityAlertsController(IAlertService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AlertDto>>> List([FromQuery] AlertListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<AlertDto>> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.AcknowledgeAsync(id, cancellationToken));

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<AlertDto>> Resolve(Guid id, [FromBody] NoteRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ResolveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/dismiss")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<AlertDto>> Dismiss(Guid id, [FromBody] NoteRequest request, CancellationToken cancellationToken) =>
        Ok(await service.DismissAsync(id, request, cancellationToken));
}

[Route("api/v{version:apiVersion}/security/events")]
public sealed class SecurityEventsController(IAlertService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<SecurityEventDto>>> List([FromQuery] EventListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.EventsAsync(query, cancellationToken));
}

[Route("api/v{version:apiVersion}/security/policies")]
public sealed class SecurityPoliciesController(IPolicyService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PolicyDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpGet("effective")]
    public async Task<ActionResult<EffectivePolicyDto>> Effective(CancellationToken cancellationToken) =>
        Ok(await service.GetEffectiveAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PolicyDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.SecurityManage)]
    [ProducesResponseType<PolicyDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PolicyDto>> Create([FromBody] SavePolicyRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPost("{id:guid}/revise")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<PolicyDto>> Revise(Guid id, CancellationToken cancellationToken)
    {
        var created = await service.ReviseAsync(id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<PolicyDto>> Update(Guid id, [FromBody] SavePolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateDraftAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<PolicyDto>> Submit(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SubmitAsync(id, cancellationToken));

    [HttpPost("{id:guid}/withdraw")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<PolicyDto>> Withdraw(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.WithdrawAsync(id, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.SecurityApprove)]
    public async Task<ActionResult<PolicyDto>> Approve(Guid id, [FromBody] NoteRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.SecurityApprove)]
    public async Task<ActionResult<PolicyDto>> Reject(Guid id, [FromBody] NoteRequest request, CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteDraftAsync(id, cancellationToken);
        return NoContent();
    }
}

[Route("api/v{version:apiVersion}/security/advisories")]
public sealed class SecurityAdvisoriesController(IAdvisoryService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdvisoryDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.SecurityManage)]
    [ProducesResponseType<AdvisoryDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AdvisoryDto>> Create([FromBody] SaveAdvisoryRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<AdvisoryDto>> Update(Guid id, [FromBody] SaveAdvisoryRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/enable")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<AdvisoryDto>> Enable(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetEnabledAsync(id, true, cancellationToken));

    [HttpPost("{id:guid}/disable")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<AdvisoryDto>> Disable(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetEnabledAsync(id, false, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

[Route("api/v{version:apiVersion}/security/remediation")]
public sealed class SecurityRemediationController(IRemediationService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<RemediationDto>>> List([FromQuery] RemediationListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RemediationDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.SecurityManage)]
    [ProducesResponseType<RemediationDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RemediationDto>> Create([FromBody] CreateRemediationRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.SecurityApprove)]
    public async Task<ActionResult<RemediationDto>> Approve(Guid id, [FromBody] NoteRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.SecurityApprove)]
    public async Task<ActionResult<RemediationDto>> Reject(Guid id, [FromBody] NoteRequest request, CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Policies.SecurityManage)]
    public async Task<ActionResult<RemediationDto>> Cancel(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(id, cancellationToken));
}

[Route("api/v{version:apiVersion}/security/audit")]
public sealed class SecurityAuditController(IAuditService service) : SecurityControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditEntryDto>>> List([FromQuery] AuditListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));
}

[Route("api/v{version:apiVersion}/security")]
public sealed class SecurityReportsController(IReportService service) : SecurityControllerBase
{
    /// <summary>Fleet summary: scores, severities, top issues, trend and what is waiting for approval.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<SecuritySummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await service.SummaryAsync(cancellationToken));

    /// <summary>Every rule, the severity rubric, the remediation catalogue, the scoring method and the product scope statement.</summary>
    [HttpGet("rules")]
    public ActionResult<RulesCatalogDto> Rules() => Ok(service.Rules());

    [HttpGet("reports/devices.csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> DevicesCsv(CancellationToken cancellationToken)
    {
        var export = await service.DevicesCsvAsync(cancellationToken);
        return File(export.Content, "text/csv; charset=utf-8", export.FileName);
    }

    [HttpGet("reports/findings.csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> FindingsCsv([FromQuery] bool includeResolved, CancellationToken cancellationToken)
    {
        var export = await service.FindingsCsvAsync(includeResolved, cancellationToken);
        return File(export.Content, "text/csv; charset=utf-8", export.FileName);
    }
}

/// <summary>
/// The agent-facing API. Authenticated by the per-device key (not a user JWT), rate limited, size limited, and able to reach
/// only the three operations a device legitimately needs.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[AllowAnonymous]
[Produces("application/json")]
[EnableRateLimiting(RateLimitPolicies.DeviceIngest)]
[Route("api/v{version:apiVersion}/security/ingest")]
public sealed class SecurityIngestController(IDeviceGateway gateway) : ControllerBase
{
    private const long MaxReportBytes = 4_000_000;

    [HttpPost("posture")]
    [RequestSizeLimit(MaxReportBytes)]
    public async Task<ActionResult<ReportReceiptDto>> Report([FromBody] PostureReport report, CancellationToken cancellationToken) =>
        Ok(await gateway.RunAsync(KeyFromRequest(), call => call.Ingest.ReportAsync(call.Endpoint, report, cancellationToken), cancellationToken));

    [HttpGet("commands")]
    public async Task<ActionResult<IReadOnlyList<AgentCommandDto>>> Commands(CancellationToken cancellationToken) =>
        Ok(await gateway.RunAsync(KeyFromRequest(), call => call.Ingest.GetCommandsAsync(call.Endpoint, cancellationToken), cancellationToken));

    [HttpPost("commands/{id:guid}/result")]
    [RequestSizeLimit(16_000)]
    public async Task<IActionResult> Result(Guid id, [FromBody] CommandResultRequest result, CancellationToken cancellationToken)
    {
        await gateway.RunAsync(
            KeyFromRequest(),
            async call =>
            {
                await call.Ingest.SubmitResultAsync(call.Endpoint, id, result, cancellationToken);
                return true;
            },
            cancellationToken);
        return NoContent();
    }

    private string? KeyFromRequest() => Request.Headers.TryGetValue(DeviceKey.HeaderName, out var values) ? values.ToString() : null;
}

public static class RateLimitPolicies
{
    public const string DeviceIngest = "device-ingest";
}
