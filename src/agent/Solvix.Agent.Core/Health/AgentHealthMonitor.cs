namespace Solvix.Agent.Core.Health;

using System.Collections.Concurrent;
using System.Diagnostics;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;

/// <summary>Agent health module: aggregates component issues into the report sent with each heartbeat.</summary>
public sealed class AgentHealthMonitor(TimeProvider time, ConnectionMonitor connection)
{
    private readonly ConcurrentDictionary<string, HealthIssue> _issues = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

    public DateTimeOffset StartedAtUtc => _startedAt;

    public void Report(string component, HealthIssueSeverity severity, string message) =>
        _issues[component] = new HealthIssue { Component = component, Severity = severity, Message = message.Length > 1000 ? message[..1000] : message };

    public void Clear(string component) => _issues.TryRemove(component, out _);

    public IReadOnlyCollection<HealthIssue> Issues => _issues.Values.ToList();

    public AgentHealthReport Build(DateTimeOffset? lastInventoryAtUtc, DateTimeOffset? certificateNotAfterUtc, string? osBuild, int previousFailures)
    {
        var issues = _issues.Values.OrderBy(i => i.Component, StringComparer.Ordinal).Take(50).ToList();
        var status = issues.Any(i => i.Severity == HealthIssueSeverity.Error) ? AgentHealthStatus.Unhealthy
            : issues.Count > 0 || previousFailures > 0 ? AgentHealthStatus.Degraded
            : AgentHealthStatus.Healthy;
        var now = time.GetUtcNow();
        return new AgentHealthReport
        {
            Status = status,
            StartedAtUtc = _startedAt,
            UptimeSeconds = (long)(now - _startedAt).TotalSeconds,
            LastInventoryAtUtc = lastInventoryAtUtc,
            LastSuccessfulContactAtUtc = connection.LastSuccessAtUtc,
            ConsecutiveFailures = previousFailures,
            WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
            CertificateNotAfterUtc = certificateNotAfterUtc,
            OsBuild = osBuild,
            Issues = issues,
        };
    }
}
