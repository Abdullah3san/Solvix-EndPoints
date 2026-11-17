namespace Solvix.Agent.Contracts;

public enum FileOperationKind
{
    /// <summary>List a folder (empty path: the folders the computer allows).</summary>
    List = 1,

    /// <summary>Copy a file from the computer to the technician.</summary>
    Download = 2,

    /// <summary>Copy a file from the technician to the computer.</summary>
    Upload = 3,
}

public enum FileOperationStatus
{
    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

public static class FileLimits
{
    public const int MaxPathLength = 1024;
    public const int ChunkBytes = 256 * 1024;
    public const long MaxFileBytes = 100L * 1024 * 1024;
}

public sealed record RemoteFileEntry
{
    public required string Name { get; init; }

    /// <summary>Full path on the computer (use it for the next list/download).</summary>
    public required string Path { get; init; }

    public bool IsDirectory { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? ModifiedAtUtc { get; init; }
}

/// <summary>Work the technician asked the computer to do; the agent claims it by long poll.</summary>
public sealed record AgentFileOperation
{
    public required Guid Id { get; init; }

    public required FileOperationKind Kind { get; init; }

    public required string Path { get; init; }

    /// <summary>Uploads: size of the file waiting on the server.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Uploads: expected lower-case hex SHA-256; the agent refuses a file that does not match.</summary>
    public string? Sha256 { get; init; }

    public bool Overwrite { get; init; }
}

public sealed record PendingFileOperationResponse
{
    public AgentFileOperation? Operation { get; init; }

    /// <summary>The session is over; stop polling.</summary>
    public bool Ended { get; init; }
}

/// <summary>Download: the agent announces the file before sending chunks.</summary>
public sealed record BeginFileDownloadRequest
{
    public required long SizeBytes { get; init; }
}

/// <summary>The agent's final word on an operation.</summary>
public sealed record FileOperationResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>List: the folder that was listed (canonical) and its entries.</summary>
    public string? Path { get; init; }

    public IReadOnlyList<RemoteFileEntry>? Entries { get; init; }

    /// <summary>Download/upload: lower-case hex SHA-256 of the bytes transferred.</summary>
    public string? Sha256 { get; init; }

    public long? SizeBytes { get; init; }
}
