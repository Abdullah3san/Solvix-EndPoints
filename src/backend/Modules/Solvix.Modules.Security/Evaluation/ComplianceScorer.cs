namespace Solvix.Modules.Security.Evaluation;

using Solvix.Modules.Security.Domain;

/// <summary>One line of the score explanation.</summary>
public sealed record ScorePenalty(string RuleKey, string Title, Severity Severity, int Points, string Note);

/// <summary>A compliance score together with everything needed to explain it.</summary>
/// <param name="Score">0–100. 100 means no scored failures; it is not a statement that the device is secure.</param>
/// <param name="Coverage">Share of applicable checks that could actually be assessed (0–1). Unknown checks lower it.</param>
public sealed record ComplianceScore(
    int Score,
    ComplianceBand Band,
    double Coverage,
    int FailedChecks,
    int UnknownChecks,
    int AcceptedChecks,
    IReadOnlyList<ScorePenalty> Penalties,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Score = 100 − Σ penalties, floored at 0. Penalty per failed check: Critical 40, High 20, Medium 10, Low 4.
/// Repeats of the same rule (for example ten vulnerable products) are capped at twice the severity weight so one kind
/// of problem cannot hide every other result. Unknown and not-applicable checks never cost points; unknown checks lower
/// <see cref="ComplianceScore.Coverage"/> instead. Findings with an active risk acceptance are listed but cost nothing.
///
/// Band: NonCompliant if score &lt; 70 or any Critical failure; otherwise AtRisk if score &lt; 90, any High failure, or
/// coverage &lt; 90%; otherwise Compliant. A stale or never-reporting device is Unknown.
/// </summary>
public static class ComplianceScorer
{
    public const double MinimumCoverageForCompliant = 0.9;
    public const int CompliantThreshold = 90;
    public const int NonCompliantThreshold = 70;

    public static int PenaltyFor(Severity severity) => severity switch
    {
        Severity.Critical => 40,
        Severity.High => 20,
        Severity.Medium => 10,
        Severity.Low => 4,
        _ => 0,
    };

    public static ComplianceScore Score(IReadOnlyList<CheckResult> results, IReadOnlySet<(string RuleKey, string SubjectKey)> acceptedRisks, bool isStale)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(acceptedRisks);

        var penalties = new List<ScorePenalty>();
        var perRuleTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var failed = 0;
        var unknownRules = new HashSet<string>(StringComparer.Ordinal);
        var assessedRules = new HashSet<string>(StringComparer.Ordinal);
        var accepted = 0;
        var anyCritical = false;
        var anyHigh = false;

        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case CheckOutcome.Pass:
                    assessedRules.Add(r.RuleKey);
                    break;
                case CheckOutcome.Unknown:
                    unknownRules.Add(r.RuleKey);
                    break;
                case CheckOutcome.Fail:
                    assessedRules.Add(r.RuleKey);
                    if (acceptedRisks.Contains((r.RuleKey, r.SubjectKey)))
                    {
                        accepted++;
                        penalties.Add(new ScorePenalty(r.RuleKey, r.Title, r.Severity, 0, "Risk accepted by an administrator; not scored."));
                        break;
                    }

                    failed++;
                    anyCritical |= r.Severity == Severity.Critical;
                    anyHigh |= r.Severity == Severity.High;
                    var weight = PenaltyFor(r.Severity);
                    var cap = weight * 2;
                    perRuleTotals.TryGetValue(r.RuleKey, out var already);
                    var points = Math.Max(0, Math.Min(weight, cap - already));
                    perRuleTotals[r.RuleKey] = already + points;
                    penalties.Add(new ScorePenalty(
                        r.RuleKey,
                        r.Title,
                        r.Severity,
                        points,
                        points < weight ? "Counted at a reduced value because this rule already failed several times." : r.Evidence));
                    break;
                default:
                    break;
            }
        }

        var total = penalties.Sum(p => p.Points);
        var score = Math.Max(0, 100 - total);
        var denominator = assessedRules.Count + unknownRules.Count;
        var coverage = denominator == 0 ? 0 : assessedRules.Count / (double)denominator;

        var reasons = new List<string>();
        ComplianceBand band;
        if (isStale)
        {
            band = ComplianceBand.Unknown;
            reasons.Add("The device has not reported recently, so its current state is unknown.");
        }
        else if (denominator == 0)
        {
            band = ComplianceBand.Unknown;
            reasons.Add("No checks could be assessed.");
        }
        else if (score < NonCompliantThreshold || anyCritical)
        {
            band = ComplianceBand.NonCompliant;
            reasons.Add(anyCritical
                ? "At least one Critical finding is open."
                : $"The score is below {NonCompliantThreshold}.");
        }
        else if (score < CompliantThreshold || anyHigh || coverage < MinimumCoverageForCompliant)
        {
            band = ComplianceBand.AtRisk;
            if (score < CompliantThreshold)
            {
                reasons.Add($"The score is below {CompliantThreshold}.");
            }

            if (anyHigh)
            {
                reasons.Add("At least one High finding is open.");
            }

            if (coverage < MinimumCoverageForCompliant)
            {
                reasons.Add($"Only {coverage:P0} of applicable checks could be assessed; missing data prevents a Compliant rating.");
            }
        }
        else
        {
            band = ComplianceBand.Compliant;
            reasons.Add("No scored failures above the threshold and all required data was collected.");
        }

        return new ComplianceScore(
            score,
            band,
            Math.Round(coverage, 3),
            failed,
            unknownRules.Count,
            accepted,
            penalties.Where(p => p.Points > 0 || p.Note.StartsWith("Risk accepted", StringComparison.Ordinal)).OrderByDescending(p => p.Points).ThenBy(p => p.RuleKey, StringComparer.Ordinal).ToList(),
            reasons);
    }
}
