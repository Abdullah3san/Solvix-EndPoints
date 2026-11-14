namespace Solvix.Agent.Tests;

using System.Net;
using System.Text;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Windows.Inventory;

public sealed class CoreLogicTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0-beta.1", "2.0.0", -1)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3+build.5", "1.2.3", 0)]
    [InlineData("1.2.3.4", "1.2.3", 1)]
    public void Agent_versions_compare_semantically(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(AgentVersion.Parse(left).CompareTo(AgentVersion.Parse(right))));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-bad chars")]
    [InlineData("1.2.3.4.5")]
    public void Invalid_versions_are_rejected(string value) => Assert.False(AgentVersion.TryParse(value, out _));

    [Fact]
    public void Backoff_grows_exponentially_with_bounded_jitter()
    {
        var options = new BackoffOptions { InitialSeconds = 5, MaxSeconds = 900, Multiplier = 2, JitterRatio = 0.2 };
        var low = new ExponentialBackoff(options, () => 0);
        var high = new ExponentialBackoff(options, () => 1);
        Assert.Equal(TimeSpan.Zero, low.Delay(0));
        Assert.Equal(4, low.Delay(1).TotalSeconds, 3);
        Assert.Equal(6, high.Delay(1).TotalSeconds, 3);
        Assert.Equal(32, low.Delay(4).TotalSeconds, 3);
        Assert.Equal(900, high.Delay(50).TotalSeconds, 3);
        Assert.Equal(720, low.Delay(50).TotalSeconds, 3);
    }

    [Theory]
    [InlineData(19045, 1, "Windows 10", true)]
    [InlineData(22631, 1, "Windows 11", true)]
    [InlineData(26100, 1, "Windows 11", true)]
    [InlineData(14393, 3, "Windows Server 2016", true)]
    [InlineData(17763, 2, "Windows Server 2019", true)]
    [InlineData(20348, 3, "Windows Server 2022", true)]
    [InlineData(26100, 3, "Windows Server 2025", true)]
    [InlineData(9600, 3, "Windows Server (unsupported)", false)]
    [InlineData(9600, 1, "Windows (unsupported)", false)]
    public void Windows_versions_are_mapped_to_families(int build, int productType, string family, bool supported)
    {
        var type = (WindowsVersionInfo.ProductType)productType;
        Assert.Equal(family, WindowsVersionInfo.Family(build, type));
        Assert.Equal(supported, WindowsVersionInfo.IsSupported(build, type));
    }

    [Fact]
    public void Windows_11_friendly_name_ignores_the_legacy_product_name()
    {
        Assert.Equal("Windows 11 Pro", WindowsVersionInfo.FriendlyName(22631, WindowsVersionInfo.ProductType.Workstation, "Windows 10 Pro", "Professional"));
        Assert.Equal("Windows 10 Enterprise LTSC", WindowsVersionInfo.FriendlyName(17763, WindowsVersionInfo.ProductType.Workstation, null, "EnterpriseS"));
        Assert.Equal("Windows Server 2022 Datacenter", WindowsVersionInfo.FriendlyName(20348, WindowsVersionInfo.ProductType.Server, "Windows Server 2022 Datacenter", "ServerDatacenter"));
        Assert.Equal("22H2", WindowsVersionInfo.DisplayVersionFor(19045, WindowsVersionInfo.ProductType.Workstation));
        Assert.Null(WindowsVersionInfo.DisplayVersionFor(20348, WindowsVersionInfo.ProductType.Server));
    }

    [Fact]
    public void Inventory_hash_ignores_volatile_values_only()
    {
        var report = new InventoryReport
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            AgentVersion = "1.0.0",
            Hostname = "PC1",
            Memory = new MemoryInfo { TotalPhysicalBytes = 100, AvailablePhysicalBytes = 50 },
            Volumes = [new VolumeInfo { DriveLetter = "C:", SizeBytes = 10, FreeBytes = 5 }],
            Software = [new InstalledApplication { Name = "A", Version = "1" }],
        };
        var hash = InventoryHasher.Compute(report);
        Assert.Equal(hash, InventoryHasher.Compute(report with
        {
            CollectedAtUtc = DateTimeOffset.UtcNow.AddHours(5),
            Memory = report.Memory with { AvailablePhysicalBytes = 1 },
            Volumes = [report.Volumes[0] with { FreeBytes = 1 }],
        }));
        Assert.NotEqual(hash, InventoryHasher.Compute(report with { Software = [new InstalledApplication { Name = "A", Version = "2" }] }));
        Assert.NotEqual(hash, InventoryHasher.Compute(report with { Hostname = "PC2" }));
    }

    [Fact]
    public async Task Inventory_service_isolates_failing_modules_and_bounds_the_report()
    {
        var many = new ManyApps();
        var service = new InventoryService([new Failing(), many, new StaticCollector()], TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<InventoryService>.Instance);
        var report = await service.CollectAsync(CancellationToken.None);
        Assert.Equal("Contoso", report.System!.Manufacturer);
        var issue = Assert.Single(report.CollectionIssues);
        Assert.Equal("broken", issue.Component);
        Assert.Equal(InventoryReport.MaxSoftwareEntries, report.Software.Count);
        Assert.Equal(64, report.ContentHash!.Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AgentApiFailure.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, AgentApiFailure.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, AgentApiFailure.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentApiFailure.Transient)]
    [InlineData(HttpStatusCode.BadRequest, AgentApiFailure.Rejected)]
    [InlineData(HttpStatusCode.Conflict, AgentApiFailure.Rejected)]
    public void Http_failures_are_classified(HttpStatusCode status, AgentApiFailure expected) =>
        Assert.Equal(expected, AgentApiException.Classify(status));

    [Fact]
    public async Task Api_client_maps_problem_details_and_network_errors()
    {
        using var problemClient = new AgentApiClient(new HttpClient(new Respond(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"status":401,"title":"Unauthorized","code":"enrollment_token_expired"}""", Encoding.UTF8, "application/problem+json"),
        })) { BaseAddress = new Uri("https://agents.solvix.test/") });
        var error = await Assert.ThrowsAsync<AgentApiException>(() => problemClient.EnrollAsync(
            new EnrollmentRequest { EnrollmentToken = "t", CertificateSigningRequestPem = "x", Device = new DeviceIdentityInfo { Hostname = "h", AgentVersion = "1.0.0" } },
            CancellationToken.None));
        Assert.Equal(AgentApiFailure.Authentication, error.Failure);
        Assert.Equal("enrollment_token_expired", error.Code);

        using var offline = new AgentApiClient(new HttpClient(new Respond(() => throw new HttpRequestException("No route to host"))) { BaseAddress = new Uri("https://agents.solvix.test/") });
        var network = await Assert.ThrowsAsync<AgentApiException>(() => offline.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None));
        Assert.Equal(AgentApiFailure.Transient, network.Failure);

        using var proxyError = new AgentApiClient(new HttpClient(new Respond(() => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") })) { BaseAddress = new Uri("https://agents.solvix.test/") });
        Assert.Equal(AgentApiFailure.Transient, (await Assert.ThrowsAsync<AgentApiException>(() => proxyError.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None))).Failure);
    }

    [Fact]
    public void Api_client_refuses_plain_http()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions { ServerUrl = new Uri("http://agents.solvix.test/") });
        Assert.Throws<InvalidOperationException>(() => new AgentApiClient(options, new NoTransport()));
    }

    [Fact]
    public async Task Connections_are_recycled_when_the_device_credential_changes()
    {
        // A TLS connection opened before enrollment carries no client certificate for its whole
        // lifetime; the client must not reuse it once a certificate exists (or after renewal).
        var transport = new VersionedTransport();
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions { ServerUrl = new Uri("https://agents.solvix.test/") });
        using var client = new AgentApiClient(options, transport);
        await client.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None);
        await client.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None);
        Assert.Equal(1, transport.HandlersCreated);

        transport.CredentialVersion = "enrolled-thumbprint";
        await client.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None);
        Assert.Equal(2, transport.HandlersCreated);

        transport.CredentialVersion = "renewed-thumbprint";
        await client.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None);
        await client.CheckForUpdateAsync("stable", "1.0.0", CancellationToken.None);
        Assert.Equal(3, transport.HandlersCreated);
    }

    [Theory]
    [InlineData((ushort)9, "Laptop")]
    [InlineData((ushort)3, "Desktop")]
    [InlineData((ushort)23, "Server")]
    [InlineData((ushort)1, "Other")]
    public void Chassis_types_are_mapped(ushort value, string expected) => Assert.Equal(expected, SystemCollector.Chassis(value));

    [Fact]
    public void Disk_bus_types_are_mapped()
    {
        Assert.Equal("NVMe", DiskCollector.BusType(17));
        Assert.Equal("SATA", DiskCollector.BusType(11));
        Assert.Null(DiskCollector.BusType(null));
    }

    private sealed class Failing : IInventoryCollector
    {
        public string Name => "broken";

        public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken) => throw new InvalidOperationException("WMI provider failure");
    }

    private sealed class ManyApps : IInventoryCollector
    {
        public string Name => "software";

        public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
        {
            builder.Software.AddRange(Enumerable.Range(0, InventoryReport.MaxSoftwareEntries + 50).Select(i => new InstalledApplication { Name = $"App {i:D5}" }));
            return Task.CompletedTask;
        }
    }

    private sealed class Respond(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private sealed class VersionedTransport : IAgentTransport
    {
        public int HandlersCreated { get; private set; }

        public string? CredentialVersion { get; set; }

        public HttpMessageHandler CreateHandler()
        {
            HandlersCreated++;
            return new Respond(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"updateAvailable":false}""", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoTransport : IAgentTransport
    {
        public HttpMessageHandler CreateHandler() => new HttpClientHandler();
    }
}
