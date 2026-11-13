namespace Solvix.Modules.RemoteManagement.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Authorization;
using Solvix.Modules.RemoteManagement.CommandBroker;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>
/// Predefined system operations on one device. Each route is one allow-listed command type;
/// the body only carries confirmation and the few validated parameters an operation accepts.
/// The endpoint permission is the minimum; the broker re-checks it together with device scope,
/// time window and approval policy. Responses are 202: the command runs asynchronously.
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/remote/devices/{deviceId:guid}/operations")]
public sealed class RemoteOperationsController(ICommandBroker broker) : ApiControllerBase
{
    [HttpPost("system-info")]
    [RequirePermission(Permissions.Remote.View)]
    public Task<ActionResult<CommandExecutionDto>> SystemInfo(Guid deviceId, CancellationToken ct) => Run(deviceId, CommandTypes.SystemInfo, null, ct);

    [HttpPost("processes")]
    [RequirePermission(Permissions.Remote.View)]
    public Task<ActionResult<CommandExecutionDto>> Processes(Guid deviceId, CancellationToken ct) => Run(deviceId, CommandTypes.Processes, null, ct);

    [HttpPost("services")]
    [RequirePermission(Permissions.Remote.View)]
    public Task<ActionResult<CommandExecutionDto>> Services(Guid deviceId, CancellationToken ct) => Run(deviceId, CommandTypes.Services, null, ct);

    [HttpPost("uptime")]
    [RequirePermission(Permissions.Remote.View)]
    public Task<ActionResult<CommandExecutionDto>> Uptime(Guid deviceId, CancellationToken ct) => Run(deviceId, CommandTypes.Uptime, null, ct);

    [HttpPost("logged-in-users")]
    [RequirePermission(Permissions.Remote.View)]
    public Task<ActionResult<CommandExecutionDto>> LoggedInUsers(Guid deviceId, CancellationToken ct) => Run(deviceId, CommandTypes.LoggedInUsers, null, ct);

    [HttpPost("lock")]
    [RequirePermission(Permissions.Remote.Lock)]
    public Task<ActionResult<CommandExecutionDto>> Lock(Guid deviceId, [FromBody] OperationRequest? request, CancellationToken ct) => Run(deviceId, CommandTypes.Lock, request, ct);

    [HttpPost("logoff")]
    [RequirePermission(Permissions.Remote.Restart)]
    public Task<ActionResult<CommandExecutionDto>> Logoff(Guid deviceId, [FromBody] OperationRequest request, CancellationToken ct) => Run(deviceId, CommandTypes.Logoff, request, ct);

    [HttpPost("restart")]
    [RequirePermission(Permissions.Remote.Restart)]
    public Task<ActionResult<CommandExecutionDto>> Restart(Guid deviceId, [FromBody] OperationRequest request, CancellationToken ct) => Run(deviceId, CommandTypes.Restart, request, ct);

    [HttpPost("shutdown")]
    [RequirePermission(Permissions.Remote.Shutdown)]
    public Task<ActionResult<CommandExecutionDto>> Shutdown(Guid deviceId, [FromBody] OperationRequest request, CancellationToken ct) => Run(deviceId, CommandTypes.Shutdown, request, ct);

    private async Task<ActionResult<CommandExecutionDto>> Run(Guid deviceId, string type, OperationRequest? request, CancellationToken ct)
    {
        var execution = await broker.RequestAsync(deviceId, type, request ?? new OperationRequest(), ct);
        return AcceptedAtAction(nameof(RemoteCommandsController.Get), "RemoteCommands", new { id = execution.Id, version = "1" }, execution);
    }
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/remote/commands")]
public sealed class RemoteCommandsController(ICommandBroker broker) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Remote.View)]
    public async Task<ActionResult<PagedResult<CommandExecutionDto>>> List([FromQuery] CommandListQuery query, CancellationToken ct) =>
        Ok(await broker.ListAsync(query, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Remote.View)]
    public async Task<ActionResult<CommandExecutionDto>> Get(Guid id, CancellationToken ct) => Ok(await broker.GetAsync(id, ct));

    /// <summary>Cancels a command; requires the permission the command itself needs.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Remote.View)]
    public async Task<ActionResult<CommandExecutionDto>> Cancel(Guid id, CancellationToken ct) => Ok(await broker.CancelAsync(id, ct));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/remote/approvals")]
