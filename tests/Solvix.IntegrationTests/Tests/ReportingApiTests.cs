namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.Modules.Reporting.Application;
using Solvix.SharedKernel.Identity;

[Collection(ApiCollection.Name)]
public sealed class ReportingApiTests(ApiFixture fixture)
{
    private static readonly string[] AllReports =
    [
        "device-inventory", "device-availability", "asset-lifecycle", "software-inventory",
        "patch-compliance", "security-compliance", "deployment", "technician-activity",
    ];

    private static object? Metric(ReportResult r, string label) =>
        r.Summary.Single(m => m.Label == label).Value is System.Text.Json.JsonElement e ? e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => e.GetDouble(),
            System.Text.Json.JsonValueKind.String => e.GetString(),
            _ => null,
        } : null;

    private static int Col(ReportResult r, string key) => r.Columns.ToList().FindIndex(c => c.Key == key);

    private static string? Cell(ReportResult r, int row, string key)
    {
        var v = ((System.Text.Json.JsonElement)r.Rows[row][Col(r, key)]!);
        return v.ValueKind == System.Text.Json.JsonValueKind.Null ? null : v.ToString();
    }

    private async Task<ReportResult> RunAsync(HttpClient client, string key, string query = "") =>
        await HttpAssert.OkAsync<ReportResult>(await client.GetAsync($"/api/v1/reports/{key}{query}"));

    [Fact]
    public async Task Every_report_runs_on_an_empty_workspace()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var catalog = await HttpAssert.OkAsync<List<ReportInfo>>(await owner.GetAsync("/api/v1/reports"));
        Assert.Equal(AllReports.Order().ToArray(), catalog.Select(r => r.Key).Order().ToArray());
        foreach (var key in AllReports)
        {
            var report = await RunAsync(owner, key);
            Assert.Equal(key, report.Report);
            Assert.NotEmpty(report.Columns);
            Assert.NotEmpty(report.Summary);
            Assert.False(report.Truncated);
        }
    }

    [Fact]
    public async Task Reports_are_admin_only()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var member = await fixture.AddRoleMemberAsync(tenant);
        using var client = fixture.CreateClient(member.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/reports")).StatusCode);
        foreach (var key in AllReports)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/reports/{key}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/reports/{key}?format=csv")).StatusCode);
        }

        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/reports/deployment")).StatusCode);

        using var owner = fixture.CreateClient(tenant.Token);
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/reports/not-a-report"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Inventory_availability_and_deployment_reports_reflect_the_fleet()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var d1 = await fixture.EnrollNewDeviceAsync(tenant, "ALPHA-01");
        var d2 = await fixture.EnrollNewDeviceAsync(tenant, "BETA-02");
        await d1.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("ALPHA-01"));
        using var owner = fixture.CreateClient(tenant.Token);
        await owner.PostAsync($"/api/v1/devices/{d2.DeviceId}/retire", null);
        await fixture.SimulateSilenceAsync(d1.DeviceId, TimeSpan.FromDays(3)); // live monitoring (Phase 04) marks it offline

        var inventory = await RunAsync(owner, "device-inventory");
        Assert.Equal(2d, Metric(inventory, "Devices"));
        Assert.Equal(1d, Metric(inventory, "Active"));
        Assert.Equal(1d, Metric(inventory, "Retired"));
        Assert.Equal("ALPHA-01", Cell(inventory, 0, "hostname"));
        Assert.Equal("Offline", Cell(inventory, 0, "connectivity"));
        Assert.Equal("16", Cell(inventory, 0, "ramGb"));

        var filtered = await RunAsync(owner, "device-inventory", "?search=beta");
        Assert.Equal("BETA-02", Assert.Single(filtered.Rows.Select((_, i) => Cell(filtered, i, "hostname"))));

        var availability = await RunAsync(owner, "device-availability");
        Assert.Equal(1d, Metric(availability, "Active devices"));
        Assert.Equal(1d, Metric(availability, "Offline now"));
        Assert.Equal("Offline < 7 days", Cell(availability, 0, "bucket"));

        var deployment = await RunAsync(owner, "deployment");
        Assert.Equal(1d, Metric(deployment, "Active devices"));
        Assert.Equal(1d, Metric(deployment, "Pending an agent update")); // enrolled agent 1.0.0 < latest release 1.2.0
        Assert.Equal(1d, Metric(deployment, "Agent 1.0.0"));
    }

    [Fact]
    public async Task Availability_trend_is_built_from_hourly_samples()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var d1 = await fixture.EnrollNewDeviceAsync(tenant);
        var d2 = await fixture.EnrollNewDeviceAsync(tenant);
        await fixture.SimulateSilenceAsync(d2.DeviceId, TimeSpan.FromHours(2)); // live monitoring (Phase 04) marks it offline
        using var agent = fixture.CreateDeviceClient(d1.Certificate);
        await agent.HeartbeatAsync();
        await fixture.FlushPresenceAsync();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAvailabilityJobRunner>().RunAsync(default);
            await scope.ServiceProvider.GetRequiredService<IAvailabilityJobRunner>().RunAsync(default); // same hour: upsert, still one sample
        }

        using var owner = fixture.CreateClient(tenant.Token);
        var report = await RunAsync(owner, "device-availability", "?days=7");
        Assert.Equal(1d, Metric(report, "Trend samples (hourly)"));
        Assert.Equal(50d, Metric(report, "Average availability, last 7 days (%)"));
    }

    [Fact]
    public async Task Software_inventory_groups_applications_across_devices()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var d1 = await fixture.EnrollNewDeviceAsync(tenant);
        var d2 = await fixture.EnrollNewDeviceAsync(tenant);
        await d1.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("H1", appName: "Zebra Tool"));
        await d2.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("H2", appName: "Zebra Tool"));
        using var owner = fixture.CreateClient(tenant.Token);

        var report = await RunAsync(owner, "software-inventory");
        Assert.Equal(2d, Metric(report, "Devices with an inventory"));
        var rows = Enumerable.Range(0, report.Rows.Count).Select(i => (Name: Cell(report, i, "name"), Devices: Cell(report, i, "devices"))).ToList();
        Assert.Contains(("Zebra Tool", "2"), rows);
        Assert.Contains(("Microsoft Edge", "2"), rows);

        var search = await RunAsync(owner, "software-inventory", "?search=zebra");
        Assert.Equal("Zebra Tool", Assert.Single(Enumerable.Range(0, search.Rows.Count).Select(i => Cell(search, i, "name"))));
        // LIKE wildcards in the search are literal.
        Assert.Empty((await RunAsync(owner, "software-inventory", "?search=%25")).Rows);
    }

    [Fact]
    public async Task Patch_and_security_compliance_flag_the_right_devices()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var good = await fixture.EnrollNewDeviceAsync(tenant, "GOOD");
        var behind = await fixture.EnrollNewDeviceAsync(tenant, "BEHIND");
        var risky = await fixture.EnrollNewDeviceAsync(tenant, "RISKY");
        // Current agent but no inventory yet: nothing failed, but facts are missing -> "Unknown", never "Compliant".
        var unknown = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), DeviceTestKit.Identity(hostname: "NOINV") with { AgentVersion = SolvixApiFactory.LatestAgentVersion });
        await good.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("GOOD", revision: 4460));
        await behind.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("BEHIND", revision: 3000));
        await risky.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("RISKY", secureBoot: false, bitLocker: false, osSupported: false, revision: 4460));
        using (var agent = fixture.CreateDeviceClient(good.Certificate))
        {
            await agent.HeartbeatAsync();
        }

        using var owner = fixture.CreateClient(tenant.Token);
        var patch = await RunAsync(owner, "patch-compliance");
        string State(string host) => Enumerable.Range(0, patch.Rows.Count).Select(i => (Cell(patch, i, "hostname"), Cell(patch, i, "state"))).Single(x => x.Item1 == host).Item2!;
        Assert.Equal("Current", State("GOOD"));
        Assert.Equal("Behind", State("BEHIND"));
        Assert.Equal("Unsupported OS", State("RISKY"));
        Assert.Equal(1d, Metric(patch, "Current"));
        Assert.Equal(1d, Metric(patch, "Behind"));
        Assert.NotNull(patch.Notes);

        var security = await RunAsync(owner, "security-compliance");
        string Result(string host) => Enumerable.Range(0, security.Rows.Count).Select(i => (Cell(security, i, "hostname"), Cell(security, i, "result"))).Single(x => x.Item1 == host).Item2!;
        Assert.Equal("Non-compliant", Result("RISKY"));
        Assert.Equal("Unknown", Result("NOINV"));
        Assert.Equal(1d, Metric(security, "Unsupported OS"));
        Assert.Equal(1d, Metric(security, "Secure Boot off"));
        Assert.Equal(1d, Metric(security, "Disk not fully encrypted"));
        Assert.NotEqual("Compliant", Result("NOINV"));
        Assert.Equal("Non-compliant", Result("GOOD")); // agent 1.0.0 is behind the 1.2.0 release
        Assert.Equal(3d, Metric(security, "Agent out of date"));
        _ = unknown;
    }

    [Fact]
    public async Task Asset_lifecycle_uses_administrator_entered_dates()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollNewDeviceAsync(tenant, "OLDBOX");
        var fresh = await fixture.EnrollNewDeviceAsync(tenant, "NEWBOX");
        using var owner = fixture.CreateClient(tenant.Token);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var updated = await HttpAssert.OkAsync<DeviceDetailsDto>(await owner.PutAsJsonAsync($"/api/v1/devices/{device.DeviceId}/lifecycle", new UpdateDeviceLifecycleRequest(today.AddYears(-6), today.AddYears(-3)), ApiFixture.Json));
        Assert.Equal(today.AddYears(-3), updated.WarrantyExpiresOn);
        await owner.PutAsJsonAsync($"/api/v1/devices/{fresh.DeviceId}/lifecycle", new UpdateDeviceLifecycleRequest(today.AddMonths(-2), today.AddDays(30)), ApiFixture.Json);
        await HttpAssert.ProblemAsync(await owner.PutAsJsonAsync($"/api/v1/devices/{device.DeviceId}/lifecycle", new UpdateDeviceLifecycleRequest(today, today.AddDays(-1)), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var report = await RunAsync(owner, "asset-lifecycle");
        string Get(string host, string col) => Enumerable.Range(0, report.Rows.Count).Select(i => (Cell(report, i, "hostname"), Cell(report, i, col))).Single(x => x.Item1 == host).Item2!;
        Assert.Equal("Expired", Get("OLDBOX", "warranty"));
        Assert.Equal("Refresh candidate", Get("OLDBOX", "recommendation"));
        Assert.Equal("Purchase date", Get("OLDBOX", "ageBasis"));
        Assert.Equal("Expiring within 90 days", Get("NEWBOX", "warranty"));
        Assert.Equal("OK", Get("NEWBOX", "recommendation"));
        Assert.Equal(1d, Metric(report, "Warranty expired"));
        Assert.Equal(1d, Metric(report, "Warranty expiring within 90 days"));
    }

    [Fact]
    public async Task Technician_activity_summarises_resolutions_and_sla_outcomes()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var member = await fixture.AddRoleMemberAsync(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        using var tech = fixture.CreateClient(member.Token);
        var technician = await HttpAssert.OkAsync<TechnicianDto>(await owner.PostAsJsonAsync("/api/v1/helpdesk/technicians", new CreateTechnicianRequest(member.UserId, 10, null, null), ApiFixture.Json), HttpStatusCode.Created);

        for (var i = 0; i < 3; i++)
        {
            var created = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.PostAsJsonAsync("/api/v1/helpdesk/tickets", new CreateTicketRequest($"T{i}", "d", null, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
            var id = created.Summary.Id;
            await tech.PostAsJsonAsync($"/api/v1/helpdesk/tickets/{id}/assign", new AssignTicketRequest(technician.Id), ApiFixture.Json);
            await tech.PostAsJsonAsync($"/api/v1/helpdesk/tickets/{id}/comments", new AddCommentRequest("working", false), ApiFixture.Json);
            await tech.PostAsJsonAsync($"/api/v1/helpdesk/tickets/{id}/comments", new AddCommentRequest("note", true), ApiFixture.Json);
            if (i < 2)
            {
                await tech.PostAsJsonAsync($"/api/v1/helpdesk/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.Resolved, "fixed"), ApiFixture.Json);
            }
        }

        var report = await RunAsync(owner, "technician-activity");
        Assert.Equal(1d, Metric(report, "Technicians"));
        var users = await HttpAssert.OkAsync<Solvix.SharedKernel.Results.PagedResult<Solvix.Modules.Identity.Application.Users.TenantUserDto>>(await owner.GetAsync("/api/v1/users?pageSize=100"));
        Assert.Equal(users.Items.Single(u => u.UserId == member.UserId).DisplayName, Cell(report, 0, "technician"));
        Assert.Equal("1", Cell(report, 0, "open"));
        Assert.Equal("3", Cell(report, 0, "assigned"));
        Assert.Equal("2", Cell(report, 0, "resolved"));
        Assert.Equal("3", Cell(report, 0, "replies"));
        Assert.Equal("3", Cell(report, 0, "notes"));
        Assert.Equal("100", Cell(report, 0, "slaMetPct"));
        Assert.NotNull(Cell(report, 0, "avgFirstResponseMin"));
    }

    [Fact]
    public async Task Csv_export_has_a_bom_headers_and_neutralises_spreadsheet_formulas()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollNewDeviceAsync(tenant, "CSV-01");
        using var owner = fixture.CreateClient(tenant.Token);
        await owner.PutAsJsonAsync($"/api/v1/devices/{device.DeviceId}", new UpdateDeviceRequest("=HYPERLINK(\"http://evil.test\",\"click\")", "n"), ApiFixture.Json);

        var response = await owner.GetAsync("/api/v1/reports/device-inventory?format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("device-inventory-", response.Content.Headers.ContentDisposition!.FileName, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl!.NoStore && response.Headers.CacheControl.Private);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var csv = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Hostname,Display name,Status", csv, StringComparison.Ordinal);
        Assert.Contains("CSV-01", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil.test\"\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(",=HYPERLINK", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_never_include_another_tenants_data()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var deviceA = await fixture.EnrollNewDeviceAsync(a, "TENANT-A-PC");
        await deviceA.UploadInventoryAsync(fixture, Phase12Kit.SampleInventory("TENANT-A-PC", appName: "A-Only Software"));
        await fixture.EnrollNewDeviceAsync(b, "TENANT-B-PC");
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);
        await ownerA.PostAsJsonAsync("/api/v1/helpdesk/tickets", new CreateTicketRequest("A ticket", "d", null, null, null, null, null), ApiFixture.Json);

        foreach (var key in AllReports)
        {
            var json = await ownerB.GetStringAsync($"/api/v1/reports/{key}");
            Assert.DoesNotContain("TENANT-A-PC", json, StringComparison.Ordinal);
            Assert.DoesNotContain("A-Only Software", json, StringComparison.Ordinal);
            var csv = await ownerB.GetStringAsync($"/api/v1/reports/{key}?format=csv");
            Assert.DoesNotContain("TENANT-A-PC", csv, StringComparison.Ordinal);
        }

        var software = await RunAsync(ownerB, "software-inventory");
        Assert.Equal(0d, Metric(software, "Devices with an inventory"));
        Assert.Equal(1d, Metric(await RunAsync(ownerB, "device-inventory"), "Devices"));
    }
}
