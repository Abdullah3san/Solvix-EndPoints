namespace Solvix.IntegrationTests.Fixtures;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Configuration;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Application.Deployments;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Helpers for Phase 09 tests. Devices are enrolled with the real Phase 03 flow and authenticate with
/// their certificate; installing updates, restarting and enforcing settings arrive as signed commands
/// that the simulated executor verifies and answers through the command channel.
/// </summary>
public static class EndpointTestKit
{
    public static Task<string> CreateEnrollmentTokenAsync(this ApiFixture fixture, TenantSession admin) =>
        fixture.CreateTokenAsync(admin, maxUses: 100);

    public static async Task<SimulatedDevice> EnrollDeviceAsync(this ApiFixture fixture, string token, string hostname)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var enrolled = await fixture.EnrollAsync(token, DeviceTestKit.Identity(hostname: hostname));
        var device = new SimulatedDevice(fixture, enrolled);
        await device.HeartbeatAsync();
        return device;
    }

    /// <summary>A user who manages endpoints but holds no high-impact approval permission (formerly "Admin").</summary>
    public static async Task<string> AddEndpointManagerAsync(this ApiFixture fixture, TenantSession owner)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var member = await fixture.AddMemberWithPermissionsAsync(owner, Permissions.Endpoints.Manage, Permissions.Devices.View);
        return member.Token;
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

/// <summary>Thrown when the server refuses a report for a command that is no longer running.</summary>
public sealed class AgentConflictException(string message) : Exception(message);

/// <summary>What one poll handed the device: the work response plus the signed commands it fetched.</summary>
public sealed record DeviceWork(
    AgentWorkResponse Work,
    IReadOnlyList<PatchJobDto> PatchJobs,
    IReadOnlyList<RestartRequestDto> Restarts)
{
    public string ConfigurationRevision => Work.ConfigurationRevision;

    public bool InventoryRequested => Work.InventoryRequested;
}

/// <summary>
/// A device with its agent (certificate-authenticated HTTP client) and its privileged executor (an
/// in-memory "Windows" for configuration settings). Commands are fetched and verified like the agent does.
/// </summary>
public sealed class SimulatedDevice
{
    private readonly Dictionary<Guid, Guid> _jobCommands = [];
    private readonly Queue<CommandEnvelope> _configurationCommands = new();

