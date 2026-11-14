namespace Solvix.UnitTests.Security;

using System.Text;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Application.Reports;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Exceptions;

public sealed class ScoringAndEventTests
{
    private static readonly IReadOnlySet<(string, string)> None = new HashSet<(string, string)>();

    private static CheckResult Fail(string rule, Severity severity, string subject = "") => new(rule, subject, CheckOutcome.Fail, severity, rule, "evidence");

    private static CheckResult Pass(string rule) => new(rule, string.Empty, CheckOutcome.Pass, Severity.Info, rule, "ok");

    [Fact]
    public void No_failures_scores_100_and_is_compliant()
    {
        var s = ComplianceScorer.Score([Pass("A"), Pass("B")], None, false);
        Assert.Equal(100, s.Score);
        Assert.Equal(ComplianceBand.Compliant, s.Band);
    }

    [Theory]
    [InlineData(Severity.Critical, 60, ComplianceBand.NonCompliant)]
    [InlineData(Severity.High, 80, ComplianceBand.AtRisk)]
    [InlineData(Severity.Medium, 90, ComplianceBand.Compliant)]
    [InlineData(Severity.Low, 96, ComplianceBand.Compliant)]
    public void Single_failure_penalties_and_bands_follow_the_rubric(Severity severity, int score, ComplianceBand band)
    {
        var s = ComplianceScorer.Score([Pass("A"), Fail("X", severity)], None, false);
        Assert.Equal(score, s.Score);
        Assert.Equal(band, s.Band);
    }

    [Fact]
    public void Repeats_of_one_rule_are_capped_at_twice_the_weight()
    {
        var results = Enumerable.Range(0, 10).Select(i => Fail("SW-001", Severity.High, $"p{i}")).Append(Pass("A")).ToList();
        var s = ComplianceScorer.Score(results, None, false);
        Assert.Equal(60, s.Score); // 100 - 2*20
        Assert.Equal(10, s.FailedChecks);
    }

    [Fact]
    public void Accepted_risks_cost_nothing_but_stay_visible()
    {
        var accepted = new HashSet<(string, string)> { ("X", "") };
        var s = ComplianceScorer.Score([Pass("A"), Fail("X", Severity.High)], accepted, false);
        Assert.Equal(100, s.Score);
        Assert.Equal(1, s.AcceptedChecks);
        Assert.Contains(s.Penalties, p => p.Points == 0);
    }

    [Fact]
    public void Unknown_checks_do_not_cost_points_but_block_a_compliant_rating()
    {
        var results = new List<CheckResult> { Pass("A") };
        results.AddRange(Enumerable.Range(0, 3).Select(i => new CheckResult($"U{i}", "", CheckOutcome.Unknown, Severity.Info, "u", "missing")));
        var s = ComplianceScorer.Score(results, None, false);
        Assert.Equal(100, s.Score);
        Assert.Equal(0.25, s.Coverage);
        Assert.Equal(ComplianceBand.AtRisk, s.Band);
        Assert.Equal(3, s.UnknownChecks);
    }

    [Fact]
    public void Stale_devices_are_unknown_whatever_their_score()
    {
        Assert.Equal(ComplianceBand.Unknown, ComplianceScorer.Score([Pass("A")], None, true).Band);
    }

