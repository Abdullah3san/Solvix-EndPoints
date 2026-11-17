namespace Solvix.Modules.Support.Application;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Technician-facing file transfer inside a Files session.</summary>
public interface IRemoteFileService
{
    Task<RemoteFileOperationDto> ListAsync(Guid sessionId, string? path, int waitSeconds, CancellationToken cancellationToken);

    Task<RemoteDownload> DownloadAsync(Guid sessionId, string? path, CancellationToken cancellationToken);

    Task<RemoteFileOperationDto> UploadAsync(Guid sessionId, string? path, bool overwrite, Stream content, CancellationToken cancellationToken);

    Task<RemoteFileOperationDto> GetAsync(Guid sessionId, Guid operationId, int waitSeconds, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteFileOperationDto>> ListOperationsAsync(Guid sessionId, CancellationToken cancellationToken);
}

internal sealed partial class RemoteFileService(
    ISolvixDbContext db,
    IClock clock,
    ILogger<RemoteFileService> logger) : IRemoteFileService
{
    public const int MaxWaitSeconds = 25;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task<RemoteFileOperationDto> ListAsync(Guid sessionId, string? path, int waitSeconds, CancellationToken cancellationToken)
    {
        var session = await ActiveSessionAsync(sessionId, cancellationToken);
        var operation = RemoteFileOperation.Request(session.TenantId, sessionId, FileOperationKind.List, ValidatePath(path, allowEmpty: true), null, null, false);
        db.Set<RemoteFileOperation>().Add(operation);
        session.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        LogOperation(logger, sessionId, operation.Id, FileOperationKind.List, operation.Path);
        return await GetAsync(sessionId, operation.Id, waitSeconds, cancellationToken);
    }

    public async Task<RemoteDownload> DownloadAsync(Guid sessionId, string? path, CancellationToken cancellationToken)
    {
        var session = await ActiveSessionAsync(sessionId, cancellationToken);
        var operation = RemoteFileOperation.Request(session.TenantId, sessionId, FileOperationKind.Download, ValidatePath(path, allowEmpty: false), null, null, false);
        db.Set<RemoteFileOperation>().Add(operation);
        session.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        LogOperation(logger, sessionId, operation.Id, FileOperationKind.Download, operation.Path);

        // Wait until the computer announced the file (or refused).
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var state = await Snapshot(operation.Id, cancellationToken);
            if (state.Status == RemoteFileOperationStatus.Failed)
            {
                throw new ConflictException(state.Error ?? "The computer could not send the file.", "file_download_failed");
            }

            if (state.SizeBytes is { } size)
            {
                return new RemoteDownload
                {
                    OperationId = operation.Id,
                    FileName = SafeFileName(operation.Path),
                    SizeBytes = size,
                    Chunks = StreamAsync(operation.Id, size, cancellationToken),
                };
            }

            if (timer.Elapsed > TimeSpan.FromSeconds(MaxWaitSeconds))
            {
                throw new ConflictException("The computer did not answer in time. It may be offline.", "file_download_timeout");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public async Task<RemoteFileOperationDto> UploadAsync(Guid sessionId, string? path, bool overwrite, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var session = await ActiveSessionAsync(sessionId, cancellationToken);
        var operation = RemoteFileOperation.Stage(session.TenantId, sessionId, ValidatePath(path, allowEmpty: false), overwrite);
        db.Set<RemoteFileOperation>().Add(operation);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileLimits.ChunkBytes];
            long total = 0;
            var index = 0;
            while (true)
            {
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled == 0)
                {
                    break;
                }

                total += filled;
                if (total > FileLimits.MaxFileBytes)
                {
                    throw new ValidationException("file", $"The file is larger than the {FileLimits.MaxFileBytes / (1024 * 1024)} MB limit.");
                }

                hash.AppendData(buffer, 0, filled);
                db.Set<RemoteFileChunk>().Add(RemoteFileChunk.Create(session.TenantId, operation.Id, index++, buffer.AsSpan(0, filled).ToArray()));
                if (index % 16 == 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            if (total == 0)
            {
                throw new ValidationException("file", "The file is empty.");
            }

            operation.MarkReady(total, Convert.ToHexStringLower(hash.GetHashAndReset()));
            session.Touch(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Never leave a half-received upload where the computer could pick it up.
            operation.Fail("The upload did not complete.", clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        LogOperation(logger, sessionId, operation.Id, FileOperationKind.Upload, operation.Path);
        return await GetAsync(sessionId, operation.Id, 0, cancellationToken);
    }

    public async Task<RemoteFileOperationDto> GetAsync(Guid sessionId, Guid operationId, int waitSeconds, CancellationToken cancellationToken)
    {
        await FindSessionAsync(sessionId, cancellationToken);
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var operation = await db.Set<RemoteFileOperation>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId && o.SessionId == sessionId, cancellationToken)
                ?? throw new NotFoundException("File operation", operationId);
            var finished = operation.Status is RemoteFileOperationStatus.Completed or RemoteFileOperationStatus.Failed;
            if (finished || timer.Elapsed >= wait)
            {
                return ToDto(operation);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ToDto(operation);
            }
        }
    }

    public async Task<IReadOnlyList<RemoteFileOperationDto>> ListOperationsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await FindSessionAsync(sessionId, cancellationToken);
        var operations = await db.Set<RemoteFileOperation>().AsNoTracking()
            .Where(o => o.SessionId == sessionId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);
        return operations.Select(ToDto).ToList();
    }

    private async IAsyncEnumerable<byte[]> StreamAsync(Guid operationId, long size, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long sent = 0;
        var index = 0;
        var stalled = Stopwatch.StartNew();
        while (sent < size)
        {
            var data = await db.Set<RemoteFileChunk>().AsNoTracking()
                .Where(c => c.OperationId == operationId && c.Index == index)
                .Select(c => c.Data)
                .FirstOrDefaultAsync(cancellationToken);
            if (data is null)
            {
                var state = await Snapshot(operationId, cancellationToken);
                if (state.Status == RemoteFileOperationStatus.Failed)
                {
                    throw new ConflictException(state.Error ?? "The transfer failed.", "file_download_failed");
                }

                if (stalled.Elapsed > StallTimeout)
                {
                    throw new ConflictException("The transfer stalled.", "file_download_timeout");
                }

                await Task.Delay(PollInterval, cancellationToken);
                continue;
            }

            stalled.Restart();
            sent += data.Length;
            index++;
            yield return data;
        }

        // Delivered: the staged copy is no longer needed.
        await db.Set<RemoteFileChunk>().Where(c => c.OperationId == operationId).ExecuteDeleteAsync(CancellationToken.None);
    }

    private async Task<(RemoteFileOperationStatus Status, long? SizeBytes, string? Error)> Snapshot(Guid operationId, CancellationToken cancellationToken)
    {
        var s = await db.Set<RemoteFileOperation>().AsNoTracking().Where(o => o.Id == operationId).Select(o => new { o.Status, o.SizeBytes, o.Error }).FirstAsync(cancellationToken);
        return (s.Status, s.SizeBytes, s.Error);
    }

    private async Task<RemoteSession> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Set<RemoteSession>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException("Remote session", sessionId);
        if (session.Kind != RemoteSessionKind.Files)
        {
            throw new ConflictException("This is not a file session.", "remote_wrong_kind");
        }

        return session;
    }

    private async Task<RemoteSession> ActiveSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(sessionId, cancellationToken);
        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (session.Status != RemoteSessionStatus.Active)
        {
            throw new ConflictException("The session is not active.", "remote_session_not_active");
        }

        return session;
    }

