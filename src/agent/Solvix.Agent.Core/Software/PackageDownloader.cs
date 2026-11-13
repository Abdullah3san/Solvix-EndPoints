namespace Solvix.Agent.Core.Software;

using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;

public sealed record DownloadResult(string? Path, string? ErrorCode, string? ErrorMessage)
{
    public bool Succeeded => Path is not null;
}

/// <summary>
/// Downloads a package over the device-certificate channel into the agent's staging directory and
/// verifies its size and SHA-256 against the signed command. A partial download is kept under the
/// package's checksum and resumed with an HTTP Range request after a network failure, an agent
/// restart or a later retry of the same deployment (interrupted deployments).
/// </summary>
public sealed partial class PackageDownloader(
    ISoftwarePackageClient client,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<PackageDownloader> logger)
{
    public static readonly TimeSpan PartialRetention = TimeSpan.FromDays(7);

    public string StagingDirectory => Path.Combine(options.Value.ResolveDataDirectory(), "software", "staging");

    public async Task<DownloadResult> DownloadAsync(Guid executionId, SoftwareOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var settings = options.Value.Software;
        if (operation.SizeBytes > settings.MaxPackageBytes)
        {
            return new DownloadResult(null, SoftwareErrorCodes.TooLarge, "The package exceeds this device's size limit.");
        }

        Directory.CreateDirectory(StagingDirectory);
        PruneStalePartials();
        var partial = Path.Combine(StagingDirectory, operation.Sha256 + ".part");
        var staged = Path.Combine(StagingDirectory, $"{executionId:N}.pkg");
        var attempts = Math.Clamp(settings.DownloadAttempts, 1, 10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    if (file.Length > operation.SizeBytes)
                    {
                        file.SetLength(0);
                    }

                    if (file.Length < operation.SizeBytes)
                    {
                        file.Seek(0, SeekOrigin.End);
                        if (file.Length > 0)
                        {
                            LogResuming(logger, executionId, file.Length, operation.SizeBytes);
                        }

                        await client.DownloadSoftwarePackageAsync(executionId, file.Length, file, operation.SizeBytes, cancellationToken);
                    }
                }

                break;
            }
            catch (AgentApiException ex) when (ex.Failure != AgentApiFailure.Transient || ex.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                return new DownloadResult(null, SoftwareErrorCodes.PackageUnavailable, $"The server did not provide the package: {ex.Message}");
            }
            catch (InvalidDataException ex)
            {
                File.Delete(partial);
                return new DownloadResult(null, SoftwareErrorCodes.TooLarge, ex.Message);
            }
            catch (Exception ex) when (ex is AgentApiException or IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= attempts)
                {
                    return new DownloadResult(null, SoftwareErrorCodes.DownloadFailed, $"The download failed after {attempts} attempts: {ex.Message}");
                }

                LogRetry(logger, executionId, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << attempt)), time, cancellationToken);
            }
        }

        var length = new FileInfo(partial).Length;
        if (length != operation.SizeBytes)
        {
            // Short or long body: the next attempt starts again (a short file is resumed, a long one truncated).
            return new DownloadResult(null, SoftwareErrorCodes.DownloadFailed, $"Received {length} of {operation.SizeBytes} bytes.");
        }

        string actual;
        await using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        if (!string.Equals(actual, operation.Sha256, StringComparison.Ordinal))
        {
            File.Delete(partial);
            return new DownloadResult(null, SoftwareErrorCodes.HashMismatch, $"The package checksum {actual} does not match the signed checksum {operation.Sha256}; it was discarded.");
        }

        File.Move(partial, staged, overwrite: true);
        return new DownloadResult(staged, null, null);
    }

    public void Discard(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void PruneStalePartials()
    {
        var cutoff = time.GetUtcNow().UtcDateTime - PartialRetention;
        foreach (var file in Directory.EnumerateFiles(StagingDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use by another execution.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming package download for {ExecutionId} at {Offset} of {Size} bytes")]
    private static partial void LogResuming(ILogger logger, Guid executionId, long offset, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Package download for {ExecutionId} failed (attempt {Attempt}): {Message}")]
    private static partial void LogRetry(ILogger logger, Guid executionId, int attempt, string message);
}
