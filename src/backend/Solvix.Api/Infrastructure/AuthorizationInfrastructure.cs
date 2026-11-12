namespace Solvix.Api.Infrastructure;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Identity;

/// <summary>
/// Writes "forbidden" as an RFC 9457 problem with a machine-readable reason, so
/// a client can tell "you lack a permission" from "this workspace requires MFA".
/// </summary>
internal sealed partial class ProblemDetailsAuthorizationResultHandler(
    IProblemDetailsService problemDetails,
    ILogger<ProblemDetailsAuthorizationResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (!authorizeResult.Forbidden)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var reasons = authorizeResult.AuthorizationFailure?.FailureReasons.Select(r => r.Message).ToHashSet(StringComparer.Ordinal) ?? [];
        var mfaRequired = reasons.Contains(AuthorizationFailureCodes.MfaEnrollmentRequired);
        var required = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PermissionRequirement>().Select(r => r.Permission).ToArray() ?? [];

        var userId = context.User.FindFirst(SolvixClaimTypes.Subject)?.Value;
        LogDenied(logger, userId, context.Request.Method, context.Request.Path, mfaRequired ? AuthorizationFailureCodes.MfaEnrollmentRequired : string.Join(",", required));

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = mfaRequired
                ? "This workspace requires multi-factor authentication. Set it up in your account security settings to continue."
                : "You do not have permission to perform this action.",
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = mfaRequired ? AuthorizationFailureCodes.MfaEnrollmentRequired : AuthorizationFailureCodes.PermissionDenied;
        if (!mfaRequired && required.Length > 0)
        {
            problem.Extensions["requiredPermissions"] = required;
        }

        await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access denied for user {UserId} on {Method} {Path}: {Reason}")]
    private static partial void LogDenied(ILogger logger, string? userId, string method, PathString path, string reason);
}

/// <summary>
/// Refuses to start the application when a controller action does not say how
/// it is protected. Every action must be exactly one of: anonymous
/// (<c>[AllowAnonymous]</c>), permission-protected (<c>[RequirePermission]</c>)
/// or deliberately open to every signed-in member (<c>[AuthenticatedOnly]</c>).
/// A new endpoint therefore cannot ship unprotected, or "protected" only by
/// being signed in, by accident.
/// </summary>
internal sealed class ExplicitAuthorizationConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var problems = new List<string>();
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                var attributes = action.Attributes.Concat(controller.Attributes).ToList();
                var anonymous = attributes.OfType<IAllowAnonymous>().Any();
                var permission = attributes.OfType<RequirePermissionAttribute>().Any();
                var authenticatedOnly = attributes.OfType<AuthenticatedOnlyAttribute>().Any();
                var declarations = (anonymous ? 1 : 0) + (permission ? 1 : 0) + (authenticatedOnly ? 1 : 0);
                if (declarations != 1)
                {
                    problems.Add($"{controller.ControllerType.Name}.{action.ActionName} ({(declarations == 0 ? "no access declaration" : "conflicting access declarations")})");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Every controller action must declare exactly one of [AllowAnonymous], [RequirePermission] or [AuthenticatedOnly]: "
                + string.Join("; ", problems));
        }
    }
}
