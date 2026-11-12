namespace Solvix.Modules.Identity.Application.Auth;

using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Tenancy;

public interface ITokenService
{
    /// <summary>Issues a short-lived access token bound to <paramref name="session"/>.</summary>
    IssuedToken Issue(User user, TenantDescriptor tenant, UserSession session);
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
