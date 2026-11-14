namespace Solvix.Modules.Security.Evaluation;

using Solvix.Modules.Security.Domain;

/// <summary>An alert the event rules want raised. <see cref="DedupeKey"/> keeps re-delivered events from alerting twice.</summary>
public sealed record AlertCandidate(string DedupeKey, string RuleKey, Severity Severity, string Title, string Evidence);

/// <summary>
/// Server-side interpretation of forwarded Windows events. The agent only forwards events from an allow-list
/// and the server enforces the same list, so a compromised or buggy agent cannot push arbitrary log content.
/// </summary>
public static class EventRules
{
    public const string SecurityLog = "Security";
    public const string SystemLog = "System";
    public const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";

    public static readonly IReadOnlySet<(string Log, int EventId)> AllowedEvents = new HashSet<(string, int)>
    {
        (SecurityLog, 1102),
        (SystemLog, 104),
        (SecurityLog, 4625),
        (SecurityLog, 4720),
        (SecurityLog, 4732),
        (DefenderLog, 1116),
        (DefenderLog, 1117),
        (DefenderLog, 5001),
    };

    public static bool IsAllowed(ReportedEvent e) =>
        AllowedEvents.Contains((e.Log, e.EventId));

    public static IReadOnlyList<AlertCandidate> Evaluate(IReadOnlyList<ReportedEvent> events, PolicySettings policy)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(policy);

        var candidates = new List<AlertCandidate>();
        foreach (var e in events.Where(IsAllowed))
        {
            var key = $"{e.Log}:{e.RecordId}";
            var summary = PostureEvaluator.Sanitize(e.Summary);
            switch ((e.Log, e.EventId))
            {
                case (SecurityLog, 1102):
                case (SystemLog, 104):
                    candidates.Add(Candidate(RuleCatalog.EventAuditCleared, $"EVT-001:{key}", $"{e.Log} log cleared at {e.TimeCreatedUtc:u}. {summary}"));
                    break;
                case (DefenderLog, 1116):
                    candidates.Add(Candidate(RuleCatalog.EventMalwareDetected, $"EVT-002:{key}", $"Detected at {e.TimeCreatedUtc:u}. {summary}"));
                    break;
                case (DefenderLog, 5001):
                    candidates.Add(Candidate(RuleCatalog.EventDefenderDisabled, $"EVT-003:{key}", $"Disabled at {e.TimeCreatedUtc:u}."));
                    break;
                case (SecurityLog, 4732) when string.Equals(e.TargetGroup?.Trim(), "Administrators", StringComparison.OrdinalIgnoreCase):
                    candidates.Add(Candidate(RuleCatalog.EventAdminGroupChange, $"EVT-005:{key}", $"Membership of Administrators changed at {e.TimeCreatedUtc:u}. {summary}"));
                    break;
                case (SecurityLog, 4720):
                    candidates.Add(Candidate(RuleCatalog.EventAccountCreated, $"EVT-006:{key}", $"Account created at {e.TimeCreatedUtc:u}. {summary}"));
                    break;
                default:
                    break;
            }
        }

        candidates.AddRange(FailedLogonBursts(events, policy));
        return candidates;
    }

    /// <summary>Sliding window over 4625 events: one alert per window start hour that exceeds the threshold.</summary>
    private static IEnumerable<AlertCandidate> FailedLogonBursts(IReadOnlyList<ReportedEvent> events, PolicySettings policy)
    {
        var failures = events
            .Where(e => e is { Log: SecurityLog, EventId: 4625 })
            .Select(e => e.TimeCreatedUtc)
            .OrderBy(t => t)
            .ToList();
        if (failures.Count < policy.FailedLogonThreshold)
        {
            yield break;
        }

        var window = TimeSpan.FromMinutes(policy.FailedLogonWindowMinutes);
        var peaks = new Dictionary<DateTimeOffset, (int Count, DateTimeOffset Start)>();
        var left = 0;
        for (var right = 0; right < failures.Count; right++)
        {
            while (failures[right] - failures[left] > window)
            {
                left++;
            }

            var count = right - left + 1;
            if (count < policy.FailedLogonThreshold)
            {
                continue;
            }

            var bucket = new DateTimeOffset(failures[left].UtcDateTime.Date.AddHours(failures[left].UtcDateTime.Hour), TimeSpan.Zero);
            if (!peaks.TryGetValue(bucket, out var peak) || count > peak.Count)
            {
                peaks[bucket] = (count, failures[left]);
            }
        }

        var rule = RuleCatalog.Get(RuleCatalog.EventFailedLogons);
        foreach (var (bucket, peak) in peaks.OrderBy(p => p.Key))
        {
            yield return new AlertCandidate(
                $"EVT-004:{bucket:yyyyMMddHH}",
                rule.Key,
                peak.Count >= policy.FailedLogonThreshold * 5 ? Severity.High : rule.DefaultSeverity,
                rule.Title,
                $"{peak.Count} failed sign-ins within {policy.FailedLogonWindowMinutes} minute(s) starting {peak.Start:u} (threshold {policy.FailedLogonThreshold}).");
        }
    }

    private static AlertCandidate Candidate(string ruleKey, string dedupeKey, string evidence)
    {
        var rule = RuleCatalog.Get(ruleKey);
        return new AlertCandidate(dedupeKey, ruleKey, rule.DefaultSeverity, rule.Title, evidence.Length > 500 ? evidence[..499] + "…" : evidence);
    }
}