    public SimulatedDevice(ApiFixture fixture, EnrolledDevice enrolled)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(enrolled);
        Enrolled = enrolled;
        Http = fixture.CreateDeviceClient(enrolled.Certificate);
        Settings = new InMemoryDeviceSettings();
        Snapshots = new InMemorySnapshotStore();
        ISettingProvider[] providers =
            [Settings.For(SettingKind.ScreenLock), Settings.For(SettingKind.Power), Settings.For(SettingKind.UsbStorage), Settings.For(SettingKind.Service), Settings.For(SettingKind.FirewallProfile)];
        Evaluator = new ConfigurationEvaluator(providers, Snapshots, new InMemoryAppliedSettingsLedger(), TimeProvider.System, NullLogger<ConfigurationEvaluator>.Instance);
        Monitor = new ConfigurationEvaluator(providers, new InMemorySnapshotStore(), new InMemoryAppliedSettingsLedger(), TimeProvider.System, NullLogger<ConfigurationEvaluator>.Instance)
        {
            ReadOnly = true,
        };
        Api = new DeviceApi(this);
    }

    public EnrolledDevice Enrolled { get; }

    public HttpClient Http { get; }

    /// <summary>The agent's endpoint management calls (kept close to the agent client's shape).</summary>
    public DeviceApi Api { get; }

    public InMemoryDeviceSettings Settings { get; }

    public InMemorySnapshotStore Snapshots { get; }

    /// <summary>The executor's evaluator (applies and reverts).</summary>
    public ConfigurationEvaluator Evaluator { get; }

    /// <summary>The agent's read-only evaluator.</summary>
    public ConfigurationEvaluator Monitor { get; }

    public Guid DeviceId => Enrolled.DeviceId;

    public Guid TenantId => Enrolled.Enrollment.TenantId;

    public DateTimeOffset LastBootUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(-1);

    public string? Revision { get; private set; }

    /// <summary>One agent poll: posts the boot time, then fetches and accepts the signed commands it caused.</summary>
    public async Task<DeviceWork> HeartbeatAsync()
    {
        var work = await HttpAssert.OkAsync<AgentWorkResponse>(
            await Http.PostAsJsonAsync("/" + EndpointAgentRoutes.Work, new AgentWorkRequest(LastBootUtc), ContractJson.Options));
        var jobs = new List<PatchJobDto>();
        var restarts = new List<RestartRequestDto>();
        foreach (var signed in await Http.FetchAsync())
        {
            var envelope = await Http.VerifyAsync(signed);
            switch (envelope.CommandType)
            {
                case CommandTypes.PatchInstall:
                    Assert.Equal(HttpStatusCode.NoContent, (await Http.AckAsync(envelope.ExecutionId)).StatusCode);
                    var job = ContractJson.Deserialize<PatchJobDto>(envelope.Parameters["job"])!;
                    _jobCommands[job.JobId] = envelope.ExecutionId;
                    jobs.Add(job);
                    break;
                case CommandTypes.Restart:
                    Assert.Equal(HttpStatusCode.NoContent, (await Http.AckAsync(envelope.ExecutionId)).StatusCode);
                    restarts.Add(new RestartRequestDto(
                        envelope.ExecutionId,
                        "Restart",
                        DateTimeOffset.UtcNow,
                        null,
                        int.Parse(envelope.Parameters.GetValueOrDefault("delaySeconds") ?? "0", CultureInfo.InvariantCulture),
                        envelope.Parameters.GetValueOrDefault("message") ?? string.Empty));
                    break;
                case CommandTypes.ConfigurationApply:
                    _configurationCommands.Enqueue(envelope);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected command {envelope.CommandType}.");
            }
        }

        return new DeviceWork(work, jobs, restarts);
    }

    /// <summary>
    /// The agent's configuration cycle: read-only check and report, then the poll that lets the server send
    /// a signed <c>configuration.apply</c>, which the executor runs. Returns the last report the device sent.
    /// </summary>
    public async Task<ConfigurationReport?> SyncConfigurationAsync(bool force = true)
    {
        var document = await Api.GetConfigurationAsync(force ? null : Revision, CancellationToken.None);
        if (document is null)
        {
            return null;
        }

        Revision = document.Revision;
        var report = Monitor.Evaluate(document);
        await Api.ReportConfigurationAsync(report, CancellationToken.None);
        await HeartbeatAsync();
        while (_configurationCommands.TryDequeue(out var command))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await Http.AckAsync(command.ExecutionId)).StatusCode);
            var applied = Evaluator.Evaluate(ContractJson.Deserialize<ConfigurationDocument>(command.Parameters["document"])!);
            Assert.Equal(HttpStatusCode.NoContent, (await Http.ResultAsync(command.ExecutionId, CommandOutcome.Succeeded, ContractJson.Serialize(applied))).StatusCode);
            report = applied;
        }

        return report;
    }

    public Guid CommandFor(Guid jobId) =>
        _jobCommands.TryGetValue(jobId, out var executionId) ? executionId : throw new AgentConflictException("No command for this job on this device.");

    public sealed class DeviceApi(SimulatedDevice device)
    {
        public async Task UploadInventoryAsync(PatchInventoryReport report, CancellationToken cancellationToken)
        {
            var response = await device.Http.PostAsJsonAsync("/" + EndpointAgentRoutes.PatchInventory, report, ContractJson.Options, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        /// <summary>The executor's answer to a <c>patch.install</c> command: the job result as command output.</summary>
        public Task ReportResultAsync(Guid jobId, PatchJobResult result, CancellationToken cancellationToken) =>
            ReportCommandResultAsync(device.CommandFor(jobId), result, cancellationToken);

        public async Task ReportCommandResultAsync(Guid executionId, PatchJobResult result, CancellationToken cancellationToken)
        {
            var response = await device.Http.ResultAsync(executionId, CommandOutcome.Succeeded, ContractJson.Serialize(result));
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                throw new AgentConflictException(await response.Content.ReadAsStringAsync(cancellationToken));
            }

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        /// <summary>The executor's answer to a <c>system.restart</c> command.</summary>
        public async Task AcknowledgeRestartAsync(Guid restartId, RestartAck ack, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(ack);
            var response = ack.Status == RestartAckStatus.Initiated
                ? await device.Http.ResultAsync(restartId, CommandOutcome.Succeeded, "{}")
                : await device.Http.ResultAsync(restartId, CommandOutcome.Failed, null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public async Task<ConfigurationDocument?> GetConfigurationAsync(string? knownRevision, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/" + EndpointAgentRoutes.Configuration);
            if (knownRevision is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{knownRevision}\"");
            }

            using var response = await device.Http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<ConfigurationDocument>(ContractJson.Options, cancellationToken);
        }

        public async Task ReportConfigurationAsync(ConfigurationReport report, CancellationToken cancellationToken)
        {
            var response = await device.Http.PostAsJsonAsync("/" + EndpointAgentRoutes.ConfigurationReport, report, ContractJson.Options, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
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