    [Fact]
    public void Score_never_goes_below_zero()
    {
        var results = Enumerable.Range(0, 20).Select(i => Fail($"R{i}", Severity.Critical)).ToList();
        Assert.Equal(0, ComplianceScorer.Score(results, None, false).Score);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("2.0", "2.0.0.0", 0)]
    [InlineData("v3.1.4-beta", "3.1.4", 0)]
    public void Versions_compare_numerically(string a, string b, int expected)
    {
        Assert.True(VersionComparer.TryParse(a, out var pa));
        Assert.True(VersionComparer.TryParse(b, out var pb));
        Assert.Equal(expected, Math.Sign(VersionComparer.Compare(pa, pb)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("99999999999.1")]
    public void Bad_versions_do_not_parse(string text) => Assert.False(VersionComparer.TryParse(text, out _));

    private static ReportedEvent Event(string log, int id, long record, DateTimeOffset? at = null, string? group = null) =>
        new() { Log = log, EventId = id, RecordId = record, TimeCreatedUtc = at ?? DateTimeOffset.UtcNow, Summary = "s", TargetGroup = group };

    [Fact]
    public void Audit_log_cleared_is_a_critical_alert()
    {
        var c = Assert.Single(EventRules.Evaluate([Event("Security", 1102, 1)], PolicySettings.Default));
        Assert.Equal(Severity.Critical, c.Severity);
    }

    [Fact]
    public void Non_allow_listed_events_are_ignored()
    {
        Assert.Empty(EventRules.Evaluate([Event("Security", 4624, 1), Event("Application", 1102, 2)], PolicySettings.Default));
    }

    [Fact]
    public void Admin_group_change_only_alerts_for_administrators()
    {
        Assert.Empty(EventRules.Evaluate([Event("Security", 4732, 1, group: "Users")], PolicySettings.Default));
        Assert.Single(EventRules.Evaluate([Event("Security", 4732, 2, group: "administrators")], PolicySettings.Default));
    }

    [Fact]
    public void Failed_logon_burst_alerts_once_per_hour_bucket()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var events = Enumerable.Range(0, 12).Select(i => Event("Security", 4625, i, start.AddSeconds(i * 10))).ToList();
        var c = Assert.Single(EventRules.Evaluate(events, PolicySettings.Default));
        Assert.Equal(Severity.Medium, c.Severity);
        Assert.Equal(c.DedupeKey, EventRules.Evaluate(events, PolicySettings.Default).Single().DedupeKey);
    }

    [Fact]
    public void Slow_failed_logons_below_the_window_rate_do_not_alert()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        var events = Enumerable.Range(0, 12).Select(i => Event("Security", 4625, i, start.AddMinutes(i * 30))).ToList();
        Assert.Empty(EventRules.Evaluate(events, PolicySettings.Default));
    }

