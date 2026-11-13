namespace Solvix.Infrastructure.Identity;

using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;

/// <summary>
/// Who is calling: identity from the validated token, access from the grant the
/// tenant resolution middleware looked up for this request.
/// </summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor, IAccessContext access) : ICurrentUser
{
    private static readonly IReadOnlySet<string> NoPermissions = FrozenSet<string>.Empty;

    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirst(SolvixClaimTypes.Subject)?.Value, out var id) ? id : null;

    public string? Email => Principal?.FindFirst(SolvixClaimTypes.Email)?.Value;

    public Guid? SessionId => access.Grant?.SessionId;

    public IReadOnlySet<string> Permissions => access.Grant?.Permissions ?? NoPermissions;

    public IReadOnlyList<string> Roles => access.Grant?.Roles ?? [];

    public bool HasPermission(string permission) => access.Grant?.Permissions.Contains(permission) == true;

    public Guid RequireUserId() => UserId ?? throw new UnauthorizedException();

    public Guid RequireSessionId() => SessionId ?? throw new UnauthorizedException();
}

/// <summary>Address and user agent of the caller, as established by the forwarded-headers middleware.</summary>
internal sealed class HttpClientInfo(IHttpContextAccessor accessor) : IClientInfo
{
    public const string CorrelationItemKey = "X-Correlation-Id";

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var value = accessor.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public string? CorrelationId =>
        accessor.HttpContext?.Items.TryGetValue(CorrelationItemKey, out var value) == true ? value?.ToString() : null;
}
