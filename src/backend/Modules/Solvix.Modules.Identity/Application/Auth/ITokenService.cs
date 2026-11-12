namespace Solvix.Modules.Identity.Application.Auth;

using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

public interface ITokenService
{
    IssuedToken Issue(User user, TenantDescriptor tenant, TenantRole role);
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
