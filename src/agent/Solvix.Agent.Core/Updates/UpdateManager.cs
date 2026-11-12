namespace Solvix.Agent.Core.Updates;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.State;

/// <summary>Verifies the publisher signature of an installer package (Authenticode on Windows).</summary>
public interface IPackageSignatureVerifier
{
    /// <summary>Returns null when the file is validly signed by a trusted publisher, otherwise the reason.</summary>
    string? Verify(string packagePath, IReadOnlyCollection<string> trustedPublisherThumbprints);
}

/// <summary>Installs a verified package. Only ever invoked by the privileged updater with a local file.</summary>
public interface IPackageInstaller
{
    Task<int> InstallAsync(string packagePath, CancellationToken cancellationToken);
}

/// <summary>Refuses everything: used where no signature verification exists (non-Windows).</summary>
public sealed class UnsupportedSignatureVerifier : IPackageSignatureVerifier
{
    public string? Verify(string packagePath, IReadOnlyCollection<string> trustedPublisherThumbprints) =>
        "Package signature verification is not available on this platform.";
}

public enum UpdateOutcome
{
    UpToDate = 1,
    Disabled = 2,
    Staged = 3,
    AlreadyStaged = 4,
    Refused = 5,
}

/// <summary>
/// Agent update management. The server can only *offer* a release; the agent
/// stages it only when every local rule holds:
///  - automatic updates are enabled locally;
///  - the version is strictly newer than the running agent (no downgrades);
///  - the URL is HTTPS on a locally allow-listed host;
///  - the download does not exceed the local size limit and matches the SHA-256;
///  - the package carries a valid Authenticode signature from a pinned publisher.
/// Installation happens in a separate privileged process that repeats the checks.
/// Nothing in the release manifest is ever executed or used as a command line.
/// </summary>
public sealed partial class UpdateManager(
    IOptions<AgentOptions> options,
    IHttpClientFactory httpClients,
    IPackageSignatureVerifier verifier,
    TimeProvider time,
    ILogger<UpdateManager> logger)
{
    public const string DownloadClientName = "solvix-agent-updates";

    private string StagingDirectory => Path.Combine(options.Value.ResolveDataDirectory(), "updates");

    private string ManifestPath => Path.Combine(StagingDirectory, "staged.json");

    /// <summary>Validates a release offered by the server against local policy. Returns null when acceptable.</summary>
    public string? Evaluate(AgentReleaseInfo release, string runningVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        var settings = options.Value.Updates;
        if (!settings.Enabled)
        {
            return "Automatic updates are disabled by local policy.";
        }

        if (!AgentVersion.TryParse(release.Version, out var offered) || !AgentVersion.TryParse(runningVersion, out var running))
        {
            return "Invalid version.";
        }

        if (offered <= running)
        {
            return $"Offered version {offered} is not newer than {running}.";
        }

        if (!release.DownloadUrl.IsAbsoluteUri || release.DownloadUrl.Scheme != Uri.UriSchemeHttps)
        {
            return "Download URL must be HTTPS.";
        }

        if (!settings.AllowedDownloadHosts.Contains(release.DownloadUrl.Host, StringComparer.OrdinalIgnoreCase))
        {
            return $"Download host '{release.DownloadUrl.Host}' is not allow-listed.";
        }

        if (release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
        {
            return "Invalid SHA-256.";
        }

        if (release.SizeBytes > settings.MaxPackageBytes)
        {
            return "Package exceeds the local size limit.";
        }

        if (settings.TrustedPublisherThumbprints.Count == 0)
        {
            return "No trusted publisher is configured.";
        }

        return null;
    }

    public async Task<UpdateOutcome> ProcessAsync(UpdateCheckResponse check, string runningVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!options.Value.Updates.Enabled)
        {
            return UpdateOutcome.Disabled;
        }

        if (!check.UpdateAvailable || check.Release is null)
        {
            return UpdateOutcome.UpToDate;
        }

        var release = check.Release;
        var refusal = Evaluate(release, runningVersion);
        if (refusal is not null)
        {
            LogRefused(logger, release.Version, refusal);
            return UpdateOutcome.Refused;
        }

        if (ReadStaged() is { } staged && staged.Version == release.Version && File.Exists(staged.PackagePath))
        {
            return UpdateOutcome.AlreadyStaged;
        }

        Directory.CreateDirectory(StagingDirectory);
        var packagePath = Path.Combine(StagingDirectory, $"SolvixAgent-{AgentVersion.Parse(release.Version)}.msi");
        var tempPath = packagePath + ".download";
        try
        {
            var actualHash = await DownloadAsync(release.DownloadUrl, tempPath, options.Value.Updates.MaxPackageBytes, cancellationToken);
            if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                LogRefused(logger, release.Version, "SHA-256 mismatch");
                return UpdateOutcome.Refused;
            }

            var signature = verifier.Verify(tempPath, options.Value.Updates.TrustedPublisherThumbprints);
            if (signature is not null)
            {
                LogRefused(logger, release.Version, signature);
                return UpdateOutcome.Refused;
            }

            File.Move(tempPath, packagePath, overwrite: true);
            var manifest = new StagedUpdate { Version = release.Version, PackagePath = packagePath, Sha256 = actualHash, StagedAtUtc = time.GetUtcNow() };
            AtomicFile.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, AgentStateJsonContext.Default.StagedUpdate));
            LogStaged(logger, release.Version);
            return UpdateOutcome.Staged;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public StagedUpdate? ReadStaged()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(ManifestPath), AgentStateJsonContext.Default.StagedUpdate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Privileged step (scheduled task as SYSTEM): re-verifies the staged package
    /// independently of the unprivileged service and installs it.
    /// </summary>
    public async Task<int> ApplyStagedAsync(IPackageInstaller installer, string runningVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installer);
        var staged = ReadStaged();
        if (staged is null)
        {
            return 0;
        }

        var stagingRoot = Path.GetFullPath(StagingDirectory) + Path.DirectorySeparatorChar;
        var package = Path.GetFullPath(staged.PackagePath);
        string? problem = null;
        if (!package.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(package))
        {
            problem = "Staged package path is outside the staging directory or missing.";
        }
        else if (!AgentVersion.TryParse(staged.Version, out var v) || !AgentVersion.TryParse(runningVersion, out var r) || v <= r)
        {
            problem = "Staged version is not newer than the installed agent.";
        }
        else
        {
            await using var stream = File.OpenRead(package);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, staged.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problem = "Staged package hash changed.";
            }
            else
            {
                problem = verifier.Verify(package, options.Value.Updates.TrustedPublisherThumbprints);
            }
        }

        if (problem is not null)
        {
            LogRefused(logger, staged.Version, problem);
            Discard();
            return 2;
        }

        LogInstalling(logger, staged.Version);
        var exitCode = await installer.InstallAsync(package, cancellationToken);
        Discard();
        return exitCode;
    }

    public void Discard()
    {
        if (Directory.Exists(StagingDirectory))
        {
            Directory.Delete(StagingDirectory, recursive: true);
        }
    }

    private async Task<string> DownloadAsync(Uri url, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(DownloadClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("Package exceeds the local size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException("Package exceeds the local size limit.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent update {Version} refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string version, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent update {Version} downloaded, verified and staged")]
    private static partial void LogStaged(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing staged agent update {Version}")]
    private static partial void LogInstalling(ILogger logger, string version);
}
