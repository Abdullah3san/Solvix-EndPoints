namespace Solvix.Modules.Billing.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Payments;

/// <summary>
/// Payment provider notifications. Anonymous by necessity; authenticity is the HMAC signature over the raw body
/// (see <see cref="StripeSignature"/>). Point the provider at the API directly, not through the web proxy.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/billing/webhooks")]
public sealed class BillingWebhooksController(IPaymentWebhookProcessor processor, IPaymentProvider provider) : ControllerBase
{
    public const string RateLimitPolicy = "billing-webhook";

    private const int MaxBodyBytes = 256 * 1024;

    [HttpPost("{providerName}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(string providerName, CancellationToken cancellationToken)
    {
        if (!string.Equals(providerName, provider.Name, StringComparison.OrdinalIgnoreCase) || !provider.SupportsCheckout)
        {
            return NotFound();
        }

        // The signature covers the exact bytes received, so the body is read raw (never model-bound). The size is
        // bounded here as well as by [RequestSizeLimit] so that chunked uploads cannot exceed it either.
        if (Request.ContentLength > MaxBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        var payload = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var signature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            var result = await processor.HandleAsync(payload, signature, cancellationToken);
            return Ok(new { result = result.ToString().ToLowerInvariant() });
        }
        catch (WebhookSignatureException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid webhook", extensions: new Dictionary<string, object?> { ["code"] = "invalid_signature" });
        }
    }
}
