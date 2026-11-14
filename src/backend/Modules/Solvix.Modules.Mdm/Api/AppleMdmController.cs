namespace Solvix.Modules.Mdm.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Mdm.Application.Apple;

/// <summary>
/// Device-facing endpoints of the Apple MDM protocol. Anonymous by design: the device is authenticated by the
/// <c>Mdm-Signature</c> of its identity certificate (check-in / server) or by a single-use secret (enroll).
/// Hidden from OpenAPI because administrators never call these.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiVersion(1.0)]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/v{version:apiVersion}/mdm/apple")]
public sealed class AppleMdmController(IAppleDeviceProtocolService protocol) : ControllerBase
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private const string PlistContentType = "application/x-apple-plist";

    /// <summary>Serves the enrollment profile for a single-use enrollment link.</summary>
    [HttpGet("enroll")]
    public async Task<IActionResult> Enroll([FromQuery] string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return NotFound();
        }

        var profile = await protocol.RedeemEnrollmentAsync(token, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return File(profile, "application/x-apple-aspen-config", "enroll.mobileconfig");
    }

    [HttpPut("{handle}/checkin")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> CheckIn(string handle, CancellationToken cancellationToken)
    {
        var (status, body) = await protocol.CheckInAsync(handle, await ReadBodyAsync(cancellationToken), Request.Headers["Mdm-Signature"].ToString(), cancellationToken);
        return ToResult(status, body);
    }

    [HttpPut("{handle}/server")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Server(string handle, CancellationToken cancellationToken)
    {
        var (status, body) = await protocol.ServerAsync(handle, await ReadBodyAsync(cancellationToken), Request.Headers["Mdm-Signature"].ToString(), cancellationToken);
        return ToResult(status, body);
    }

    private async Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private IActionResult ToResult(int status, byte[]? body)
    {
        if (body is { Length: > 0 })
        {
            return File(body, PlistContentType);
        }

        // MVC (ApiController client-error mapping) and the status-code pages middleware would both decorate an empty
        // 4xx with a problem-details body. MDM devices expect the bare status, so write it directly.
        if (HttpContext.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
        {
            statusCodePages.Enabled = false;
        }

        Response.StatusCode = status;
        Response.ContentLength = 0;
        return new EmptyResult();
    }
}
