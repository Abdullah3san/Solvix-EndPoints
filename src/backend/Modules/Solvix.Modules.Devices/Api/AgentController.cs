namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Agent;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Security;
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
    public const string EnrollmentPollRateLimitPolicy = "agent-enrollment-poll";

    /// <summary>
    /// Enrolls a device. With a short-lived enrollment token and a CSR it returns the device certificate (200).
    /// With a deployment profile code (a generic installer) the request is held for administrator approval (202):
    /// the agent then polls <c>enroll/poll</c>.
    /// </summary>
    [HttpPost("enroll")]
    [AllowAnonymous]
    [EnableRateLimiting(EnrollmentRateLimitPolicy)]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType<EnrollmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<EnrollmentPendingResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Enroll(
        [FromBody] EnrollmentRequest request,
        [FromServices] IAgentEnrollmentService enrollment,
        [FromServices] IPendingEnrollmentIntake intake,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (DeploymentTokens.IsProfileCode(request.EnrollmentToken))
        {
            var pending = await intake.SubmitAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return StatusCode(StatusCodes.Status202Accepted, pending);
        }

        return Ok(await enrollment.EnrollAsync(request, cancellationToken));
    }

    /// <summary>A waiting agent asks whether an administrator approved it; the certificate is returned once approved.</summary>
    [HttpPost("enroll/poll")]
    [AllowAnonymous]
    [EnableRateLimiting(EnrollmentPollRateLimitPolicy)]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType<EnrollmentPollResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EnrollmentPollResponse>> EnrollPoll(
        [FromBody] EnrollmentPollRequest request,
        [FromServices] IPendingEnrollmentIntake intake,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await intake.PollAsync(request, cancellationToken));
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
