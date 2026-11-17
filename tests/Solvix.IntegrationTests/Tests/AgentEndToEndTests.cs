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
using Solvix.Agent.Core.Remote;
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
    public async Task Technician_runs_a_command_on_the_laptop_through_a_consented_remote_session()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var agent = new InProcessAgent(fixture, RemoteAccessMode.Unattended);
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant));
        var runtime = agent.Services.GetRequiredService<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var deviceId = agent.Services.GetRequiredService<IAgentStateStore>().Load().DeviceId!.Value;

        var worker = agent.Services.GetRequiredService<RemoteAccessWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await runtime.RunCycleAsync(CancellationToken.None); // heartbeat reports the local policy
            var session = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(
                await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/remote-sessions", new Solvix.Modules.Support.Application.StartRemoteSessionRequest("e2e"), ApiFixture.Json),
                HttpStatusCode.Created);

            // The agent picks the request up on its own, accepts (unattended policy) and starts a real shell.
            var active = DateTimeOffset.UtcNow.AddSeconds(20);
            while ((await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"))).Status
                   != Solvix.Modules.Support.Domain.RemoteSessionStatus.Active)
            {
                Assert.True(DateTimeOffset.UtcNow < active, "The agent never accepted the session.");
                await Task.Delay(300);
            }

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new Solvix.Modules.Support.Application.RemoteInputRequest("echo solvix-remote-ok\n"), ApiFixture.Json)).StatusCode);
            var seen = new System.Text.StringBuilder();
            long after = 0;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!seen.ToString().Contains("solvix-remote-ok", StringComparison.Ordinal) && DateTimeOffset.UtcNow < deadline)
            {
                var output = await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/output?after={after}&wait=2");
                var dto = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteOutputDto>(output);
                foreach (var chunk in dto.Chunks)
                {
                    seen.Append(chunk.Text);
                    after = chunk.Sequence;
                }
            }

            Assert.Contains("solvix-remote-ok", seen.ToString(), StringComparison.Ordinal);

            // `exit` ends the shell and with it the session.
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new Solvix.Modules.Support.Application.RemoteInputRequest("exit\n"), ApiFixture.Json);
            Solvix.Modules.Support.Application.RemoteSessionDto final;
            var until = DateTimeOffset.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(300);
                final = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"));
            }
            while (final.Status == Solvix.Modules.Support.Domain.RemoteSessionStatus.Active && DateTimeOffset.UtcNow < until);

            Assert.Equal(Solvix.Modules.Support.Domain.RemoteSessionStatus.Ended, final.Status);
            Assert.Equal(RemoteConsent.Unattended, final.Consent);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task With_consent_required_and_nobody_to_ask_the_agent_declines()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var agent = new InProcessAgent(fixture, RemoteAccessMode.ConsentRequired);
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant));
        var runtime = agent.Services.GetRequiredService<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var deviceId = agent.Services.GetRequiredService<IAgentStateStore>().Load().DeviceId!.Value;

        var worker = agent.Services.GetRequiredService<RemoteAccessWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await runtime.RunCycleAsync(CancellationToken.None);
            var session = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(
                await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/remote-sessions", new Solvix.Modules.Support.Application.StartRemoteSessionRequest(null), ApiFixture.Json),
                HttpStatusCode.Created);
            Solvix.Modules.Support.Application.RemoteSessionDto final;
            var until = DateTimeOffset.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(300);
                final = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"));
            }
            while (final.Status == Solvix.Modules.Support.Domain.RemoteSessionStatus.Pending && DateTimeOffset.UtcNow < until);
            Assert.Equal(Solvix.Modules.Support.Domain.RemoteSessionStatus.Denied, final.Status);
            Assert.Null(final.Consent);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class SyntheticDesktop : IRemoteDesktop
    {
        private int _frame;

        public List<ScreenInputEvent> Injected { get; } = [];

        public bool IsAvailable => true;

        public Task<ScreenFrame?> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ScreenFrame?>(new ScreenFrame([0xFF, 0xD8, 0xFF, 0xE0, (byte)Interlocked.Increment(ref _frame), 0xFF, 0xD9], 320, 200));

        public Task InjectAsync(ScreenInputEvent input, CancellationToken cancellationToken)
        {
            lock (Injected)
            {
                Injected.Add(input);
            }

            return Task.CompletedTask;
        }
    }

    private async Task<(Guid DeviceId, Guid Session)> OpenSessionAsync(HttpClient admin, InProcessAgent agent, RemoteSessionKind kind, bool control = false)
    {
        var deviceId = agent.Services.GetRequiredService<IAgentStateStore>().Load().DeviceId!.Value;
        var session = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(
            await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/remote-sessions", new Solvix.Modules.Support.Application.StartRemoteSessionRequest("e2e", kind, control), ApiFixture.Json),
            HttpStatusCode.Created);
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        while ((await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"))).Status
               != Solvix.Modules.Support.Domain.RemoteSessionStatus.Active)
        {
            Assert.True(DateTimeOffset.UtcNow < until, "The agent never accepted the session.");
            await Task.Delay(200);
        }

        return (deviceId, session.Id);
    }

    [Fact]
    public async Task Technician_transfers_files_with_the_laptop_inside_its_allowed_folder_only()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var agent = new InProcessAgent(fixture, RemoteAccessMode.Unattended, RemoteFeatures.Shell | RemoteFeatures.Files);
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant));
        var runtime = agent.Services.GetRequiredService<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var worker = agent.Services.GetRequiredService<RemoteAccessWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await runtime.RunCycleAsync(CancellationToken.None);
            var (_, sessionId) = await OpenSessionAsync(admin, agent, RemoteSessionKind.Files);
            var transfer = Path.Combine(agent.DataDirectory, "transfer");
            var outside = Path.Combine(agent.DataDirectory, "outside-secret.txt");
            await File.WriteAllTextAsync(outside, "not for transfer");

            // The computer offers only its transfer folder.
            var roots = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{sessionId}/files/list", new Solvix.Modules.Support.Application.ListRemoteFilesRequest(null), ApiFixture.Json));
            Assert.Equal(transfer, Assert.Single(roots.Entries!).Path);

            // Upload from the console lands on the laptop, verified.
            var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(FileLimits.ChunkBytes * 2 + 321);
            var staged = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.PostAsync($"/api/v1/remote-sessions/{sessionId}/files/upload?path={Uri.EscapeDataString(Path.Combine(transfer, "tool.bin"))}", new ByteArrayContent(data)),
                HttpStatusCode.Accepted);
            var uploaded = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.GetAsync($"/api/v1/remote-sessions/{sessionId}/files/operations/{staged.Id}?wait=20"));
            Assert.Equal(Solvix.Modules.Support.Domain.RemoteFileOperationStatus.Completed, uploaded.Status);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(transfer, "tool.bin")));

            // Download the same file back through the real agent.
            var download = await admin.GetAsync($"/api/v1/remote-sessions/{sessionId}/files/download?path={Uri.EscapeDataString(Path.Combine(transfer, "tool.bin"))}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(data, await download.Content.ReadAsByteArrayAsync());

            // Listing shows it.
            var listing = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{sessionId}/files/list", new Solvix.Modules.Support.Application.ListRemoteFilesRequest(transfer), ApiFixture.Json));
            Assert.Contains(listing.Entries!, e => e.Name == "tool.bin" && e.SizeBytes == data.Length);

            // Whatever the technician asks, the computer refuses anything outside its folder.
            var traversal = Path.Combine(transfer, "..", "outside-secret.txt");
            await HttpAssert.ProblemAsync(
                await admin.GetAsync($"/api/v1/remote-sessions/{sessionId}/files/download?path={Uri.EscapeDataString(traversal)}"), HttpStatusCode.Conflict, "file_download_failed");
            var escape = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{sessionId}/files/list", new Solvix.Modules.Support.Application.ListRemoteFilesRequest(agent.DataDirectory), ApiFixture.Json));
            Assert.Equal(Solvix.Modules.Support.Domain.RemoteFileOperationStatus.Failed, escape.Status);
            var evil = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.PostAsync($"/api/v1/remote-sessions/{sessionId}/files/upload?path={Uri.EscapeDataString(Path.Combine(agent.DataDirectory, "planted.txt"))}", new ByteArrayContent([1, 2, 3])),
                HttpStatusCode.Accepted);
            var refused = await HttpAssert.OkAsync<Solvix.Modules.Support.Application.RemoteFileOperationDto>(
                await admin.GetAsync($"/api/v1/remote-sessions/{sessionId}/files/operations/{evil.Id}?wait=20"));
            Assert.Equal(Solvix.Modules.Support.Domain.RemoteFileOperationStatus.Failed, refused.Status);
            Assert.False(File.Exists(Path.Combine(agent.DataDirectory, "planted.txt")));

            // Every step is in the audit trail.
            var audit = await HttpAssert.OkAsync<List<Solvix.Modules.Support.Application.RemoteFileOperationDto>>(await admin.GetAsync($"/api/v1/remote-sessions/{sessionId}/files/operations"));
            Assert.True(audit.Count >= 6);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Technician_watches_and_controls_the_screen_only_as_far_as_was_granted()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var desktop = new SyntheticDesktop();
        using var agent = new InProcessAgent(fixture, RemoteAccessMode.Unattended, RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control, desktop);
        agent.Services.GetRequiredService<BootstrapTokenStore>().Save(await fixture.CreateTokenAsync(tenant));
        var runtime = agent.Services.GetRequiredService<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var worker = agent.Services.GetRequiredService<RemoteAccessWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await runtime.RunCycleAsync(CancellationToken.None);

            // View only.
            var (_, viewOnly) = await OpenSessionAsync(admin, agent, RemoteSessionKind.Screen, control: false);
            var frame = await admin.GetAsync($"/api/v1/remote-sessions/{viewOnly}/screen/frame?after=0&wait=10");
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
            Assert.Equal("320", frame.Headers.GetValues("X-Frame-Width").Single());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{viewOnly}/input", new Solvix.Modules.Support.Application.RemoteInputRequest("""{"type":"move","x":0.1,"y":0.1}"""), ApiFixture.Json)).StatusCode);
            await admin.PostAsync($"/api/v1/remote-sessions/{viewOnly}/end", null);
            Assert.Empty(desktop.Injected);

            // With control: the click reaches the desktop; the picture keeps flowing.
            var (_, controlled) = await OpenSessionAsync(admin, agent, RemoteSessionKind.Screen, control: true);
            var first = await admin.GetAsync($"/api/v1/remote-sessions/{controlled}/screen/frame?after=0&wait=10");
            var seq = long.Parse(first.Headers.GetValues("X-Frame-Sequence").Single());
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{controlled}/input", new Solvix.Modules.Support.Application.RemoteInputRequest("""{"type":"down","x":0.25,"y":0.5,"button":"left"}"""), ApiFixture.Json);
            for (var i = 0; i < 100 && desktop.Injected.Count == 0; i++)
            {
                await Task.Delay(100);
            }

            var click = Assert.Single(desktop.Injected);
            Assert.Equal((ScreenInputEvent.Down, 0.25, 0.5, "left"), (click.Type, click.X, click.Y, click.Button));
            // The synthetic desktop changes every frame, so newer frames keep arriving.
            var next = await admin.GetAsync($"/api/v1/remote-sessions/{controlled}/screen/frame?after={seq}&wait=10");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.True(long.Parse(next.Headers.GetValues("X-Frame-Sequence").Single()) > seq);

            // Ending the session stops the agent's sharing.
            await admin.PostAsync($"/api/v1/remote-sessions/{controlled}/end", null);
            var ended = await admin.GetAsync($"/api/v1/remote-sessions/{controlled}/screen/frame?after=0&wait=0");
            Assert.Equal("Ended", ended.Headers.GetValues("X-Session-Status").Single());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
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

        public InProcessAgent(ApiFixture fixture, RemoteAccessMode remoteMode = RemoteAccessMode.Disabled, RemoteFeatures features = RemoteFeatures.Shell, IRemoteDesktop? desktop = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SolvixAgent:ServerUrl"] = "https://localhost/",
                ["SolvixAgent:DataDirectory"] = _directory,
                ["SolvixAgent:Backoff:JitterRatio"] = "0",
                ["SolvixAgent:RemoteAccess:Mode"] = remoteMode.ToString(),
                ["SolvixAgent:RemoteAccess:Features"] = features.ToString(),
                ["SolvixAgent:LocalPipeName"] = "solvix-e2e-" + Guid.NewGuid().ToString("N"),
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSolvixAgentCore(configuration);
            services.AddSingleton<IDeviceKeyStore>(_ => new FileDeviceKeyStore(_directory));
            services.AddSingleton<ISecretProtector, FilePermissionSecretProtector>();
            services.AddSingleton<IInventoryCollector, TestHardwareCollector>();
            services.AddSingleton(Network);
            if (desktop is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(desktop));
            }

            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            services.Replace(ServiceDescriptor.Singleton<IAgentTransport>(sp =>
                new TestServerTransport(fixture, sp.GetRequiredService<DeviceCredentialStore>(), Network)));
            Services = services.BuildServiceProvider();
        }

        public string DataDirectory => _directory;

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
