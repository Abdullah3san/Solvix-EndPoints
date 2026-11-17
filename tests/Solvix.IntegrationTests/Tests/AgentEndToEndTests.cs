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
using Solvix.Agent.Core.LocalApi;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
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
        Assert.Equal(DeviceConnectivity.Online, details.Summary.Connectivity);
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
        await fixture.ExecutePlatformSqlAsync("UPDATE devices SET last_seen_at_utc = now() - interval '1 hour' WHERE id = @d", ("d", deviceId));
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}"));
        Assert.Equal(DeviceConnectivity.Offline, details.Summary.Connectivity);

        // --- Reconnection -------------------------------------------------------
        agent.Network.Offline = false;
        await runtime.RunCycleAsync(CancellationToken.None);
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{deviceId}"));
        Assert.Equal(DeviceConnectivity.Online, details.Summary.Connectivity);
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
    public async Task Person_at_the_laptop_raises_a_ticket_and_chats_with_a_technician_through_the_agent()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var agent = new InProcessAgent(fixture);
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant));
        await agent.Services.GetRequiredService<AgentRuntime>().RunCycleAsync(CancellationToken.None);
        var deviceId = agent.Services.GetRequiredService<IAgentStateStore>().Load().DeviceId!.Value;

        var server = agent.Services.GetRequiredService<LocalApiServer>();
        await server.StartAsync(CancellationToken.None);
        try
        {
            var user = new LocalApiClient(agent.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Solvix.Agent.Core.Configuration.AgentOptions>>().Value.LocalPipeName);

            // Ticket from the laptop: lands in the console, tied to the device and to the OS user.
            var ticket = await user.CallAsync(
                new LocalRequest { Op = LocalOps.SubmitTicket, Ticket = new SubmitTicketRequest { Subject = "VPN will not connect", RequesterName = "spoofed", Priority = TicketPriority.High } },
                CancellationToken.None);
            Assert.True(ticket.Ok, ticket.Error);
            var tickets = await HttpAssert.OkAsync<PagedResult<Solvix.Modules.Support.Application.TicketSummaryDto>>(await admin.GetAsync("/api/v1/tickets"));
            var summary = Assert.Single(tickets.Items);
            Assert.Equal("VPN will not connect", summary.Subject);
            Assert.Equal(deviceId, summary.DeviceId);
            Assert.Equal(Environment.UserName, summary.RequesterName);

            // Technician answers in chat; the person's long poll receives it; their reply reaches the console.
            var poll = user.CallAsync(new LocalRequest { Op = LocalOps.PollChat, After = 0, WaitSeconds = 15 }, CancellationToken.None);
            await Task.Delay(1000);
            await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/chat", new Solvix.Modules.Support.Application.SendChatRequest("Which network are you on?"), ApiFixture.Json);
            var received = await poll;
            Assert.Equal("Which network are you on?", Assert.Single(received.Messages!).Body);

            Assert.True((await user.CallAsync(new LocalRequest { Op = LocalOps.SendChat, Body = "Home wifi" }, CancellationToken.None)).Ok);
            var thread = await HttpAssert.OkAsync<List<Solvix.Modules.Support.Application.ChatMessageDto>>(await admin.GetAsync($"/api/v1/devices/{deviceId}/chat"));
            Assert.Equal(["Which network are you on?", "Home wifi"], thread.Select(m => m.Body));
            Assert.Equal(Environment.UserName, thread[1].SenderName);

            // The technician resolves the ticket; the person sees the new status.
            await admin.PatchAsJsonAsync($"/api/v1/tickets/{summary.Id}", new Solvix.Modules.Support.Application.UpdateTicketRequest(TicketStatus.Resolved, null, null), ApiFixture.Json);
            var mine = await user.CallAsync(new LocalRequest { Op = LocalOps.ListTickets }, CancellationToken.None);
            Assert.Equal(TicketStatus.Resolved, Assert.Single(mine.Tickets!).Status);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private sealed class InProcessAgent : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "solvix-e2e-agent-" + Guid.NewGuid().ToString("N"));

        public InProcessAgent(ApiFixture fixture)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SolvixAgent:ServerUrl"] = "https://localhost/",
                ["SolvixAgent:DataDirectory"] = _directory,
                ["SolvixAgent:Backoff:JitterRatio"] = "0",
                ["SolvixAgent:LocalPipeName"] = "solvix-e2e-" + Guid.NewGuid().ToString("N"),
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
