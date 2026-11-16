namespace Solvix.Api.Infrastructure;

/// <summary>Bound from <c>Solvix:RateLimits</c>.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Solvix:RateLimits";

    public const string AuthPolicy = "auth";

    /// <summary>Sign-in, tenant switch and registration attempts per client address per minute (credential stuffing / signup abuse).</summary>
    public int AuthPerMinute { get; set; } = 20;
}
