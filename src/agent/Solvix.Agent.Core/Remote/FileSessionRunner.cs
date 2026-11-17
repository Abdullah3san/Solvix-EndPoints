namespace Solvix.Agent.Core.Remote;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;

/// <summary>
/// Carries out the file operations a technician asks for during a Files session: list, download,
/// upload. Every path goes through <see cref="FileSandbox"/>; every upload is verified against the
/// SHA-256 the technician's file had before it replaces anything.
/// </summary>
public sealed partial class FileSessionRunner(IAgentApiClient api, IOptions<AgentOptions> options, ILogger<FileSessionRunner> logger)
{
    private const int PollSeconds = 20;
    private const int MaxEntries = 5000;

    /// <summary>The agent's own exchange folder plus the folders the installer allowed.</summary>
    public FileSandbox CreateSandbox()
    {
        var transfer = Path.Combine(options.Value.ResolveDataDirectory(), "transfer");
        Directory.CreateDirectory(transfer);
        return new FileSandbox([transfer, .. options.Value.RemoteAccess.FileFolders]);
    }

    public async Task RunAsync(Guid sessionId, CancellationTokenSource session)
    {
        var sandbox = CreateSandbox();
        try
        {
            while (!session.IsCancellationRequested)
            {
                var next = await api.ClaimFileOperationAsync(sessionId, PollSeconds, session.Token);
                if (next.Ended)
                {
                    return;
                }

                if (next.Operation is { } operation)
                {
                    await ExecuteAsync(sessionId, operation, sandbox, session.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session over.
        }
        catch (AgentApiException ex)
        {
            LogRelayStopped(logger, sessionId, ex.Message);
        }
    }

    internal async Task ExecuteAsync(Guid sessionId, AgentFileOperation operation, FileSandbox sandbox, CancellationToken cancellationToken)
    {
        FileOperationResult result;
        try
        {
            result = operation.Kind switch
            {
                FileOperationKind.List => List(operation, sandbox),
                FileOperationKind.Download => await DownloadAsync(sessionId, operation, sandbox, cancellationToken),
                FileOperationKind.Upload => await UploadAsync(sessionId, operation, sandbox, cancellationToken),
                _ => Fail("Unsupported operation."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogFailed(logger, operation.Kind, ex.GetType().Name);
            result = Fail(ex is UnauthorizedAccessException ? "Access to that file was denied on the computer." : "The file could not be read or written.");
        }

        LogDone(logger, sessionId, operation.Kind, result.Success);
        await api.CompleteFileOperationAsync(sessionId, operation.Id, result, cancellationToken);
    }

    private static FileOperationResult List(AgentFileOperation operation, FileSandbox sandbox)
    {
        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            // The folders this computer offers.
            return new FileOperationResult
            {
                Success = true,
                Path = string.Empty,
                Entries = sandbox.Roots.Select(r => new RemoteFileEntry { Name = r, Path = r, IsDirectory = true }).ToList(),
            };
        }

        if (!sandbox.TryResolve(operation.Path, out var folder, out var error))
        {
            return Fail(error);
        }

        if (!Directory.Exists(folder))
        {
            return Fail("That folder does not exist.");
        }

        var entries = new List<RemoteFileEntry>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(i => i is not DirectoryInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue; // links are never offered
            }

            entries.Add(new RemoteFileEntry
            {
                Name = info.Name,
                Path = info.FullName,
                IsDirectory = info is DirectoryInfo,
                SizeBytes = info is FileInfo f ? f.Length : null,
                ModifiedAtUtc = info.LastWriteTimeUtc,
            });
            if (entries.Count >= MaxEntries)
            {
                break;
            }
        }

        return new FileOperationResult { Success = true, Path = folder, Entries = entries };
    }

    private async Task<FileOperationResult> DownloadAsync(Guid sessionId, AgentFileOperation operation, FileSandbox sandbox, CancellationToken cancellationToken)
    {
        if (!sandbox.TryResolve(operation.Path, out var path, out var error))
        {
            return Fail(error);
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Fail("That file does not exist.");
        }

        var limit = Math.Min(FileLimits.MaxFileBytes, (long)options.Value.RemoteAccess.MaxFileMegabytes * 1024 * 1024);
        if (info.Length > limit)
        {
            return Fail($"The file is larger than the {limit / (1024 * 1024)} MB transfer limit.");
        }

        if (info.Length == 0)
        {
            return Fail("The file is empty.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        await api.BeginFileDownloadAsync(sessionId, operation.Id, info.Length, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[FileLimits.ChunkBytes];
        long sent = 0;
        var index = 0;
        while (sent < info.Length)
        {
            var filled = 0;
            while (filled < buffer.Length && sent + filled < info.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, (int)Math.Min(buffer.Length - filled, info.Length - sent - filled)), cancellationToken);
                if (read == 0)
                {
                    return Fail("The file changed while it was being sent.");
                }

                filled += read;
            }

            hash.AppendData(buffer, 0, filled);
            await api.PostFileChunkAsync(sessionId, operation.Id, index++, buffer.AsSpan(0, filled).ToArray(), cancellationToken);
            sent += filled;
        }

        return new FileOperationResult { Success = true, SizeBytes = sent, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) };
    }

    private async Task<FileOperationResult> UploadAsync(Guid sessionId, AgentFileOperation operation, FileSandbox sandbox, CancellationToken cancellationToken)
    {
        if (!sandbox.TryResolve(operation.Path, out var target, out var error))
        {
            return Fail(error);
        }

        var limit = Math.Min(FileLimits.MaxFileBytes, (long)options.Value.RemoteAccess.MaxFileMegabytes * 1024 * 1024);
        if (operation.SizeBytes <= 0 || operation.SizeBytes > limit)
        {
            return Fail($"The file is larger than the {limit / (1024 * 1024)} MB transfer limit.");
        }

        var directory = Path.GetDirectoryName(target);
        if (directory is null || !Directory.Exists(directory))
        {
            return Fail("The destination folder does not exist.");
        }

        if (Directory.Exists(target))
        {
            return Fail("A folder with that name already exists.");
        }

        if (File.Exists(target) && !operation.Overwrite)
        {
            return Fail("A file with that name already exists on the computer.");
        }

        // Receive into a temporary file next to the target; it only replaces the target after it verified.
        var temporary = Path.Combine(directory, $".solvix-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                for (var index = 0; received < operation.SizeBytes; index++)
                {
                    var chunk = await api.GetUploadChunkAsync(sessionId, operation.Id, index, cancellationToken);
                    received += chunk.Length;
                    if (chunk.Length == 0 || received > operation.SizeBytes)
                    {
                        return Fail("The upload did not match its announced size.");
                    }

                    hash.AppendData(chunk);
                    await output.WriteAsync(chunk, cancellationToken);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (received != operation.SizeBytes || !string.Equals(actual, operation.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("The file was damaged in transit (checksum mismatch) and was not saved.");
            }

            File.Move(temporary, target, overwrite: operation.Overwrite);
            return new FileOperationResult { Success = true, SizeBytes = received, Sha256 = actual, Path = target };
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Best effort: a leftover .tmp is harmless.
            }
        }
    }

    private static FileOperationResult Fail(string error) => new() { Success = false, Error = error };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote file session {SessionId}: {Kind} finished (success: {Success})")]
    private static partial void LogDone(ILogger logger, Guid sessionId, FileOperationKind kind, bool success);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote file operation {Kind} failed: {Error}")]
    private static partial void LogFailed(ILogger logger, FileOperationKind kind, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote file session {SessionId} relay stopped: {Reason}")]
    private static partial void LogRelayStopped(ILogger logger, Guid sessionId, string reason);
}
