namespace Solvix.Api.Middleware;

using System.Diagnostics;
using Serilog.Context;

/// <summary>
/// Accepts or generates an <c>X-Correlation-Id</c>, echoes it on the response
/// and pushes it (plus the W3C trace id) into the structured log context.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
            && !string.IsNullOrWhiteSpace(incoming) && incoming.ToString().Length <= 128
            ? incoming.ToString()
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Response.Headers[HeaderName] = correlationId;
        context.Items[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TraceId", Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier))
        {
            await next(context);
        }
    }
}
