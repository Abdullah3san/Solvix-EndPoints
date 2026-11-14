namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Security.Application;
using Solvix.Modules.Security.Application.Ingest;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class SecurityApiTests(ApiFixture fixture)
{
    private const string Base = "/api/v1/security";

    // ------------------------------------------------------------------ helpers

    private sealed record Actor(HttpClient Client, Guid UserId, string Email);

    private async Task<Actor> AddMemberAsync(TenantSession tenant, TenantRole role)
    {
        using var owner = fixture.CreateClient(tenant.Token);
        var email = $"{role}-{Guid.NewGuid():N}@example.test".ToLowerInvariant();
        var added = await HttpAssert.OkAsync<MemberDto>(
            await owner.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(email, role.ToString(), "Passw0rd!Secure", role), ApiFixture.Json),
            HttpStatusCode.Created);
        var auth = await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug);
        return new Actor(fixture.CreateClient(auth.AccessToken), added.UserId, email);
    }

    private static PostureReport Healthy(string host = "PC-01") => PostureEvaluatorFixtures.Healthy() with { Hostname = host, CollectedAtUtc = DateTimeOffset.UtcNow };

    private async Task<(EnrollmentDto Enrollment, HttpClient Device)> EnrollAsync(HttpClient admin, string hostname = "PC-01")
    {
        var enrolled = await HttpAssert.OkAsync<EnrollmentDto>(
            await admin.PostAsJsonAsync($"{Base}/endpoints", new EnrollEndpointRequest(hostname), ApiFixture.Json), HttpStatusCode.Created);
        return (enrolled, DeviceClient(enrolled.DeviceKey));
    }

    private HttpClient DeviceClient(string? key)
    {
        var client = fixture.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Solvix-Device-Key", key);
        }

        return client;
    }

    private static async Task<ReportReceiptDto> ReportAsync(HttpClient device, PostureReport report)
    {
        await Task.Delay(1100); // the test host enforces a 1 s minimum between reports
        return await HttpAssert.OkAsync<ReportReceiptDto>(await device.PostAsJsonAsync($"{Base}/ingest/posture", report, ApiFixture.Json));
    }

    private static async Task<T> Get<T>(HttpClient client, string url) => await HttpAssert.OkAsync<T>(await client.GetAsync(url));

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o, ApiFixture.Json), System.Text.Encoding.UTF8, "application/json");

    private static PolicySettings Enforcing(params string[] actions) =>
        PolicySettings.Default with { EnforcementMode = EnforcementMode.ApprovalRequired, AllowedRemediationActions = actions };

    /// <summary>Admin drafts + submits, a different owner approves.</summary>
    private static async Task<PolicyDto> ActivatePolicyAsync(HttpClient admin, HttpClient owner, PolicySettings settings, string name = "Baseline")
    {
        var draft = await HttpAssert.OkAsync<PolicyDto>(await admin.PostAsJsonAsync($"{Base}/policies", new SavePolicyRequest(name, null, settings), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.OkAsync<PolicyDto>(await admin.PostAsync($"{Base}/policies/{draft.Id}/submit", null));
        return await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsJsonAsync($"{Base}/policies/{draft.Id}/approve", new NoteRequest("ok"), ApiFixture.Json));
    }

    // ------------------------------------------------------------------ authorization

    [Fact]
    public async Task Security_api_requires_authentication_and_denies_ordinary_members()
    {
        var t = await fixture.RegisterTenantAsync();
        var member = await AddMemberAsync(t, TenantRole.Member);
        var admin = await AddMemberAsync(t, TenantRole.Admin);

        string[] reads = ["summary", "rules", "endpoints", "findings", "alerts", "events", "policies", "policies/effective", "advisories", "remediation", "audit", "reports/devices.csv", "reports/findings.csv"];
        using var anonymous = fixture.CreateClient();
        foreach (var path in reads)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Base}/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync($"{Base}/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync($"{Base}/{path}")).StatusCode);
        }

        // Writes are denied to members too.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PostAsJsonAsync($"{Base}/endpoints", new EnrollEndpointRequest("x"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PostAsync($"{Base}/policies/{Guid.NewGuid()}/approve", Json(new { }))).StatusCode);
    }

    [Fact]
    public void Every_security_controller_except_ingest_inherits_the_restrictive_base()
    {
        var controllers = typeof(Solvix.Modules.Security.SecurityModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(controllers);
        foreach (var c in controllers.Where(c => c.Name != "SecurityIngestController"))
        {
            Assert.True(typeof(Solvix.Modules.Security.Api.SecurityControllerBase).IsAssignableFrom(c), c.Name);
        }

        // The anonymous surface is exactly the ingest controller.
        var anonymous = controllers.Where(c => c.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Length > 0).ToList();
        Assert.Equal("SecurityIngestController", Assert.Single(anonymous).Name);
    }

    // ------------------------------------------------------------------ enrolment & device auth

    [Fact]
    public async Task Device_key_is_shown_once_and_never_stored_or_returned_again()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (enrolled, _) = await EnrollAsync(admin.Client);
        var secret = enrolled.DeviceKey.Split('.')[3];

        var responses = new[]
        {
            await (await admin.Client.GetAsync($"{Base}/endpoints")).Content.ReadAsStringAsync(),
            await (await admin.Client.GetAsync($"{Base}/endpoints/{enrolled.Endpoint.Id}")).Content.ReadAsStringAsync(),
            await (await admin.Client.GetAsync($"{Base}/audit")).Content.ReadAsStringAsync(),
        };
        foreach (var body in responses)
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
        }

        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", t.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("SELECT key_hash FROM security_endpoints WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", enrolled.Endpoint.Id);
        var hash = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.Equal(32, hash.Length);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(secret), hash);
    }

    [Fact]
    public async Task Ingest_rejects_missing_malformed_wrong_and_cross_tenant_keys_identically()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var adminA = await AddMemberAsync(a, TenantRole.Admin);
        var adminB = await AddMemberAsync(b, TenantRole.Admin);
        var (ea, _) = await EnrollAsync(adminA.Client);
        var (eb, _) = await EnrollAsync(adminB.Client);
        var partsA = ea.DeviceKey.Split('.');
        var partsB = eb.DeviceKey.Split('.');

        string[] bad =
        [
            string.Empty,
            "garbage",
            $"{partsA[0]}.{partsA[1]}.{partsA[2]}.{new string('A', 43)}",                // right ids, wrong secret
            $"{partsA[0]}.{partsB[1]}.{partsA[2]}.{partsA[3]}",                         // A's device and secret under B's tenant
            $"{partsB[0]}.{partsA[1]}.{partsB[2]}.{partsB[3]}",                         // B's device and secret under A's tenant
            $"{partsA[0]}.{partsA[1]}.{Guid.NewGuid():N}.{partsA[3]}",                  // unknown device id
        ];
        var bodies = new HashSet<string>();
        foreach (var key in bad)
        {
            using var client = DeviceClient(key.Length == 0 ? null : key);
            var response = await client.PostAsJsonAsync($"{Base}/ingest/posture", Healthy(), ApiFixture.Json);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
            bodies.Add(problem.GetProperty("detail").GetString()!);
        }

        Assert.Single(bodies); // no oracle: every failure says the same thing

        // A user token is not a device credential and a device key is not a user token.
        using var asUser = fixture.CreateClient(a.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await asUser.PostAsJsonAsync($"{Base}/ingest/posture", Healthy(), ApiFixture.Json)).StatusCode);
        using var keyAsBearer = fixture.CreateClient(ea.DeviceKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyAsBearer.GetAsync($"{Base}/summary")).StatusCode);
    }

    [Fact]
    public async Task Rotated_and_revoked_keys_stop_working()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy());

        var rotated = await HttpAssert.OkAsync<EnrollmentDto>(await admin.Client.PostAsync($"{Base}/endpoints/{e.Endpoint.Id}/rotate-key", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync($"{Base}/ingest/posture", Healthy(), ApiFixture.Json)).StatusCode);
        var fresh = DeviceClient(rotated.DeviceKey);
        await ReportAsync(fresh, Healthy());

        await HttpAssert.OkAsync<EndpointSummaryDto>(await admin.Client.PostAsync($"{Base}/endpoints/{e.Endpoint.Id}/revoke", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync($"{Base}/ingest/commands")).StatusCode);
    }

    // ------------------------------------------------------------------ ingest, findings, alerts

    [Fact]
    public async Task Unhealthy_report_creates_explained_findings_and_alerts_and_a_later_clean_report_resolves_them()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);

        var bad = Healthy() with
        {
            Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false, ActiveThreatCount = 1 },
            Firewall = new FirewallStatus { Profiles = [new("Domain", true, "Block"), new("Private", true, "Block"), new("Public", false, "Block")] },
        };
        var receipt = await ReportAsync(device, bad);
        Assert.Equal(ComplianceBand.NonCompliant, receipt.Band); // Critical threat present
        Assert.Equal(20, receipt.Score);                         // 100 - 40 (DEF-006 Critical) - 20 (DEF-001 High) - 20 (FW-003 High)

        var findings = await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}");
        Assert.Contains(findings.Items, f => f.RuleKey == "DEF-006" && f.Severity == Severity.Critical);
        var rt = Assert.Single(findings.Items, f => f.RuleKey == "DEF-001");
        Assert.False(string.IsNullOrWhiteSpace(rt.Explanation.WhyItMatters));
        Assert.False(string.IsNullOrWhiteSpace(rt.Explanation.SeverityRationale));
        Assert.False(string.IsNullOrWhiteSpace(rt.Explanation.HowToFix));

        var alerts = await Get<PagedResult<AlertDto>>(admin.Client, $"{Base}/alerts?openOnly=true");
        Assert.True(alerts.Items.Count(a => a.EndpointId == e.Endpoint.Id) >= 3); // critical + 2 high

        var detail = await Get<EndpointDetailDto>(admin.Client, $"{Base}/endpoints/{e.Endpoint.Id}");
        Assert.NotEmpty(detail.ScoreReasons);
        Assert.NotEmpty(detail.ScorePenalties);
        Assert.Contains(detail.Checks, c => c.Outcome == CheckOutcome.Pass);
        Assert.NotNull(detail.Facts?.Defender);

        // A later clean report resolves findings (positive evidence) and closes the linked alerts.
        var clean = await ReportAsync(device, Healthy());
        Assert.Equal(100, clean.Score);
        Assert.Equal(ComplianceBand.Compliant, clean.Band);
        var open = await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}");
        Assert.Empty(open.Items);
        var resolved = await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}&status=Resolved");
        Assert.True(resolved.TotalCount >= 3);
        var stillOpen = await Get<PagedResult<AlertDto>>(admin.Client, $"{Base}/alerts?openOnly=true");
        Assert.DoesNotContain(stillOpen.Items, a => a.EndpointId == e.Endpoint.Id);
    }

    [Fact]
    public async Task Missing_data_never_resolves_an_open_finding()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });

        // The next report cannot read Defender at all.
        var blind = await ReportAsync(device, Healthy() with { Defender = null });
        var findings = await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}");
        Assert.Contains(findings.Items, f => f.RuleKey == "DEF-001" && f.Status == FindingStatus.Open);
        Assert.NotEqual(ComplianceBand.Compliant, blind.Band);
    }

    [Fact]
    public async Task Reports_faster_than_the_minimum_interval_are_rate_limited()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (_, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy());
        var second = await device.PostAsJsonAsync($"{Base}/ingest/posture", Healthy(), ApiFixture.Json);
        await HttpAssert.ProblemAsync(second, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.True(second.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Hostile_or_oversized_reports_are_rejected_without_side_effects()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);

        var tooMany = Healthy() with { Software = Enumerable.Range(0, PostureReport.MaxSoftwareItems + 1).Select(i => new InstalledSoftware($"app{i}", "1.0", null)).ToList() };
        await HttpAssert.ProblemAsync(await device.PostAsJsonAsync($"{Base}/ingest/posture", tooMany, ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var future = Healthy() with { CollectedAtUtc = DateTimeOffset.UtcNow.AddDays(2) };
        await HttpAssert.ProblemAsync(await device.PostAsJsonAsync($"{Base}/ingest/posture", future, ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var longName = Healthy() with { Software = [new(new string('x', 5000), "1", null)] };
        await HttpAssert.ProblemAsync(await device.PostAsJsonAsync($"{Base}/ingest/posture", longName, ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var detail = await Get<EndpointDetailDto>(admin.Client, $"{Base}/endpoints/{e.Endpoint.Id}");
        Assert.Null(detail.Summary.LastReportAtUtc);
    }

    [Fact]
    public async Task Only_allow_listed_events_are_stored_and_event_alerts_are_deduplicated()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);
        var events = new List<ReportedEvent>
        {
            new() { Log = "Security", EventId = 1102, RecordId = 10, TimeCreatedUtc = DateTimeOffset.UtcNow, Summary = "cleared\nby someone" },
            new() { Log = "Security", EventId = 4624, RecordId = 11, TimeCreatedUtc = DateTimeOffset.UtcNow, Summary = "logon (not allow-listed)" },
        };
        await ReportAsync(device, Healthy() with { Events = events });
        await ReportAsync(device, Healthy() with { Events = events }); // re-delivered

        var stored = await Get<PagedResult<SecurityEventDto>>(admin.Client, $"{Base}/events?endpointId={e.Endpoint.Id}");
        var only = Assert.Single(stored.Items);
        Assert.Equal(1102, only.EventId);
        Assert.DoesNotContain('\n', only.Summary!);

        var alerts = await Get<PagedResult<AlertDto>>(admin.Client, $"{Base}/alerts?endpointId={e.Endpoint.Id}");
        var alert = Assert.Single(alerts.Items, a => a.RuleKey == "EVT-001");
        Assert.Equal(Severity.Critical, alert.Severity);

        // Triage workflow.
        var ack = await HttpAssert.OkAsync<AlertDto>(await admin.Client.PostAsync($"{Base}/alerts/{alert.Id}/acknowledge", null));
        Assert.Equal(AlertStatus.Acknowledged, ack.Status);
        await HttpAssert.ProblemAsync(await admin.Client.PostAsync($"{Base}/alerts/{alert.Id}/dismiss", Json(new { note = "" })), HttpStatusCode.BadRequest, "validation_failed");
        var dismissed = await HttpAssert.OkAsync<AlertDto>(await admin.Client.PostAsync($"{Base}/alerts/{alert.Id}/dismiss", Json(new { note = "planned maintenance" })));
        Assert.Equal(AlertStatus.Dismissed, dismissed.Status);
    }

    [Fact]
    public async Task Vulnerable_software_advisories_drive_findings_and_re_evaluation()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy() with { Software = [new("Contoso Reader", "1.0.0", "Contoso")] });
        Assert.Empty((await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}&ruleKey=SW-001")).Items);

        // Adding an advisory re-evaluates the stored report immediately, without waiting for the next check-in.
        var advisory = await HttpAssert.OkAsync<AdvisoryDto>(
            await admin.Client.PostAsJsonAsync($"{Base}/advisories", new SaveAdvisoryRequest("Reader RCE", "Contoso Reader", null, null, "2.0.0", Severity.Critical, "CVE-2000-0001"), ApiFixture.Json),
            HttpStatusCode.Created);
        var finding = Assert.Single((await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}&ruleKey=SW-001")).Items);
        Assert.Equal(Severity.Critical, finding.Severity);

        await HttpAssert.OkAsync<AdvisoryDto>(await admin.Client.PostAsync($"{Base}/advisories/{advisory.Id}/disable", null));
        Assert.Empty((await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}&ruleKey=SW-001")).Items);

        // Over-broad rules are refused.
        await HttpAssert.ProblemAsync(
            await admin.Client.PostAsJsonAsync($"{Base}/advisories", new SaveAdvisoryRequest("x", "a", null, null, "2.0", Severity.High, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Accepting_the_risk_of_a_serious_finding_needs_an_owner_and_is_time_boxed()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        using var owner = fixture.CreateClient(t.Token);
        var (e, device) = await EnrollAsync(admin.Client);
        var receipt = await ReportAsync(device, Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });
        Assert.Equal(80, receipt.Score);
        var finding = Assert.Single((await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}")).Items);

        var until = DateTimeOffset.UtcNow.AddDays(30);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.PostAsJsonAsync($"{Base}/findings/{finding.Id}/accept-risk", new AcceptRiskRequest("because", until), ApiFixture.Json)).StatusCode);
        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync($"{Base}/findings/{finding.Id}/accept-risk", new AcceptRiskRequest("because", DateTimeOffset.UtcNow.AddDays(400)), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        var accepted = await HttpAssert.OkAsync<FindingDto>(await owner.PostAsJsonAsync($"{Base}/findings/{finding.Id}/accept-risk", new AcceptRiskRequest("compensating control", until), ApiFixture.Json));
        Assert.Equal(FindingStatus.Accepted, accepted.Status);

        var next = await ReportAsync(device, Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });
        Assert.Equal(100, next.Score); // accepted risk is visible but not scored
    }

    // ------------------------------------------------------------------ policy & approval

    [Fact]
    public async Task Policy_needs_a_different_owner_to_approve_and_is_immutable_once_submitted()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var admin2 = await AddMemberAsync(t, TenantRole.Admin);

        var draft = await HttpAssert.OkAsync<PolicyDto>(await admin.Client.PostAsJsonAsync($"{Base}/policies", new SavePolicyRequest("P", null, PolicySettings.Default), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin2.Client.PutAsJsonAsync($"{Base}/policies/{draft.Id}", new SavePolicyRequest("P2", null, PolicySettings.Default), ApiFixture.Json)).StatusCode);
        await HttpAssert.OkAsync<PolicyDto>(await admin.Client.PostAsync($"{Base}/policies/{draft.Id}/submit", null));
        await HttpAssert.ProblemAsync(await admin.Client.PutAsJsonAsync($"{Base}/policies/{draft.Id}", new SavePolicyRequest("P2", null, PolicySettings.Default), ApiFixture.Json), HttpStatusCode.Conflict, "conflict");

        // Admins cannot approve at all; the author (admin) cannot either.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin2.Client.PostAsJsonAsync($"{Base}/policies/{draft.Id}/approve", new NoteRequest(null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.PostAsJsonAsync($"{Base}/policies/{draft.Id}/approve", new NoteRequest(null), ApiFixture.Json)).StatusCode);
        var approved = await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsJsonAsync($"{Base}/policies/{draft.Id}/approve", new NoteRequest("reviewed"), ApiFixture.Json));
        Assert.Equal(PolicyStatus.Active, approved.Status);

        // An owner-authored policy cannot be approved by that same owner.
        var own = await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsJsonAsync($"{Base}/policies", new SavePolicyRequest("Own", null, PolicySettings.Default), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsync($"{Base}/policies/{own.Id}/submit", null));
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/policies/{own.Id}/approve", new NoteRequest(null), ApiFixture.Json), HttpStatusCode.Forbidden, "forbidden");

        // Invalid settings never become a policy.
        var bad = await admin.Client.PostAsJsonAsync($"{Base}/policies", new SavePolicyRequest("Bad", null, PolicySettings.Default with { MaxSignatureAgeDays = 0 }), ApiFixture.Json);
        await HttpAssert.ProblemAsync(bad, HttpStatusCode.BadRequest, "validation_failed");

        // Approving a new version retires the old one; exactly one stays active.
        var rejected = await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsJsonAsync($"{Base}/policies/{own.Id}/reject", new NoteRequest("not now"), ApiFixture.Json));
        Assert.Equal(PolicyStatus.Rejected, rejected.Status);
        var v2 = await HttpAssert.OkAsync<PolicyDto>(await admin2.Client.PostAsync($"{Base}/policies/{approved.Id}/revise", null), HttpStatusCode.Created);
        await HttpAssert.OkAsync<PolicyDto>(await admin2.Client.PostAsync($"{Base}/policies/{v2.Id}/submit", null));
        await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsJsonAsync($"{Base}/policies/{v2.Id}/approve", new NoteRequest(null), ApiFixture.Json));
        var all = await Get<List<PolicyDto>>(owner, $"{Base}/policies");
        Assert.Single(all, p => p.Status == PolicyStatus.Active);
        Assert.Contains(all, p => p.Id == approved.Id && p.Status == PolicyStatus.Retired);
    }

    // ------------------------------------------------------------------ remediation: detection is not remediation

    [Fact]
    public async Task Detect_only_is_a_hard_stop_for_remediation()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy() with { Defender = Healthy().Defender! with { SignatureAgeDays = 30 } });

        var request = new CreateRemediationRequest(e.Endpoint.Id, null, "defender.update_signatures", null, "signatures are stale");
        await HttpAssert.ProblemAsync(await admin.Client.PostAsJsonAsync($"{Base}/remediation", request, ApiFixture.Json), HttpStatusCode.Conflict, "conflict");

        // Nothing was queued for the agent either.
        var commands = await Get<List<AgentCommandDto>>(device, $"{Base}/ingest/commands");
        Assert.Empty(commands);
        var all = await Get<PagedResult<RemediationDto>>(admin.Client, $"{Base}/remediation");
        Assert.Empty(all.Items);
    }

    [Fact]
    public async Task Approved_remediation_reaches_the_agent_once_as_a_catalogue_key_and_is_audited()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        await ActivatePolicyAsync(admin.Client, owner, Enforcing("firewall.enable_profile", "defender.enable_realtime"));
        var (e, device) = await EnrollAsync(admin.Client);
        var fwOff = new FirewallStatus { Profiles = [new("Domain", true, "Block"), new("Private", true, "Block"), new("Public", false, "Block")] };
        await ReportAsync(device, Healthy() with { Firewall = fwOff });

        // The policy proposed it, but proposing is not approving.
        var pending = await Get<PagedResult<RemediationDto>>(admin.Client, $"{Base}/remediation?pendingOnly=true");
        var proposed = Assert.Single(pending.Items);
        Assert.True(proposed.RequestedBySystem);
        Assert.Equal("Public", proposed.Parameter);
        Assert.Empty(await Get<List<AgentCommandDto>>(device, $"{Base}/ingest/commands"));

        // Disallowed action, unknown action and injected parameters are refused.
        await HttpAssert.ProblemAsync(await admin.Client.PostAsJsonAsync($"{Base}/remediation", new CreateRemediationRequest(e.Endpoint.Id, null, "config.disable_smb1", null, "x"), ApiFixture.Json), HttpStatusCode.Conflict);
        await HttpAssert.ProblemAsync(await admin.Client.PostAsJsonAsync($"{Base}/remediation", new CreateRemediationRequest(e.Endpoint.Id, null, "powershell.run", "calc", "x"), ApiFixture.Json), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(await admin.Client.PostAsJsonAsync($"{Base}/remediation", new CreateRemediationRequest(e.Endpoint.Id, null, "firewall.enable_profile", "Public; whoami", "x"), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        // A requester cannot approve their own request; a different approver can.
        var mine = await HttpAssert.OkAsync<RemediationDto>(
            await admin.Client.PostAsJsonAsync($"{Base}/remediation", new CreateRemediationRequest(e.Endpoint.Id, null, "defender.enable_realtime", null, "RTP is off"), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.PostAsJsonAsync($"{Base}/remediation/{mine.Id}/approve", new NoteRequest(null), ApiFixture.Json)).StatusCode);
        await HttpAssert.OkAsync<RemediationDto>(await owner.PostAsJsonAsync($"{Base}/remediation/{mine.Id}/approve", new NoteRequest("go"), ApiFixture.Json));
        await HttpAssert.OkAsync<RemediationDto>(await owner.PostAsJsonAsync($"{Base}/remediation/{proposed.Id}/approve", new NoteRequest("go"), ApiFixture.Json));
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/remediation/{proposed.Id}/approve", new NoteRequest("again"), ApiFixture.Json), HttpStatusCode.Conflict);

        // The agent receives only {id, action, parameter, expiry} and only once.
        var raw = await (await device.GetAsync($"{Base}/ingest/commands")).Content.ReadAsStringAsync();
        var commands = JsonSerializer.Deserialize<List<AgentCommandDto>>(raw, ApiFixture.Json)!;
        Assert.Equal(2, commands.Count);
        Assert.Contains(commands, c => c.Action == "firewall.enable_profile" && c.Parameter == "Public");
        using (var doc = JsonDocument.Parse(raw))
        {
            Assert.All(doc.RootElement.EnumerateArray(), el => Assert.Equal(4, el.EnumerateObject().Count()));
        }

        Assert.Empty(await Get<List<AgentCommandDto>>(device, $"{Base}/ingest/commands"));
        var cmd = commands.First(c => c.Action == "firewall.enable_profile");
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"{Base}/ingest/commands/{cmd.Id}/result", new CommandResultRequest(true, "done"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"{Base}/ingest/commands/{cmd.Id}/result", new CommandResultRequest(true, "again"), ApiFixture.Json)).StatusCode);

        // Another tenant's device cannot report on this command.
        var other = await fixture.RegisterTenantAsync();
        var otherAdmin = await AddMemberAsync(other, TenantRole.Admin);
        var (_, otherDevice) = await EnrollAsync(otherAdmin.Client);
        Assert.Equal(HttpStatusCode.NotFound, (await otherDevice.PostAsJsonAsync($"{Base}/ingest/commands/{cmd.Id}/result", new CommandResultRequest(true, "x"), ApiFixture.Json)).StatusCode);

        // Success does not resolve the finding: only the next report can.
        Assert.Contains((await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings?endpointId={e.Endpoint.Id}")).Items, f => f.RuleKey == "FW-003");

        var audit = await Get<PagedResult<AuditEntryDto>>(owner, $"{Base}/audit?pageSize=200");
        foreach (var action in new[] { "policy.approved", "remediation.requested", "remediation.approved", "remediation.dispatched", "remediation.completed", "endpoint.enrolled" })
        {
            Assert.Contains(audit.Items, a => a.Action == action);
        }

        var approval = audit.Items.First(a => a.Action == "remediation.approved");
        Assert.Equal(AuditActorType.User, approval.ActorType);
        Assert.Equal(AuditActorType.Device, audit.Items.First(a => a.Action == "remediation.dispatched").ActorType);
        Assert.Equal(AuditActorType.System, audit.Items.First(a => a.Action == "remediation.requested" && a.ActorType == AuditActorType.System).ActorType);
    }

    [Fact]
    public async Task High_risk_remediation_needs_an_owner_and_low_risk_can_be_auto_approved_only_by_policy()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var admin2 = await AddMemberAsync(t, TenantRole.Admin);
        var settings = PolicySettings.Default with
        {
            EnforcementMode = EnforcementMode.AutoApproveLowRisk,
            AllowedRemediationActions = ["config.disable_smb1", "defender.update_signatures"],
        };
        await ActivatePolicyAsync(admin.Client, owner, settings);
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy() with
        {
            Configuration = Healthy().Configuration! with { Smb1ServerEnabled = true },
            Defender = Healthy().Defender! with { SignatureAgeDays = 20 },
        });

        var all = (await Get<PagedResult<RemediationDto>>(admin.Client, $"{Base}/remediation")).Items;
        var low = Assert.Single(all, r => r.ActionKey == "defender.update_signatures");
        Assert.True(low.AutoApproved);
        Assert.Equal(RemediationStatus.Approved, low.Status);
        var high = Assert.Single(all, r => r.ActionKey == "config.disable_smb1");
        Assert.False(high.AutoApproved);
        Assert.Equal(RemediationStatus.PendingApproval, high.Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin2.Client.PostAsJsonAsync($"{Base}/remediation/{high.Id}/approve", new NoteRequest(null), ApiFixture.Json)).StatusCode);
        await HttpAssert.OkAsync<RemediationDto>(await owner.PostAsJsonAsync($"{Base}/remediation/{high.Id}/approve", new NoteRequest(null), ApiFixture.Json));
        _ = e;
    }

    [Fact]
    public async Task Approval_is_not_a_standing_licence_policy_must_still_permit_at_delivery()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var admin2 = await AddMemberAsync(t, TenantRole.Admin);
        var first = await ActivatePolicyAsync(admin.Client, owner, Enforcing("defender.quick_scan"));
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy());
        var req = await HttpAssert.OkAsync<RemediationDto>(
            await admin.Client.PostAsJsonAsync($"{Base}/remediation", new CreateRemediationRequest(e.Endpoint.Id, null, "defender.quick_scan", null, "scan"), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.OkAsync<RemediationDto>(await owner.PostAsJsonAsync($"{Base}/remediation/{req.Id}/approve", new NoteRequest(null), ApiFixture.Json));

        // The tenant moves back to detect-only before the agent checks in.
        var v2 = await HttpAssert.OkAsync<PolicyDto>(await admin2.Client.PostAsJsonAsync($"{Base}/policies", new SavePolicyRequest("Detect only", null, PolicySettings.Default), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.OkAsync<PolicyDto>(await admin2.Client.PostAsync($"{Base}/policies/{v2.Id}/submit", null));
        await HttpAssert.OkAsync<PolicyDto>(await owner.PostAsJsonAsync($"{Base}/policies/{v2.Id}/approve", new NoteRequest(null), ApiFixture.Json));

        Assert.Empty(await Get<List<AgentCommandDto>>(device, $"{Base}/ingest/commands"));
        var after = await Get<RemediationDto>(admin.Client, $"{Base}/remediation/{req.Id}");
        Assert.Equal(RemediationStatus.Cancelled, after.Status);
        _ = first;
    }

    // ------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task One_tenant_cannot_see_or_touch_another_tenants_security_data()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var adminA = await AddMemberAsync(a, TenantRole.Admin);
        var adminB = await AddMemberAsync(b, TenantRole.Admin);
        var (ea, deviceA) = await EnrollAsync(adminA.Client, "A-PC");
        await ReportAsync(deviceA, Healthy("A-PC") with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });
        var findingA = Assert.Single((await Get<PagedResult<FindingDto>>(adminA.Client, $"{Base}/findings")).Items);
        var advisory = await HttpAssert.OkAsync<AdvisoryDto>(await adminA.Client.PostAsJsonAsync($"{Base}/advisories", new SaveAdvisoryRequest("t", "abc", null, null, "1.0", Severity.Low, null), ApiFixture.Json), HttpStatusCode.Created);

        Assert.Empty((await Get<PagedResult<EndpointSummaryDto>>(adminB.Client, $"{Base}/endpoints")).Items);
        Assert.Empty((await Get<PagedResult<FindingDto>>(adminB.Client, $"{Base}/findings")).Items);
        Assert.Empty((await Get<PagedResult<AlertDto>>(adminB.Client, $"{Base}/alerts")).Items);
        Assert.Empty(await Get<List<AdvisoryDto>>(adminB.Client, $"{Base}/advisories"));
        Assert.DoesNotContain((await Get<PagedResult<AuditEntryDto>>(adminB.Client, $"{Base}/audit")).Items, x => x.EntityId == ea.Endpoint.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.Client.GetAsync($"{Base}/endpoints/{ea.Endpoint.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.Client.GetAsync($"{Base}/findings/{findingA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.Client.PostAsync($"{Base}/endpoints/{ea.Endpoint.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.Client.DeleteAsync($"{Base}/endpoints/{ea.Endpoint.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.Client.DeleteAsync($"{Base}/advisories/{advisory.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.Client.PostAsJsonAsync($"{Base}/findings/{findingA.Id}/accept-risk", new AcceptRiskRequest("x", DateTimeOffset.UtcNow.AddDays(1)), ApiFixture.Json)).StatusCode);

        // A forged X-Tenant-Id header is rejected, not honoured.
        using var forged = fixture.CreateClient(adminB.Client.DefaultRequestHeaders.Authorization!.Parameter);
        forged.DefaultRequestHeaders.Add("X-Tenant-Id", a.TenantId.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.GetAsync($"{Base}/endpoints")).StatusCode);

        // The summary only counts the caller's devices.
        Assert.Equal(1, (await Get<SecuritySummaryDto>(adminA.Client, $"{Base}/summary")).TotalDevices);
        Assert.Equal(0, (await Get<SecuritySummaryDto>(adminB.Client, $"{Base}/summary")).TotalDevices);
    }

    [Fact]
    public async Task Row_level_security_protects_every_security_table_from_raw_sql()
    {
        var a = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(a, TenantRole.Admin);
        var (_, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });

        var tables = Solvix.Infrastructure.Persistence.Migrations.Phase10SecurityRlsMigration.TenantTables
            .Concat(Solvix.Infrastructure.Persistence.Migrations.Phase10SecurityRlsMigration.AppendOnlyTables).ToList();

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        foreach (var table in tables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", Guid.NewGuid().ToString());
            await set.ExecuteNonQueryAsync();
        }

        foreach (var table in tables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", a.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var own = new NpgsqlCommand("SELECT count(*) FROM security_endpoints", connection);
        Assert.Equal(1L, (long)(await own.ExecuteScalarAsync())!);
    }

    // ------------------------------------------------------------------ audit trail integrity

    [Fact]
    public async Task Audit_trail_is_append_only_even_for_the_schema_owner()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        await EnrollAsync(admin.Client);
        var audit = await Get<PagedResult<AuditEntryDto>>(admin.Client, $"{Base}/audit");
        var entry = Assert.Single(audit.Items, a => a.Action == "endpoint.enrolled");
        Assert.Equal(admin.Email, entry.ActorLabel);
        Assert.Equal(admin.UserId, entry.ActorId);

        foreach (var connectionString in new[] { fixture.Database.AppConnectionString, fixture.Database.MigratorConnectionString })
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
            {
                set.Parameters.AddWithValue("t", t.TenantId.ToString());
                await set.ExecuteNonQueryAsync();
            }

            // Row-level security has no UPDATE/DELETE policy on this table, so these match nothing.
            foreach (var sql in new[] { "UPDATE security_audit SET summary = 'tampered'", "DELETE FROM security_audit" })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                try
                {
                    // Schema owner: no UPDATE/DELETE policy, so nothing is visible to change.
                    Assert.Equal(0, await command.ExecuteNonQueryAsync());
                }
                catch (PostgresException denied)
                {
                    // Runtime role: the privilege itself was revoked.
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
                }
            }

            await using var truncate = new NpgsqlCommand("TRUNCATE security_audit", connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // Backstop: even a superuser, who bypasses row-level security, is stopped by the trigger.
        var adminConnection = Environment.GetEnvironmentVariable("SOLVIX_TEST_PG_ADMIN") ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        var adminOnTestDb = new NpgsqlConnectionStringBuilder(adminConnection) { Database = fixture.Database.DatabaseName }.ConnectionString;
        await using (var superuser = new NpgsqlConnection(adminOnTestDb))
        {
            await superuser.OpenAsync();
            foreach (var sql in new[] { "UPDATE security_audit SET summary = 'tampered'", "DELETE FROM security_audit", "TRUNCATE security_audit" })
            {
                await using var command = new NpgsqlCommand(sql, superuser);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            }
        }

        var again = await Get<PagedResult<AuditEntryDto>>(admin.Client, $"{Base}/audit");
        Assert.DoesNotContain(again.Items, a => a.Summary == "tampered");
        Assert.Contains(again.Items, a => a.Id == entry.Id);
    }

    [Fact]
    public async Task Policy_changes_record_before_and_after_in_the_audit_trail()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var draft = await HttpAssert.OkAsync<PolicyDto>(await admin.Client.PostAsJsonAsync($"{Base}/policies", new SavePolicyRequest("P", null, PolicySettings.Default), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.OkAsync<PolicyDto>(await admin.Client.PutAsJsonAsync($"{Base}/policies/{draft.Id}", new SavePolicyRequest("P", null, PolicySettings.Default with { MaxSignatureAgeDays = 5 }), ApiFixture.Json));
        var entries = await Get<PagedResult<AuditEntryDto>>(admin.Client, $"{Base}/audit?action=policy.updated&entityId={draft.Id}");
        var updated = Assert.Single(entries.Items);
        Assert.Contains("\"before\"", updated.Details, StringComparison.Ordinal);
        Assert.Contains("\"after\"", updated.Details, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ reporting

    [Fact]
    public async Task Reports_summarise_the_fleet_neutralise_spreadsheet_formulas_and_audit_the_export()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (_, device) = await EnrollAsync(admin.Client, "=cmd|' /C calc'!A0");
        await ReportAsync(device, Healthy("=cmd|' /C calc'!A0") with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });
        var (_, device2) = await EnrollAsync(admin.Client, "PC-OK");
        await ReportAsync(device2, Healthy("PC-OK"));
        await EnrollAsync(admin.Client, "PC-NEW");

        var summary = await Get<SecuritySummaryDto>(admin.Client, $"{Base}/summary");
        Assert.Equal(3, summary.TotalDevices);
        Assert.Equal(2, summary.ReportingDevices);
        Assert.Equal(1, summary.Bands[ComplianceBand.Compliant]);
        Assert.Equal(1, summary.Bands[ComplianceBand.AtRisk]);
        Assert.Equal(1, summary.Bands[ComplianceBand.Unknown]);
        Assert.Equal(90, summary.AverageScore);
        Assert.Contains(summary.TopRules, r => r.RuleKey == "DEF-001");
        Assert.True(summary.OpenAlerts >= 1);
        Assert.False(summary.EnforcementEnabled);
        Assert.NotEmpty(summary.Trend);

        var csv = await (await admin.Client.GetAsync($"{Base}/reports/devices.csv")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("\n=cmd", csv, StringComparison.Ordinal);
        Assert.Contains("'=cmd", csv, StringComparison.Ordinal);
        var findingsCsv = await (await admin.Client.GetAsync($"{Base}/reports/findings.csv")).Content.ReadAsStringAsync();
        Assert.Contains("DEF-001", findingsCsv, StringComparison.Ordinal);

        var audit = await Get<PagedResult<AuditEntryDto>>(admin.Client, $"{Base}/audit?action=report.exported");
        Assert.Equal(2, audit.TotalCount);
    }

    [Fact]
    public async Task Rules_endpoint_explains_severity_scoring_and_states_what_the_product_is_not()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var rules = await Get<RulesCatalogDto>(admin.Client, $"{Base}/rules");
        Assert.True(rules.Rules.Count >= 25);
        Assert.Equal(5, rules.Severities.Count);
        Assert.Contains("not antivirus", rules.ProductScope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EDR", rules.ProductScope, StringComparison.Ordinal);
        Assert.All(rules.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.HowToFix)));
        Assert.DoesNotContain(rules.RemediationActions, a => a.Key.Contains("bitlocker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Deleting_a_device_removes_its_history_but_keeps_the_audit_trail()
    {
        var t = await fixture.RegisterTenantAsync();
        var admin = await AddMemberAsync(t, TenantRole.Admin);
        var (e, device) = await EnrollAsync(admin.Client);
        await ReportAsync(device, Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"{Base}/endpoints/{e.Endpoint.Id}")).StatusCode);
        Assert.Empty((await Get<PagedResult<FindingDto>>(admin.Client, $"{Base}/findings")).Items);
        Assert.Empty((await Get<PagedResult<AlertDto>>(admin.Client, $"{Base}/alerts")).Items);
        var audit = await Get<PagedResult<AuditEntryDto>>(admin.Client, $"{Base}/audit?entityId={e.Endpoint.Id}");
        Assert.Contains(audit.Items, a => a.Action == "endpoint.deleted");
        Assert.Contains(audit.Items, a => a.Action == "endpoint.enrolled");
    }
}

/// <summary>Shared healthy report for tests (mirrors the unit-test fixture).</summary>
internal static class PostureEvaluatorFixtures
{
    public static PostureReport Healthy() => new()
    {
        CollectedAtUtc = DateTimeOffset.UtcNow,
        Defender = new DefenderStatus
        {
            AMRunningMode = "Normal", AntivirusEnabled = true, RealTimeProtectionEnabled = true, TamperProtected = true,
            SignatureAgeDays = 1, QuickScanAgeDays = 2, ActiveThreatCount = 0, ThirdPartyAntivirusRegistered = false,
        },
        Firewall = new FirewallStatus { Profiles = [new("Domain", true, "Block"), new("Private", true, "Block"), new("Public", true, "Block")] },
        BitLocker = new BitLockerStatus { Available = true, TpmPresent = true, Volumes = [new("C:", "OperatingSystem", true, 100, "XtsAes256", "FullyEncrypted")] },
        LocalAdmins = new LocalAdminStatus { Members = [new("PC\\admin", "User", "S-1-5-21-1-1-1-1001")], BuiltInAdministratorEnabled = false, GuestAccountEnabled = false },
        Usb = new UsbStatus { StorageDriverDisabled = false, DenyRead = false, DenyWrite = false },
        Software = [new("Contoso Reader", "2.0.0", "Contoso")],
        Configuration = new ConfigurationStatus { Smb1ServerEnabled = false, UacEnabled = true, RdpEnabled = false, SecureBootEnabled = true, WindowsUpdateDisabledByPolicy = false },
    };
}
