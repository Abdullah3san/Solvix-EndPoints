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

/// <summary>
/// Access tokens identify the user, the tenant and the session, and nothing
/// else. They deliberately carry no roles or permissions: those are resolved on
/// the server for every request, so a token can never grant more than the
/// caller's current roles do.
/// </summary>
internal sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken Issue(User user, TenantDescriptor tenant, UserSession session)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(session);

        var now = clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        if (expires > session.ExpiresAtUtc)
        {
            expires = session.ExpiresAtUtc;
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var claims = new List<Claim>
        {
            new(SolvixClaimTypes.Subject, user.Id.ToString()),
            new(SolvixClaimTypes.Email, user.Email),
            new(SolvixClaimTypes.Name, user.DisplayName),
            new(SolvixClaimTypes.TenantId, tenant.Id.ToString()),
            new(SolvixClaimTypes.TenantSlug, tenant.Slug),
            new(SolvixClaimTypes.SessionId, session.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(SolvixClaimTypes.AuthenticationMethod, "pwd"),
        };
        if (session.MfaVerified)
        {
            claims.Add(new Claim(SolvixClaimTypes.AuthenticationMethod, "mfa"));
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
