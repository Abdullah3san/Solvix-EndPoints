namespace Solvix.Infrastructure.Caching;

public sealed class RedisOptions
{
    public const string SectionName = "Solvix:Redis";

    /// <summary>Key prefix applied by the distributed cache for this deployment.</summary>
    public string InstanceName { get; set; } = "solvix:";
}
