namespace Solvix.IntegrationTests.Fixtures;

using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Solvix.Modules.Identity.Application.Mfa;

/// <summary>Forges access tokens the way an attacker with (or without) the signing key would.</summary>
public static class TestTokens
{
    public static string Mint(
        Guid userId,
        Guid tenantId,
        Guid? sessionId,
        string signingKey = SolvixApiFactory.JwtSigningKey,
        DateTime? expires = null,
        string issuer = SolvixApiFactory.JwtIssuer,
        string audience = SolvixApiFactory.JwtAudience,
        params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("email", "forged@example.test"),
            new("name", "Forged"),
            new("tid", tenantId.ToString()),
            new("tslug", "forged"),
        };
        if (sessionId is { } sid)
        {
            claims.Add(new Claim("sid", sid.ToString()));
        }

        claims.AddRange(extraClaims);
        var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = exp.AddMinutes(-30),
            IssuedAt = exp.AddMinutes(-30),
            Expires = exp,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>Re-encodes the payload of a real token with one claim changed, keeping the original signature.</summary>
    public static string TamperPayload(string token, string claim, string newValue)
    {
        ArgumentNullException.ThrowIfNull(token);
        var parts = token.Split('.');
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload[claim] = newValue;
        return $"{parts[0]}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.{parts[2]}";
    }

    /// <summary>An unsigned token claiming "alg: none".</summary>
    public static string Unsigned(Guid userId, Guid tenantId, Guid sessionId)
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(
            $$"""{"sub":"{{userId}}","tid":"{{tenantId}}","sid":"{{sessionId}}","iss":"{{SolvixApiFactory.JwtIssuer}}","aud":"{{SolvixApiFactory.JwtAudience}}","exp":{{exp}}}""");
        return $"{header}.{payload}.";
    }
}

/// <summary>
/// Produces authenticator codes for one secret. The server never accepts a
/// time step twice, so each call uses the next step, starting one step in the
/// past (inside the allowed drift) and waiting for the clock when it must.
/// </summary>
public sealed class Authenticator(string secret)
{
    private long _lastStep = long.MinValue;

    public async Task<string> NextCodeAsync()
    {
        var step = Math.Max(_lastStep + 1, Totp.StepAt(DateTimeOffset.UtcNow) - Totp.AllowedDrift);
        while (step > Totp.StepAt(DateTimeOffset.UtcNow) + Totp.AllowedDrift)
        {
            await Task.Delay(500);
        }

        _lastStep = step;
        return Totp.Compute(secret, step);
    }

    /// <summary>A well-formed code that is not valid for any accepted step.</summary>
    public string WrongCode()
    {
        var now = Totp.StepAt(DateTimeOffset.UtcNow);
        var valid = Enumerable.Range(-2, 5).Select(i => Totp.Compute(secret, now + i)).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, 1000).Select(i => i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).First(c => !valid.Contains(c));
    }
}