    private static string ValidatePath(string? path, bool allowEmpty)
    {
        var value = path?.Trim() ?? string.Empty;
        if (value.Length == 0 && !allowEmpty)
        {
            throw new ValidationException("path", "A path is required.");
        }

        if (value.Length > FileLimits.MaxPathLength || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ValidationException("path", "Not a valid path.");
        }

        return value;
    }

    internal static string SafeFileName(string path)
    {
        var name = path.Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? "download";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static RemoteFileOperationDto ToDto(RemoteFileOperation o) => new(
        o.Id, o.SessionId, o.Kind, o.Path, o.Status, o.Error, o.SizeBytes, o.Sha256,
        o.EntriesJson is null ? null : JsonSerializer.Deserialize<List<RemoteFileEntry>>(o.EntriesJson, Json),
        o.CreatedAtUtc, o.CompletedAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote file session {SessionId}: operation {OperationId} {Kind} {Path}")]
    private static partial void LogOperation(ILogger logger, Guid sessionId, Guid operationId, FileOperationKind kind, string path);
}

/// <summary>Agent side of file transfer: claim operations, move the bytes, report the result.</summary>
public interface IAgentRemoteFileService
{
    Task<PendingFileOperationResponse> NextAsync(Guid sessionId, int waitSeconds, CancellationToken cancellationToken);

    Task BeginDownloadAsync(Guid sessionId, Guid operationId, long sizeBytes, CancellationToken cancellationToken);

    Task PostChunkAsync(Guid sessionId, Guid operationId, int index, byte[] data, CancellationToken cancellationToken);

    Task<byte[]> GetUploadChunkAsync(Guid sessionId, Guid operationId, int index, CancellationToken cancellationToken);

    Task CompleteAsync(Guid sessionId, Guid operationId, FileOperationResult result, CancellationToken cancellationToken);
}

internal sealed class AgentRemoteFileService(ISolvixDbContext db, ICurrentDevice currentDevice, IClock clock) : IAgentRemoteFileService
{
    public const int MaxWaitSeconds = 25;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxEntries = 5000;

    public async Task<PendingFileOperationResponse> NextAsync(Guid sessionId, int waitSeconds, CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var timer = Stopwatch.StartNew();
        var session = await FindSessionAsync(sessionId, cancellationToken);
        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        while (true)
        {
            // Polling reads are untracked so every pass sees what the technician has written since.
            var status = await db.Set<RemoteSession>().AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.Status).FirstAsync(cancellationToken);
            if (status != RemoteSessionStatus.Active)
            {
                return new PendingFileOperationResponse { Ended = true };
            }

            var pendingId = await db.Set<RemoteFileOperation>().AsNoTracking()
                .Where(o => o.SessionId == sessionId && o.Status == RemoteFileOperationStatus.Pending)
                .OrderBy(o => o.CreatedAtUtc)
                .Select(o => (Guid?)o.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (pendingId is { } id)
            {
                var operation = await db.Set<RemoteFileOperation>().FirstAsync(o => o.Id == id, cancellationToken);
                operation.Claim();
                session.Touch(clock.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                return new PendingFileOperationResponse
                {
                    Operation = new AgentFileOperation
                    {
                        Id = operation.Id,
                        Kind = operation.Kind,
                        Path = operation.Path,
                        SizeBytes = operation.SizeBytes ?? 0,
                        Sha256 = operation.Sha256,
                        Overwrite = operation.Overwrite,
                    },
                };
            }

            if (timer.Elapsed >= wait)
            {
                return new PendingFileOperationResponse();
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new PendingFileOperationResponse();
            }
        }
    }

    public async Task BeginDownloadAsync(Guid sessionId, Guid operationId, long sizeBytes, CancellationToken cancellationToken)
    {
        if (sizeBytes is < 0 or > FileLimits.MaxFileBytes)
        {
            throw new ValidationException("sizeBytes", "Not an allowed file size.");
        }

        var operation = await FindOperationAsync(sessionId, operationId, cancellationToken);
        operation.Announce(sizeBytes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PostChunkAsync(Guid sessionId, Guid operationId, int index, byte[] data, CancellationToken cancellationToken)
    {
        var operation = await FindOperationAsync(sessionId, operationId, cancellationToken);
        if (operation.Kind != FileOperationKind.Download || operation.Status != RemoteFileOperationStatus.Running || operation.SizeBytes is not { } size)
        {
            throw new ConflictException("The operation is not a running download.", "file_operation_not_running");
        }

        if (index < 0 || (long)index * FileLimits.ChunkBytes >= Math.Max(size, 1))
        {
            throw new ValidationException("index", "The chunk lies outside the announced file.");
        }

        db.Set<RemoteFileChunk>().Add(RemoteFileChunk.Create(operation.TenantId, operationId, index, data));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<byte[]> GetUploadChunkAsync(Guid sessionId, Guid operationId, int index, CancellationToken cancellationToken)
    {
        var operation = await FindOperationAsync(sessionId, operationId, cancellationToken);
        if (operation.Kind != FileOperationKind.Upload || operation.Status != RemoteFileOperationStatus.Running)
        {
            throw new ConflictException("The operation is not a running upload.", "file_operation_not_running");
        }

        return await db.Set<RemoteFileChunk>().AsNoTracking()
            .Where(c => c.OperationId == operationId && c.Index == index)
            .Select(c => c.Data)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("File chunk", index);
    }

    public async Task CompleteAsync(Guid sessionId, Guid operationId, FileOperationResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var operation = await FindOperationAsync(sessionId, operationId, cancellationToken);
        string? entriesJson = null;
        if (result.Success && operation.Kind == FileOperationKind.List)
        {
            var entries = (result.Entries ?? []).Take(MaxEntries).ToList();
            entriesJson = JsonSerializer.Serialize(entries, RemoteFileService.Json);
        }

        if (result.Success && operation.Kind == FileOperationKind.Download)
        {
            var stored = await db.Set<RemoteFileChunk>().Where(c => c.OperationId == operationId).SumAsync(c => (long)c.Data.Length, cancellationToken);
            if (stored != operation.SizeBytes)
            {
                result = new FileOperationResult { Success = false, Error = "The file did not arrive completely." };
            }
        }

        operation.Complete(result, entriesJson, clock.UtcNow);
        if (operation.Kind == FileOperationKind.Upload)
        {
            await db.Set<RemoteFileChunk>().Where(c => c.OperationId == operationId).ExecuteDeleteAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<RemoteSession> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.Set<RemoteSession>().FirstOrDefaultAsync(s => s.Id == sessionId && s.DeviceId == currentDevice.DeviceId && s.Kind == RemoteSessionKind.Files, cancellationToken)
        ?? throw new NotFoundException("Remote session", sessionId);

    private async Task<RemoteFileOperation> FindOperationAsync(Guid sessionId, Guid operationId, CancellationToken cancellationToken)
    {
        await FindSessionAsync(sessionId, cancellationToken);
        return await db.Set<RemoteFileOperation>().FirstOrDefaultAsync(o => o.Id == operationId && o.SessionId == sessionId, cancellationToken)
            ?? throw new NotFoundException("File operation", operationId);
    }
}
