namespace Solvix.Modules.Billing.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Billing.Application;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>
/// Subscription, usage and billing data of the caller's workspace. Plan and usage are visible to every member
/// (the UI needs the limits); anything that is money or personal billing data requires the workspace Owner.
/// The workspace always comes from the access token; no endpoint accepts a tenant id.
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/billing")]
public sealed class BillingController(IBillingService billing) : ApiControllerBase
{
    [HttpGet("plans")]
    [ProducesResponseType<IReadOnlyList<PlanDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PlanDto>>> Plans(CancellationToken cancellationToken) =>
        Ok(await billing.GetPlansAsync(cancellationToken));

    [HttpGet("subscription")]
    public async Task<ActionResult<SubscriptionDto>> Subscription(CancellationToken cancellationToken) =>
        Ok(await billing.GetSubscriptionAsync(cancellationToken));

    [HttpGet("usage")]
    public async Task<ActionResult<UsageDto>> Usage(CancellationToken cancellationToken) =>
        Ok(await billing.GetUsageAsync(cancellationToken));

    [HttpPost("subscription/change")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<ChangePlanResult>> ChangePlan([FromBody] ChangePlanRequest request, CancellationToken cancellationToken) =>
        Ok(await billing.ChangePlanAsync(request, cancellationToken));

    [HttpPost("subscription/cancel")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<SubscriptionDto>> Cancel(CancellationToken cancellationToken) =>
        Ok(await billing.CancelAsync(cancellationToken));

    [HttpPost("subscription/resume")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<SubscriptionDto>> Resume(CancellationToken cancellationToken) =>
        Ok(await billing.ResumeAsync(cancellationToken));

    [HttpGet("invoices")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<PagedResult<InvoiceDto>>> Invoices([FromQuery] InvoiceListQuery query, CancellationToken cancellationToken) =>
        Ok(await billing.ListInvoicesAsync(query, cancellationToken));

    [HttpGet("invoices/{id:guid}")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<InvoiceDetailsDto>> Invoice(Guid id, CancellationToken cancellationToken) =>
        Ok(await billing.GetInvoiceAsync(id, cancellationToken));

    /// <summary>Printable invoice. Served with a locked-down CSP and never cached.</summary>
    [HttpGet("invoices/{id:guid}/html")]
    [Authorize(Policy = Policies.TenantOwner)]
    [Produces("text/html")]
    public async Task<IActionResult> InvoiceHtml(Guid id, CancellationToken cancellationToken)
    {
        var html = await billing.RenderInvoiceHtmlAsync(id, cancellationToken);
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("invoices/{id:guid}/checkout")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<CheckoutDto>> Checkout(Guid id, CancellationToken cancellationToken) =>
        Ok(await billing.CreateCheckoutAsync(id, cancellationToken));

    [HttpGet("profile")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<BillingProfileDto>> Profile(CancellationToken cancellationToken)
    {
        var profile = await billing.GetProfileAsync(cancellationToken);
        return profile is null ? NoContent() : Ok(profile);
    }

    [HttpPut("profile")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<BillingProfileDto>> UpdateProfile([FromBody] UpdateBillingProfileRequest request, CancellationToken cancellationToken) =>
        Ok(await billing.UpdateProfileAsync(request, cancellationToken));

    [HttpGet("history")]
    [Authorize(Policy = Policies.TenantOwner)]
    public async Task<ActionResult<IReadOnlyList<BillingHistoryItemDto>>> History(CancellationToken cancellationToken) =>
        Ok(await billing.GetHistoryAsync(cancellationToken));
}