    [Fact]
    public void Huge_failed_logon_burst_escalates_to_high()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var events = Enumerable.Range(0, 60).Select(i => Event("Security", 4625, i, start.AddSeconds(i))).ToList();
        Assert.Equal(Severity.High, Assert.Single(EventRules.Evaluate(events, PolicySettings.Default)).Severity);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    public void Csv_cells_neutralise_formulas(string input, string expected)
    {
        var cell = Csv.Cell(input);
        if (input.StartsWith("=", StringComparison.Ordinal))
        {
            Assert.StartsWith("\"'=", cell, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(expected, cell);
        }
    }

    [Fact]
    public void Csv_output_has_bom_and_crlf()
    {
        var sb = new StringBuilder();
        Csv.Row(sb, "a", "b");
        var bytes = Csv.ToBytes(sb);
        Assert.Equal(0xEF, bytes[0]);
        Assert.EndsWith("\r\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Device_key_round_trips_and_verifies_only_the_right_secret()
    {
        var tenant = Guid.NewGuid();
        var endpoint = Guid.NewGuid();
        var secret = DeviceKey.NewSecret();
        var key = DeviceKey.Compose(tenant, endpoint, secret);

        Assert.True(DeviceKey.TryParse(key, out var t, out var e, out var s));
        Assert.Equal((tenant, endpoint, secret), (t, e, s));
        var hash = DeviceKey.Hash(secret);
        Assert.True(DeviceKey.Verify(hash, secret));
        Assert.False(DeviceKey.Verify(hash, DeviceKey.NewSecret()));
        Assert.False(DeviceKey.Verify(null, secret));
        Assert.DoesNotContain(secret, Convert.ToBase64String(hash), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sxk1.a.b.c")]
    [InlineData("sxk2.00000000000000000000000000000000.00000000000000000000000000000000.0123456789012345678901234567890123456789012")]
    public void Malformed_device_keys_are_rejected(string? key) => Assert.False(DeviceKey.TryParse(key, out _, out _, out _));

    [Fact]
    public void Device_keys_are_unique_and_high_entropy()
    {
        var set = Enumerable.Range(0, 200).Select(_ => DeviceKey.NewSecret()).ToHashSet();
        Assert.Equal(200, set.Count);
        Assert.All(set, s => Assert.Equal(43, s.Length));
    }

    [Fact]
    public void Remediation_catalogue_validates_parameters_against_closed_sets()
    {
        Assert.True(RemediationCatalog.TryGet("firewall.enable_profile", out var fw));
        Assert.Null(RemediationCatalog.ValidateParameter(fw, "Public"));
        Assert.NotNull(RemediationCatalog.ValidateParameter(fw, "Public; rm -rf /"));
        Assert.NotNull(RemediationCatalog.ValidateParameter(fw, null));
        Assert.True(RemediationCatalog.TryGet("defender.quick_scan", out var scan));
        Assert.NotNull(RemediationCatalog.ValidateParameter(scan, "x"));
        Assert.False(RemediationCatalog.TryGet("powershell.run", out _));
    }

    [Fact]
    public void Dangerous_changes_are_deliberately_not_in_the_remediation_catalogue()
    {
        var keys = RemediationCatalog.All.Select(a => a.Key).ToList();
        Assert.DoesNotContain(keys, k => k.Contains("bitlocker", StringComparison.OrdinalIgnoreCase) || k.Contains("admin", StringComparison.OrdinalIgnoreCase) || k.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_rule_has_a_complete_explanation()
    {
        foreach (var r in RuleCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.WhyItMatters), r.Key);
            Assert.False(string.IsNullOrWhiteSpace(r.SeverityRationale), r.Key);
            Assert.False(string.IsNullOrWhiteSpace(r.HowToFix), r.Key);
            Assert.False(string.IsNullOrWhiteSpace(r.WindowsInterface), r.Key);
            if (r.RemediationActionKey is not null)
            {
                Assert.True(RemediationCatalog.TryGet(r.RemediationActionKey, out _), r.Key);
            }
        }
    }

    [Fact]
    public void Policy_settings_validation_rejects_bad_values()
    {
        Assert.Empty(PolicySettings.Default.Validate());
        Assert.NotEmpty((PolicySettings.Default with { MaxSignatureAgeDays = 0 }).Validate());
        Assert.NotEmpty((PolicySettings.Default with { AllowedRemediationActions = ["bogus"], EnforcementMode = EnforcementMode.ApprovalRequired }).Validate());
        Assert.NotEmpty((PolicySettings.Default with { AllowedRemediationActions = ["defender.quick_scan"] }).Validate());
        Assert.Empty((PolicySettings.Default with { AllowedRemediationActions = ["defender.quick_scan"], EnforcementMode = EnforcementMode.ApprovalRequired }).Validate());
    }

    // ------------------------------------------------------------- domain rules

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static RemediationRequest Request(Guid? by, string action = "defender.quick_scan", string? param = null)
    {
        RemediationCatalog.TryGet(action, out var a);
        return by is { } u
            ? RemediationRequest.ByUser(Guid.NewGuid(), Guid.NewGuid(), null, a, param, "because", u, Now)
            : RemediationRequest.BySystem(Guid.NewGuid(), Guid.NewGuid(), null, a, param, "policy", Now);
    }

    [Fact]
    public void Requester_cannot_approve_their_own_remediation()
    {
        var user = Guid.NewGuid();
        var r = Request(user);
        Assert.Throws<ForbiddenException>(() => r.Approve(user, null, Now));
        r.Approve(Guid.NewGuid(), null, Now);
        Assert.Equal(RemediationStatus.Approved, r.Status);
    }

    [Fact]
    public void Only_low_risk_actions_can_be_auto_approved()
    {
        Request(null).AutoApprove(Guid.NewGuid(), Now);
        Assert.Throws<ForbiddenException>(() => Request(null, "defender.enable_realtime").AutoApprove(Guid.NewGuid(), Now));
    }

    [Fact]
    public void Remediation_requires_a_valid_parameter_and_expires()
    {
        Assert.Throws<ValidationException>(() => Request(Guid.NewGuid(), "firewall.enable_profile", "Bogus"));
        var r = Request(Guid.NewGuid());
        Assert.Throws<ConflictException>(() => r.Approve(Guid.NewGuid(), null, Now.AddDays(8)));
        Assert.True(r.ExpireIfDue(Now.AddDays(8)));
        Assert.Equal(RemediationStatus.Expired, r.Status);
    }

    [Fact]
    public void Remediation_lifecycle_is_strictly_ordered()
    {
        var r = Request(Guid.NewGuid());
        Assert.Throws<ConflictException>(() => r.MarkDispatched(Now));
        r.Approve(Guid.NewGuid(), null, Now);
        r.MarkDispatched(Now);
        Assert.Throws<ConflictException>(() => r.Approve(Guid.NewGuid(), null, Now));
        r.Complete(true, "ok", Now);
        Assert.Equal(RemediationStatus.Succeeded, r.Status);
        Assert.Throws<ConflictException>(() => r.Complete(true, null, Now));
    }

    [Fact]
    public void Dispatched_remediation_without_result_fails_after_the_window()
    {
        var r = Request(Guid.NewGuid());
        r.Approve(Guid.NewGuid(), null, Now);
        r.MarkDispatched(Now);
        Assert.True(r.ExpireIfDue(Now.AddHours(1)));
        Assert.Equal(RemediationStatus.Failed, r.Status);
    }

    private static SecurityPolicy Policy(Guid author) => new(Guid.NewGuid(), 1, "P", null, PolicySettings.Default, author, null);

    [Fact]
    public void Policy_cannot_be_approved_by_author_or_submitter()
    {
        var author = Guid.NewGuid();
        var submitter = Guid.NewGuid();
        var p = Policy(author);
        Assert.Throws<ConflictException>(() => p.Approve(Guid.NewGuid(), null, Now)); // not submitted yet
        p.Submit(submitter, Now);
        Assert.Throws<ForbiddenException>(() => p.Approve(author, null, Now));
        Assert.Throws<ForbiddenException>(() => p.Approve(submitter, null, Now));
        p.Approve(Guid.NewGuid(), null, Now);
        Assert.Equal(PolicyStatus.Active, p.Status);
    }

    [Fact]
    public void Submitted_policy_is_immutable()
    {
        var p = Policy(Guid.NewGuid());
        p.Submit(Guid.NewGuid(), Now);
        Assert.Throws<ConflictException>(() => p.UpdateDraft("x", null, PolicySettings.Default));
    }

    [Fact]
    public void Invalid_policy_settings_are_refused_at_the_domain()
    {
        Assert.Throws<ValidationException>(() => new SecurityPolicy(Guid.NewGuid(), 1, "P", null, PolicySettings.Default with { StaleAfterDays = 0 }, Guid.NewGuid(), null));
    }

    [Fact]
    public void Findings_reopen_and_acceptance_expires()
    {
        var f = new SecurityFinding(Guid.NewGuid(), Guid.NewGuid(), "X", "", Severity.High, "t", "e", null, null, Now);
        f.Resolve(Now.AddDays(1));
        Assert.True(f.Observe(Severity.High, "t", "e", null, null, Now.AddDays(2)));
        Assert.Equal(1, f.RecurrenceCount);

        f.AcceptRisk(Guid.NewGuid(), "why", Now.AddDays(10), Now.AddDays(2));
        Assert.False(f.Observe(Severity.High, "t", "e", null, null, Now.AddDays(5)));
        Assert.Equal(FindingStatus.Accepted, f.Status);
        Assert.True(f.Observe(Severity.High, "t", "e", null, null, Now.AddDays(11)));
        Assert.Equal(FindingStatus.Open, f.Status);
    }

    [Fact]
    public void Risk_acceptance_is_time_boxed()
    {
        var f = new SecurityFinding(Guid.NewGuid(), Guid.NewGuid(), "X", "", Severity.Low, "t", "e", null, null, Now);
        Assert.Throws<ValidationException>(() => f.AcceptRisk(Guid.NewGuid(), "why", Now.AddDays(365), Now));
        Assert.Throws<ValidationException>(() => f.AcceptRisk(Guid.NewGuid(), "why", Now.AddDays(-1), Now));
        Assert.Throws<ValidationException>(() => f.AcceptRisk(Guid.NewGuid(), "  ", Now.AddDays(5), Now));
    }

    [Fact]
    public void Advisory_rejects_overbroad_or_malformed_rules()
    {
        Assert.Throws<ValidationException>(() => new SoftwareAdvisory(Guid.NewGuid(), "t", "ab", null, null, "1.0", Severity.High, null, Guid.NewGuid()));
        Assert.Throws<ValidationException>(() => new SoftwareAdvisory(Guid.NewGuid(), "t", "abc", null, null, "x", Severity.High, null, Guid.NewGuid()));
        Assert.Throws<ValidationException>(() => new SoftwareAdvisory(Guid.NewGuid(), "t", "abc", null, "2.0", "1.0", Severity.High, null, Guid.NewGuid()));
        Assert.Throws<ValidationException>(() => new SoftwareAdvisory(Guid.NewGuid(), "t", "abc", null, null, "1.0", Severity.Info, null, Guid.NewGuid()));
    }

    [Fact]
    public void Alert_state_machine_rejects_invalid_transitions()
    {
        var a = new SecurityAlert(Guid.NewGuid(), Guid.NewGuid(), "R", "k", Severity.High, "t", "e", null, Now);
        a.Acknowledge(Guid.NewGuid(), Now);
        Assert.Throws<ConflictException>(() => a.Acknowledge(Guid.NewGuid(), Now));
        Assert.Throws<ValidationException>(() => a.Dismiss(Guid.NewGuid(), "", Now));
        a.Dismiss(Guid.NewGuid(), "noise", Now);
        Assert.Throws<ConflictException>(() => a.Resolve(null, null, Now));
    }
}