public sealed class RemoteApprovalsController(ICommandBroker broker) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Remote.ApprovalsManage)]
    public async Task<ActionResult<PagedResult<ApprovalDto>>> List([FromQuery] ApprovalStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(await broker.ListApprovalsAsync(status, page, pageSize, ct));

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(Permissions.Remote.ApprovalsManage)]
    public async Task<ActionResult<ApprovalDto>> Approve(Guid id, [FromBody] ApprovalDecisionRequest? request, CancellationToken ct) =>
        Ok(await broker.ApproveAsync(id, request?.Comment, ct));

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(Permissions.Remote.ApprovalsManage)]
    public async Task<ActionResult<ApprovalDto>> Reject(Guid id, [FromBody] ApprovalDecisionRequest? request, CancellationToken ct) =>
        Ok(await broker.RejectAsync(id, request?.Comment, ct));
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/remote")]
public sealed class RemoteAccessPolicyController(IRemoteAccessAdministration administration) : ApiControllerBase
{
    [HttpGet("policy")]
    [RequirePermission(Permissions.Remote.View)]
    public async Task<ActionResult<RemoteAccessPolicyDto>> GetPolicy(CancellationToken ct) => Ok(await administration.GetPolicyAsync(ct));

    [HttpPut("policy")]
    [RequirePermission(Permissions.Security.Manage)]
    public async Task<ActionResult<RemoteAccessPolicyDto>> UpdatePolicy([FromBody] UpdateRemoteAccessPolicyRequest request, CancellationToken ct) =>
        Ok(await administration.UpdatePolicyAsync(request, ct));

    [HttpGet("scopes")]
    [RequirePermission(Permissions.Security.View)]
    public async Task<ActionResult<IReadOnlyList<RemoteAccessScopeDto>>> ListScopes(CancellationToken ct) => Ok(await administration.ListScopesAsync(ct));

    [HttpPut("scopes/{userId:guid}")]
    [RequirePermission(Permissions.Security.Manage)]
    public async Task<ActionResult<RemoteAccessScopeDto>> SetScope(Guid userId, [FromBody] UpdateRemoteAccessScopeRequest request, CancellationToken ct) =>
        Ok(await administration.SetScopeAsync(userId, request, ct));

    [HttpDelete("scopes/{userId:guid}")]
    [RequirePermission(Permissions.Security.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveScope(Guid userId, CancellationToken ct)
    {
        await administration.RemoveScopeAsync(userId, ct);
        return NoContent();
    }
}

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/remote/audit")]
public sealed class RemoteAuditController(IRemoteAuditQueries audit) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Audit.View)]
    public async Task<ActionResult<PagedResult<RemoteAuditEventDto>>> List([FromQuery] RemoteAuditQuery query, CancellationToken ct) => Ok(await audit.ListAsync(query, ct));

    [HttpGet("verify")]
    [RequirePermission(Permissions.Audit.View)]
    public async Task<ActionResult<AuditChainVerification>> Verify(CancellationToken ct) => Ok(await audit.VerifyAsync(ct));
}

/// <summary>Command channel for the Windows agent (mutual TLS device certificate only).</summary>
[ApiController]
[ApiVersion(1.0)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/agent")]
public sealed class AgentCommandsController(IAgentCommandService commands) : ControllerBase
{
    [HttpGet("commands")]
    [DeviceAuthenticated]
    public async Task<ActionResult<AgentCommandBatch>> Fetch([FromQuery] int waitSeconds = 0, CancellationToken ct = default) =>
        Ok(await commands.FetchAsync(Math.Clamp(waitSeconds, 0, 30), ct));

    [HttpPost("commands/{executionId:guid}/ack")]
    [DeviceAuthenticated]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Acknowledge(Guid executionId, [FromBody] CommandAcknowledgement acknowledgement, CancellationToken ct)
    {
        await commands.AcknowledgeAsync(executionId, acknowledgement, ct);
        return NoContent();
    }

    [HttpPost("commands/{executionId:guid}/result")]
    [DeviceAuthenticated]
    [RequestSizeLimit(1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Result(Guid executionId, [FromBody] CommandResultReport report, CancellationToken ct)
    {
        await commands.ReportResultAsync(executionId, report, ct);
        return NoContent();
    }

    [HttpGet("command-keys")]
    [DeviceAuthenticated]
    public ActionResult<CommandKeySet> Keys() => Ok(commands.GetKeySet());
}
