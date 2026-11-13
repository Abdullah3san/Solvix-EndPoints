namespace Solvix.IntegrationTests.Tests;

using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Mdm.Application;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

/// <summary>A workspace with an owner and a second administrator (needed for four-eyes approvals).</summary>
public sealed class MdmWorkspace(TenantSession session, HttpClient owner, HttpClient admin2, Guid admin2UserId)
{
    public TenantSession Session { get; } = session;

    public HttpClient Owner { get; } = owner;

    public HttpClient SecondAdmin { get; } = admin2;

    public Guid SecondAdminUserId { get; } = admin2UserId;

    public string? EnterpriseName { get; set; }
}

internal static class MdmTestKit
{
    public const string Password = "Passw0rd!Secure";

    public static async Task<MdmWorkspace> CreateWorkspaceAsync(ApiFixture fixture)
    {
        var session = await fixture.RegisterTenantAsync();
        var owner = fixture.CreateClient(session.Token);
        var email = $"admin2-{Guid.NewGuid():N}@example.test";
        var member = await HttpAssert.OkAsync<MemberDto>(
            await owner.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(email, "Second Admin", Password, TenantRole.Admin), ApiFixture.Json),
            System.Net.HttpStatusCode.Created);
        var auth = await fixture.LoginAsync(email, Password, session.Slug);
        return new MdmWorkspace(session, owner, fixture.CreateClient(auth.AccessToken), member.UserId);
    }

    public static async Task BindAndroidAsync(MdmWorkspace ws)
    {
        var url = await HttpAssert.OkAsync<SignupUrlDto>(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enterprise/android/signup", new StartSignupRequest("https://app.example.test/android/callback"), ApiFixture.Json));
        Assert.StartsWith("https://", url.Url, StringComparison.Ordinal);
        var enterprise = await HttpAssert.OkAsync<EnterpriseDto>(
            await ws.Owner.PostAsJsonAsync("/api/v1/mobile-enterprise/android/complete", new CompleteSignupRequest("enterprise-token", "Acme"), ApiFixture.Json));
        Assert.True(enterprise.IsBound);
        ws.EnterpriseName = enterprise.EnterpriseName;
    }

    public static async Task<PolicyDto> CreatePolicyAsync(
        HttpClient client, string name, MobilePlatform platform, ManagementMode mode, DeviceOwnership ownership, MobilePolicySettings? settings = null) =>
        await HttpAssert.OkAsync<PolicyDto>(
            await client.PostAsJsonAsync("/api/v1/mobile-policies", new CreatePolicyRequest(name, null, platform, mode, ownership, settings), ApiFixture.Json),
            System.Net.HttpStatusCode.Created);

    public static async Task<AppDto> CreateAppAsync(HttpClient client, MobilePlatform platform, string name, string identifier, long? storeId = null) =>
        await HttpAssert.OkAsync<AppDto>(
            await client.PostAsJsonAsync("/api/v1/mobile-apps", new CreateAppRequest(platform, name, identifier, storeId), ApiFixture.Json),
            System.Net.HttpStatusCode.Created);

    public static async Task<PublishPolicyResultDto> PublishAsync(HttpClient client, Guid policyId) =>
        await HttpAssert.OkAsync<PublishPolicyResultDto>(await client.PostAsync($"/api/v1/mobile-policies/{policyId}/publish", null));

    public static async Task<EnrollmentCreatedDto> EnrollAsync(HttpClient client, string name, ManagementScenario scenario, Guid policyId, int maxUses = 1) =>
        await HttpAssert.OkAsync<EnrollmentCreatedDto>(
            await client.PostAsJsonAsync(
                "/api/v1/mobile-enrollments",
                new CreateEnrollmentRequest(name, scenario.Platform, scenario.Mode, scenario.Ownership, policyId, 24, maxUses),
                ApiFixture.Json),
            System.Net.HttpStatusCode.Created);

    public static async Task<SyncResultDto> SyncAsync(HttpClient client) =>
        await HttpAssert.OkAsync<SyncResultDto>(await client.PostAsync("/api/v1/mobile-devices/sync", null));

    public static async Task<DeviceDto> GetDeviceAsync(HttpClient client, Guid id) =>
        (await HttpAssert.OkAsync<DeviceDetailDto>(await client.GetAsync($"/api/v1/mobile-devices/{id}"))).Device;

    public static async Task<DeviceDetailDto> GetDetailAsync(HttpClient client, Guid id) =>
        await HttpAssert.OkAsync<DeviceDetailDto>(await client.GetAsync($"/api/v1/mobile-devices/{id}"));

    public static async Task<IReadOnlyList<DeviceDto>> ListDevicesAsync(HttpClient client, string query = "") =>
        (await HttpAssert.OkAsync<PagedResult<DeviceDto>>(await client.GetAsync($"/api/v1/mobile-devices?pageSize=100{query}"))).Items;

    public static async Task<HttpResponseMessage> SendCommandAsync(HttpClient client, Guid deviceId, DeviceCommandType type, string? reason = null, Guid? appId = null, string? message = null) =>
        await client.PostAsJsonAsync($"/api/v1/mobile-devices/{deviceId}/commands", new CreateCommandRequest(type, reason, appId, message), ApiFixture.Json);

    public static async Task<IReadOnlyList<CommandDto>> ListCommandsAsync(HttpClient client, Guid deviceId) =>
        (await HttpAssert.OkAsync<PagedResult<CommandDto>>(await client.GetAsync($"/api/v1/mobile-commands?deviceId={deviceId}&pageSize=100"))).Items;

    public static AndroidDeviceInfo AndroidDevice(
        string enterprise,
        string id,
        string? tokenData,
        string managementMode = "DEVICE_OWNER",
        string ownership = "COMPANY_OWNED",
        string? policyName = null,
        string androidVersion = "14",
        string posture = "SECURE",
        bool secure = true,
        IReadOnlyList<AndroidAppReport>? apps = null) =>
        new()
        {
            Name = $"{enterprise}/devices/{id}",
            State = "ACTIVE",
            ManagementMode = managementMode,
            Ownership = ownership,
            EnrollmentTokenData = tokenData,
            AppliedPolicyName = policyName,
            LastPolicySyncTime = DateTimeOffset.UtcNow.AddMinutes(5),
            LastStatusReportTime = DateTimeOffset.UtcNow,
            Manufacturer = "Google",
            Brand = "google",
            Model = "Pixel 8",
            SerialNumber = "SER-" + id,
            AndroidVersion = androidVersion,
            SecurityPatchLevel = "2026-10-05",
            IsEncrypted = true,
            IsDeviceSecure = secure,
            DevicePosture = posture,
            PolicyCompliant = true,
            Applications = apps ?? [],
        };
}
