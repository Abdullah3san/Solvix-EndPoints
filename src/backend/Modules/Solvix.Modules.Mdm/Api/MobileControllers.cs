namespace Solvix.Modules.Mdm.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Mdm.Application;
using Solvix.Modules.Mdm.Application.Apps;
using Solvix.Modules.Mdm.Application.Commands;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Application.Enrollment;
using Solvix.Modules.Mdm.Application.Enterprise;
using Solvix.Modules.Mdm.Application.Policies;
using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Android Enterprise binding. Apple needs no per-workspace binding in this phase.</summary>
[ApiVersion(1.0)]
public sealed class MobileEnterpriseController(IMobileEnterpriseService service) : ApiControllerBase
{
    [HttpGet("{platform}")]
    [ProducesResponseType<EnterpriseDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EnterpriseDto>> Get(MobilePlatform platform, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(platform, cancellationToken));

    /// <summary>Step 1: returns the Google URL where the administrator binds a Google account.</summary>
    [HttpPost("android/signup")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<SignupUrlDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SignupUrlDto>> StartAndroidSignup([FromBody] StartSignupRequest request, CancellationToken cancellationToken) =>
        Ok(await service.StartAndroidSignupAsync(request, cancellationToken));

    /// <summary>Step 2: completes the binding with the enterprise token Google returned to the callback URL.</summary>
    [HttpPost("android/complete")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<EnterpriseDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EnterpriseDto>> CompleteAndroidSignup([FromBody] CompleteSignupRequest request, CancellationToken cancellationToken) =>
        Ok(await service.CompleteAndroidSignupAsync(request, cancellationToken));
}

[ApiVersion(1.0)]
public sealed class MobileCapabilitiesController(IMobilePolicyService policies) : ApiControllerBase
{
    /// <summary>The platform capability matrix: what each platform/ownership combination supports, and why some things are not offered.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CapabilitiesDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CapabilitiesDto>> Get() => Ok(policies.Capabilities());
}

[ApiVersion(1.0)]
public sealed class MobileAppsController(IMobileAppService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AppDto>>> List(
        [FromQuery] MobilePlatform? platform, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(platform, search, page, pageSize, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<AppDto>> Create([FromBody] CreateAppRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<AppDto>> Update(Guid id, [FromBody] UpdateAppRequest request, CancellationToken cancellationToken) =>
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

[ApiVersion(1.0)]
public sealed class MobilePoliciesController(IMobilePolicyService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<PolicyDto>>> List([FromQuery] MobilePlatform? platform, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(platform, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PolicyDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyDto>> Create([FromBody] CreatePolicyRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyDto>> Update(Guid id, [FromBody] UpdatePolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/apps")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PolicyDto>> SetApps(Guid id, [FromBody] SetPolicyAppsRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SetAppsAsync(id, request, cancellationToken));

    /// <summary>Deploys the current version to the platform and tells devices to fetch it. The result lists settings that were not applied and why.</summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<PublishPolicyResultDto>> Publish(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.PublishAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

[ApiVersion(1.0)]
public sealed class MobileEnrollmentsController(IEnrollmentService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<EnrollmentDto>>> List([FromQuery] bool includeInactive = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await service.ListAsync(includeInactive, page, pageSize, cancellationToken));

    /// <summary>Issues an enrollment. The QR code / link is returned only in this response.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<EnrollmentCreatedDto>> Create([FromBody] CreateEnrollmentRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateAsync(request, cancellationToken));

    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<EnrollmentDto>> Revoke(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(id, cancellationToken));
}

[ApiVersion(1.0)]
public sealed class MobileDevicesController(IMobileDeviceService service, IDeviceSyncService sync, IDeviceCommandService commands) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<DeviceDto>>> List([FromQuery] DeviceListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeviceDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPut("{id:guid}/policy")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDto>> AssignPolicy(Guid id, [FromBody] AssignPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AssignPolicyAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/evaluate-compliance")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDetailDto>> EvaluateCompliance(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.EvaluateComplianceAsync(id, cancellationToken));

    /// <summary>
    /// Sends a command (lock, reboot, retire, wipe, ...). Retire and wipe are created pending and only run after a
    /// different administrator approves them under <c>/mobile-commands/{id}/approve</c>.
    /// </summary>
    [HttpPost("{id:guid}/commands")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<CommandDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CommandDto>> SendCommand(Guid id, [FromBody] CreateCommandRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await commands.CreateAsync(id, request, cancellationToken));

    /// <summary>Pulls fresh inventory from the platforms, reconciles commands and re-evaluates compliance for the workspace.</summary>
    [HttpPost("sync")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<SyncResultDto>> Sync(CancellationToken cancellationToken) =>
        Ok(await sync.SyncAsync(cancellationToken));
}

[ApiVersion(1.0)]
public sealed class MobileCommandsController(IDeviceCommandService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<CommandDto>>> List([FromQuery] CommandListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CommandDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    /// <summary>Approves a destructive command. The approver must differ from the requester.</summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CommandDto>> Approve(Guid id, [FromBody] CommandDecisionRequest? request, CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(id, request ?? new CommandDecisionRequest(null), cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CommandDto>> Reject(Guid id, [FromBody] CommandDecisionRequest? request, CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(id, request ?? new CommandDecisionRequest(null), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<CommandDto>> Cancel(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(id, cancellationToken));
}

[ApiVersion(1.0)]
public sealed class MobileComplianceController(IComplianceService service) : ApiControllerBase
{
    /// <summary>Fleet compliance: totals, by platform and ownership, most common violations, and pending approvals.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ComplianceSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await service.GetSummaryAsync(cancellationToken));
}
