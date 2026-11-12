namespace Solvix.IntegrationTests.Fixtures;

using System.Net;
using System.Net.Http.Json;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Identity;

public static class AssetTestKit
{
    public static AssetInput Laptop(string? serial = null, string manufacturer = "Dell Inc.", string model = "Latitude 7440", string? name = null) => new()
    {
        Name = name ?? "Laptop " + Guid.NewGuid().ToString("N")[..6],
        Category = AssetCategory.Laptop,
        Condition = AssetCondition.New,
        Manufacturer = manufacturer,
        Model = model,
        SerialNumber = serial ?? "SN" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
    };

    public static AssetInput Of(AssetCategory category, string? serial = null, string? manufacturer = "Cisco", string? model = null) => new()
    {
        Name = $"{category} " + Guid.NewGuid().ToString("N")[..6],
        Category = category,
        Condition = AssetCondition.Good,
        Manufacturer = manufacturer,
        Model = model,
        SerialNumber = serial,
    };

    public static Task<HttpResponseMessage> PostAssetAsync(this HttpClient client, AssetInput input, AssignAssetRequest? assignTo = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync("/api/v1/assets", new CreateAssetRequest { Asset = input, AssignTo = assignTo }, ApiFixture.Json);
    }

    public static async Task<AssetDetailsDto> CreateAssetAsync(this HttpClient client, AssetInput input, AssignAssetRequest? assignTo = null) =>
        await HttpAssert.OkAsync<AssetDetailsDto>(await client.PostAssetAsync(input, assignTo), HttpStatusCode.Created);

    public static async Task<AssetDetailsDto> PostActionAsync(this HttpClient client, Guid assetId, string action, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await HttpAssert.OkAsync<AssetDetailsDto>(await client.PostAsJsonAsync($"/api/v1/assets/{assetId}/{action}", body, ApiFixture.Json));
    }

    public static async Task<AssetDetailsDto> GetAssetAsync(this HttpClient client, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await HttpAssert.OkAsync<AssetDetailsDto>(await client.GetAsync($"/api/v1/assets/{assetId}"));
    }

    /// <summary>Adds a workspace member and returns (user id, access token).</summary>
    public static async Task<(Guid UserId, string Token)> AddMemberAsync(this ApiFixture fixture, TenantSession tenant, TenantRole role = TenantRole.Member)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        var email = $"member-{Guid.NewGuid():N}@example.test";
        var member = await HttpAssert.OkAsync<MemberDto>(
            await owner.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(email, "Member " + email[7..12], "Passw0rd!Secure", role), ApiFixture.Json),
            HttpStatusCode.Created);
        var auth = await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug);
        return (member.UserId, auth.AccessToken);
    }

    public static async Task<BranchDto> CreateBranchAsync(this HttpClient client, string code)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await HttpAssert.OkAsync<BranchDto>(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Branch " + code, code, false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Created);
    }

    public static async Task<DepartmentDto> CreateDepartmentAsync(this HttpClient client, Guid branchId, string code)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await HttpAssert.OkAsync<DepartmentDto>(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(branchId, null, "Dept " + code, code, null), ApiFixture.Json),
            HttpStatusCode.Created);
    }

    public static async Task SetAutoApproveAsync(this HttpClient client, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(client);
        await HttpAssert.OkAsync<AssetSettingsDto>(await client.PutAsJsonAsync("/api/v1/asset-settings", new AssetSettingsDto("AST", "USD", enabled, 60), ApiFixture.Json));
    }
}
