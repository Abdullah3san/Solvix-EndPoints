namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Results;

/// <summary>Unauthorized asset access: anonymous callers, plain members, devices, other tenants, raw SQL and the audit log.</summary>
[Collection(ApiCollection.Name)]
public sealed class AssetSecurityTests(ApiFixture fixture)
{
    private static readonly string[] AdminGetRoutes =
    [
        "/api/v1/assets",
        "/api/v1/assets/stats",
        "/api/v1/assets/export",
        "/api/v1/asset-vendors",
        "/api/v1/asset-maintenance",
        "/api/v1/asset-audits",
        "/api/v1/asset-matches",
        "/api/v1/asset-matches/unregistered-endpoints",
        "/api/v1/asset-reports/summary",
        "/api/v1/asset-reports/depreciation",
        "/api/v1/asset-reports/audit-log",
        "/api/v1/asset-settings",
    ];

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop());

        using var anonymous = fixture.CreateClient();
        foreach (var route in AdminGetRoutes.Concat([$"/api/v1/assets/{asset.Summary.Id}", $"/api/v1/assets/{asset.Summary.Id}/qr.svg", "/api/v1/my-assets"]))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAssetAsync(AssetTestKit.Laptop())).StatusCode);
    }

    [Fact]
    public async Task Members_cannot_use_the_register_and_only_see_their_own_assets()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var (memberId, memberToken) = await fixture.AddMemberAsync(tenant);
        var theirs = await admin.CreateAssetAsync(AssetTestKit.Laptop() with { PurchaseCost = 2500m }, new AssignAssetRequest(memberId, null, null, null, null));
        var notTheirs = await admin.CreateAssetAsync(AssetTestKit.Laptop());

        using var member = fixture.CreateClient(memberToken);
        foreach (var route in AdminGetRoutes.Concat([$"/api/v1/assets/{theirs.Summary.Id}", $"/api/v1/assets/{notTheirs.Summary.Id}/history", $"/api/v1/assets/{theirs.Summary.Id}/barcode.svg"]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(route)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAssetAsync(AssetTestKit.Laptop())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/v1/assets/{notTheirs.Summary.Id}/assign", new AssignAssetRequest(memberId, null, null, null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/assets/bulk", new BulkAssetRequest { AssetIds = [notTheirs.Summary.Id], Action = BulkAssetAction.Retire, Reason = "x" }, ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/v1/asset-settings", new AssetSettingsDto("X", "USD", true, 30), ApiFixture.Json)).StatusCode);

        // "My assets" lists only theirs and carries no financial data.
        var response = await member.GetAsync("/api/v1/my-assets");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mine = System.Text.Json.JsonSerializer.Deserialize<List<MyAssetDto>>(body, ApiFixture.Json)!;
        Assert.Equal(theirs.Summary.Id, Assert.Single(mine).Id);
        Assert.DoesNotContain("2500", body, StringComparison.Ordinal);
        Assert.DoesNotContain("purchaseCost", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_certificates_cannot_access_asset_management()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        foreach (var route in AdminGetRoutes.Concat(["/api/v1/my-assets"]))
        {
            Assert.Contains((await agent.GetAsync(route)).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
    }

    [Fact]
    public async Task Another_tenants_assets_and_references_do_not_exist()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var adminA = fixture.CreateClient(a.Token);
        using var adminB = fixture.CreateClient(b.Token);

        var vendorA = await HttpAssert.OkAsync<VendorDto>(await adminA.PostAsJsonAsync("/api/v1/asset-vendors", new VendorRequest("Vendor A", null, null, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var assetA = await adminA.CreateAssetAsync(AssetTestKit.Laptop("XT-SERIAL-1") with { VendorId = vendorA.Id });
        var deviceA = await fixture.EnrollAsync(await fixture.CreateTokenAsync(a), DeviceTestKit.Identity(serial: "XT-SERIAL-1") with { Manufacturer = "Dell Inc.", Model = "Latitude 7440" });
        var suggestionA = Assert.Single((await HttpAssert.OkAsync<PagedResult<AssetDeviceLinkDto>>(await adminA.GetAsync("/api/v1/asset-matches"))).Items);
        var maintenanceA = await HttpAssert.OkAsync<MaintenanceRecordDto>(await adminA.PostAsJsonAsync(
            $"/api/v1/assets/{assetA.Summary.Id}/maintenance", new CreateMaintenanceRequest(MaintenanceType.Inspection, "Check", null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var auditA = await HttpAssert.OkAsync<PhysicalAuditDto>(await adminA.PostAsJsonAsync("/api/v1/asset-audits", new StartPhysicalAuditRequest("A audit", null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var id = assetA.Summary.Id;

        // Reads of A's ids from tenant B are 404 (never 403: existence is not disclosed).
        foreach (var route in new[]
        {
            $"/api/v1/assets/{id}", $"/api/v1/assets/{id}/assignments", $"/api/v1/assets/{id}/transfers", $"/api/v1/assets/{id}/maintenance",
            $"/api/v1/assets/{id}/history", $"/api/v1/assets/{id}/barcode.svg", $"/api/v1/assets/{id}/qr.svg",
            $"/api/v1/asset-vendors/{vendorA.Id}", $"/api/v1/asset-audits/{auditA.Id}", $"/api/v1/asset-audits/{auditA.Id}/items",
            $"/api/v1/asset-matches/devices/{deviceA.DeviceId}", $"/api/v1/assets/lookup?code={assetA.Summary.AssetTag}",
        })
        {
            Assert.True((await adminB.GetAsync(route)).StatusCode == HttpStatusCode.NotFound, route);
        }

        // Writes against A's records from tenant B are 404 and change nothing.
        var writes = new (string Route, object Body, bool Put)[]
        {
            ($"/api/v1/assets/{id}", new UpdateAssetRequest { Asset = AssetTestKit.Laptop() }, true),
            ($"/api/v1/assets/{id}/assign", new AssignAssetRequest(null, "Intruder", null, null, null), false),
            ($"/api/v1/assets/{id}/transfer", new TransferAssetRequest(null, null, "Elsewhere", null), false),
            ($"/api/v1/assets/{id}/retire", new RetireAssetRequest("x"), false),
            ($"/api/v1/assets/{id}/maintenance", new CreateMaintenanceRequest(MaintenanceType.Repair, "x", null, null, null, null), false),
            ($"/api/v1/assets/{id}/endpoint-link", new ManualLinkRequest(deviceA.DeviceId, null), true),
            ($"/api/v1/asset-vendors/{vendorA.Id}", new VendorRequest("Hijacked", null, null, null, null, null, null), true),
            ($"/api/v1/asset-maintenance/{maintenanceA.Id}/complete", new CompleteMaintenanceRequest(1m, null), false),
            ($"/api/v1/asset-matches/{suggestionA.Id}/approve", new LinkDecisionRequest(null), false),
            ($"/api/v1/asset-matches/{suggestionA.Id}/reject", new LinkDecisionRequest(null), false),
            ($"/api/v1/asset-audits/{auditA.Id}/complete", new CompletePhysicalAuditRequest(true, null), false),
            ("/api/v1/assets/bulk", new BulkAssetRequest { AssetIds = [id], Action = BulkAssetAction.Retire, Reason = "x" }, false),
            ("/api/v1/asset-matches/register-endpoint", new RegisterEndpointRequest(deviceA.DeviceId, AssetCategory.Laptop, null, null), false),
        };
        foreach (var (route, body, put) in writes)
        {
            var response = put ? await adminB.PutAsJsonAsync(route, body, ApiFixture.Json) : await adminB.PostAsJsonAsync(route, body, ApiFixture.Json);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{route}: {(int)response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/v1/asset-vendors/{vendorA.Id}")).StatusCode);

        // B cannot reference A's vendor or A's device from its own records, nor scan A's tags.
        var assetB = await adminB.CreateAssetAsync(AssetTestKit.Laptop());
        await HttpAssert.ProblemAsync(await adminB.PostAssetAsync(AssetTestKit.Laptop() with { VendorId = vendorA.Id }), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await adminB.PutAsJsonAsync($"/api/v1/assets/{assetB.Summary.Id}/endpoint-link", new ManualLinkRequest(deviceA.DeviceId, null), ApiFixture.Json), HttpStatusCode.NotFound);
        var auditB = await HttpAssert.OkAsync<PhysicalAuditDto>(await adminB.PostAsJsonAsync("/api/v1/asset-audits", new StartPhysicalAuditRequest("B audit", null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        // (Tags are numbered per tenant, so B's own first laptop legitimately shares A's tag text; A's serial exists only in A.)
        await HttpAssert.ProblemAsync(await adminB.PostAsJsonAsync($"/api/v1/asset-audits/{auditB.Id}/scan", new ScanRequest("XT-SERIAL-1", null, null, null), ApiFixture.Json), HttpStatusCode.NotFound);
        var ownTag = await HttpAssert.OkAsync<ScanResultDto>(await adminB.PostAsJsonAsync($"/api/v1/asset-audits/{auditB.Id}/scan", new ScanRequest(assetA.Summary.AssetTag, null, null, null), ApiFixture.Json));
        Assert.Equal(assetB.Summary.Id, ownTag.Item.AssetId);

        // Lists, stats, reports and exports never include A's data.
        var listB = await HttpAssert.OkAsync<PagedResult<AssetSummaryDto>>(await adminB.GetAsync("/api/v1/assets?search=XT-SERIAL"));
        Assert.Empty(listB.Items);
        Assert.Equal(1, (await HttpAssert.OkAsync<AssetStatsDto>(await adminB.GetAsync("/api/v1/assets/stats"))).Total);
        var exportB = await adminB.GetStringAsync("/api/v1/assets/export");
        Assert.DoesNotContain("XT-SERIAL-1", exportB, StringComparison.Ordinal);
        Assert.DoesNotContain("Vendor A", exportB, StringComparison.Ordinal);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<AssetDeviceLinkDto>>(await adminB.GetAsync("/api/v1/asset-matches"))).Items);
        Assert.DoesNotContain(
            (await HttpAssert.OkAsync<PagedResult<AuditLogEntryDto>>(await adminB.GetAsync("/api/v1/asset-reports/audit-log?pageSize=200"))).Items,
            e => e.AssetId == id);

        // A's records are untouched.
        var stillA = await adminA.GetAssetAsync(id);
        Assert.Equal(AssetStatus.InStock, stillA.Summary.Status);
        Assert.Null(stillA.Summary.Assignee);
        Assert.Equal("Vendor A", stillA.Summary.Vendor!.Name);
        Assert.Equal(AssetDeviceLinkStatus.Suggested, Assert.Single((await HttpAssert.OkAsync<PagedResult<AssetDeviceLinkDto>>(await adminA.GetAsync("/api/v1/asset-matches"))).Items).Status);
    }

    [Fact]
    public async Task Row_level_security_isolates_asset_tables_for_raw_sql()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var adminA = fixture.CreateClient(a.Token);
        var assetA = await adminA.CreateAssetAsync(AssetTestKit.Laptop());

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();

        // No tenant: nothing is visible.
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM assets"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM asset_audit_log"));

        await SetTenantAsync(connection, b.TenantId);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM assets WHERE id = @id", ("id", assetA.Summary.Id)));
        Assert.Equal(0L, await ExecuteAsync(connection, "UPDATE assets SET name = 'pwned' WHERE id = @id", ("id", assetA.Summary.Id)));
        Assert.Equal(0L, await ExecuteAsync(connection, "DELETE FROM asset_device_links"));

        // Inserting a row for another tenant violates the policy.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO asset_vendors (id, tenant_id, name, normalized_name, is_active, created_at_utc, updated_at_utc)
            VALUES (gen_random_uuid(), @t, 'Injected', 'INJECTED', true, now(), now())
            """,
            ("t", a.TenantId)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await SetTenantAsync(connection, a.TenantId);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM assets WHERE id = @id", ("id", assetA.Summary.Id)));
        Assert.Equal("Laptop", (await HttpAssert.OkAsync<AssetDetailsDto>(await adminA.GetAsync($"/api/v1/assets/{assetA.Summary.Id}"))).Summary.Category.ToString());
    }

    [Fact]
    public async Task Audit_log_is_append_only()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop());

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, tenant.TenantId);
        Assert.True(await ScalarAsync(connection, "SELECT count(*) FROM asset_audit_log WHERE asset_id = @id", ("id", asset.Summary.Id)) >= 1);

        foreach (var sql in new[]
        {
            "UPDATE asset_audit_log SET summary = 'rewritten' WHERE asset_id = @id",
            "DELETE FROM asset_audit_log WHERE asset_id = @id",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql, ("id", asset.Summary.Id)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
        }

        // Even the schema owner cannot rewrite history; only an explicit platform scope may delete (tenant removal).
        var owner = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var c = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
            await c.OpenAsync();
            await SetTenantAsync(c, tenant.TenantId);
            await ExecuteAsync(c, "UPDATE asset_audit_log SET summary = 'x' WHERE asset_id = @id", ("id", asset.Summary.Id));
        });
        Assert.Contains("append-only", owner.MessageText, StringComparison.Ordinal);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecutePlatformSqlAsync("TRUNCATE asset_audit_log"));
        Assert.Contains("append-only", truncate.MessageText, StringComparison.Ordinal);

        Assert.True(await ScalarAsync(connection, "SELECT count(*) FROM asset_audit_log WHERE asset_id = @id AND summary <> 'rewritten'", ("id", asset.Summary.Id)) >= 1);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection);
        command.Parameters.AddWithValue("t", tenantId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }
}
