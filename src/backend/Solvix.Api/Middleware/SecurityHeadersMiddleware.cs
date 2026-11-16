namespace Solvix.Api.Middleware;

/// <summary>
/// Defence-in-depth response headers for an API that only ever returns JSON, files and downloads. Headers a
/// controller already chose (for example the invoice page's own policy) are never overwritten.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            var headers = ctx.Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("Cross-Origin-Resource-Policy", "same-origin");
            headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");

            // The interactive API reference (Scalar) needs scripts and styles; everything else is data, not a page.
            var path = ctx.Request.Path;
            var isDocs = path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);
            if (!isDocs)
            {
                headers.TryAdd("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
            }

            if (ctx.Request.IsHttps)
            {
                headers.TryAdd("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
            }

            return Task.CompletedTask;
        }, context);
        return next(context);
    }
}
