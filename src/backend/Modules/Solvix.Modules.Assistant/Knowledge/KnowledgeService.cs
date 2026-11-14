namespace Solvix.Modules.Assistant.Knowledge;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Configuration;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Knowledge.Embeddings;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public sealed record KnowledgeDocumentDto(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    DocumentStatus Status,
    string? FailureReason,
    int ChunkCount,
    int CharacterCount,
    int RedactionCount,
    string EmbeddingModel,
    DateTimeOffset CreatedAtUtc);

/// <summary>A retrieved passage with everything needed for a source reference.</summary>
public sealed record KnowledgeHit(Guid DocumentId, string DocumentTitle, Guid ChunkId, int Ordinal, string? Section, int? Page, double Score, string Content);

public interface IKnowledgeService
{
    Task<KnowledgeDocumentDto> UploadAsync(Stream content, string fileName, string? title, CancellationToken cancellationToken);

    Task<PagedResult<KnowledgeDocumentDto>> ListAsync(PageRequest page, CancellationToken cancellationToken);

    Task<KnowledgeDocumentDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<KnowledgeDocumentDto> ReindexAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int? limit, CancellationToken cancellationToken);
}

internal sealed class KnowledgeService(
    ISolvixDbContext db,
    IAssistantPrincipalAccessor principals,
    IEmbeddingProvider embeddings,
    IAssistantAuditLog audit,
    IOptions<AssistantOptions> options) : IKnowledgeService
{
    private const int MaxChunksPerDocument = 2000;
    private readonly KnowledgeOptions _options = options.Value.Knowledge;

    public async Task<KnowledgeDocumentDto> UploadAsync(Stream content, string fileName, string? title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var principal = RequireManager();

        fileName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ValidationException("file", "A file name is required.");
        }

        var bytes = await ReadBoundedAsync(content, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (await db.Set<KnowledgeDocument>().AnyAsync(d => d.ContentHash == hash, cancellationToken))
        {
            throw new ConflictException("This document has already been uploaded.");
        }

        if (await db.Set<KnowledgeDocument>().CountAsync(cancellationToken) >= _options.MaxDocumentsPerTenant)
        {
            throw new ConflictException($"The knowledge base is limited to {_options.MaxDocumentsPerTenant} documents.");
        }

        // Extraction/validation happens before anything is persisted: a bad file leaves no trace.
        var extracted = TextExtractor.Extract(fileName, bytes);
        var redacted = extracted.Select(s => (Segment: s, Result: SecretRedactor.Redact(s.Text))).ToList();
        var segments = redacted.Select(r => new TextSegment(r.Result.Text, r.Segment.Page)).ToList();
        var redactions = redacted.Sum(r => r.Result.Count);
        var drafts = Chunker.Split(segments, _options.ChunkSizeChars, _options.ChunkOverlapChars);
        if (drafts.Count is 0 or > MaxChunksPerDocument)
        {
            throw new ValidationException("file", drafts.Count == 0 ? "No readable text was found." : $"The document is too large (more than {MaxChunksPerDocument} sections).");
        }

        var documentTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title.Trim();
        var document = new KnowledgeDocument(
            principal.TenantId,
            documentTitle.Length > KnowledgeDocument.TitleMaxLength ? documentTitle[..KnowledgeDocument.TitleMaxLength] : documentTitle,
            fileName.Length > KnowledgeDocument.FileNameMaxLength ? fileName[^KnowledgeDocument.FileNameMaxLength..] : fileName,
            TextExtractor.ContentTypeFor(Path.GetExtension(fileName).ToLowerInvariant()),
            bytes.Length,
            hash,
            principal.UserId);
        db.Set<KnowledgeDocument>().Add(document);
        await db.SaveChangesAsync(cancellationToken);

        var fullText = string.Join("\n\n", segments.Select(s => s.Text));
        try
        {
            await IndexAsync(document, drafts, fullText, redactions, cancellationToken);
        }
        catch (ServiceUnavailableException ex)
        {
            document.MarkFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await audit.RecordAsync(
            new AuditRecord(AuditKinds.KnowledgeUploaded, document.FileName, document.Status == DocumentStatus.Ready ? AuditStatus.Success : AuditStatus.Failed,
                Input: new { document.Title, document.SizeBytes, document.ChunkCount, redactions }, Summary: document.FailureReason),
            cancellationToken);
        return ToDto(document);
    }

    public async Task<PagedResult<KnowledgeDocumentDto>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = db.Set<KnowledgeDocument>().AsNoTracking();
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(d => d.Id).Skip(page.Skip).Take(page.SafePageSize).Select(Project).ToListAsync(cancellationToken);
        return new PagedResult<KnowledgeDocumentDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<KnowledgeDocumentDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<KnowledgeDocument>().AsNoTracking().Where(d => d.Id == id).Select(Project).FirstOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException("Document", id);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireManager();
        var document = await db.Set<KnowledgeDocument>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Document", id);
        db.Set<KnowledgeDocument>().Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditRecord(AuditKinds.KnowledgeDeleted, document.FileName, AuditStatus.Success, Summary: document.Title), cancellationToken);
    }

    public async Task<KnowledgeDocumentDto> ReindexAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireManager();
        var document = await db.Set<KnowledgeDocument>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Document", id);
        if (string.IsNullOrEmpty(document.ExtractedText))
        {
            throw new ConflictException("The document has no extracted text to re-index. Delete it and upload it again.");
        }

        await db.Set<KnowledgeChunk>().Where(c => c.DocumentId == id).ExecuteDeleteAsync(cancellationToken);
        var drafts = Chunker.Split([new TextSegment(document.ExtractedText, null)], _options.ChunkSizeChars, _options.ChunkOverlapChars);
        await IndexAsync(document, drafts, document.ExtractedText, document.RedactionCount, cancellationToken);
        await audit.RecordAsync(new AuditRecord(AuditKinds.KnowledgeReindexed, document.FileName, AuditStatus.Success, Summary: embeddings.ModelId), cancellationToken);
        return ToDto(document);
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int? limit, CancellationToken cancellationToken)
    {
        var cleaned = SecretRedactor.RedactText(query).Trim();
        if (cleaned.Length == 0)
        {
            return [];
        }

        var top = Math.Clamp(limit ?? _options.TopK, 1, 10);
        var vectors = await embeddings.EmbedAsync([cleaned], cancellationToken);
        var queryVector = vectors[0];
        var model = embeddings.ModelId;

        // Tenant isolation: the global query filter (and RLS) restrict this to the caller's tenant.
        var candidates = await db.Set<KnowledgeChunk>().AsNoTracking()
            .Where(c => c.EmbeddingModel == model)
            .Select(c => new { c.Id, c.Embedding })
            .Take(_options.MaxChunksScanned)
            .ToListAsync(cancellationToken);

        var best = candidates
            .Select(c => (c.Id, Score: HashingEmbeddingProvider.Cosine(queryVector, c.Embedding)))
            .Where(c => c.Score >= _options.MinScore)
            .OrderByDescending(c => c.Score)
            .Take(top)
            .ToList();
        if (best.Count == 0)
        {
            return [];
        }

        var ids = best.Select(b => b.Id).ToList();
        var rows = await db.Set<KnowledgeChunk>().AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Join(db.Set<KnowledgeDocument>().AsNoTracking(), c => c.DocumentId, d => d.Id, (c, d) => new { Chunk = c, d.Title })
            .ToListAsync(cancellationToken);

        return best
            .Join(rows, b => b.Id, r => r.Chunk.Id, (b, r) => new KnowledgeHit(
                r.Chunk.DocumentId, r.Title, r.Chunk.Id, r.Chunk.Ordinal, r.Chunk.Section, r.Chunk.Page, Math.Round(b.Score, 4),
                SecretRedactor.RedactText(r.Chunk.Content)))
            .ToList();
    }

    private async Task IndexAsync(KnowledgeDocument document, IReadOnlyList<ChunkDraft> drafts, string fullText, int redactions, CancellationToken cancellationToken)
    {
        var inputs = drafts.Select(d => d.Section is null ? d.Content : d.Section + "\n" + d.Content).ToList();
        var vectors = await embeddings.EmbedAsync(inputs, cancellationToken);
        var model = embeddings.ModelId;
        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            db.Set<KnowledgeChunk>().Add(new KnowledgeChunk(document.TenantId, document.Id, d.Ordinal, d.Content, Trim(d.Section, 300), d.Page, vectors[i], model));
        }

        document.MarkReady(fullText, fullText.Length, drafts.Count, redactions, model);
        await db.SaveChangesAsync(cancellationToken);
    }

    private AssistantPrincipal RequireManager()
    {
        var principal = principals.Current;
        if (!principal.HasAtLeast(TenantRole.Admin))
        {
            throw new ForbiddenException("Managing the knowledge base requires the Admin or Owner role.");
        }

        return principal;
    }

    private async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > _options.MaxUploadBytes)
            {
                throw new PayloadTooLargeException($"The file exceeds the {_options.MaxUploadBytes / (1024 * 1024)} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? Trim(string? value, int max) => value is { } v && v.Length > max ? v[..max] : value;

    private static readonly System.Linq.Expressions.Expression<Func<KnowledgeDocument, KnowledgeDocumentDto>> Project = d => new KnowledgeDocumentDto(
        d.Id, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.Status, d.FailureReason, d.ChunkCount, d.CharacterCount, d.RedactionCount, d.EmbeddingModel, d.CreatedAtUtc);

    private static KnowledgeDocumentDto ToDto(KnowledgeDocument d) => new(
        d.Id, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.Status, d.FailureReason, d.ChunkCount, d.CharacterCount, d.RedactionCount, d.EmbeddingModel, d.CreatedAtUtc);
}
