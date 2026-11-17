namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Web;

/// <summary>
/// Deployment profiles: the generic agent installers an administrator downloads (e.g. one per store). Devices that
/// install one wait in the approval queue (see <see cref="PendingEnrollmentsController"/>).
/// </summary>
[ApiVersion(1.0)]
public sealed class DeploymentProfilesController(IDeploymentProfileService service, IAgentPackageService packages) : ApiControllerBase
{
    private const string ExecutableContentType = "application/vnd.microsoft.portable-executable";

    [HttpGet]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<IReadOnlyList<DeploymentProfileDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpPost]
    [RequirePermission(Permissions.Devices.Enroll)]
    [ProducesResponseType<DeploymentProfileDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeploymentProfileDto>> Create([FromBody] SaveDeploymentProfileRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<DeploymentProfileDto>> Update(Guid id, [FromBody] SaveDeploymentProfileRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/enable")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<DeploymentProfileDto>> Enable(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetEnabledAsync(id, true, cancellationToken));

    [HttpPost("{id:guid}/disable")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<DeploymentProfileDto>> Disable(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.SetEnabledAsync(id, false, cancellationToken));

    /// <summary>Replaces the enrollment code. Installers downloaded earlier stop being accepted.</summary>
    [HttpPost("{id:guid}/rotate-code")]
    [RequirePermission(Permissions.Devices.Enroll)]
    public async Task<ActionResult<DeploymentProfileDto>> RotateCode(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RotateCodeAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Devices.Enroll)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Downloads the single-file installer: the agent exe with this profile's enrollment code inside. A plain GET,
    /// so the browser downloads it like any file (no script, no pasted command).
    /// </summary>
    [HttpGet("{id:guid}/installer")]
    [RequirePermission(Permissions.Devices.Enroll)]
    [Produces(ExecutableContentType)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Installer(Guid id, CancellationToken cancellationToken)
    {
        var package = await packages.BuildInstallerAsync(id, cancellationToken);
        Response.RegisterForDisposeAsync(package);
        Response.Headers.CacheControl = "no-store";
        return File(package.OpenRead(), ExecutableContentType, package.FileName);
    }
}
