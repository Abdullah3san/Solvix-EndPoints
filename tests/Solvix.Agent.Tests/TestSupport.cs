using Solvix.Agent.Core.Monitoring;
namespace Solvix.Agent.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Security;

/// <summary>Manually advanced clock.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Minimal in-memory server: signs CSRs with a test CA and records calls.</summary>
public sealed class FakeAgentApi : IAgentApiClient, IDisposable
{
    private readonly ECDsa _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _ca;

    public FakeAgentApi()
    {
        var request = new CertificateRequest("CN=Test CA", _caKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public Guid DeviceId { get; } = Guid.NewGuid();

    public Guid TenantId { get; } = Guid.NewGuid();

    public List<string> Calls { get; } = [];

    public List<InventoryReport> Inventories { get; } = [];

    public List<HeartbeatRequest> Heartbeats { get; } = [];

    /// <summary>Exceptions thrown (in order) by the next calls.</summary>
    public Queue<AgentApiException> Failures { get; } = new();

    public Func<HeartbeatResponse>? HeartbeatResponder { get; set; }

    public UpdateCheckResponse UpdateResponse { get; set; } = new() { UpdateAvailable = false };

    public AgentApiException? EnrollFailure { get; set; }

    public int CertificateLifetimeDays { get; set; } = 90;

    public static AgentApiException Transient() => new(AgentApiFailure.Transient, null, null, "Connection refused");

    public static AgentApiException ServerError() => new(AgentApiFailure.Transient, HttpStatusCode.ServiceUnavailable, null, "503 Service Unavailable");

    public static AgentApiException Unauthorized() => new(AgentApiFailure.Authentication, HttpStatusCode.Unauthorized, null, "401");

    public Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        Calls.Add("enroll");
        ThrowIfFailing();
        if (EnrollFailure is not null)
        {
            throw EnrollFailure;
        }

        var (pem, notAfter) = Sign(request.CertificateSigningRequestPem);
        return Task.FromResult(new EnrollmentResponse
        {
            DeviceId = DeviceId,
            TenantId = TenantId,
            CertificatePem = pem,
            CaCertificatesPem = [_ca.ExportCertificatePem()],
            CertificateNotAfterUtc = notAfter,
            Configuration = new AgentConfiguration { HeartbeatIntervalSeconds = 60, InventoryIntervalMinutes = 240, UpdateCheckIntervalMinutes = 360 },
        });
    }

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken)
    {
        Calls.Add("heartbeat");
        ThrowIfFailing();
        Heartbeats.Add(request);
        return Task.FromResult(HeartbeatResponder?.Invoke() ?? new HeartbeatResponse
        {
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Configuration = new AgentConfiguration { HeartbeatIntervalSeconds = 60, InventoryIntervalMinutes = 240, UpdateCheckIntervalMinutes = 360 },
        });
    }

    public Task SubmitInventoryAsync(InventoryReport report, CancellationToken cancellationToken)
    {
        Calls.Add("inventory");
        ThrowIfFailing();
        Inventories.Add(report);
        return Task.CompletedTask;
    }

    public Task<CertificateRenewalResponse> RenewCertificateAsync(CertificateRenewalRequest request, CancellationToken cancellationToken)
    {
        Calls.Add("renew");
        ThrowIfFailing();
        var (pem, notAfter) = Sign(request.CertificateSigningRequestPem);
        return Task.FromResult(new CertificateRenewalResponse { CertificatePem = pem, CaCertificatesPem = [_ca.ExportCertificatePem()], CertificateNotAfterUtc = notAfter });
    }

    public Task<UpdateCheckResponse> CheckForUpdateAsync(string channel, string currentVersion, CancellationToken cancellationToken)
    {
        Calls.Add("updates");
        ThrowIfFailing();
        return Task.FromResult(UpdateResponse);
    }

    public void Dispose()
    {
        _ca.Dispose();
        _caKey.Dispose();
    }

    private (string Pem, DateTimeOffset NotAfter) Sign(string csrPem)
    {
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={DeviceId}"), csr.PublicKey, HashAlgorithmName.SHA256);
        var notAfter = DateTimeOffset.UtcNow.AddDays(CertificateLifetimeDays);
        using var cert = request.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_caKey), DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, RandomNumberGenerator.GetBytes(8));
        return (cert.ExportCertificatePem(), new DateTimeOffset(cert.NotAfter.ToUniversalTime()));
    }

    private void ThrowIfFailing()
    {
        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }
    }
}

