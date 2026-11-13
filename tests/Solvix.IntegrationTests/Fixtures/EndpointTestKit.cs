namespace Solvix.IntegrationTests.Fixtures;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Solvix.Agent.Core.Api;
using Solvix.Agent.Core.Configuration;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.EndpointManagement.Application.Deployments;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Members;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

/// <summary>Helpers for Phase 09 tests: enrolment, simulated agents, extra users and scheduler ticks.</summary>
public static class EndpointTestKit
{
    public static async Task<string> CreateEnrollmentTokenAsync(
        this ApiFixture fixture, TenantSession admin, int? maxUses = null, Guid? branchId = null, Guid? departmentId = null, Guid? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(admin);
        using var client = fixture.CreateClient(admin.Token);
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await client.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Test", 24, maxUses, branchId, departmentId, groupId), ApiFixture.Json),
            HttpStatusCode.Created);
        return created.Token;
    }

    public static async Task<SimulatedDevice> EnrollDeviceAsync(this ApiFixture fixture, string token, string hostname, string? hardwareId = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var device = new SimulatedDevice(fixture);
        var response = await device.Api.EnrollAsync(
            new EnrollDeviceRequest(token, hostname, hardwareId ?? Guid.NewGuid().ToString(), "corp.example", "Windows 11 Enterprise", "24H2", "26100.2314", "1.0.0"),
            CancellationToken.None);
        device.Bind(response);
        await device.HeartbeatAsync();
        return device;
    }

    /// <summary>Adds a user with the given role to the tenant and returns their token.</summary>
    public static async Task<string> AddUserAsync(this ApiFixture fixture, TenantSession owner, TenantRole role)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(owner);
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.test";
        const string password = "Passw0rd!Secure";
        using var client = fixture.CreateClient(owner.Token);
        await HttpAssert.OkAsync<MemberDto>(
            await client.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(email, $"{role} user", password, role), ApiFixture.Json),
            HttpStatusCode.Created);
        AuthResponse auth = await fixture.LoginAsync(email, password, owner.Slug);
        return auth.AccessToken;
    }

    /// <summary>Advances the deployment state machine for one tenant at an explicit instant.</summary>
    public static async Task TickAsync(this ApiFixture fixture, Guid tenantId, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        await using var scope = fixture.Factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenantId);
        await scope.ServiceProvider.GetRequiredService<IDeploymentOrchestrator>().TickAsync(now ?? DateTimeOffset.UtcNow, CancellationToken.None);
    }

    public static UpdateInfo Update(
        string id,
        string title,
        bool installed = false,
        UpdateClassification classification = UpdateClassification.SecurityUpdates,
        MsrcSeverity severity = MsrcSeverity.Critical,
        bool rebootRequired = false) =>
        new(id, 1, title, [title.Split(' ')[0].Replace("KB", string.Empty, StringComparison.Ordinal)], classification, severity,
            DateTimeOffset.UtcNow.AddDays(-3), 50_000_000, RebootBehavior.CanRequestReboot, installed, installed, false, false, rebootRequired,
            "https://support.microsoft.com", ["CVE-2026-0001"]);

    public static PatchInventoryReport Inventory(IReadOnlyList<UpdateInfo> updates, IReadOnlyList<UpdateHistoryEntry>? history = null, bool rebootRequired = false) =>
        new(
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow,
            OperationResultCode.Succeeded,
            null,
            null,
            new UpdateServiceStatus("Running", "ScheduledInstallation", "WindowsUpdate", DateTimeOffset.UtcNow, null),
            new RebootStatus(rebootRequired, rebootRequired ? ["Windows Update"] : [], DateTimeOffset.UtcNow.AddDays(-1)),
            updates,
            history ?? []);
}

/// <summary>
/// A device driven through the real agent HTTP client (Solvix.Agent.Core) against
/// the in-process API, with an in-memory "Windows" for configuration settings.
/// </summary>
public sealed class SimulatedDevice
{
    private string? _credential;

