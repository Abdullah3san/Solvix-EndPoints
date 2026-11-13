namespace Solvix.Modules.EndpointManagement.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class ConfigurationPoliciesController(IConfigurationPolicyService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConfigurationPolicySummaryDto>>> List([FromQuery] bool includeArchived, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(includeArchived, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConfigurationPolicyDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    /// <summary>Creates a policy with a first draft version.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<ConfigurationPolicyDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ConfigurationPolicyDto>> Create([FromBody] CreateConfigurationPolicyRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<ConfigurationPolicyDto>> Update(Guid id, [FromBody] UpdateConfigurationPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    /// <summary>Stops delivering the policy (assignments with RemovalBehavior=Revert restore previous values).</summary>
    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<ConfigurationPolicyDto>> Archive(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.ArchiveAsync(id, cancellationToken));

    [HttpGet("{id:guid}/versions/{number:int}")]
    public async Task<ActionResult<PolicyVersionDto>> GetVersion(Guid id, int number, CancellationToken cancellationToken) =>
        Ok(await service.GetVersionAsync(id, number, cancellationToken));

    [HttpPost("{id:guid}/versions")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<PolicyVersionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PolicyVersionDto>> CreateVersion(Guid id, [FromBody] CreatePolicyVersionRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateVersionAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/versions/{number:int}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyVersionDto>> UpdateVersion(Guid id, int number, [FromBody] UpdatePolicyVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateDraftAsync(id, number, request, cancellationToken));

    [HttpPost("{id:guid}/versions/{number:int}/submit")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyVersionDto>> Submit(Guid id, int number, CancellationToken cancellationToken) =>
        Ok(await service.SubmitAsync(id, number, cancellationToken));

    /// <summary>Approves a version. Elevated risk needs a second admin; destructive needs an Owner and acknowledgeDestructive=true.</summary>
    [HttpPost("{id:guid}/versions/{number:int}/approve")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyVersionDto>> Approve(Guid id, int number, [FromBody] ReviewPolicyVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(id, number, request, cancellationToken));

    [HttpPost("{id:guid}/versions/{number:int}/reject")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyVersionDto>> Reject(Guid id, int number, [FromBody] ReviewPolicyVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(id, number, request, cancellationToken));

    /// <summary>Activates an approved version, or re-activates a superseded one (fast rollback).</summary>
    [HttpPost("{id:guid}/versions/{number:int}/publish")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyVersionDto>> Publish(Guid id, int number, CancellationToken cancellationToken) =>
        Ok(await service.PublishAsync(id, number, cancellationToken));

    /// <summary>Copies an older version into a new draft for review.</summary>
    [HttpPost("{id:guid}/versions/{number:int}/restore")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<PolicyVersionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PolicyVersionDto>> Restore(Guid id, int number, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.RestoreAsync(id, number, cancellationToken));

    [HttpGet("{id:guid}/assignments")]
    public async Task<ActionResult<IReadOnlyList<AssignmentDto>>> Assignments(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.ListAssignmentsAsync(id, cancellationToken));

    /// <summary>Assigns (or excludes) the policy to all devices, a branch, a department, a device group or a device.</summary>
    [HttpPost("{id:guid}/assignments")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<AssignmentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AssignmentDto>> Assign(Guid id, [FromBody] SaveAssignmentRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.AssignAsync(id, request, cancellationToken));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/configuration")]
public sealed class ConfigurationController(IConfigurationPolicyService policies, IConfigurationReportingService reporting) : ApiControllerBase
{
    [HttpPut("assignments/{assignmentId:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<AssignmentDto>> UpdateAssignment(Guid assignmentId, [FromBody] UpdateAssignmentRequest request, CancellationToken cancellationToken) =>
        Ok(await policies.UpdateAssignmentAsync(assignmentId, request, cancellationToken));

    /// <summary>Removes an assignment; devices revert or keep the settings according to its removal behaviour.</summary>
    [HttpDelete("assignments/{assignmentId:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveAssignment(Guid assignmentId, CancellationToken cancellationToken)
    {
        await policies.RemoveAssignmentAsync(assignmentId, cancellationToken);
        return NoContent();
    }

    /// <summary>Settings set differently by several policies on the same device.</summary>
    [HttpGet("conflicts")]
    public async Task<ActionResult<IReadOnlyList<DeviceConflictsDto>>> Conflicts([FromQuery] bool includeResolved, CancellationToken cancellationToken) =>
        Ok(await policies.ListConflictsAsync(includeResolved, cancellationToken));

    /// <summary>Effective configuration of one device with compliance state and conflicts.</summary>
    [HttpGet("devices/{deviceId:guid}")]
    public async Task<ActionResult<DeviceConfigurationDto>> Device(Guid deviceId, CancellationToken cancellationToken) =>
        Ok(await policies.GetDeviceConfigurationAsync(deviceId, cancellationToken));

    [HttpGet("compliance")]
    public async Task<ActionResult<ConfigurationComplianceDto>> Compliance(CancellationToken cancellationToken) =>
        Ok(await reporting.GetComplianceAsync(cancellationToken));

    /// <summary>History of detected drift and remediation.</summary>
    [HttpGet("drift")]
    public async Task<ActionResult<PagedResult<DriftEventDto>>> Drift([FromQuery] DriftQuery query, CancellationToken cancellationToken) =>
        Ok(await reporting.ListDriftAsync(query, cancellationToken));

    /// <summary>Built-in, reviewed configuration baselines.</summary>
    [HttpGet("baselines")]
    public ActionResult<IReadOnlyList<BaselineDto>> Baselines() => Ok(reporting.ListBaselines());

    [HttpPost("baselines/policies")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<ConfigurationPolicyDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ConfigurationPolicyDto>> CreateFromBaseline([FromBody] CreateFromBaselineRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await reporting.CreateFromBaselineAsync(request, cancellationToken));
}
