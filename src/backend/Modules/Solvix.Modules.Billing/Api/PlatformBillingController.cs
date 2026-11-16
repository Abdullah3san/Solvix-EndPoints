namespace Solvix.Modules.Billing.Api;

using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Configuration;

/// <summary>
/// Platform-operator API (Enterprise terms, manual payments, suspension). Guarded by a shared secret in the
/// <c>X-Operator-Key</c> header, compared in constant time. When <c>Solvix:Billing:OperatorApiKey</c> is not
/// configured the endpoints do not exist (404). Expose them only on an internal network.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[AllowAnonymous]
[ServiceFilter(typeof(OperatorKeyFilter))]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
[Route("api/v{version:apiVersion}/platform/billing/tenants/{tenantId:guid}")]
public sealed class PlatformBillingController(IPlatformBillingService platform) : ControllerBase
{
    [HttpPut("subscription")]
    public async Task<ActionResult<SubscriptionDto>> Configure(Guid tenantId, [FromBody] OperatorConfigureSubscriptionRequest request, CancellationToken cancellationToken) =>
        Ok(await platform.ConfigureSubscriptionAsync(tenantId, request, cancellationToken));

    [HttpPost("invoices/{invoiceId:guid}/mark-paid")]
    public async Task<ActionResult<InvoiceDto>> MarkPaid(Guid tenantId, Guid invoiceId, [FromBody] OperatorMarkPaidRequest request, CancellationToken cancellationToken) =>
        Ok(await platform.MarkInvoicePaidAsync(tenantId, invoiceId, request, cancellationToken));

    [HttpPost("suspend")]
    public async Task<IActionResult> Suspend(Guid tenantId, [FromBody] OperatorSuspendRequest request, CancellationToken cancellationToken)
    {
        await platform.SuspendAsync(tenantId, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("reactivate")]
    public async Task<IActionResult> Reactivate(Guid tenantId, CancellationToken cancellationToken)
    {
        await platform.ReactivateAsync(tenantId, cancellationToken);
        return NoContent();
    }

    [HttpPut("tax-rate")]
    public async Task<IActionResult> SetTaxRate(Guid tenantId, [FromBody] OperatorTaxRateRequest request, CancellationToken cancellationToken)
    {
        await platform.SetTaxRateAsync(tenantId, request, cancellationToken);
        return NoContent();
    }
}

internal sealed class OperatorKeyFilter(IOptions<BillingOptions> options) : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Operator-Key";

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var configured = options.Value.OperatorApiKey;
        if (string.IsNullOrEmpty(configured))
        {
            context.Result = new NotFoundResult();
            return Task.CompletedTask;
        }

        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
        {
            context.Result = new UnauthorizedResult();
        }

        return Task.CompletedTask;
    }
}
