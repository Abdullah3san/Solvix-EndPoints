namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;

public sealed class KnowledgeChunk : TenantEntity
{
    private KnowledgeChunk()
    {
    }

    public KnowledgeChunk(Guid tenantId, Guid documentId, int ordinal, string content, string? section, int? page, float[] embedding, string embeddingModel)
        : base(tenantId)
    {
        DocumentId = documentId;
        Ordinal = ordinal;
        Content = content;
        Section = section;
        Page = page;
        Embedding = embedding;
        EmbeddingModel = embeddingModel;
    }

    public Guid DocumentId { get; private set; }

    public int Ordinal { get; private set; }

    public string Content { get; private set; } = string.Empty;

    /// <summary>Nearest heading, used in source references.</summary>
    public string? Section { get; private set; }

    public int? Page { get; private set; }

    public float[] Embedding { get; private set; } = [];

    public string EmbeddingModel { get; private set; } = string.Empty;
}
