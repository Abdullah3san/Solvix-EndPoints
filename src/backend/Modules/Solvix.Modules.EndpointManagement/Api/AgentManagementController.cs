namespace Solvix.Modules.EndpointManagement.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.EndpointManagement.Application.Agent;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Application.Patches;
using Solvix.SharedKernel.Web;

/// <summary>Patch and configuration endpoints for the Solvix agent (device credential required).</summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent")]
[Authorize(Policy = Policies.Agent)]
public sealed class AgentManagementController(
    IAgentHeartbeatService devices,
    IPatchInventoryIngestion inventory,
    IAgentPatchingService patching,
    IConfigurationEngine configuration) : ControllerBase
{
    /// <summary>Uploads a Windows Update Agent scan: missing and installed updates, history, service and restart state.</summary>
    [HttpPost("patches/inventory")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Inventory([FromBody] PatchInventoryReport report, CancellationToken cancellationToken)
    {
        var device = await devices.RequireCallerAsync(cancellationToken);
        await inventory.IngestAsync(device.Id, report, cancellationToken);
        return NoContent();
    }

    /// <summary>Reports progress of a patch job and renews its lease.</summary>
    [HttpPost("jobs/{jobId:guid}/progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Progress(Guid jobId, [FromBody] PatchJobProgress progress, CancellationToken cancellationToken)
    {
        var device = await devices.RequireCallerAsync(cancellationToken);
        await patching.ReportProgressAsync(device.Id, jobId, progress, cancellationToken);
        return NoContent();
    }

    /// <summary>Reports the final result of a patch job attempt.</summary>
    [HttpPost("jobs/{jobId:guid}/result")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Result(Guid jobId, [FromBody] PatchJobResult result, CancellationToken cancellationToken)
    {
        var device = await devices.RequireCallerAsync(cancellationToken);
        await patching.CompleteJobAsync(device.Id, jobId, result, cancellationToken);
        return NoContent();
    }

    [HttpPost("restarts/{restartId:guid}/ack")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> AcknowledgeRestart(Guid restartId, [FromBody] RestartAck ack, CancellationToken cancellationToken)
    {
        var device = await devices.RequireCallerAsync(cancellationToken);
        await patching.AcknowledgeRestartAsync(device.Id, restartId, ack, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The device's desired configuration. Send the last revision in
    /// <c>If-None-Match</c> to get 304 when nothing changed.
    /// </summary>
    [HttpGet("configuration")]
    [ProducesResponseType<ConfigurationDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<ConfigurationDocument>> Configuration(CancellationToken cancellationToken)
    {
        var device = await devices.RequireCallerAsync(cancellationToken);
        var document = await configuration.BuildDocumentAsync(device.Id, persist: true, cancellationToken);
        var etag = $"\"{document.Revision}\"";
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(document);
    }

    /// <summary>Per-setting evaluation results: compliance, drift, remediation, refusals and reverts.</summary>
    [HttpPost("configuration/report")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ConfigurationReport([FromBody] ConfigurationReport report, CancellationToken cancellationToken)
    {
        var device = await devices.RequireCallerAsync(cancellationToken);
        await configuration.IngestReportAsync(device.Id, report, cancellationToken);
        return NoContent();
    }
}
