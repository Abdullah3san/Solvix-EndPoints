namespace Solvix.Modules.Assistant.Security;

using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Configuration;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Per-user budget for model calls (chat messages and AI reports), keyed by tenant and user.
/// Applied as an MVC filter rather than through the global rate-limiting middleware: that
/// middleware runs before authentication and partitions callers by address, while this limit
/// needs the signed-in user (one office behind a NAT must not share a single budget).
/// </summary>
internal sealed class AssistantRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter;

    public AssistantRateLimiter(IOptions<AssistantOptions> options)
    {
        var permits = Math.Max(1, options.Value.ChatPermitsPerMinute);
        limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    }

    public RateLimitLease Acquire(Guid tenantId, Guid userId) =>
        limiter.AttemptAcquire(string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}:{userId:N}"));

    public void Dispose() => limiter.Dispose();
}

/// <summary>Marks an action that calls the model; see <see cref="AssistantRateLimiter"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AssistantRateLimitAttribute() : TypeFilterAttribute(typeof(AssistantRateLimitFilter));

internal sealed class AssistantRateLimitFilter(AssistantRateLimiter limiter, ICurrentUser user, ITenantContext tenant) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var lease = limiter.Acquire(tenant.RequireTenantId(), user.RequireUserId());
        if (!lease.IsAcquired)
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "You have sent too many assistant requests. Wait a minute and try again.",
                Instance = context.HttpContext.Request.Path,
            };
            problem.Extensions["code"] = "too_many_requests";
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status429TooManyRequests };
            return;
        }

        await next();
    }
}
