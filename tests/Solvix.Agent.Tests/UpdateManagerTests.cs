namespace Solvix.Agent.Tests;

using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Updates;

public sealed class UpdateManagerTests : IDisposable
{
    private static readonly byte[] Package = RandomNumberGenerator.GetBytes(4096);
    private static readonly string PackageHash = Convert.ToHexStringLower(SHA256.HashData(Package));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solvix-updates-" + Guid.NewGuid().ToString("N"));
    private readonly AgentOptions _options;
    private readonly FakeVerifier _verifier = new();
    private readonly StubHttp _http = new();

    public UpdateManagerTests()
    {
        _options = new AgentOptions
        {
            DataDirectory = _directory,
            Updates = new UpdateOptions { AllowedDownloadHosts = ["downloads.solvix.test"], TrustedPublisherThumbprints = ["AB"] },
        };
    }

    [Theory]
    [InlineData("1.0.0", "https://downloads.solvix.test/a.msi", "not newer")]
    [InlineData("0.9.0", "https://downloads.solvix.test/a.msi", "not newer")]
    [InlineData("1.1.0", "http://downloads.solvix.test/a.msi", "HTTPS")]
    [InlineData("1.1.0", "https://evil.example/a.msi", "not allow-listed")]
    [InlineData("1.1.0", "https://downloads.solvix.test.evil.example/a.msi", "not allow-listed")]
    public void Local_policy_refuses_unsafe_releases(string version, string url, string reason)
    {
        var refusal = Create().Evaluate(Release(version, url), "1.0.0");
        Assert.NotNull(refusal);
        Assert.Contains(reason, refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Updates_require_a_pinned_publisher_and_can_be_disabled_locally()
    {
        _options.Updates.TrustedPublisherThumbprints = [];
        Assert.Contains("publisher", Create().Evaluate(Release("1.1.0"), "1.0.0"), StringComparison.OrdinalIgnoreCase);
        _options.Updates.TrustedPublisherThumbprints = ["AB"];
        _options.Updates.Enabled = false;
        Assert.Contains("disabled", Create().Evaluate(Release("1.1.0"), "1.0.0"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_release_is_downloaded_verified_and_staged()
    {
        var manager = Create();
        var outcome = await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") }, "1.0.0", CancellationToken.None);
        Assert.Equal(UpdateOutcome.Staged, outcome);
        var staged = manager.ReadStaged()!;
        Assert.Equal("1.1.0", staged.Version);
        Assert.Equal(Package, await File.ReadAllBytesAsync(staged.PackagePath));
        Assert.Equal(1, _verifier.Calls);

        Assert.Equal(UpdateOutcome.AlreadyStaged, await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") }, "1.0.0", CancellationToken.None));
        Assert.Equal(1, _http.Requests);
    }

    [Fact]
    public async Task Hash_mismatch_is_refused_and_nothing_is_staged()
    {
        var manager = Create();
        var release = Release("1.1.0") with { Sha256 = new string('0', 64) };
        Assert.Equal(UpdateOutcome.Refused, await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = release }, "1.0.0", CancellationToken.None));
        Assert.Null(manager.ReadStaged());
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "updates")));
    }

    [Fact]
    public async Task Invalid_signature_is_refused()
    {
        _verifier.Result = "Signer is not a trusted publisher.";
        var manager = Create();
        Assert.Equal(UpdateOutcome.Refused, await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") }, "1.0.0", CancellationToken.None));
        Assert.Null(manager.ReadStaged());
    }

    [Fact]
    public async Task Oversized_download_is_aborted()
    {
        _options.Updates.MaxPackageBytes = 1024;
        var manager = Create();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") with { SizeBytes = 100 } }, "1.0.0", CancellationToken.None));
        Assert.Null(manager.ReadStaged());
    }

    [Fact]
    public async Task Privileged_apply_re_verifies_and_rejects_a_tampered_package()
    {
        var manager = Create();
        await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") }, "1.0.0", CancellationToken.None);
        var staged = manager.ReadStaged()!;
        await File.WriteAllBytesAsync(staged.PackagePath, RandomNumberGenerator.GetBytes(4096));

        var installer = new RecordingInstaller();
        Assert.Equal(2, await manager.ApplyStagedAsync(installer, "1.0.0", CancellationToken.None));
        Assert.Empty(installer.Installed);
        Assert.Null(manager.ReadStaged());
    }

    [Fact]
    public async Task Privileged_apply_installs_a_verified_package_once()
    {
        var manager = Create();
        await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") }, "1.0.0", CancellationToken.None);
        var installer = new RecordingInstaller();
        Assert.Equal(0, await manager.ApplyStagedAsync(installer, "1.0.0", CancellationToken.None));
        Assert.Single(installer.Installed);
        Assert.Equal(2, _verifier.Calls); // verified again in the privileged process
        Assert.Equal(0, await manager.ApplyStagedAsync(installer, "1.0.0", CancellationToken.None));
        Assert.Single(installer.Installed);
    }

    [Fact]
    public async Task Privileged_apply_refuses_a_manifest_pointing_outside_the_staging_directory()
    {
        var manager = Create();
        await manager.ProcessAsync(new UpdateCheckResponse { UpdateAvailable = true, Release = Release("1.1.0") }, "1.0.0", CancellationToken.None);
        var outside = Path.Combine(_directory, "evil.msi");
        await File.WriteAllBytesAsync(outside, Package);
        var manifest = Path.Combine(_directory, "updates", "staged.json");
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace(manager.ReadStaged()!.PackagePath.Replace("\\", "\\\\", StringComparison.Ordinal), outside.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));

        var installer = new RecordingInstaller();
        Assert.Equal(2, await manager.ApplyStagedAsync(installer, "1.0.0", CancellationToken.None));
        Assert.Empty(installer.Installed);
    }

    [Fact]
    public void Unsupported_platform_verifier_refuses_everything() =>
        Assert.NotNull(new UnsupportedSignatureVerifier().Verify("x.msi", ["AB"]));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static AgentReleaseInfo Release(string version, string url = "https://downloads.solvix.test/SolvixAgent.msi") => new()
    {
        Version = version,
        Channel = "stable",
        DownloadUrl = new Uri(url),
        Sha256 = PackageHash,
        SizeBytes = Package.Length,
    };

    private UpdateManager Create() =>
        new(Options.Create(_options), _http, _verifier, TimeProvider.System, NullLogger<UpdateManager>.Instance);

    private sealed class FakeVerifier : IPackageSignatureVerifier
    {
        public string? Result { get; set; }

        public int Calls { get; private set; }

        public string? Verify(string packagePath, IReadOnlyCollection<string> trustedPublisherThumbprints)
        {
            Calls++;
            return Result;
        }
    }

    private sealed class RecordingInstaller : IPackageInstaller
    {
        public List<string> Installed { get; } = [];

        public Task<int> InstallAsync(string packagePath, CancellationToken cancellationToken)
        {
            Installed.Add(packagePath);
            return Task.FromResult(0);
        }
    }

    private sealed class StubHttp : IHttpClientFactory
    {
        public int Requests { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(StubHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
            }
        }
    }
}
