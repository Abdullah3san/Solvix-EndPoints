namespace Solvix.Agent.Tests;

using System.Security.Cryptography;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Health;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;

public sealed class AgentRuntimeTests
{
    private const string Token = "svxe_test-token";

    [Fact]
    public async Task Without_a_token_the_agent_waits_and_reports_not_enrolled()
    {
        using var agent = new AgentHarness();
        var delay = await agent.Get<AgentRuntime>().RunCycleAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(1), delay);
        Assert.Empty(agent.Api.Calls);
        Assert.Contains(agent.Get<AgentHealthMonitor>().Issues, i => i.Component == AgentRuntime.EnrollmentComponent);
    }

    [Fact]
    public async Task Enrollment_stores_credential_deletes_token_then_heartbeats_and_sends_inventory()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();

        Assert.Equal(TimeSpan.Zero, await runtime.RunCycleAsync(CancellationToken.None));
        Assert.False(agent.Get<BootstrapTokenStore>().HasToken);
        var state = agent.Get<IAgentStateStore>().Load();
        Assert.Equal(agent.Api.DeviceId, state.DeviceId);
        Assert.True(state.IsEnrolled);
        var certificate = agent.Get<DeviceCredentialStore>().GetClientCertificate();
        Assert.NotNull(certificate);
        Assert.True(certificate.HasPrivateKey);

        var delay = await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Equal(["enroll", "heartbeat", "inventory", "updates"], agent.Api.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), delay);
        var report = Assert.Single(agent.Api.Inventories);
        Assert.Equal("Contoso", report.System!.Manufacturer);
        Assert.NotNull(report.ContentHash);
    }

    [Fact]
    public async Task Enrollment_key_and_state_survive_a_restart()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        await agent.Get<AgentRuntime>().RunCycleAsync(CancellationToken.None);

        // A second credential store instance (= new process) loads the same identity.
        using var reloaded = new DeviceCredentialStore(
            new Solvix.Agent.Core.Configuration.AgentOptions { DataDirectory = agent.DataDirectory }.Wrap(),
            new FileDeviceKeyStore(agent.DataDirectory),
            new FileAgentStateStore(new Solvix.Agent.Core.Configuration.AgentOptions { DataDirectory = agent.DataDirectory }.Wrap()));
        var certificate = reloaded.GetClientCertificate();
        Assert.NotNull(certificate);
        Assert.Equal(agent.Get<DeviceCredentialStore>().GetClientCertificate()!.Thumbprint, certificate.Thumbprint);
    }

    [Fact]
    public async Task Rejected_token_is_not_retried_in_a_loop()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        agent.Api.EnrollFailure = new AgentApiException(AgentApiFailure.Authentication, System.Net.HttpStatusCode.Unauthorized, "enrollment_token_expired", "expired");
        var runtime = agent.Get<AgentRuntime>();

        Assert.Equal(TimeSpan.FromMinutes(5), await runtime.RunCycleAsync(CancellationToken.None));
        Assert.False(agent.Get<BootstrapTokenStore>().HasToken);
        Assert.True(File.Exists(Path.Combine(agent.DataDirectory, "enrollment.token.rejected")));
        Assert.True(agent.Get<BootstrapTokenStore>().HasRejectedToken);
        Assert.Equal(0, agent.KeyFileCount); // the generated key was discarded

        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Single(agent.Api.Calls); // no second attempt without a new token

        // Supplying a new token clears the rejected marker.
        agent.Get<BootstrapTokenStore>().Save(Token + "-new");
        Assert.False(agent.Get<BootstrapTokenStore>().HasRejectedToken);
        Assert.True(agent.Get<BootstrapTokenStore>().HasToken);
    }

    [Fact]
    public async Task Transient_failures_back_off_exponentially_and_recovery_is_reported()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);

        // Server goes away.
        for (var i = 0; i < 5; i++)
        {
            agent.Api.Failures.Enqueue(FakeAgentApi.Transient());
        }

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            delays.Add(await runtime.RunCycleAsync(CancellationToken.None));
            agent.Time.Advance(delays[^1]);
        }

        Assert.Equal([5, 10, 20, 40, 80], delays.Select(d => d.TotalSeconds));
        var connection = agent.Get<ConnectionMonitor>();
        Assert.Equal(5, connection.ConsecutiveFailures);
        Assert.False(connection.IsConnected);
        Assert.Contains(agent.Get<AgentHealthMonitor>().Issues, i => i.Component == AgentRuntime.CommunicationComponent);

        // Server is back: the heartbeat reports the outage, then the state resets.
        await runtime.RunCycleAsync(CancellationToken.None);
        var reconnect = agent.Api.Heartbeats.Single();
        Assert.Equal(5, reconnect.Health.ConsecutiveFailures);
        Assert.Equal(AgentHealthStatus.Degraded, reconnect.Health.Status);
        Assert.True(connection.IsConnected);
        Assert.Equal(0, connection.ConsecutiveFailures);
        Assert.DoesNotContain(agent.Get<AgentHealthMonitor>().Issues, i => i.Component == AgentRuntime.CommunicationComponent);
    }

    [Fact]
    public async Task Backoff_is_capped()
    {
        using var agent = new AgentHarness(o => o.Backoff.MaxSeconds = 30);
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        for (var i = 0; i < 10; i++)
        {
            agent.Api.Failures.Enqueue(FakeAgentApi.Transient());
        }

        TimeSpan last = default;
        for (var i = 0; i < 10; i++)
        {
            last = await runtime.RunCycleAsync(CancellationToken.None);
        }

        Assert.Equal(TimeSpan.FromSeconds(30), last);
    }

    [Fact]
    public async Task Revoked_credential_triggers_re_enrollment_when_a_new_token_is_available()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var firstKey = agent.Get<IAgentStateStore>().Load().KeyName;

        // The device was retired; an admin supplies a new token.
        agent.Get<BootstrapTokenStore>().Save(Token + "-2");
        for (var i = 0; i < 3; i++)
        {
            agent.Api.Failures.Enqueue(FakeAgentApi.Unauthorized());
            await runtime.RunCycleAsync(CancellationToken.None);
        }

        Assert.False(agent.Get<IAgentStateStore>().Load().IsEnrolled);
        Assert.Equal(TimeSpan.Zero, await runtime.RunCycleAsync(CancellationToken.None));
        var state = agent.Get<IAgentStateStore>().Load();
        Assert.True(state.IsEnrolled);
        Assert.NotEqual(firstKey, state.KeyName);
        Assert.Equal(2, agent.Api.Calls.Count(c => c == "enroll"));
    }

    [Fact]
    public async Task Revoked_credential_without_token_reports_an_error_and_keeps_backing_off()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        for (var i = 0; i < 4; i++)
        {
            agent.Api.Failures.Enqueue(FakeAgentApi.Unauthorized());
            await runtime.RunCycleAsync(CancellationToken.None);
        }

        Assert.True(agent.Get<IAgentStateStore>().Load().IsEnrolled);
        var issue = Assert.Single(agent.Get<AgentHealthMonitor>().Issues, i => i.Component == AgentRuntime.CredentialComponent);
        Assert.Equal(HealthIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public async Task Unchanged_inventory_is_not_re_uploaded_but_changes_and_admin_requests_are()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Single(agent.Api.Inventories);

        // Interval elapsed, only volatile data (free space) changed: skipped.
        agent.Collector.FreeBytes = 5;
        agent.Time.Advance(TimeSpan.FromMinutes(241));
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Single(agent.Api.Inventories);

        // Real change: uploaded.
        agent.Collector.Software.Add(new InstalledApplication { Name = "New App" });
        agent.Time.Advance(TimeSpan.FromMinutes(241));
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, agent.Api.Inventories.Count);

        // Administrator asks for a refresh: uploaded on the next cycle even though nothing changed.
        agent.Api.HeartbeatResponder = () => new HeartbeatResponse { ServerTimeUtc = DateTimeOffset.UtcNow, Configuration = new AgentConfiguration(), InventoryRequested = true };
        agent.Time.Advance(TimeSpan.FromSeconds(61));
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Equal(3, agent.Api.Inventories.Count);
    }

    [Fact]
    public async Task Server_configuration_is_clamped_to_local_bounds()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        agent.Api.HeartbeatResponder = () => new HeartbeatResponse
        {
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Configuration = new AgentConfiguration { HeartbeatIntervalSeconds = 1, InventoryIntervalMinutes = 0, UpdateCheckIntervalMinutes = int.MaxValue },
        };
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var delay = await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(15), delay);
        var configuration = agent.Get<IAgentStateStore>().Load().Configuration;
        Assert.Equal(15, configuration.InventoryIntervalMinutes);
        Assert.Equal(7 * 24 * 60, configuration.UpdateCheckIntervalMinutes);
    }

    [Fact]
    public async Task Certificate_is_renewed_when_recommended_and_the_old_key_is_deleted_after_first_use()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var before = agent.Get<IAgentStateStore>().Load();
        var oldThumbprint = agent.Get<DeviceCredentialStore>().GetClientCertificate()!.Thumbprint;

        agent.Api.HeartbeatResponder = () => new HeartbeatResponse { ServerTimeUtc = DateTimeOffset.UtcNow, Configuration = new AgentConfiguration(), CertificateRenewalRecommended = true };
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Contains("renew", agent.Api.Calls);
        var renewed = agent.Get<IAgentStateStore>().Load();
        Assert.NotEqual(before.KeyName, renewed.KeyName);
        Assert.Equal(before.KeyName, renewed.RetiredKeyName);
        Assert.NotEqual(oldThumbprint, agent.Get<DeviceCredentialStore>().GetClientCertificate()!.Thumbprint);
        Assert.Equal(2, agent.KeyFileCount);

        agent.Api.HeartbeatResponder = null;
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Null(agent.Get<IAgentStateStore>().Load().RetiredKeyName);
        Assert.Equal(1, agent.KeyFileCount);
    }

    [Fact]
    public async Task Certificate_is_renewed_locally_when_70_percent_of_its_lifetime_has_passed()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var state = agent.Get<IAgentStateStore>().Load();
        agent.Get<IAgentStateStore>().Save(state with { CertificateIssuedAtUtc = agent.Time.Now.AddDays(-80), CertificateNotAfterUtc = agent.Time.Now.AddDays(10) });

        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Contains("renew", agent.Api.Calls);
    }

    [Fact]
    public async Task Failed_renewal_discards_the_new_key_and_keeps_the_current_credential()
    {
        using var agent = new AgentHarness();
        agent.Get<BootstrapTokenStore>().Save(Token);
        var runtime = agent.Get<AgentRuntime>();
        await runtime.RunCycleAsync(CancellationToken.None);
        var before = agent.Get<IAgentStateStore>().Load();
        agent.Api.HeartbeatResponder = () => new HeartbeatResponse { ServerTimeUtc = DateTimeOffset.UtcNow, Configuration = new AgentConfiguration(), CertificateRenewalRecommended = true };

        // Heartbeat ok, renewal fails.
        var original = agent.Api.Failures;
        agent.Api.HeartbeatResponder = () =>
        {
            original.Enqueue(FakeAgentApi.Transient());
            return new HeartbeatResponse { ServerTimeUtc = DateTimeOffset.UtcNow, Configuration = new AgentConfiguration(), CertificateRenewalRecommended = true };
        };
        await runtime.RunCycleAsync(CancellationToken.None);
        Assert.Equal(before.KeyName, agent.Get<IAgentStateStore>().Load().KeyName);
        Assert.Equal(1, agent.KeyFileCount);
    }

    [Fact]
    public void Heartbeat_response_contract_has_no_command_surface()
    {
        // Structural guard: the only server-to-agent data are schedules and booleans.
        var allowed = new[] { typeof(DateTimeOffset), typeof(AgentConfiguration), typeof(bool) };
        Assert.All(typeof(HeartbeatResponse).GetProperties(), p => Assert.Contains(p.PropertyType, allowed));
        Assert.All(typeof(AgentConfiguration).GetProperties(), p => Assert.Equal(typeof(int), p.PropertyType));
    }

    [Fact]
    public async Task Generated_keys_are_p256_and_only_readable_by_the_owner()
    {
        using var agent = new AgentHarness();
        using var key = agent.Keys.CreateKey(out var name);
        Assert.Equal(256, key.KeySize);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(agent.DataDirectory, "keys", name + ".pem"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        await Task.CompletedTask;
        Assert.Throws<ArgumentException>(() => agent.Keys.OpenKey("../../etc/passwd"));
        _ = ECCurve.NamedCurves.nistP256;
    }
}
