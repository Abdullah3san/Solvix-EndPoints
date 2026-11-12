namespace Solvix.Api.Infrastructure;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// Single place where exceptions become RFC 9457 problem responses. Domain
/// exceptions map to specific statuses; everything else is a 500 whose
/// details are never exposed to the caller.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code, errors) = Map(exception);

        if (status >= 500)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            LogHandled(logger, status, code, exception.Message);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500 && !environment.IsDevelopment() ? "An unexpected error occurred." : exception.Message,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["code"] = code;
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static (int Status, string Title, string Code, IReadOnlyDictionary<string, string[]>? Errors) Map(Exception exception) =>
        exception switch
        {
            ValidationException v => (StatusCodes.Status400BadRequest, "Validation failed", v.Code, v.Errors),
            NotFoundException n => (StatusCodes.Status404NotFound, "Resource not found", n.Code, null),
            ConflictException c => (StatusCodes.Status409Conflict, "Conflict", c.Code, null),
            UnauthorizedException u => (StatusCodes.Status401Unauthorized, "Unauthorized", u.Code, null),
            TenantInactiveException t => (StatusCodes.Status403Forbidden, "Tenant inactive", t.Code, null),
            TenantContextMissingException t => (StatusCodes.Status401Unauthorized, "Tenant context missing", t.Code, null),
            CrossTenantAccessException x => (StatusCodes.Status403Forbidden, "Forbidden", x.Code, null),
            ForbiddenException f => (StatusCodes.Status403Forbidden, "Forbidden", f.Code, null),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "Conflict", "unique_violation", null),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } =>
                (StatusCodes.Status409Conflict, "Conflict", "reference_violation", null),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } } =>
                (StatusCodes.Status403Forbidden, "Forbidden", "row_level_security", null),
            OperationCanceledException => (499, "Request cancelled", "cancelled", null),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error", "internal_error", null),
        };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request failed with {Status} ({Code}): {Message}")]
    private static partial void LogHandled(ILogger logger, int status, string code, string message);
}
