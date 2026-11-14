namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

public sealed class KnowledgeDocument : TenantEntity
{
    public const int TitleMaxLength = 300;
    public const int FileNameMaxLength = 260;

    private KnowledgeDocument()
    {
    }

    public KnowledgeDocument(Guid tenantId, string title, string fileName, string contentType, long sizeBytes, string contentHash, Guid uploadedByUserId)
        : base(tenantId)
    {
        Title = Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength);
        FileName = Guard.NotNullOrWhiteSpace(fileName, "fileName", FileNameMaxLength);
        ContentType = contentType;
        SizeBytes = sizeBytes;
        ContentHash = contentHash;
        UploadedByUserId = uploadedByUserId;
        Status = DocumentStatus.Processing;
        EmbeddingModel = string.Empty;
    }

    public string Title { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 (hex) of the uploaded bytes; unique per tenant to reject duplicates.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    public DocumentStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public int ChunkCount { get; private set; }

    public int CharacterCount { get; private set; }

    /// <summary>Number of secret-like values replaced by a placeholder at ingestion.</summary>
    public int RedactionCount { get; private set; }

    public string EmbeddingModel { get; private set; } = string.Empty;

    public Guid UploadedByUserId { get; private set; }

    /// <summary>Extracted, redacted text. Kept so the document can be re-chunked/re-embedded without the original file.</summary>
    public string? ExtractedText { get; private set; }

    public void MarkReady(string extractedText, int characterCount, int chunkCount, int redactionCount, string embeddingModel)
    {
        ExtractedText = extractedText;
        CharacterCount = characterCount;
        ChunkCount = chunkCount;
        RedactionCount = redactionCount;
        EmbeddingModel = embeddingModel;
        Status = DocumentStatus.Ready;
        FailureReason = null;
    }

    public void MarkFailed(string reason)
    {
        Status = DocumentStatus.Failed;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }

    public void Rename(string title) => Title = Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength);
}
