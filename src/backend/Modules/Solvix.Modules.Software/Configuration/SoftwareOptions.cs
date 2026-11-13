namespace Solvix.Modules.Software.Configuration;

public sealed class SoftwareOptions
{
    public const string SectionName = "Solvix:Software";

    public PackageStorageOptions Storage { get; set; } = new();

    /// <summary>Platform upper bound for one package; tenants cannot exceed it.</summary>
    public long MaxPackageBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Interval of the deployment engine (scheduling, dispatch, result collection, retries, expiry).</summary>
    public int ProcessorIntervalSeconds { get; set; } = 15;

    /// <summary>Tests drive the processor explicitly.</summary>
    public bool RunProcessor { get; set; } = true;

    /// <summary>Commands created per deployment per pass (throttles large rollouts).</summary>
    public int DispatchBatchSize { get; set; } = 200;

    /// <summary>Re-hash a stored package before serving it when the last verification is older than this.</summary>
    public int ReverifyAfterMinutes { get; set; } = 60;
}

public sealed class PackageStorageOptions
{
    /// <summary>Directory for package files; outside any web root, readable only by the API account.</summary>
    public string RootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "software-packages");
}
