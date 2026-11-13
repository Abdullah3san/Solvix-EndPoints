namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.SharedKernel.Web;

/// <summary>
/// Endpoints called by the Solvix Windows agent. All but enrolment require the
/// device credential (<c>Authorization: Device sxd_...</c>); people's JWTs are
/// rejected by the <see cref="Policies.Agent"/> policy and agents are rejected by
/// every user policy.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent")]
[Authorize(Policy = Policies.Agent)]
public sealed class AgentController(IAgentEnrollmentService enrollment, IAgentHeartbeatService heartbeat) : ControllerBase
{
    /// <summary>Enrols a device with an enrolment token and returns its credential (shown once).</summary>
    [HttpPost("enroll")]
    [AllowAnonymous]
    [ProducesResponseType<EnrollDeviceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EnrollDeviceResponse>> Enroll([FromBody] EnrollDeviceRequest request, CancellationToken cancellationToken) =>
        Ok(await enrollment.EnrollAsync(request, cancellationToken));

    /// <summary>Reports liveness and basic inventory; returns the work waiting for the device.</summary>
    [HttpPost("heartbeat")]
    [ProducesResponseType<AgentWorkResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentWorkResponse>> Heartbeat([FromBody] AgentHeartbeatRequest request, CancellationToken cancellationToken) =>
        Ok(await heartbeat.HeartbeatAsync(request, cancellationToken));
}
