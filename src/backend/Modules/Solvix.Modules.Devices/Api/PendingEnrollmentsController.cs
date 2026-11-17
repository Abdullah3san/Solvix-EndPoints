namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>The approval queue: computers that installed a generic agent installer and wait for an administrator.</summary>
[ApiVersion(1.0)]
public sealed class PendingEnrollmentsController(IPendingEnrollmentService service) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<PagedResult<PendingEnrollmentDto>>> List([FromQuery] PendingEnrollmentListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    /// <summary>Number of open requests (for the badge in the navigation).</summary>
    [HttpGet("count")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<PendingEnrollmentCountDto>> Count(CancellationToken cancellationToken) =>
        Ok(await service.CountAsync(cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<PendingEnrollmentDto>> Approve(Guid id, [FromBody] DecideEnrollmentRequest? request, CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(id, request?.Note, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<PendingEnrollmentDto>> Reject(Guid id, [FromBody] DecideEnrollmentRequest? request, CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(id, request?.Note, cancellationToken));

    [HttpPost("bulk/approve")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<BulkDecisionResultDto>> ApproveMany([FromBody] BulkDecideEnrollmentsRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ApproveManyAsync(request.Ids, request.Note, cancellationToken));

    [HttpPost("bulk/reject")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<BulkDecisionResultDto>> RejectMany([FromBody] BulkDecideEnrollmentsRequest request, CancellationToken cancellationToken) =>
        Ok(await service.RejectManyAsync(request.Ids, request.Note, cancellationToken));
}
