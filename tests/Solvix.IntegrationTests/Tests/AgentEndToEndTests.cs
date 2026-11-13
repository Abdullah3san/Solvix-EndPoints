namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Health;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Results;

/// <summary>The real agent core (runtime, enrollment, credential store, API client) against the real API.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgentEndToEndTests(ApiFixture fixture)
{
    [Fact]
    public async Task Agent_enrolls_reports_survives_disconnection_and_recovers_from_credential_loss()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var agent = new InProcessAgent(fixture);
        var runtime = agent.Services.GetRequiredService<AgentRuntime>();

        // --- Enrollment -----------------------------------------------------
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant, maxUses: 1));
        Assert.Equal(TimeSpan.Zero, await runtime.RunCycleAsync(CancellationToken.None));
        var state = agent.Services.GetRequiredService<IAgentStateStore>().Load();
        Assert.True(state.IsEnrolled);
        Assert.Equal(tenant.TenantId, state.TenantId);
        Assert.False(agent.Services.GetRequiredService<BootstrapTokenStore>().HasToken);
        var deviceId = state.DeviceId!.Value;

        // --- Heartbeat + inventory + update check -------------------------------
        var wait = await runtime.RunCycleAsync(CancellationToken.None);
        Assert.InRange(wait, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}"));
        Assert.Equal(ConnectivityState.Online, details.Summary.Connectivity);
        Assert.Equal(AgentInfo.Version, details.Summary.AgentVersion);
        Assert.True(details.Summary.AgentUpdateAvailable);
        Assert.Equal("Contoso", details.Summary.Manufacturer);
        var inventory = await HttpAssert.OkAsync<DeviceInventoryDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}/inventory"));
        Assert.Equal("Solvix Test App", Assert.Single(inventory.Report.Software).Name);

        // The offered update was refused locally (no allow-listed host / pinned publisher): reported, not installed.
        Assert.Contains(agent.Services.GetRequiredService<AgentHealthMonitor>().Issues, i => i.Component == AgentRuntime.UpdatesComponent);

        // --- Disconnection: network down, exponential backoff ------------------
        agent.Network.Offline = true;
        var delays = new List<TimeSpan>();
        agent.Clock.Advance(wait);
        for (var i = 0; i < 4; i++)
        {
            delays.Add(await runtime.RunCycleAsync(CancellationToken.None));
            agent.Clock.Advance(delays[^1]);
        }

        Assert.True(delays.Zip(delays.Skip(1)).All(p => p.Second > p.First), string.Join(", ", delays));
        await fixture.SimulateSilenceAsync(deviceId, TimeSpan.FromHours(1));
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}"));
        Assert.Equal(ConnectivityState.Offline, details.Summary.Connectivity);

        // --- Reconnection -------------------------------------------------------
        agent.Network.Offline = false;
        await runtime.RunCycleAsync(CancellationToken.None);
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}"));
        Assert.Equal(ConnectivityState.Online, details.Summary.Connectivity);
        Assert.Equal(4, details.Health!.ConsecutiveFailures);
        Assert.True(agent.Services.GetRequiredService<ConnectionMonitor>().IsConnected);

        // --- Credential loss: device deleted by an admin, new token supplied ---
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/devices/{deviceId}")).StatusCode);
        agent.Clock.Advance(TimeSpan.FromMinutes(2));
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant, maxUses: 1));
        for (var i = 0; i < 3; i++)
        {
            agent.Clock.Advance(await runtime.RunCycleAsync(CancellationToken.None)); // 401 x3 -> credential reset
        }

        Assert.Equal(TimeSpan.Zero, await runtime.RunCycleAsync(CancellationToken.None)); // re-enrolled
        var newState = agent.Services.GetRequiredService<IAgentStateStore>().Load();
        Assert.NotEqual(deviceId, newState.DeviceId);
        await runtime.RunCycleAsync(CancellationToken.None);
        var devices = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"));
        Assert.Equal(newState.DeviceId, Assert.Single(devices.Items).Id);
    }

    [Fact]
    public async Task Real_agent_verifies_executes_and_reports_signed_commands_and_refuses_tampered_ones()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var agent = new InProcessAgent(fixture);
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant, maxUses: 1));
        await agent.Services.GetRequiredService<AgentRuntime>().RunCycleAsync(CancellationToken.None);
        var deviceId = agent.Services.GetRequiredService<IAgentStateStore>().Load().DeviceId!.Value;
        var processor = agent.Services.GetRequiredService<Solvix.Agent.Core.RemoteManagement.CommandProcessor>();

        // A read-only operation runs on the device and its result reaches the technician.
        var uptime = await admin.RequestAsync(deviceId, "uptime");
        await processor.PollOnceAsync(CancellationToken.None);
        await processor.DrainAsync();
        var done = await admin.GetCommandAsync(uptime.Id);
        Assert.Equal(Solvix.Modules.RemoteManagement.Domain.CommandExecutionStatus.Succeeded, done.Status);
        Assert.Contains("uptimeSeconds", done.Output, StringComparison.Ordinal);

        // Someone with write access to the database changes a queued command: the device refuses it.
        var info = await admin.RequestAsync(deviceId, "system-info");
        await fixture.ExecutePlatformSqlAsync(
            "UPDATE remote_command_executions SET signed_command = replace(signed_command, '\"signature\":\"', '\"signature\":\"AA') WHERE id = @id",
            ("id", info.Id));
        await processor.PollOnceAsync(CancellationToken.None);
        await processor.DrainAsync();
        var refused = await admin.GetCommandAsync(info.Id);
        Assert.Equal(Solvix.Modules.RemoteManagement.Domain.CommandExecutionStatus.Rejected, refused.Status);
        Assert.Equal(Solvix.Agent.Contracts.RemoteManagement.CommandRejectionCodes.InvalidSignature, refused.ErrorCode);

        // A privileged operation without the executor service fails cleanly (and is approved first).
        var restart = await admin.RequestAsync(deviceId, "restart", new Solvix.Modules.RemoteManagement.CommandBroker.OperationRequest
        {
            ConfirmDeviceName = (await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}"))).Summary.Hostname,
        });
        var approver = await fixture.AddMemberWithPermissionsAsync(tenant, Solvix.SharedKernel.Authorization.Permissions.Remote.Restart, Solvix.SharedKernel.Authorization.Permissions.Remote.ApprovalsManage);
        using var approverClient = fixture.CreateClient(approver.Token);
        await HttpAssert.OkAsync<Solvix.Modules.RemoteManagement.CommandBroker.ApprovalDto>(await approverClient.PostAsJsonAsync(
            $"/api/v1/remote/approvals/{restart.ApprovalId}/approve", new Solvix.Modules.RemoteManagement.CommandBroker.ApprovalDecisionRequest(null), ApiFixture.Json));
        await processor.PollOnceAsync(CancellationToken.None);
        await processor.DrainAsync();
        var failed = await admin.GetCommandAsync(restart.Id);
        Assert.Equal(Solvix.Modules.RemoteManagement.Domain.CommandExecutionStatus.Failed, failed.Status);
        Assert.Equal(Solvix.Agent.Core.RemoteManagement.UnavailablePrivilegedOperationHost.ErrorCode, failed.ErrorCode);
    }

    [Fact]
    public async Task Real_agent_downloads_verifies_and_installs_a_deployed_package_and_reports_the_result()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var installed = new List<Solvix.Agent.Contracts.Software.DetectedApplication>();
        var executor = new RecordingExecutor(installed);
        using var agent = new InProcessAgent(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<Solvix.Agent.Core.Software.IInstalledApplicationSource>(new ListApplications(installed)));
            services.Replace(ServiceDescriptor.Singleton<Solvix.Agent.Core.Software.ISoftwareExecutor>(executor));
        });
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant, maxUses: 1));
        await agent.Services.GetRequiredService<AgentRuntime>().RunCycleAsync(CancellationToken.None);
        var deviceId = agent.Services.GetRequiredService<IAgentStateStore>().Load().DeviceId!.Value;
        var processor = agent.Services.GetRequiredService<Solvix.Agent.Core.RemoteManagement.CommandProcessor>();

        var bytes = SoftwareTestKit.Msi();
        var package = await admin.ConfigureAsync((await admin.UploadAsync("notes.msi", bytes)).Id, SoftwareTestKit.MsiConfiguration());
        var created = await admin.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, deviceId));
        foreach (var permissions in new[] { 1, 2 })
        {
            var approver = await fixture.AddMemberWithPermissionsAsync(tenant, Solvix.SharedKernel.Authorization.Permissions.Software.Approve);
            using var client = fixture.CreateClient(approver.Token);
            await HttpAssert.OkAsync<Solvix.Modules.Software.Deployments.DeploymentDetailDto>(await client.DecideAsync(created.Deployment.Id, true));
        }

        await fixture.RunDeploymentsAsync();
        await processor.PollOnceAsync(CancellationToken.None);
        await processor.DrainAsync();

        // The executor received the signed command and the package the agent downloaded and verified.
        var (request, staged) = Assert.Single(executor.Requests);
        Assert.Equal(bytes, staged);
        Assert.Equal(deviceId, request.DeviceId);

        var detail = await admin.GetDeploymentAsync(created.Deployment.Id);
        var target = Assert.Single(detail.Targets);
        Assert.Equal(Solvix.Modules.Software.Domain.TargetStatus.Succeeded, target.Status);
        Assert.Equal(Solvix.Agent.Contracts.Software.SoftwareOutcome.Installed, target.Outcome);
        Assert.Equal("5.2.0", target.DetectedVersion);
        Assert.Equal(Solvix.Modules.Software.Domain.DeploymentStatus.Completed, detail.Deployment.Status);

        // A second deployment of the same package finds it installed and does not download or run anything.
        var again = await admin.CreateDeploymentAsync(SoftwareTestKit.Deployment(package.Id, deviceId));
        foreach (var permissions in new[] { 1, 2 })
        {
            var approver = await fixture.AddMemberWithPermissionsAsync(tenant, Solvix.SharedKernel.Authorization.Permissions.Software.Approve);
            using var client = fixture.CreateClient(approver.Token);
            await HttpAssert.OkAsync<Solvix.Modules.Software.Deployments.DeploymentDetailDto>(await client.DecideAsync(again.Deployment.Id, true));
        }

        await fixture.RunDeploymentsAsync();
        await processor.PollOnceAsync(CancellationToken.None);
        await processor.DrainAsync();
        Assert.Single(executor.Requests);
        Assert.Equal(Solvix.Agent.Contracts.Software.SoftwareOutcome.AlreadyInstalled, Assert.Single((await admin.GetDeploymentAsync(again.Deployment.Id)).Targets).Outcome);
    }

    private sealed class ListApplications(List<Solvix.Agent.Contracts.Software.DetectedApplication> items) : Solvix.Agent.Core.Software.IInstalledApplicationSource
    {
        public IReadOnlyList<Solvix.Agent.Contracts.Software.DetectedApplication> GetInstalledApplications() => [.. items];
    }

    private sealed class RecordingExecutor(List<Solvix.Agent.Contracts.Software.DetectedApplication> installed) : Solvix.Agent.Core.Software.ISoftwareExecutor
    {
        public List<(Solvix.Agent.Core.Software.ExecutorRequest Request, byte[] Staged)> Requests { get; } = [];

        public Task<Solvix.Agent.Core.Software.ExecutorResponse> ExecuteAsync(Solvix.Agent.Core.Software.ExecutorRequest request, CancellationToken cancellationToken)
        {
            Requests.Add((request, File.ReadAllBytes(request.PackagePath!)));
            installed.Add(new Solvix.Agent.Contracts.Software.DetectedApplication("Contoso Notes", "5.2.0", "Contoso Ltd", SoftwareTestKit.ProductCode));
            return Task.FromResult(new Solvix.Agent.Core.Software.ExecutorResponse { Executed = true, ExitCode = 0 });
        }
    }

    private sealed class InProcessAgent : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "solvix-e2e-agent-" + Guid.NewGuid().ToString("N"));

        public InProcessAgent(ApiFixture fixture, Action<IServiceCollection>? configure = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SolvixAgent:ServerUrl"] = "https://localhost/",
                ["SolvixAgent:DataDirectory"] = _directory,
                ["SolvixAgent:Backoff:JitterRatio"] = "0",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSolvixAgentCore(configuration);
            services.AddSingleton<IDeviceKeyStore>(_ => new FileDeviceKeyStore(_directory));
            services.AddSingleton<ISecretProtector, FilePermissionSecretProtector>();
            services.AddSingleton<IInventoryCollector, TestHardwareCollector>();
            services.AddSingleton(Network);
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            services.Replace(ServiceDescriptor.Singleton<IAgentTransport>(sp =>
                new TestServerTransport(fixture, sp.GetRequiredService<DeviceCredentialStore>(), Network)));
            configure?.Invoke(services);
            Services = services.BuildServiceProvider();
        }

        public NetworkSwitch Network { get; } = new();

        public ManualClock Clock { get; } = new();

        public ServiceProvider Services { get; }

        public void Dispose()
        {
            Services.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NetworkSwitch
    {
        public bool Offline { get; set; }
    }

    /// <summary>
    /// Routes the agent's HTTPS calls into the in-process server. The TLS layer is
    /// replaced by the trusted-proxy contract (forwarded certificate + scheme);
    /// real mutual TLS is covered by <see cref="AgentMutualTlsTests"/>.
    /// </summary>
    private sealed class TestServerTransport(ApiFixture fixture, DeviceCredentialStore credentials, NetworkSwitch network) : IAgentTransport
    {
        public HttpMessageHandler CreateHandler() => new ProxyHandler(credentials, network) { InnerHandler = fixture.Factory.Server.CreateHandler() };

        private sealed class ProxyHandler(DeviceCredentialStore credentials, NetworkSwitch network) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (network.Offline)
                {
                    throw new HttpRequestException("Network is unreachable", new System.Net.Sockets.SocketException(101));
                }

                request.Headers.Add("X-Forwarded-Proto", "https");
                if (credentials.GetClientCertificate() is { } certificate)
                {
                    request.Headers.Add(SolvixApiFactory.ForwardedCertificateHeader, ClientCertificateHeader.Encode(certificate));
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    private sealed class TestHardwareCollector : IInventoryCollector
    {
        private readonly string _hardwareId = Guid.NewGuid().ToString();

        public string Name => "system";

        public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
        {
            builder.System = new SystemInfo { Manufacturer = "Contoso", Model = "Book 15", HardwareId = _hardwareId, SerialNumber = "E2E-" + _hardwareId[..8] };
            builder.Software.Add(new InstalledApplication { Name = "Solvix Test App", Version = "1.0" });
            return Task.CompletedTask;
        }
    }
}
