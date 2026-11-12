namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.SharedKernel.Results;

/// <summary>Registration, numbering, duplicate serial numbers, assignment, transfers, lifecycle and the register queries.</summary>
[Collection(ApiCollection.Name)]
public sealed class AssetRegisterTests(ApiFixture fixture)
{
    [Fact]
    public async Task Assets_are_numbered_automatically_per_category_and_tags_are_unique()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);

        var first = await admin.CreateAssetAsync(AssetTestKit.Laptop());
        var second = await admin.CreateAssetAsync(AssetTestKit.Laptop());
        var printer = await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.Printer, manufacturer: "HP"));
        Assert.Equal("AST-LAP-000001", first.Summary.AssetTag);
        Assert.Equal("AST-LAP-000002", second.Summary.AssetTag);
        Assert.Equal("AST-PRN-000001", printer.Summary.AssetTag);
        Assert.Equal(AssetStatus.InStock, first.Summary.Status);
        Assert.Equal("USD", (await admin.CreateAssetAsync(AssetTestKit.Laptop() with { PurchaseCost = 10m })).Summary.Currency);

        // Numbering is per tenant.
        var other = await fixture.RegisterTenantAsync();
        using var otherAdmin = fixture.CreateClient(other.Token);
        Assert.Equal("AST-LAP-000001", (await otherAdmin.CreateAssetAsync(AssetTestKit.Laptop())).Summary.AssetTag);

        // A manual tag is normalised and must be unique; the generator skips numbers that are taken manually.
        var manual = await admin.CreateAssetAsync(AssetTestKit.Laptop() with { AssetTag = "ast-lap-000004" });
        Assert.Equal("AST-LAP-000004", manual.Summary.AssetTag);
        await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop() with { AssetTag = "AST-LAP-000004" }), HttpStatusCode.Conflict, "asset_tag_duplicate");
        await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop() with { AssetTag = "bad tag!" }), HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("AST-LAP-000005", (await admin.CreateAssetAsync(AssetTestKit.Laptop())).Summary.AssetTag);

        // A custom prefix starts its own sequence.
        await HttpAssert.OkAsync<AssetSettingsDto>(await admin.PutAsJsonAsync("/api/v1/asset-settings", new AssetSettingsDto("acme", "eur", false, 30), ApiFixture.Json));
        var prefixed = await admin.CreateAssetAsync(AssetTestKit.Laptop() with { PurchaseCost = 900m });
        Assert.Equal("ACME-LAP-000001", prefixed.Summary.AssetTag);
        Assert.Equal("EUR", prefixed.Summary.Currency);

        // The tag never changes after registration.
        var update = await admin.PutAsJsonAsync($"/api/v1/assets/{first.Summary.Id}", new UpdateAssetRequest { Asset = AssetTestKit.Laptop() with { AssetTag = "OTHER-1" } }, ApiFixture.Json);
        await HttpAssert.ProblemAsync(update, HttpStatusCode.BadRequest, "validation_failed");

        // Concurrent registrations never receive the same number.
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var c = fixture.CreateClient(tenant.Token);
            return (await c.CreateAssetAsync(AssetTestKit.Of(AssetCategory.Monitor, manufacturer: "Dell"))).Summary.AssetTag;
        }));
        Assert.Equal(8, parallel.Distinct().Count());
    }

    [Fact]
    public async Task Duplicate_serial_numbers_are_rejected_per_manufacturer_by_the_api_and_the_database()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var original = await admin.CreateAssetAsync(AssetTestKit.Laptop("ABC123XYZ", "Dell Inc."));

        // Same serial: case, whitespace and manufacturer spelling ("Dell Inc." vs "DELL") do not make it different.
        var dup = await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop("  abc123xyz ", "DELL")), HttpStatusCode.Conflict, "asset_serial_duplicate");
        Assert.Contains(original.Summary.AssetTag, dup.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A different manufacturer may legitimately use the same serial string.
        var otherMaker = await admin.CreateAssetAsync(AssetTestKit.Laptop("ABC123XYZ", "Lenovo"));
        Assert.Equal("ABC123XYZ", otherMaker.Summary.SerialNumber);

        // Placeholder serials are not identities: any number of assets may carry them.
        await admin.CreateAssetAsync(AssetTestKit.Laptop("To be filled by O.E.M.", "Dell Inc."));
        await admin.CreateAssetAsync(AssetTestKit.Laptop("To be filled by O.E.M.", "Dell Inc."));

        // Editing an asset into a duplicate is rejected too.
        var third = await admin.CreateAssetAsync(AssetTestKit.Laptop("ZZZ999", "Dell Inc."));
        await HttpAssert.ProblemAsync(
            await admin.PutAsJsonAsync($"/api/v1/assets/{third.Summary.Id}", new UpdateAssetRequest { Asset = AssetTestKit.Laptop("abc123xyz", "Dell") }, ApiFixture.Json),
            HttpStatusCode.Conflict,
            "asset_serial_duplicate");

        // Another tenant can register the same hardware serial (tenants are isolated).
        var other = await fixture.RegisterTenantAsync();
        using var otherAdmin = fixture.CreateClient(other.Token);
        await otherAdmin.CreateAssetAsync(AssetTestKit.Laptop("ABC123XYZ", "Dell Inc."));

        // Racing requests: the database unique index lets exactly one through.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var c = fixture.CreateClient(tenant.Token);
            return (await c.PostAssetAsync(AssetTestKit.Laptop("RACE-SERIAL-1", "HP"))).StatusCode;
        }));
        Assert.Equal(1, responses.Count(s => s == HttpStatusCode.Created));
        Assert.All(responses.Where(s => s != HttpStatusCode.Created), s => Assert.Equal(HttpStatusCode.Conflict, s));

        // Bypassing the application does not help either.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecutePlatformSqlAsync(
            "UPDATE assets SET serial_number_key = 'ABC123XYZ', manufacturer_key = 'dell' WHERE id = @id",
            ("id", third.Summary.Id)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Assignment_history_tracks_members_and_employees_without_logins()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var (memberId, memberToken) = await fixture.AddMemberAsync(tenant);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop());

        var assigned = await admin.PostActionAsync(asset.Summary.Id, "assign", new AssignAssetRequest(memberId, null, "E-100", new DateOnly(2027, 1, 1), "New starter"));
        Assert.Equal(AssetStatus.InUse, assigned.Summary.Status);
        Assert.Equal(memberId, assigned.Summary.Assignee!.UserId);
        Assert.True(assigned.Summary.Assignee.IsMember);

        // The member sees the asset under "my assets" (without financial data); other members do not.
        using var member = fixture.CreateClient(memberToken);
        var mine = await HttpAssert.OkAsync<List<MyAssetDto>>(await member.GetAsync("/api/v1/my-assets"));
        Assert.Equal(asset.Summary.Id, Assert.Single(mine).Id);
        var (_, otherToken) = await fixture.AddMemberAsync(tenant);
        using var otherMember = fixture.CreateClient(otherToken);
        Assert.Empty(await HttpAssert.OkAsync<List<MyAssetDto>>(await otherMember.GetAsync("/api/v1/my-assets")));

        // Re-assigning to an employee without a login closes the previous assignment.
        var reassigned = await admin.PostActionAsync(asset.Summary.Id, "assign", new AssignAssetRequest(null, "Warehouse picker", "E-200", null, null));
        Assert.Equal("Warehouse picker", reassigned.Summary.Assignee!.DisplayName);
        Assert.False(reassigned.Summary.Assignee.IsMember);
        Assert.Empty(await HttpAssert.OkAsync<List<MyAssetDto>>(await member.GetAsync("/api/v1/my-assets")));

        var returned = await admin.PostActionAsync(asset.Summary.Id, "return", new ReturnAssetRequest(AssetCondition.Fair, "Scratched lid"));
        Assert.Equal(AssetStatus.InStock, returned.Summary.Status);
        Assert.Null(returned.Summary.Assignee);
        Assert.Equal(AssetCondition.Fair, returned.Summary.Condition);

        var history = await HttpAssert.OkAsync<List<AssignmentDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/assignments"));
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.NotNull(h.ReturnedAtUtc));
        Assert.Equal("Warehouse picker", history[0].Assignee.DisplayName);
        Assert.Equal(AssetCondition.Fair, history[0].ReturnCondition);
        Assert.Equal(memberId, history[1].Assignee.UserId);
        Assert.Equal("Re-assigned.", history[1].ReturnNotes);

        // Validation: nobody, unknown user, returning an unassigned asset.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/assign", new AssignAssetRequest(null, " ", null, null, null), ApiFixture.Json), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/assign", new AssignAssetRequest(Guid.NewGuid(), null, null, null, null), ApiFixture.Json), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/return", new ReturnAssetRequest(null, null), ApiFixture.Json), HttpStatusCode.Conflict, "asset_not_assigned");

        // The database keeps at most one open assignment per asset.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecutePlatformSqlAsync(
            """
            INSERT INTO asset_assignments (id, tenant_id, asset_id, assignee_name, assigned_at_utc, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, @a, 'x', now(), now(), now());
            INSERT INTO asset_assignments (id, tenant_id, asset_id, assignee_name, assigned_at_utc, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, @a, 'y', now(), now(), now());
            """,
            ("t", tenant.TenantId),
            ("a", asset.Summary.Id)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Transfers_move_assets_between_branches_and_keep_history()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var hq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await admin.GetAsync("/api/v1/branches"))).Items);
        var jeddah = await admin.CreateBranchAsync("JED");
        var it = await admin.CreateDepartmentAsync(jeddah.Id, "IT");
        var hqFinance = await admin.CreateDepartmentAsync(hq.Id, "FIN");

        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop() with { BranchId = hq.Id, Location = "Floor 2" });
        Assert.Equal(hq.Id, asset.Summary.Branch!.Id);

        // A department implies its branch.
        var moved = await admin.PostActionAsync(asset.Summary.Id, "transfer", new TransferAssetRequest(null, it.Id, "Store room", "Relocation"));
        Assert.Equal(jeddah.Id, moved.Summary.Branch!.Id);
        Assert.Equal(it.Id, moved.Summary.Department!.Id);
        Assert.Equal("Store room", moved.Summary.Location);

        var back = await admin.PostActionAsync(asset.Summary.Id, "transfer", new TransferAssetRequest(hq.Id, null, null, null));
        Assert.Null(back.Summary.Department);

        var transfers = await HttpAssert.OkAsync<List<TransferDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/transfers"));
        Assert.Equal(2, transfers.Count);
        Assert.Equal(hq.Id, transfers[1].FromBranch!.Id);
        Assert.Equal("Floor 2", transfers[1].FromLocation);
        Assert.Equal(jeddah.Id, transfers[1].ToBranch!.Id);
        Assert.Equal("Branch JED", transfers[1].ToBranch!.Name);
        Assert.Equal("Relocation", transfers[1].Reason);
        Assert.Equal(jeddah.Id, transfers[0].FromBranch!.Id);

        // Invalid targets.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/transfer", new TransferAssetRequest(jeddah.Id, hqFinance.Id, null, null), ApiFixture.Json), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/transfer", new TransferAssetRequest(hq.Id, null, null, null), ApiFixture.Json), HttpStatusCode.Conflict, "asset_transfer_no_change");
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/transfer", new TransferAssetRequest(Guid.NewGuid(), null, null, null), ApiFixture.Json), HttpStatusCode.NotFound);

        // Another tenant's branch does not exist here.
        var other = await fixture.RegisterTenantAsync();
        using var otherAdmin = fixture.CreateClient(other.Token);
        var otherHq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await otherAdmin.GetAsync("/api/v1/branches"))).Items);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/transfer", new TransferAssetRequest(otherHq.Id, null, null, null), ApiFixture.Json), HttpStatusCode.NotFound);

        // The database refuses a cross-tenant branch reference even without the API.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecutePlatformSqlAsync(
            "UPDATE assets SET branch_id = @b WHERE id = @id", ("b", otherHq.Id), ("id", asset.Summary.Id)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);

        // Bulk transfer is all-or-nothing.
        var a1 = await admin.CreateAssetAsync(AssetTestKit.Laptop());
        var a2 = await admin.CreateAssetAsync(AssetTestKit.Laptop());
        var disposed = await admin.CreateAssetAsync(AssetTestKit.Laptop());
        await admin.PostActionAsync(disposed.Summary.Id, "retire", new RetireAssetRequest("Broken"));
        await admin.PostActionAsync(disposed.Summary.Id, "dispose", new DisposeAssetRequest(DisposalMethod.Recycled, null, null, "WEEE-1", null));
        var failed = await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/assets/bulk", new BulkAssetRequest { AssetIds = [a1.Summary.Id, a2.Summary.Id, disposed.Summary.Id], Action = BulkAssetAction.Transfer, BranchId = jeddah.Id }, ApiFixture.Json),
            HttpStatusCode.Conflict,
            "bulk_action_failed");
        Assert.Contains(disposed.Summary.AssetTag, failed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Null((await admin.GetAssetAsync(a1.Summary.Id)).Summary.Branch);

        var bulk = await HttpAssert.OkAsync<BulkAssetResultDto>(await admin.PostAsJsonAsync(
            "/api/v1/assets/bulk", new BulkAssetRequest { AssetIds = [a1.Summary.Id, a2.Summary.Id], Action = BulkAssetAction.Transfer, BranchId = jeddah.Id, Location = "Rack 4", Reason = "Bulk move" }, ApiFixture.Json));
        Assert.Equal(2, bulk.Processed);
        Assert.Equal(jeddah.Id, (await admin.GetAssetAsync(a2.Summary.Id)).Summary.Branch!.Id);
        Assert.Single(await HttpAssert.OkAsync<List<TransferDto>>(await admin.GetAsync($"/api/v1/assets/{a2.Summary.Id}/transfers")));
    }

    [Fact]
    public async Task Lifecycle_maintenance_retirement_and_disposal()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var vendor = await HttpAssert.OkAsync<VendorDto>(
            await admin.PostAsJsonAsync("/api/v1/asset-vendors", new VendorRequest("Gulf IT Services", "Sara", "sara@gulf.example", null, "https://gulf.example", null, null), ApiFixture.Json),
            HttpStatusCode.Created);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop() with { VendorId = vendor.Id }, new AssignAssetRequest(null, "Field engineer", null, null, null));
        Assert.Equal(AssetStatus.InUse, asset.Summary.Status);

        // Maintenance takes the asset out of use and restores the previous status.
        var record = await HttpAssert.OkAsync<MaintenanceRecordDto>(await admin.PostAsJsonAsync(
            $"/api/v1/assets/{asset.Summary.Id}/maintenance", new CreateMaintenanceRequest(MaintenanceType.Repair, "Replace keyboard", null, vendor.Id, "Gulf IT", null, StartNow: true), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(MaintenanceStatus.InProgress, record.Status);
        Assert.Equal(AssetStatus.InMaintenance, (await admin.GetAssetAsync(asset.Summary.Id)).Summary.Status);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/retire", new RetireAssetRequest("x"), ApiFixture.Json), HttpStatusCode.Conflict, "asset_maintenance_open");
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/assign", new AssignAssetRequest(null, "Other", null, null, null), ApiFixture.Json), HttpStatusCode.Conflict, "asset_not_assignable");

        var done = await HttpAssert.OkAsync<MaintenanceRecordDto>(await admin.PostAsJsonAsync($"/api/v1/asset-maintenance/{record.Id}/complete", new CompleteMaintenanceRequest(149.999m, "Keyboard replaced"), ApiFixture.Json));
        Assert.Equal(150.00m, done.Cost);
        Assert.Equal(AssetStatus.InUse, (await admin.GetAssetAsync(asset.Summary.Id)).Summary.Status);
        var scheduled = await HttpAssert.OkAsync<MaintenanceRecordDto>(await admin.PostAsJsonAsync(
            $"/api/v1/assets/{asset.Summary.Id}/maintenance", new CreateMaintenanceRequest(MaintenanceType.Preventive, "Battery check", null, null, null, new DateOnly(2027, 3, 1)), ApiFixture.Json), HttpStatusCode.Created);
        var timeline = await HttpAssert.OkAsync<PagedResult<MaintenanceRecordDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/maintenance"));
        Assert.Equal([scheduled.Id, record.Id], timeline.Items.Select(i => i.Id).ToArray());
        await HttpAssert.ProblemAsync(await admin.DeleteAsync($"/api/v1/asset-vendors/{vendor.Id}"), HttpStatusCode.Conflict, "vendor_in_use");

        // Retirement closes the open assignment; disposal requires retirement and makes the record read-only.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/dispose", new DisposeAssetRequest(DisposalMethod.Sold, null, 50m, null, null), ApiFixture.Json), HttpStatusCode.Conflict, "asset_not_retired");
        var retired = await admin.PostActionAsync(asset.Summary.Id, "retire", new RetireAssetRequest("End of life"));
        Assert.Equal(AssetStatus.Retired, retired.Summary.Status);
        Assert.Null(retired.Summary.Assignee);
        Assert.NotNull(Assert.Single(await HttpAssert.OkAsync<List<AssignmentDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/assignments"))).ReturnedAtUtc);

        var reinstated = await admin.PostActionAsync(asset.Summary.Id, "reinstate", new { });
        Assert.Equal(AssetStatus.InStock, reinstated.Summary.Status);
        await admin.PostActionAsync(asset.Summary.Id, "retire", new RetireAssetRequest("End of life"));
        var disposed = await admin.PostActionAsync(asset.Summary.Id, "dispose", new DisposeAssetRequest(DisposalMethod.Sold, null, 50m, "INV-77", "Sold to staff"));
        Assert.Equal(AssetStatus.Disposed, disposed.Summary.Status);
        Assert.Equal(DisposalMethod.Sold, disposed.DisposalMethod);
        Assert.Equal(50m, disposed.DisposalValue);

        await HttpAssert.ProblemAsync(
            await admin.PutAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}", new UpdateAssetRequest { Asset = AssetTestKit.Laptop() }, ApiFixture.Json),
            HttpStatusCode.Conflict, "asset_disposed");
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/reinstate", new { }, ApiFixture.Json), HttpStatusCode.Conflict);

        // Every step is in the audit log, newest first, attributed to the administrator.
        var history = await HttpAssert.OkAsync<PagedResult<AuditLogEntryDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/history?pageSize=100"));
        var actions = history.Items.Select(i => i.Action).Reverse().ToList();
        Assert.Equal(
            ["asset.registered", "asset.assigned", "maintenance.scheduled", "maintenance.started", "maintenance.completed", "maintenance.scheduled", "asset.retired", "asset.reinstated", "asset.retired", "asset.disposed"],
            actions);
        Assert.All(history.Items, i => Assert.Equal(AuditActorKind.User, i.ActorKind));
        Assert.Equal(tenant.Auth.User.Id, history.Items[0].Actor!.Id);
        Assert.Equal("Disposed", history.Items[0].Changes!["status"].To);
    }

    [Fact]
    public async Task Edits_are_audited_and_protected_against_lost_updates()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop("EDIT-1"));

        var input = AssetTestKit.Laptop("EDIT-1", model: "Latitude 7450") with { Name = asset.Summary.Name, WarrantyEndDate = new DateOnly(2028, 1, 31) };
        var updated = await HttpAssert.OkAsync<AssetDetailsDto>(await admin.PutAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}", new UpdateAssetRequest { Asset = input, Version = asset.Version }, ApiFixture.Json));
        Assert.Equal("Latitude 7450", updated.Summary.Model);
        Assert.NotEqual(asset.Version, updated.Version);

        // A stale version (someone else saved in between) is refused.
        await HttpAssert.ProblemAsync(
            await admin.PutAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}", new UpdateAssetRequest { Asset = input with { Model = "X" }, Version = asset.Version }, ApiFixture.Json),
            HttpStatusCode.Conflict, "concurrency_conflict");

        var entry = (await HttpAssert.OkAsync<PagedResult<AuditLogEntryDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/history"))).Items[0];
        Assert.Equal("asset.updated", entry.Action);
        Assert.Equal(new AuditChangeDto("Latitude 7440", "Latitude 7450"), entry.Changes!["model"]);
        Assert.Equal(new AuditChangeDto(null, "2028-01-31"), entry.Changes["warrantyEndDate"]);
        Assert.False(entry.Changes.ContainsKey("name"));

        // Domain rules surface as validation errors.
        await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop() with { PurchaseCost = 100m, SalvageValue = 200m }), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop() with { WarrantyStartDate = new DateOnly(2026, 1, 2), WarrantyEndDate = new DateOnly(2026, 1, 1) }), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop() with { DepreciationMethod = DepreciationMethod.StraightLine }), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await admin.PostAssetAsync(AssetTestKit.Laptop() with { Currency = "dollars", PurchaseCost = 1m }), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Database_constraints_back_the_domain_rules()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop() with { PurchaseCost = 1000m, PurchaseDate = new DateOnly(2026, 1, 1) });

        foreach (var sql in new[]
        {
            "UPDATE assets SET salvage_value = 2000 WHERE id = @id",
            "UPDATE assets SET purchase_cost = -1 WHERE id = @id",
            "UPDATE assets SET status = 'InUse' WHERE id = @id",
            "UPDATE assets SET status = 'Disposed', retired_at_utc = now() WHERE id = @id",
            "UPDATE assets SET depreciation_method = 'StraightLine' WHERE id = @id",
            "UPDATE assets SET assigned_user_id = gen_random_uuid() WHERE id = @id",
            "UPDATE assets SET asset_tag = 'lower-case' WHERE id = @id",
            "UPDATE assets SET warranty_start_date = '2026-02-01', warranty_end_date = '2026-01-01' WHERE id = @id",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecutePlatformSqlAsync(sql, ("id", asset.Summary.Id)));
            Assert.True(ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation, $"{sql}: {ex.SqlState} {ex.MessageText}");
        }
    }

    [Fact]
    public async Task Depreciation_is_calculated_per_asset_and_reported_by_category()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop() with
        {
            PurchaseDate = new DateOnly(2024, 1, 15),
            PurchaseCost = 1200m,
            SalvageValue = 120m,
            DepreciationMethod = DepreciationMethod.StraightLine,
            UsefulLifeMonths = 36,
        });

        var asOf = await HttpAssert.OkAsync<DepreciationResult>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/depreciation?asOf=2025-01-15"));
        Assert.Equal(12, asOf.MonthsElapsed);
        Assert.Equal(360m, asOf.AccumulatedDepreciation);
        Assert.Equal(840m, asOf.BookValue);
        Assert.Equal(new DateOnly(2027, 1, 15), asOf.FullyDepreciatedOn);

        var end = await HttpAssert.OkAsync<DepreciationResult>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/depreciation?asOf=2030-01-01"));
        Assert.Equal(120m, end.BookValue);

        await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.Server, "SRV-1", "HPE") with
        {
            PurchaseDate = new DateOnly(2024, 1, 15), PurchaseCost = 6000m, DepreciationMethod = DepreciationMethod.DecliningBalance, UsefulLifeMonths = 60,
        });
        var report = await HttpAssert.OkAsync<DepreciationReportDto>(await admin.GetAsync("/api/v1/asset-reports/depreciation?asOf=2025-01-15"));
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(7200m, report.Cost);
        var laptops = report.Rows.Single(r => r.Category == AssetCategory.Laptop);
        Assert.Equal(840m, laptops.BookValue);
        var servers = report.Rows.Single(r => r.Category == AssetCategory.Server);
        Assert.True(servers.AccumulatedDepreciation > 6000m * 12 / 60, "declining balance front-loads depreciation");

        var csv = await admin.GetStringAsync("/api/v1/asset-reports/depreciation/export?asOf=2025-01-15");
        Assert.Contains(asset.Summary.AssetTag, csv, StringComparison.Ordinal);
        Assert.Contains("840.00", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_supports_search_filters_sorting_paging_and_export()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var jed = await admin.CreateBranchAsync("JED");
        var a = await admin.CreateAssetAsync(AssetTestKit.Laptop("FILTER-A") with { Name = "Alpha", PurchaseCost = 300m, BranchId = jed.Id, WarrantyEndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)) });
        var b = await admin.CreateAssetAsync(AssetTestKit.Laptop("FILTER-B") with { Name = "Bravo", PurchaseCost = 100m, WarrantyEndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)) });
        var c = await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.NetworkSwitch, "SW-1", "Cisco", "C9300") with { Name = "=cmd|' /C calc'!A0", PurchaseCost = 200m });
        await admin.PostActionAsync(b.Summary.Id, "assign", new AssignAssetRequest(null, "Nadia", "E-9", null, null));

        async Task<PagedResult<AssetSummaryDto>> Query(string qs) => await HttpAssert.OkAsync<PagedResult<AssetSummaryDto>>(await admin.GetAsync("/api/v1/assets?" + qs));

        Assert.Equal(b.Summary.Id, Assert.Single((await Query("search=nadia")).Items).Id);
        Assert.Equal(c.Summary.Id, Assert.Single((await Query("search=c9300")).Items).Id);
        Assert.Equal(2, (await Query("category=Laptop")).TotalCount);
        Assert.Equal(3, (await Query("category=Laptop&category=NetworkSwitch")).TotalCount);
        Assert.Equal(b.Summary.Id, Assert.Single((await Query("status=InUse")).Items).Id);
        Assert.Equal(b.Summary.Id, Assert.Single((await Query("assigned=true")).Items).Id);
        Assert.Equal(a.Summary.Id, Assert.Single((await Query($"branchId={jed.Id}")).Items).Id);
        Assert.Equal(a.Summary.Id, Assert.Single((await Query("warranty=ExpiringSoon")).Items).Id);
        Assert.Equal(b.Summary.Id, Assert.Single((await Query("warranty=Expired")).Items).Id);
        Assert.Equal(c.Summary.Id, Assert.Single((await Query("endpointLink=NotApplicable")).Items).Id);
        Assert.Equal(2, (await Query("endpointLink=Unlinked")).TotalCount);

        var byCost = await Query("sort=PurchaseCost&direction=Desc");
        Assert.Equal([a.Summary.Id, c.Summary.Id, b.Summary.Id], byCost.Items.Select(i => i.Id).ToArray());
        var paged = await Query("sort=Name&pageSize=2&page=2");
        Assert.Equal(3, paged.TotalCount);
        Assert.Equal(2, paged.TotalPages);
        Assert.Equal(b.Summary.Id, Assert.Single(paged.Items).Id);

        var stats = await HttpAssert.OkAsync<AssetStatsDto>(await admin.GetAsync("/api/v1/assets/stats"));
        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.InUse);
        Assert.Equal(1, stats.WarrantyExpiringSoon);

        var export = await admin.GetAsync("/api/v1/assets/export?category=NetworkSwitch&category=Laptop&sort=Name");
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var csv = await export.Content.ReadAsStringAsync();
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Asset tag,Name,Category", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("'=cmd", lines[1][(lines[1].IndexOf(',', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal); // formula neutralised
        Assert.Contains("Nadia", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Physical_audits_find_misplaced_and_missing_assets()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var store = await admin.CreateBranchAsync("STR");
        var elsewhere = await admin.CreateBranchAsync("ELS");
        var found = await admin.CreateAssetAsync(AssetTestKit.Laptop("AUD-1") with { BranchId = store.Id });
        var byQr = await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.ZebraHandheld, "ZB-77", "Zebra Technologies", "TC52") with { BranchId = store.Id });
        var bySerial = await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.Printer, "PRN-SER-5", "HP") with { BranchId = store.Id });
        var missing = await admin.CreateAssetAsync(AssetTestKit.Laptop("AUD-2") with { BranchId = store.Id });
        var stray = await admin.CreateAssetAsync(AssetTestKit.Laptop("AUD-3") with { BranchId = elsewhere.Id });
        var retired = await admin.CreateAssetAsync(AssetTestKit.Laptop("AUD-4") with { BranchId = store.Id });
        await admin.PostActionAsync(retired.Summary.Id, "retire", new RetireAssetRequest("old"));

        var audit = await HttpAssert.OkAsync<PhysicalAuditDto>(
            await admin.PostAsJsonAsync("/api/v1/asset-audits", new StartPhysicalAuditRequest("Q4 store count", store.Id, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(4, audit.Expected);
        Assert.Equal(4, audit.Pending);

        async Task<ScanResultDto> Scan(string code) =>
            await HttpAssert.OkAsync<ScanResultDto>(await admin.PostAsJsonAsync($"/api/v1/asset-audits/{audit.Id}/scan", new ScanRequest(code, "Shelf A", null, null), ApiFixture.Json));

        Assert.False((await Scan(found.Summary.AssetTag.ToLowerInvariant())).AlreadyScanned);
        Assert.True((await Scan(found.Summary.AssetTag)).AlreadyScanned);
        Assert.Equal(byQr.Summary.Id, (await Scan(byQr.QrPayload)).Item.AssetId);
        Assert.Equal(bySerial.Summary.Id, (await Scan("prn-ser-5")).Item.AssetId);
        var unexpected = await Scan(stray.Summary.AssetTag);
        Assert.Equal(PhysicalAuditResult.Unexpected, unexpected.Item.Result);
        Assert.Equal(elsewhere.Id, unexpected.Item.ExpectedBranch!.Id);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/asset-audits/{audit.Id}/scan", new ScanRequest("NO-SUCH-TAG", null, null, null), ApiFixture.Json), HttpStatusCode.NotFound);

        var completed = await HttpAssert.OkAsync<PhysicalAuditDto>(await admin.PostAsJsonAsync($"/api/v1/asset-audits/{audit.Id}/complete", new CompletePhysicalAuditRequest(true, "Done"), ApiFixture.Json));
        Assert.Equal(PhysicalAuditStatus.Completed, completed.Status);
        Assert.Equal(3, completed.Found);
        Assert.Equal(1, completed.Missing);
        Assert.Equal(1, completed.Unexpected);
        Assert.Equal(0, completed.Pending);

        Assert.Equal(AssetStatus.Missing, (await admin.GetAssetAsync(missing.Summary.Id)).Summary.Status);
        Assert.NotNull((await admin.GetAssetAsync(found.Summary.Id)).Summary.LastAuditedAtUtc);
        var missingItems = await HttpAssert.OkAsync<PagedResult<PhysicalAuditItemDto>>(await admin.GetAsync($"/api/v1/asset-audits/{audit.Id}/items?result=Missing"));
        Assert.Equal(missing.Summary.Id, Assert.Single(missingItems.Items).AssetId);

        // A closed audit cannot be changed; a found-again asset returns to stock.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/asset-audits/{audit.Id}/scan", new ScanRequest(found.Summary.AssetTag, null, null, null), ApiFixture.Json), HttpStatusCode.Conflict, "audit_closed");
        Assert.Equal(AssetStatus.InStock, (await admin.PostActionAsync(missing.Summary.Id, "mark-found", new { })).Summary.Status);
    }

    [Fact]
    public async Task Labels_render_barcode_and_qr_code_svg()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop());
        Assert.Equal("SOLVIX-ASSET:" + asset.Summary.AssetTag, asset.QrPayload);

        foreach (var kind in new[] { "barcode.svg", "qr.svg" })
        {
            var response = await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/{kind}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            var svg = await response.Content.ReadAsStringAsync();
            Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(asset.Summary.AssetTag, await admin.GetStringAsync($"/api/v1/assets/{asset.Summary.Id}/barcode.svg"), StringComparison.Ordinal);
        var lookup = await HttpAssert.OkAsync<AssetLookupDto>(await admin.GetAsync($"/api/v1/assets/lookup?code={Uri.EscapeDataString(asset.QrPayload)}"));
        Assert.Equal(asset.Summary.Id, lookup.Id);
    }
}
