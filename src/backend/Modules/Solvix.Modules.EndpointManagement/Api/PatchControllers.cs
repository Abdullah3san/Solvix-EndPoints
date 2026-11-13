namespace Solvix.Modules.EndpointManagement.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.EndpointManagement.Application.Deployments;
using Solvix.Modules.EndpointManagement.Application.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Application.Patches;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class PatchesController(IPatchCatalogService catalog, IPatchComplianceService compliance) : ApiControllerBase
{
    /// <summary>Patch inventory across the tenant with per-patch device counts.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<PatchDto>>> List([FromQuery] PatchListQuery query, CancellationToken cancellationToken) =>
        Ok(await catalog.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PatchDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await catalog.GetAsync(id, cancellationToken));

    [HttpPost("approve")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<IReadOnlyList<PatchDto>>> Approve([FromBody] PatchDecisionRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.ApproveAsync(request, cancellationToken));

    [HttpPost("decline")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<IReadOnlyList<PatchDto>>> Decline([FromBody] PatchDecisionRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.DeclineAsync(request, cancellationToken));

    [HttpPost("reset-approval")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<IReadOnlyList<PatchDto>>> ResetApproval([FromBody] PatchDecisionRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.ResetAsync(request, cancellationToken));

    /// <summary>Compliance against approved patches, with branch/department/group breakdowns.</summary>
    [HttpGet("compliance")]
    public async Task<ActionResult<PatchComplianceReportDto>> Compliance([FromQuery] ComplianceQuery query, CancellationToken cancellationToken) =>
        Ok(await compliance.GetReportAsync(query, cancellationToken));

    [HttpGet("compliance/report.csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> ComplianceCsv([FromQuery] ComplianceQuery query, CancellationToken cancellationToken)
    {
        var csv = await compliance.ExportCsvAsync(query, cancellationToken);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"patch-compliance-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    /// <summary>Devices whose last installation of a patch failed (WUA history and deployment results).</summary>
    [HttpGet("failures")]
    public async Task<ActionResult<PagedResult<PatchFailureDto>>> Failures([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        Ok(await compliance.ListFailuresAsync(page, pageSize, cancellationToken));

    /// <summary>Windows Update status and patch list of one device.</summary>
    [HttpGet("devices/{deviceId:guid}")]
    public async Task<ActionResult<DeviceUpdateStatusDto>> DeviceStatus(Guid deviceId, CancellationToken cancellationToken) =>
        Ok(await compliance.GetDeviceStatusAsync(deviceId, cancellationToken));

    [HttpPost("devices/{deviceId:guid}/scan")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RequestScan(Guid deviceId, CancellationToken cancellationToken)
    {
        await compliance.RequestScanAsync(deviceId, cancellationToken);
        return Accepted();
    }
}

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class PatchAutoApprovalRulesController(IPatchCatalogService catalog) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AutoApprovalRuleDto>>> List(CancellationToken cancellationToken) =>
        Ok(await catalog.ListRulesAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType<AutoApprovalRuleDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AutoApprovalRuleDto>> Create([FromBody] SaveAutoApprovalRuleRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await catalog.CreateRuleAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AutoApprovalRuleDto>> Update(Guid id, [FromBody] SaveAutoApprovalRuleRequest request, CancellationToken cancellationToken) =>
        Ok(await catalog.UpdateRuleAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await catalog.DeleteRuleAsync(id, cancellationToken);
        return NoContent();
    }
}

[ApiVersion(1.0)]
public sealed class PatchDeploymentsController(IPatchDeploymentService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<DeploymentDto>>> List([FromQuery] DeploymentListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeploymentDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    /// <summary>Per-device progress of a deployment.</summary>
    [HttpGet("{id:guid}/devices")]
    public async Task<ActionResult<PagedResult<DeploymentJobDto>>> Devices(Guid id, [FromQuery] PatchJobStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default) =>
        Ok(await service.ListJobsAsync(id, status, page, pageSize, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<DeploymentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeploymentDto>> Create([FromBody] SaveDeploymentRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Update(Guid id, [FromBody] SaveDeploymentRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Authorises the deployment to change devices from its scheduled start.</summary>
    [HttpPost("{id:guid}/schedule")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Schedule(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.ScheduleAsync(id, cancellationToken));

    [HttpPost("{id:guid}/unschedule")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Unschedule(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.UnscheduleAsync(id, cancellationToken));

    [HttpPost("{id:guid}/promote")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Promote(Guid id, [FromBody] DeploymentActionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.PromoteAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/pause")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Pause(Guid id, [FromBody] DeploymentActionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.PauseAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Resume(Guid id, [FromBody] DeploymentActionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ResumeAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeploymentDto>> Cancel(Guid id, [FromBody] DeploymentActionRequest request, CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(id, request, cancellationToken));
}

[ApiVersion(1.0)]
public sealed class RestartRequestsController(IRestartService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<DeviceRestartDto>>> List([FromQuery] RestartListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<DeviceRestartDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeviceRestartDto>> Create([FromBody] CreateRestartRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateAsync(request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceRestartDto>> Cancel(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(id, cancellationToken));
}

[ApiVersion(1.0)]
public sealed class MaintenanceWindowsController(IMaintenanceWindowService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MaintenanceWindowDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenanceWindowDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<MaintenanceWindowDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MaintenanceWindowDto>> Create([FromBody] SaveMaintenanceWindowRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<MaintenanceWindowDto>> Update(Guid id, [FromBody] SaveMaintenanceWindowRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
