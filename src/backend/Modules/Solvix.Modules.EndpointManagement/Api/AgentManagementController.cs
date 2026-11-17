namespace Solvix.Modules.EndpointManagement.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.EndpointManagement.Application.Agent;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Application.Patches;
using Solvix.SharedKernel.Authorization;

/// <summary>
/// Patch and configuration endpoints for the Solvix agent, authenticated with the device certificate
/// (mutual TLS). The agent only reports what it observes; changes reach the device as signed commands.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent/endpoint-management")]
public sealed class AgentManagementController(
    ICurrentDevice device,
    IPatchInventoryIngestion inventory,
    IAgentPatchingService patching,
    IConfigurationEngine configuration) : ControllerBase
{
    /// <summary>Polled by the agent: restart detection, configuration revision and whether to rescan updates.</summary>
    [HttpPost("work")]
    [DeviceAuthenticated]
    [ProducesResponseType<AgentWorkResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentWorkResponse>> Work([FromBody] AgentWorkRequest request, CancellationToken cancellationToken) =>
        Ok(await patching.GetWorkAsync(device.DeviceId, request, cancellationToken));

    /// <summary>Uploads a Windows Update Agent scan: missing and installed updates, history, service and restart state.</summary>
    [HttpPost("patches/inventory")]
    [DeviceAuthenticated]
    [RequestSizeLimit(16 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Inventory([FromBody] PatchInventoryReport report, CancellationToken cancellationToken)
    {
        await inventory.IngestAsync(device.DeviceId, report, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The device's desired configuration. Send the last revision in
    /// <c>If-None-Match</c> to get 304 when nothing changed.
    /// </summary>
    [HttpGet("configuration")]
    [DeviceAuthenticated]
    [ProducesResponseType<ConfigurationDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<ConfigurationDocument>> Configuration(CancellationToken cancellationToken)
    {
        var document = await configuration.BuildDocumentAsync(device.DeviceId, persist: true, cancellationToken);
        var etag = $"\"{document.Revision}\"";
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(document);
    }

    /// <summary>Per-setting evaluation results of the agent's (read-only) check: compliance and drift.</summary>
    [HttpPost("configuration/report")]
    [DeviceAuthenticated]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ConfigurationReport([FromBody] ConfigurationReport report, CancellationToken cancellationToken)
    {
        await configuration.IngestReportAsync(device.DeviceId, report, cancellationToken);
        return NoContent();
    }
}
