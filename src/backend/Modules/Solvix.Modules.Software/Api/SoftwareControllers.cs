namespace Solvix.Modules.Software.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Agent.Contracts;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Deployments;
using Solvix.Modules.Software.Domain;
using Solvix.Modules.Software.Inventory;
using Solvix.Modules.Software.Packages;
using Solvix.Modules.Software.Policies;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Software inventory reported by the agents, and restriction-rule compliance.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/software")]
public sealed class SoftwareInventoryController(ISoftwareInventoryQueries inventory, ISoftwarePolicyService policies, IDeploymentService deployments) : ApiControllerBase
{
    [HttpGet("inventory/catalog")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<PagedResult<SoftwareCatalogEntryDto>>> Catalog([FromQuery] CatalogQuery query, CancellationToken ct) => Ok(await inventory.CatalogAsync(query, ct));

    [HttpGet("inventory/installations")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<IReadOnlyList<SoftwareInstallationDto>>> Installations([FromQuery] string name, [FromQuery] string? version, CancellationToken ct) =>
        Ok(await inventory.InstallationsAsync(name, version, ct));

    [HttpGet("devices/{deviceId:guid}/inventory")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<IReadOnlyList<DeviceSoftwareDto>>> DeviceInventory(Guid deviceId, CancellationToken ct) => Ok(await inventory.ForDeviceAsync(deviceId, ct));

    [HttpGet("devices/{deviceId:guid}/deployments")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<IReadOnlyList<DeploymentTargetDto>>> DeviceHistory(Guid deviceId, CancellationToken ct) => Ok(await deployments.DeviceHistoryAsync(deviceId, ct));

    [HttpGet("compliance")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<ComplianceReport>> Compliance([FromQuery] Guid? deviceId, CancellationToken ct) => Ok(await policies.EvaluateComplianceAsync(deviceId, ct));
}

/// <summary>The package repository: upload, validation results, configuration, integrity checks and version detection.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/software/packages")]
public sealed class SoftwarePackagesController(IPackageService packages, IDeploymentService deployments) : ApiControllerBase
{
    /// <summary>
    /// Uploads an installer as the raw request body (<c>application/octet-stream</c>). The file is validated,
    /// hashed and stored; it is never executed on the server.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.Software.PackagesManage)]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<PackageDto>> Upload([FromQuery] string fileName, CancellationToken ct)
    {
        // Checked here rather than with [Consumes], which would answer before authorization.
        if (!string.Equals(Request.ContentType?.Split(';')[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var package = await packages.UploadAsync(Request.Body, fileName, ct);
        return CreatedAtAction(nameof(Get), new { id = package.Id, version = "1" }, package);
    }

    [HttpGet]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<PagedResult<PackageDto>>> List([FromQuery] PackageListQuery query, CancellationToken ct) => Ok(await packages.ListAsync(query, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<PackageDto>> Get(Guid id, CancellationToken ct) => Ok(await packages.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Software.PackagesManage)]
    public async Task<ActionResult<PackageDto>> Configure(Guid id, [FromBody] ConfigurePackageRequest request, CancellationToken ct) => Ok(await packages.ConfigureAsync(id, request, ct));

    [HttpPost("{id:guid}/verify")]
    [RequirePermission(Permissions.Software.PackagesManage)]
    public async Task<ActionResult<PackageVerification>> Verify(Guid id, CancellationToken ct) => Ok(await packages.VerifyAsync(id, ct));

    [HttpPost("{id:guid}/retire")]
    [RequirePermission(Permissions.Software.PackagesManage)]
    public async Task<ActionResult<PackageDto>> Retire(Guid id, CancellationToken ct) => Ok(await packages.RetireAsync(id, ct));

    /// <summary>Runs the package's detection rule on one device (read-only signed command).</summary>
    [HttpPost("{id:guid}/detect/{deviceId:guid}")]
    [RequirePermission(Permissions.Software.Deploy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<DetectionRequestResult>> Detect(Guid id, Guid deviceId, CancellationToken ct) =>
        Accepted(await deployments.RequestDetectionAsync(id, deviceId, ct));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/software/detections")]
public sealed class SoftwareDetectionsController(IDeploymentService deployments) : ApiControllerBase
{
    [HttpGet("{executionId:guid}")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<DetectionResultDto>> Get(Guid executionId, CancellationToken ct) => Ok(await deployments.GetDetectionAsync(executionId, ct));
}

/// <summary>Deployments: review, request, approve, follow, cancel and report.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/software/deployments")]
public sealed class SoftwareDeploymentsController(IDeploymentService deployments) : ApiControllerBase
{
    /// <summary>"Review deployment": resolves targets, conflicts, sensitivity and required approvals without storing anything.</summary>
    [HttpPost("preview")]
    [RequirePermission(Permissions.Software.Deploy)]
    public async Task<ActionResult<DeploymentPreviewDto>> Preview([FromBody] CreateDeploymentRequest request, CancellationToken ct) => Ok(await deployments.PreviewAsync(request, ct));

    [HttpPost]
    [RequirePermission(Permissions.Software.Deploy)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeploymentDetailDto>> Create([FromBody] CreateDeploymentRequest request, CancellationToken ct)
    {
        var created = await deployments.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Deployment.Id, version = "1" }, created);
    }

    [HttpGet]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<PagedResult<DeploymentDto>>> List([FromQuery] DeploymentListQuery query, CancellationToken ct) => Ok(await deployments.ListAsync(query, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<DeploymentDetailDto>> Get(Guid id, CancellationToken ct) => Ok(await deployments.GetAsync(id, ct));

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(Permissions.Software.Approve)]
    public async Task<ActionResult<DeploymentDetailDto>> Approve(Guid id, [FromBody] DeploymentDecisionRequest? request, CancellationToken ct) =>
        Ok(await deployments.ApproveAsync(id, request?.Comment, ct));

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(Permissions.Software.Approve)]
    public async Task<ActionResult<DeploymentDetailDto>> Reject(Guid id, [FromBody] DeploymentDecisionRequest? request, CancellationToken ct) =>
        Ok(await deployments.RejectAsync(id, request?.Comment, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Software.Deploy)]
    public async Task<ActionResult<DeploymentDetailDto>> Cancel(Guid id, CancellationToken ct) => Ok(await deployments.CancelAsync(id, ct));

    /// <summary>Creates a new deployment (needing approval again) for the devices that failed.</summary>
    [HttpPost("{id:guid}/redeploy-failed")]
    [RequirePermission(Permissions.Software.Deploy)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeploymentDetailDto>> RedeployFailed(Guid id, CancellationToken ct)
    {
        var created = await deployments.RedeployFailedAsync(id, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Deployment.Id, version = "1" }, created);
    }

    [HttpGet("{id:guid}/report")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<DeploymentReport>> Report(Guid id, CancellationToken ct) => Ok(await deployments.ReportAsync(id, ct));

    [HttpGet("{id:guid}/report.csv")]
    [RequirePermission(Permissions.Software.View)]
    [Produces("text/csv")]
    public async Task<IActionResult> ReportCsv(Guid id, CancellationToken ct)
    {
        var report = await deployments.ReportAsync(id, ct);
        return File(Encoding.UTF8.GetBytes(DeploymentService.ToCsv(report)), "text/csv; charset=utf-8", $"deployment-{id:N}.csv");
    }
}

/// <summary>Deployment groups, the deployment policy and software restriction rules.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/software")]
public sealed class SoftwarePoliciesController(ISoftwarePolicyService policies) : ApiControllerBase
{
    [HttpGet("deployment-groups")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<IReadOnlyList<DeploymentGroupDto>>> ListGroups(CancellationToken ct) => Ok(await policies.ListGroupsAsync(ct));

    [HttpPost("deployment-groups")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    public async Task<ActionResult<DeploymentGroupDto>> CreateGroup([FromBody] SaveDeploymentGroupRequest request, CancellationToken ct) => Ok(await policies.SaveGroupAsync(null, request, ct));

    [HttpPut("deployment-groups/{id:guid}")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    public async Task<ActionResult<DeploymentGroupDto>> UpdateGroup(Guid id, [FromBody] SaveDeploymentGroupRequest request, CancellationToken ct) => Ok(await policies.SaveGroupAsync(id, request, ct));

    [HttpDelete("deployment-groups/{id:guid}")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken ct)
    {
        await policies.DeleteGroupAsync(id, ct);
        return NoContent();
    }

    [HttpGet("policy")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<DeploymentPolicyDto>> GetPolicy(CancellationToken ct) => Ok(await policies.GetPolicyAsync(ct));

    [HttpPut("policy")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    public async Task<ActionResult<DeploymentPolicyDto>> UpdatePolicy([FromBody] UpdateDeploymentPolicyRequest request, CancellationToken ct) => Ok(await policies.UpdatePolicyAsync(request, ct));

    [HttpGet("restriction-rules")]
    [RequirePermission(Permissions.Software.View)]
    public async Task<ActionResult<IReadOnlyList<RestrictionRuleDto>>> ListRules(CancellationToken ct) => Ok(await policies.ListRulesAsync(ct));

    [HttpPost("restriction-rules")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    public async Task<ActionResult<RestrictionRuleDto>> CreateRule([FromBody] SaveRestrictionRuleRequest request, CancellationToken ct) => Ok(await policies.SaveRuleAsync(null, request, ct));

    [HttpPut("restriction-rules/{id:guid}")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    public async Task<ActionResult<RestrictionRuleDto>> UpdateRule(Guid id, [FromBody] SaveRestrictionRuleRequest request, CancellationToken ct) => Ok(await policies.SaveRuleAsync(id, request, ct));

    [HttpDelete("restriction-rules/{id:guid}")]
    [RequirePermission(Permissions.Software.PoliciesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        await policies.DeleteRuleAsync(id, ct);
        return NoContent();
    }
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/software/audit")]
public sealed class SoftwareAuditController(ISoftwareAuditQueries audit) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Audit.View)]
    public async Task<ActionResult<PagedResult<SoftwareAuditEventDto>>> List([FromQuery] SoftwareAuditQuery query, CancellationToken ct) => Ok(await audit.ListAsync(query, ct));

    [HttpGet("verify")]
    [RequirePermission(Permissions.Audit.View)]
    public async Task<ActionResult<SoftwareAuditVerification>> Verify(CancellationToken ct) => Ok(await audit.VerifyAsync(ct));
}

/// <summary>Package download for the Windows agent (mutual TLS device certificate; resumable with Range).</summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/agent/software")]
public sealed class AgentSoftwareController(IAgentPackageService packages) : ControllerBase
{
    [HttpGet("executions/{executionId:guid}/package")]
    [DeviceAuthenticated]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    public async Task<IActionResult> Package(Guid executionId, CancellationToken ct)
    {
        var download = await packages.AuthorizeDownloadAsync(executionId, ct);
        Response.Headers["X-Content-SHA256"] = download.Sha256;
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(download.Path, "application/octet-stream", enableRangeProcessing: true);
    }
}