/// <summary>Deterministic inventory module for tests.</summary>
public sealed class StaticCollector : IInventoryCollector
{
    public string Name => "system";

    public string Manufacturer { get; set; } = "Contoso";

    public long FreeBytes { get; set; } = 100;

    public List<InstalledApplication> Software { get; } = [new() { Name = "App", Version = "1.0" }];

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        builder.System = new SystemInfo { Manufacturer = Manufacturer, HardwareId = "4C4C4544-0042-3510-8051-B4C04F4E4A32", SerialNumber = "ABC123" };
        builder.Volumes.Add(new VolumeInfo { DriveLetter = "C:", SizeBytes = 1000, FreeBytes = FreeBytes });
        builder.Software.AddRange(Software);
        return Task.CompletedTask;
    }
}

/// <summary>A fully wired agent (real core services) against <see cref="FakeAgentApi"/>.</summary>
public sealed class AgentHarness : IDisposable
{
    public AgentHarness(Action<AgentOptions>? configure = null)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "solvix-agent-test-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SolvixAgent:ServerUrl"] = "https://agents.solvix.test/",
            ["SolvixAgent:DataDirectory"] = DataDirectory,
            ["SolvixAgent:Backoff:JitterRatio"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSolvixAgentCore(configuration);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
        services.Replace(ServiceDescriptor.Singleton<IAgentApiClient>(Api));
        services.AddSingleton<IDeviceKeyStore>(_ => Keys);
        services.AddSingleton<ISecretProtector, FilePermissionSecretProtector>();
        services.AddSingleton<IInventoryCollector>(Collector);
        services.Replace(ServiceDescriptor.Singleton<ISystemHealthProbe>(SystemProbe));
        services.Replace(ServiceDescriptor.Singleton<INetworkReachabilityProbe>(NetworkProbe));
        if (configure is not null)
        {
            services.PostConfigure(configure);
        }

        Provider = services.BuildServiceProvider();
    }

    public string DataDirectory { get; }

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));

    public FakeAgentApi Api { get; } = new();

    public StaticCollector Collector { get; } = new();

    public FakeSystemHealthProbe SystemProbe { get; } = new();

    public FakeNetworkProbe NetworkProbe { get; } = new();

    public FileDeviceKeyStore Keys => _keys ??= new FileDeviceKeyStore(DataDirectory);

    public ServiceProvider Provider { get; }

    private FileDeviceKeyStore? _keys;

    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    public int KeyFileCount => Directory.Exists(Path.Combine(DataDirectory, "keys")) ? Directory.GetFiles(Path.Combine(DataDirectory, "keys")).Length : 0;

    public void Dispose()
    {
        Provider.Dispose();
        Api.Dispose();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, true);
        }
    }
}

internal static class OptionsExtensions
{
    public static IOptions<AgentOptions> Wrap(this AgentOptions options) => Options.Create(options);
}

public sealed class FakeSystemHealthProbe : ISystemHealthProbe
{
    public DateTimeOffset BootTimeUtc { get; set; } = new(2026, 11, 12, 7, 0, 0, TimeSpan.Zero);

    public SystemHealthSnapshot Snapshot { get; set; } = new()
    {
        TotalMemoryBytes = 16L << 30,
        AvailableMemoryBytes = 8L << 30,
        SystemDrive = "C:\\",
        SystemDriveTotalBytes = 500L << 30,
        SystemDriveFreeBytes = 200L << 30,
        RebootPending = false,
    };

    public DateTimeOffset GetBootTimeUtc() => BootTimeUtc;

    public SystemHealthSnapshot Capture() => Snapshot;
}

public sealed class FakeNetworkProbe : INetworkReachabilityProbe
{
    public int Calls { get; private set; }

    public bool GatewayReachable { get; set; } = true;

    public Task<NetworkReachabilitySnapshot?> ProbeAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<NetworkReachabilitySnapshot?>(new NetworkReachabilitySnapshot
        {
            CheckedAtUtc = DateTimeOffset.UnixEpoch,
            Checks =
            [
                new NetworkCheck { Kind = NetworkCheckKind.Interface, Target = "Ethernet", Success = true },
                new NetworkCheck { Kind = NetworkCheckKind.Gateway, Target = "192.168.1.1", Success = GatewayReachable, LatencyMs = GatewayReachable ? 1 : null },
            ],
        });
    }
}
