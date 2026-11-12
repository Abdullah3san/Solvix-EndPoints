namespace Solvix.Infrastructure.Identity;

using Microsoft.AspNetCore.Http;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirst(SolvixClaimTypes.Subject)?.Value, out var id) ? id : null;

    public string? Email => Principal?.FindFirst(SolvixClaimTypes.Email)?.Value;

    public TenantRole? Role => Enum.TryParse<TenantRole>(Principal?.FindFirst(SolvixClaimTypes.Role)?.Value, out var role) ? role : null;

    public Guid RequireUserId() => UserId ?? throw new UnauthorizedException();
}