    public SimulatedDevice(ApiFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Http = fixture.CreateClient();
        Api = new AgentApiClient(Http, () => _credential);
        Settings = new InMemoryDeviceSettings();
        Snapshots = new InMemorySnapshotStore();
        Evaluator = new ConfigurationEvaluator(
            [Settings.For(SettingKind.ScreenLock), Settings.For(SettingKind.Power), Settings.For(SettingKind.UsbStorage), Settings.For(SettingKind.Service), Settings.For(SettingKind.FirewallProfile)],
            Snapshots,
            new InMemoryAppliedSettingsLedger(),
            TimeProvider.System,
            NullLogger<ConfigurationEvaluator>.Instance);
    }

    public HttpClient Http { get; }

    public AgentApiClient Api { get; }

    public InMemoryDeviceSettings Settings { get; }

    public InMemorySnapshotStore Snapshots { get; }

    public ConfigurationEvaluator Evaluator { get; }

    public Guid DeviceId { get; private set; }

    public Guid TenantId { get; private set; }

    public string Credential => _credential!;

    public DateTimeOffset LastBootUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(-1);

    public string? Revision { get; private set; }

    internal void Bind(EnrollDeviceResponse response)
    {
        DeviceId = response.DeviceId;
        TenantId = response.TenantId;
        _credential = response.DeviceCredential;
    }

    public Task<AgentWorkResponse> HeartbeatAsync() =>
        Api.HeartbeatAsync(new AgentHeartbeatRequest($"host-{DeviceId:N}"[..20], "corp.example", "Windows 11 Enterprise", "24H2", "26100.2314", "1.0.0", LastBootUtc), CancellationToken.None);

    /// <summary>Fetches the document (honouring the ETag), evaluates it locally and reports back.</summary>
    public async Task<ConfigurationReport?> SyncConfigurationAsync(bool force = true)
    {
        var document = await Api.GetConfigurationAsync(force ? null : Revision, CancellationToken.None);
        if (document is null)
        {
            return null;
        }

        Revision = document.Revision;
        var report = Evaluator.Evaluate(document);
        await Api.ReportConfigurationAsync(report, CancellationToken.None);
        return report;
    }
}

/// <summary>Key/value store standing in for the registry, power plan, SCM and firewall on a simulated device.</summary>
public sealed class InMemoryDeviceSettings
{
    public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

    public ISettingProvider For(SettingKind kind) => new Provider(kind, Values);

    public static string ValueOf(ConfigurationSetting setting) => setting switch
    {
        ScreenLockSetting l => l.Value.ToString(CultureInfo.InvariantCulture),
        PowerSetting p => p.Value.ToString(CultureInfo.InvariantCulture),
        UsbStorageSetting u => u.Access.ToString(),
        ServiceSetting s => $"{s.StartupType}/{s.State}",
        FirewallProfileSetting f => $"{f.Enabled}/{f.DefaultInboundAction}/{f.DefaultOutboundAction}",
        _ => setting.Describe(),
    };

    private sealed class Provider(SettingKind kind, Dictionary<string, string?> values) : ISettingProvider
    {
        public SettingKind Kind => kind;

        public bool SupportsRollback => true;

        public Observation Evaluate(ConfigurationSetting desired)
        {
            var actual = values.GetValueOrDefault(desired.Key);
            return new Observation(true, actual == ValueOf(desired), ValueOf(desired), actual ?? "(unset)");
        }

        public void Apply(ConfigurationSetting desired) => values[desired.Key] = ValueOf(desired);

        public string Capture(ConfigurationSetting desired) => values.GetValueOrDefault(desired.Key) ?? "<unset>";

        public void Restore(ConfigurationSetting desired, string capturedState) =>
            values[desired.Key] = capturedState == "<unset>" ? null : capturedState;
    }
}
