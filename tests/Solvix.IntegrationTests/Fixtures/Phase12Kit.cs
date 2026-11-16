namespace Solvix.IntegrationTests.Fixtures;

using System.Net;
using System.Net.Http.Json;
using Solvix.Agent.Contracts;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Domain;
using Solvix.Modules.Identity.Application.Members;
using Solvix.SharedKernel.Identity;

public sealed record MemberSession(Guid UserId, string Email, string Token);

public static class Phase12Kit
{
    /// <summary>Client for the platform-operator API (shared secret header).</summary>
    public static HttpClient OperatorClient(this ApiFixture fixture, string? key = SolvixApiFactory.OperatorKey)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var client = fixture.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Operator-Key", key);
        }

        return client;
    }

    public static async Task<MemberSession> AddMemberAsync(this ApiFixture fixture, TenantSession tenant, TenantRole role = TenantRole.Member)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.test";
        var add = await owner.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(email, role + " User", "Passw0rd!Secure", role), ApiFixture.Json);
        Assert.True(add.IsSuccessStatusCode, await add.Content.ReadAsStringAsync());
        var auth = await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug);
        return new MemberSession(auth.User.Id, email, auth.AccessToken);
    }

    /// <summary>Gives a tenant a negotiated subscription with an explicit device limit (Enterprise terms).</summary>
    public static async Task SetDeviceLimitAsync(this ApiFixture fixture, TenantSession tenant, int limit)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(tenant);
        using var op = fixture.OperatorClient();
        var response = await op.PutAsJsonAsync(
            $"/api/v1/platform/billing/tenants/{tenant.TenantId}/subscription",
            new OperatorConfigureSubscriptionRequest("enterprise", BillingInterval.Monthly, limit, 0, DateTimeOffset.UtcNow.AddDays(30)),
            ApiFixture.Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    public static async Task<EnrolledDevice> EnrollNewDeviceAsync(this ApiFixture fixture, TenantSession tenant, string? hostname = null)
    {
        var token = await fixture.CreateTokenAsync(tenant);
        return await fixture.EnrollAsync(token, DeviceTestKit.Identity(hostname: hostname));
    }

    public static InventoryReport SampleInventory(string hostname, bool secureBoot = true, bool? bitLocker = true, bool osSupported = true, int revision = 4460, string? appName = null) => new()
    {
        CollectedAtUtc = DateTimeOffset.UtcNow,
        AgentVersion = DeviceTestKit.AgentVersion,
        Hostname = hostname,
        ContentHash = new string('c', 64),
        System = new SystemInfo { Manufacturer = "Contoso", Model = "Book 15", SerialNumber = "SN-" + Guid.NewGuid().ToString("N")[..6] },
        Bios = new BiosInfo { Manufacturer = "Contoso", Version = "1.2.3", FirmwareType = "UEFI", SecureBootEnabled = secureBoot, ReleaseDate = DateTimeOffset.UtcNow.AddYears(-2) },
        OperatingSystem = new OperatingSystemInfo
        {
            Name = "Windows 11 Pro", Version = "10.0.22631", DisplayVersion = "23H2", BuildNumber = "22631", UpdateBuildRevision = revision,
            Architecture = "x64", IsSupported = osSupported, LastBootUpTimeUtc = DateTimeOffset.UtcNow.AddDays(-2),
        },
        Processors = [new ProcessorInfo { Name = "Intel(R) Core(TM) i7", Cores = 8 }],
        Memory = new MemoryInfo { TotalPhysicalBytes = 16L * 1024 * 1024 * 1024 },
        Volumes = [new VolumeInfo { DriveLetter = "C:", FileSystem = "NTFS", SizeBytes = 500_000_000_000, DriveType = "Fixed", BitLockerProtected = bitLocker }],
        Software =
        [
            new InstalledApplication { Name = appName ?? "7-Zip 24.08 (x64)", Version = "24.08", Publisher = "Igor Pavlov" },
            new InstalledApplication { Name = "Microsoft Edge", Version = "130.0", Publisher = "Microsoft Corporation" },
        ],
    };

    public static async Task UploadInventoryAsync(this EnrolledDevice device, ApiFixture fixture, InventoryReport report)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(fixture);
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        var response = await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, report, ApiFixture.Json);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
