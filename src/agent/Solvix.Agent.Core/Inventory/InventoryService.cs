namespace Solvix.Agent.Core.Inventory;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;

public interface IInventoryService
{
    Task<InventoryReport> CollectAsync(CancellationToken cancellationToken);
}

/// <summary>Runs every registered collector with a time budget and assembles a bounded report.</summary>
public sealed partial class InventoryService(
    IEnumerable<IInventoryCollector> collectors,
    TimeProvider time,
    ILogger<InventoryService> logger) : IInventoryService
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromMinutes(2);

    public async Task<InventoryReport> CollectAsync(CancellationToken cancellationToken)
    {
        var builder = new InventoryBuilder();
        var issues = new List<HealthIssue>();
        foreach (var collector in collectors)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CollectorTimeout);
            try
            {
                await collector.CollectAsync(builder, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                issues.Add(new HealthIssue { Component = collector.Name, Severity = HealthIssueSeverity.Warning, Message = "Collection timed out." });
                LogCollectorFailed(logger, collector.Name, "timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add(new HealthIssue { Component = collector.Name, Severity = HealthIssueSeverity.Warning, Message = Truncate(ex.Message, 500) });
                LogCollectorFailed(logger, collector.Name, ex.Message);
            }
        }

        var report = new InventoryReport
        {
            CollectedAtUtc = time.GetUtcNow(),
            AgentVersion = AgentInfo.Version,
            Hostname = Environment.MachineName,
            System = builder.System,
            Bios = builder.Bios,
            OperatingSystem = builder.OperatingSystem,
            Processors = builder.Processors.Take(InventoryReport.MaxProcessors).ToList(),
            Memory = builder.Memory is null ? null : builder.Memory with { Modules = builder.Memory.Modules.Take(InventoryReport.MaxMemoryModules).ToList() },
            Disks = builder.Disks.Take(InventoryReport.MaxDisks).ToList(),
            Volumes = builder.Volumes.Take(InventoryReport.MaxVolumes).ToList(),
            NetworkAdapters = builder.NetworkAdapters.Take(InventoryReport.MaxNetworkAdapters).ToList(),
            Software = builder.Software
                .DistinctBy(a => (a.Name, a.Version, a.Scope))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryReport.MaxSoftwareEntries)
                .ToList(),
            CollectionIssues = issues,
        };
        return report with { ContentHash = InventoryHasher.Compute(report) };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inventory module {Module} failed: {Reason}")]
    private static partial void LogCollectorFailed(ILogger logger, string module, string reason);
}

/// <summary>
/// SHA-256 over the report without volatile values (timestamps, free space,
/// available memory, link state), so an unchanged machine is not re-uploaded.
/// </summary>
public static class InventoryHasher
{
    public static string Compute(InventoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var stable = report with
        {
            CollectedAtUtc = default,
            ContentHash = null,
            CollectionIssues = [],
            Memory = report.Memory is null ? null : report.Memory with { AvailablePhysicalBytes = null },
            Volumes = report.Volumes.Select(v => v with { FreeBytes = null }).ToList(),
            NetworkAdapters = report.NetworkAdapters.Select(a => a with { SpeedBitsPerSecond = null, Status = null }).ToList(),
            OperatingSystem = report.OperatingSystem is null ? null : report.OperatingSystem with { LastBootUpTimeUtc = null },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stable, AgentJsonContext.Default.InventoryReport);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
