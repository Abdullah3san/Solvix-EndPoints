namespace Solvix.Modules.Identity.Application.Auth;

using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

internal sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken Issue(User user, TenantDescriptor tenant, TenantRole role)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var claims = new List<Claim>
        {
            new(SolvixClaimTypes.Subject, user.Id.ToString()),
            new(SolvixClaimTypes.Email, user.Email),
            new(SolvixClaimTypes.Name, user.DisplayName),
            new(SolvixClaimTypes.TenantId, tenant.Id.ToString()),
            new(SolvixClaimTypes.TenantSlug, tenant.Slug),
            new(SolvixClaimTypes.Role, role.ToString()),
        };
        if (user.IsPlatformAdmin)
        {
            claims.Add(new Claim(SolvixClaimTypes.PlatformAdmin, "true"));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(claims),
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
