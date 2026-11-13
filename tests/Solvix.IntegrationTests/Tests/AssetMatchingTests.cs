namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.SharedKernel.Results;

/// <summary>
/// Integration of asset management with endpoint management: hardware identity
/// of enrolled Windows devices is matched to physical assets, reviewed by an
/// administrator, and the two records stay separate.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AssetMatchingTests(ApiFixture fixture)
{
    [Fact]
    public async Task Enrolled_device_is_suggested_for_its_asset_and_linked_only_after_approval()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop("5CD1234XYZ", "Dell Inc.", "Latitude 7440"));
        Assert.Equal(EndpointLinkState.Unlinked, asset.Summary.EndpointLink);

        // The agent reports the same hardware with different spelling/case.
        var device = await EnrollAsync(tenant, "5cd1234xyz", "DELL", "Latitude 7440");

        var suggestion = Assert.Single(await PendingAsync(admin));
        Assert.Equal(asset.Summary.Id, suggestion.Asset.Id);
        Assert.Equal(device.DeviceId, suggestion.Endpoint!.Id);
        Assert.Equal(MatchConfidence.High, suggestion.Confidence);
        Assert.False(suggestion.IsAmbiguous);
        Assert.Equal(AssetDeviceLinkSource.Automatic, suggestion.Source);
        Assert.Contains(suggestion.Evidence, e => e.Kind == "serialNumber" && e.Outcome == EvidenceOutcome.Match);
        Assert.Contains(suggestion.Evidence, e => e.Kind == "manufacturer" && e.Outcome == EvidenceOutcome.Match);

        // Review is required by default: nothing is linked yet.
        var pending = await admin.GetAssetAsync(asset.Summary.Id);
        Assert.Equal(EndpointLinkState.Suggested, pending.Summary.EndpointLink);
        Assert.Null(pending.EndpointLink);
        Assert.Equal(1, pending.PendingSuggestions);

        var approved = await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PostAsJsonAsync($"/api/v1/asset-matches/{suggestion.Id}/approve", new LinkDecisionRequest("Verified"), ApiFixture.Json));
        Assert.Equal(AssetDeviceLinkStatus.Approved, approved.Status);
        Assert.Equal(tenant.Auth.User.Id, approved.DecidedBy!.Id);

        var linked = await admin.GetAssetAsync(asset.Summary.Id);
        Assert.Equal(EndpointLinkState.Linked, linked.Summary.EndpointLink);
        Assert.Equal(device.DeviceId, linked.EndpointLink!.Endpoint.Id);
        Assert.Equal(device.DeviceId, linked.Summary.LinkedDeviceId);
        Assert.Empty(linked.EndpointLink.Discrepancies);

        // The device side sees the same association; both records still exist separately.
        var forDevice = await HttpAssert.OkAsync<DeviceAssetDto>(await admin.GetAsync($"/api/v1/asset-matches/devices/{device.DeviceId}"));
        Assert.Equal(asset.Summary.Id, forDevice.Approved!.Asset.Id);
        var deviceRecord = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal("5cd1234xyz", deviceRecord.Summary.SerialNumber);
        Assert.Equal("5CD1234XYZ", linked.Summary.SerialNumber);

        // Already-decided suggestions cannot be approved twice; the audit log records the decisions.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/asset-matches/{suggestion.Id}/approve", new LinkDecisionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict);
        var history = await HttpAssert.OkAsync<PagedResult<AuditLogEntryDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/history"));
        var matchSuggested = history.Items.Single(i => i.Action == "endpoint_match.suggested");
        Assert.Equal(AuditActorKind.System, matchSuggested.ActorKind);
        Assert.Contains(history.Items, i => i.Action == "endpoint_link.approved" && i.ActorKind == AuditActorKind.User);
    }

    [Fact]
    public async Task Assets_registered_after_enrollment_are_matched_too()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var device = await EnrollAsync(tenant, "LATE-REG-1", "LENOVO", "20XW0026US");
        Assert.Empty(await PendingAsync(admin));

        // The asset register says "ThinkPad X1 Carbon": model differs, serial + manufacturer still make it High.
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop("LATE-REG-1", "Lenovo", "ThinkPad X1 Carbon Gen 9"));
        var suggestion = Assert.Single(await PendingAsync(admin));
        Assert.Equal(asset.Summary.Id, suggestion.Asset.Id);
        Assert.Equal(device.DeviceId, suggestion.Endpoint!.Id);
        Assert.Equal(MatchConfidence.Medium, suggestion.Confidence);
        Assert.Contains(suggestion.Evidence, e => e.Kind == "model" && e.Outcome == EvidenceOutcome.Mismatch);

        // Editing the serial away withdraws the suggestion.
        await HttpAssert.OkAsync<AssetDetailsDto>(await admin.PutAsJsonAsync(
            $"/api/v1/assets/{asset.Summary.Id}", new UpdateAssetRequest { Asset = AssetTestKit.Laptop("SOMETHING-ELSE", "Lenovo", "ThinkPad X1") with { Name = asset.Summary.Name } }, ApiFixture.Json));
        Assert.Empty(await PendingAsync(admin));
    }

    [Fact]
    public async Task Ambiguous_matches_are_never_linked_automatically()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        await admin.SetAutoApproveAsync(true);

        // Two assets share the serial string (different manufacturers recorded).
        var dell = await admin.CreateAssetAsync(AssetTestKit.Laptop("SHARED-77", "Dell Inc.", "Latitude 5440"));
        var unknown = await admin.CreateAssetAsync(AssetTestKit.Laptop("SHARED-77", "Generic", null!) with { Manufacturer = null, Model = null });
        var device = await EnrollAsync(tenant, "SHARED-77", "Dell Inc.", "Latitude 5440");

        var suggestions = await PendingAsync(admin);
        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, s => Assert.True(s.IsAmbiguous));
        Assert.All(suggestions, s => Assert.Equal(AssetDeviceLinkStatus.Suggested, s.Status));
        Assert.Contains(suggestions, s => s.Evidence.Any(e => e.Kind == "ambiguity"));
        Assert.Null((await admin.GetAssetAsync(dell.Summary.Id)).EndpointLink);

        // Approving one supersedes the competing suggestion.
        var forDell = suggestions.Single(s => s.Asset.Id == dell.Summary.Id);
        await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PostAsJsonAsync($"/api/v1/asset-matches/{forDell.Id}/approve", new LinkDecisionRequest(null), ApiFixture.Json));
        Assert.Empty(await PendingAsync(admin));
        var superseded = await HttpAssert.OkAsync<PagedResult<AssetDeviceLinkDto>>(await admin.GetAsync($"/api/v1/asset-matches?status=Superseded&assetId={unknown.Summary.Id}"));
        Assert.Equal(device.DeviceId, Assert.Single(superseded.Items).Endpoint!.Id);

        // A second endpoint reporting an already-matched serial (cloned image / swapped board) is ambiguous:
        // the first, unambiguous match was linked automatically, the newcomer waits for review.
        var single = await admin.CreateAssetAsync(AssetTestKit.Laptop("CLONED-1", "HP", "EliteBook 840"));
        var original = await EnrollAsync(tenant, "CLONED-1", "HP", "EliteBook 840");
        Assert.Equal(original.DeviceId, (await admin.GetAssetAsync(single.Summary.Id)).EndpointLink!.Endpoint.Id);
        var clone = await EnrollAsync(tenant, "CLONED-1", "HP", "EliteBook 840");
        var cloned = Assert.Single(await PendingAsync(admin), s => s.Asset.Id == single.Summary.Id);
        Assert.Equal(clone.DeviceId, cloned.Endpoint!.Id);
        Assert.True(cloned.IsAmbiguous);
        Assert.Contains(cloned.Evidence, e => e.Kind == "ambiguity" && e.Detail.Contains("2 endpoints", StringComparison.Ordinal));
        Assert.Equal(original.DeviceId, (await admin.GetAssetAsync(single.Summary.Id)).EndpointLink!.Endpoint.Id);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/asset-matches/{cloned.Id}/approve", new LinkDecisionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "asset_already_linked");
    }

    [Fact]
    public async Task Unambiguous_high_confidence_matches_are_auto_approved_only_when_enabled()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        await admin.SetAutoApproveAsync(true);

        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop("AUTO-1", "HP Inc.", "EliteBook 840 G10"));
        var device = await EnrollAsync(tenant, "AUTO-1", "HP", "HP EliteBook 840 G10 Notebook PC");
        var linked = await admin.GetAssetAsync(asset.Summary.Id);
        Assert.Equal(device.DeviceId, linked.EndpointLink!.Endpoint.Id);
        Assert.Equal(MatchConfidence.High, linked.EndpointLink.Confidence);
        var auto = (await HttpAssert.OkAsync<PagedResult<AuditLogEntryDto>>(await admin.GetAsync($"/api/v1/assets/{asset.Summary.Id}/history"))).Items.Single(i => i.Action == "endpoint_match.auto_approved");
        Assert.Equal(AuditActorKind.System, auto.ActorKind);
        Assert.Null(auto.Actor);

        // Medium confidence (manufacturer unknown on the asset) is not auto-approved.
        var medium = await admin.CreateAssetAsync(AssetTestKit.Laptop("AUTO-2") with { Manufacturer = null });
        await EnrollAsync(tenant, "AUTO-2", "HP", "EliteBook");
        Assert.Null((await admin.GetAssetAsync(medium.Summary.Id)).EndpointLink);
        Assert.Equal(MatchConfidence.Medium, Assert.Single(await PendingAsync(admin)).Confidence);

        // A category that does not run the agent (a monitor) is never High.
        var monitor = await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.Monitor, "AUTO-3", "HP", "E24"));
        await EnrollAsync(tenant, "AUTO-3", "HP", "E24");
        Assert.Null((await admin.GetAssetAsync(monitor.Summary.Id)).EndpointLink);

        // With auto-approval off (the default) the same perfect match waits for review.
        var other = await fixture.RegisterTenantAsync();
        using var otherAdmin = fixture.CreateClient(other.Token);
        var otherAsset = await otherAdmin.CreateAssetAsync(AssetTestKit.Laptop("AUTO-1", "HP Inc.", "EliteBook 840 G10"));
        await EnrollAsync(other, "AUTO-1", "HP", "HP EliteBook 840 G10 Notebook PC");
        Assert.Null((await otherAdmin.GetAssetAsync(otherAsset.Summary.Id)).EndpointLink);
        Assert.Equal(MatchConfidence.High, Assert.Single(await PendingAsync(otherAdmin)).Confidence);
    }

    [Fact]
    public async Task Rejected_pairs_are_remembered_and_placeholder_serials_never_match()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop("REJ-1", "Dell Inc."));
        var hardwareId = Guid.NewGuid().ToString();
        await EnrollAsync(tenant, "REJ-1", "Dell Inc.", "Latitude 7440", hardwareId);

        var suggestion = Assert.Single(await PendingAsync(admin));
        var rejected = await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PostAsJsonAsync($"/api/v1/asset-matches/{suggestion.Id}/reject", new LinkDecisionRequest("Different unit"), ApiFixture.Json));
        Assert.Equal(AssetDeviceLinkStatus.Rejected, rejected.Status);

        // Re-enrollment of the same machine and a full rescan do not resurrect it.
        await EnrollAsync(tenant, "REJ-1", "Dell Inc.", "Latitude 7440", hardwareId);
        var rescan = await HttpAssert.OkAsync<RescanResultDto>(await admin.PostAsync("/api/v1/asset-matches/rescan", null));
        Assert.Equal(0, rescan.SuggestionsCreated);
        Assert.Empty(await PendingAsync(admin));

        // Placeholder serial numbers are not evidence.
        await admin.CreateAssetAsync(AssetTestKit.Laptop("System Serial Number", "Dell Inc."));
        await EnrollAsync(tenant, "System Serial Number", "Dell Inc.", "Latitude 7440");
        await EnrollAsync(tenant, "0000000000", "Dell Inc.", "Latitude 7440");
        Assert.Empty(await PendingAsync(admin));
    }

    [Fact]
    public async Task Manual_links_conflicts_and_unlinking()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var device = await EnrollAsync(tenant, "MAN-DEV-1", "Microsoft Corporation", "Surface Pro 9");
        var first = await admin.CreateAssetAsync(AssetTestKit.Of(AssetCategory.Tablet, "DIFFERENT-SERIAL", "Microsoft", "Surface Pro 9"));
        var second = await admin.CreateAssetAsync(AssetTestKit.Laptop());

        // An administrator can associate records whose serials differ (e.g. replaced motherboard); evidence is still shown.
        var manual = await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PutAsJsonAsync($"/api/v1/assets/{first.Summary.Id}/endpoint-link", new ManualLinkRequest(device.DeviceId, "Board replaced"), ApiFixture.Json));
        Assert.Equal(AssetDeviceLinkStatus.Approved, manual.Status);
        Assert.Equal(AssetDeviceLinkSource.Manual, manual.Source);
        Assert.Contains(manual.Evidence, e => e.Kind == "serialNumber" && e.Outcome == EvidenceOutcome.Mismatch);
        Assert.Contains(manual.Discrepancies, d => d.Field == "serialNumber");

        // One endpoint per asset and one asset per endpoint.
        await HttpAssert.ProblemAsync(await admin.PutAsJsonAsync($"/api/v1/assets/{second.Summary.Id}/endpoint-link", new ManualLinkRequest(device.DeviceId, null), ApiFixture.Json), HttpStatusCode.Conflict, "device_already_linked");
        var otherDevice = await EnrollAsync(tenant, "MAN-DEV-2", "Dell", "x");
        await HttpAssert.ProblemAsync(await admin.PutAsJsonAsync($"/api/v1/assets/{first.Summary.Id}/endpoint-link", new ManualLinkRequest(otherDevice.DeviceId, null), ApiFixture.Json), HttpStatusCode.Conflict, "asset_already_linked");
        await HttpAssert.ProblemAsync(await admin.PutAsJsonAsync($"/api/v1/assets/{first.Summary.Id}/endpoint-link", new ManualLinkRequest(Guid.NewGuid(), null), ApiFixture.Json), HttpStatusCode.NotFound);

        // Unlink, then the device can be associated with the other asset.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/assets/{first.Summary.Id}/endpoint-link/remove", new LinkDecisionRequest("Wrong unit"), ApiFixture.Json)).StatusCode);
        Assert.Null((await admin.GetAssetAsync(first.Summary.Id)).EndpointLink);
        await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PutAsJsonAsync($"/api/v1/assets/{second.Summary.Id}/endpoint-link", new ManualLinkRequest(device.DeviceId, null), ApiFixture.Json));

        // Relinking a remembered (unlinked) pair reuses its row.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/assets/{second.Summary.Id}/endpoint-link/remove", new LinkDecisionRequest(null), ApiFixture.Json)).StatusCode);
        var relinked = await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PutAsJsonAsync($"/api/v1/assets/{first.Summary.Id}/endpoint-link", new ManualLinkRequest(device.DeviceId, null), ApiFixture.Json));
        Assert.Equal(manual.Id, relinked.Id);
    }

    [Fact]
    public async Task Hardware_identity_changes_reported_in_inventory_are_rematched()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var oldAsset = await admin.CreateAssetAsync(AssetTestKit.Laptop("INV-OLD", "Dell Inc.", "Latitude 7440"));
        var newAsset = await admin.CreateAssetAsync(AssetTestKit.Laptop("INV-NEW", "Dell Inc.", "Latitude 7440"));
        var device = await EnrollAsync(tenant, "INV-OLD", "Dell Inc.", "Latitude 7440");
        Assert.Equal(oldAsset.Summary.Id, Assert.Single(await PendingAsync(admin)).Asset.Id);

        // Motherboard swap: the agent now reports another serial number.
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        var report = new InventoryReport
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            AgentVersion = DeviceTestKit.AgentVersion,
            Hostname = device.Identity.Hostname,
            ContentHash = new string('c', 64),
            System = new SystemInfo { Manufacturer = "Dell Inc.", Model = "Latitude 7440", SerialNumber = "INV-NEW", HardwareId = device.Identity.HardwareId },
        };
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, report, ApiFixture.Json)).StatusCode);

        var pending = Assert.Single(await PendingAsync(admin));
        Assert.Equal(newAsset.Summary.Id, pending.Asset.Id);
        var withdrawn = (await HttpAssert.OkAsync<PagedResult<AuditLogEntryDto>>(await admin.GetAsync($"/api/v1/assets/{oldAsset.Summary.Id}/history"))).Items;
        Assert.Contains(withdrawn, i => i.Action == "endpoint_match.withdrawn");
    }

    [Fact]
    public async Task Unregistered_endpoints_can_be_registered_as_assets()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var (memberId, _) = await fixture.AddMemberAsync(tenant);
        var device = await EnrollAsync(tenant, "UNREG-1", "Dell Inc.", "OptiPlex 7010");
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PutAsJsonAsync($"/api/v1/devices/{device.DeviceId}/assignment", new UpdateDeviceAssignmentRequest(memberId, null, null), ApiFixture.Json));

        var unregistered = await HttpAssert.OkAsync<PagedResult<EndpointDto>>(await admin.GetAsync("/api/v1/asset-matches/unregistered-endpoints"));
        Assert.Equal(device.DeviceId, Assert.Single(unregistered.Items).Id);

        var created = await HttpAssert.OkAsync<AssetDetailsDto>(
            await admin.PostAsJsonAsync("/api/v1/asset-matches/register-endpoint", new RegisterEndpointRequest(device.DeviceId, AssetCategory.Desktop, null, null), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal("AST-DSK-000001", created.Summary.AssetTag);
        Assert.Equal(device.Identity.Hostname, created.Summary.Name);
        Assert.Equal("UNREG-1", created.Summary.SerialNumber);
        Assert.Equal("OptiPlex 7010", created.Summary.Model);
        Assert.Equal(memberId, created.Summary.Assignee!.UserId);
        Assert.Equal(device.DeviceId, created.EndpointLink!.Endpoint.Id);

        Assert.Empty((await HttpAssert.OkAsync<PagedResult<EndpointDto>>(await admin.GetAsync("/api/v1/asset-matches/unregistered-endpoints"))).Items);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/asset-matches/register-endpoint", new RegisterEndpointRequest(device.DeviceId, AssetCategory.Desktop, null, null), ApiFixture.Json),
            HttpStatusCode.Conflict, "device_already_linked");

        var coverage = (await HttpAssert.OkAsync<AssetSummaryReportDto>(await admin.GetAsync("/api/v1/asset-reports/summary"))).EndpointCoverage;
        Assert.Equal(1, coverage.ActiveEndpoints);
        Assert.Equal(0, coverage.EndpointsWithoutAsset);
        Assert.Equal(1, coverage.LinkedAssets);
    }

    [Fact]
    public async Task Physical_asset_outlives_its_endpoint_record()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var asset = await admin.CreateAssetAsync(AssetTestKit.Laptop("OUTLIVE-1"));
        var device = await EnrollAsync(tenant, "OUTLIVE-1", "Dell Inc.", "Latitude 7440");
        await HttpAssert.OkAsync<AssetDeviceLinkDto>(await admin.PutAsJsonAsync($"/api/v1/assets/{asset.Summary.Id}/endpoint-link", new ManualLinkRequest(device.DeviceId, null), ApiFixture.Json));

        // Retiring the endpoint keeps the association (the asset is still there); deleting it removes only the link.
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null));
        Assert.False((await admin.GetAssetAsync(asset.Summary.Id)).EndpointLink!.Endpoint.IsActive);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/devices/{device.DeviceId}")).StatusCode);
        var after = await admin.GetAssetAsync(asset.Summary.Id);
        Assert.Null(after.EndpointLink);
        Assert.Equal(EndpointLinkState.Unlinked, after.Summary.EndpointLink);
        Assert.Equal("OUTLIVE-1", after.Summary.SerialNumber);
    }

    private async Task<EnrolledDevice> EnrollAsync(TenantSession tenant, string serial, string manufacturer, string model, string? hardwareId = null) =>
        await fixture.EnrollAsync(
            await fixture.CreateTokenAsync(tenant),
            DeviceTestKit.Identity(hardwareId: hardwareId, serial: serial) with { Manufacturer = manufacturer, Model = model });

    private static async Task<IReadOnlyList<AssetDeviceLinkDto>> PendingAsync(HttpClient admin) =>
        (await HttpAssert.OkAsync<PagedResult<AssetDeviceLinkDto>>(await admin.GetAsync("/api/v1/asset-matches?status=Suggested&pageSize=100"))).Items;
}
