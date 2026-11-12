namespace Solvix.Api.Infrastructure;

using System.ComponentModel.DataAnnotations;

/// <summary>Brute-force protection for the authentication endpoints (<c>Solvix:RateLimiting</c>).</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "Solvix:RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Password, MFA code and one-time token checks, per client address.</summary>
    public RateLimitRule Credentials { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>Requests that send e-mail (password reset, verification), per client address.</summary>
    public RateLimitRule Email { get; set; } = new() { PermitLimit = 5, WindowSeconds = 900 };

    /// <summary>Token refresh and sign-out, per client address.</summary>
    public RateLimitRule Refresh { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };

    /// <summary>Workspace sign-up, per client address.</summary>
    public RateLimitRule Registration { get; set; } = new() { PermitLimit = 20, WindowSeconds = 600 };
}

public sealed class RateLimitRule
{
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, 86_400)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Reverse proxies in front of the API (<c>Solvix:Network</c>).</summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Solvix:Network";

    /// <summary>
    /// Addresses or CIDR ranges of the proxies (including the web front end)
    /// that are allowed to state the real client address in
    /// <c>X-Forwarded-For</c>. Empty means the header is ignored entirely:
    /// trusting it from anyone would let a caller pick their own address and
    /// walk around per-address rate limits.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    [Range(1, 10)]
    public int ForwardLimit { get; set; } = 1;
}
