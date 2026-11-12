namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Agent;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Monitoring;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Web;

/// <summary>
/// Endpoints called by the Windows agent. Communication is strictly
/// outbound from the device; responses carry data and schedules only.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent")]
[RecordAgentContact]
public sealed class AgentController(IAgentService agent) : ControllerBase
{
    public const string EnrollmentRateLimitPolicy = "agent-enrollment";

    /// <summary>Enrolls a device with a short-lived enrollment token and a CSR; returns its device certificate.</summary>
    [HttpPost("enroll")]
    [AllowAnonymous]
    [EnableRateLimiting(EnrollmentRateLimitPolicy)]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType<EnrollmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EnrollmentResponse>> Enroll(
        [FromBody] EnrollmentRequest request,
        [FromServices] IAgentEnrollmentService enrollment,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await enrollment.EnrollAsync(request, cancellationToken));
    }

    [HttpPost("heartbeat")]
    [DeviceAuthenticated]
    [HeartbeatEndpoint]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<HeartbeatResponse>> Heartbeat([FromBody] HeartbeatRequest request, CancellationToken cancellationToken) =>
        Ok(await agent.HeartbeatAsync(request, cancellationToken));

    [HttpPut("inventory")]
    [DeviceAuthenticated]
    [RequestSizeLimit(8 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Inventory([FromBody] InventoryReport report, CancellationToken cancellationToken)
    {
        await agent.SubmitInventoryAsync(report, cancellationToken);
        return NoContent();
    }

    [HttpPost("certificate")]
    [DeviceAuthenticated]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<CertificateRenewalResponse>> RenewCertificate([FromBody] CertificateRenewalRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await agent.RenewCertificateAsync(request, cancellationToken));
    }

    [HttpGet("updates")]
    [DeviceAuthenticated]
    public async Task<ActionResult<UpdateCheckResponse>> Updates([FromQuery] string? channel, [FromQuery] string? currentVersion, CancellationToken cancellationToken) =>
        Ok(await agent.CheckForUpdateAsync(channel, currentVersion, cancellationToken));
}
