namespace Solvix.Modules.Assistant.Configuration;

/// <summary>Bound from <c>Solvix:Assistant</c>. Secrets (API keys) come from environment/secret stores only.</summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Solvix:Assistant";

    public LlmOptions Llm { get; set; } = new();

    public KnowledgeOptions Knowledge { get; set; } = new();

    public RemediationOptions Remediation { get; set; } = new();

    /// <summary>Maximum model round-trips (tool loops) per user message.</summary>
    public int MaxToolIterations { get; set; } = 6;

    public int MaxHistoryMessages { get; set; } = 20;

    public int MaxMessageChars { get; set; } = 4000;

    public int MaxToolResultChars { get; set; } = 16000;

    /// <summary>Chat messages per user per minute.</summary>
    public int ChatPermitsPerMinute { get; set; } = 20;

    /// <summary>Serve a deterministic, in-memory demo fleet when no real fleet module is connected. Never allowed in Production.</summary>
    public bool UseDemoFleet { get; set; }
}

public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "claude-sonnet-5-5";

    public int MaxOutputTokens { get; set; } = 2048;

    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class RemediationOptions
{
    public int ProposalTtlMinutes { get; set; } = 1440;

    public int MaxTargetsPerProposal { get; set; } = 100;

    /// <summary>Proposals touching more devices than this are escalated to High risk (owner approval).</summary>
    public int HighRiskTargetThreshold { get; set; } = 25;

    /// <summary>When true, read-only diagnostic actions run immediately without a human click. Default off.</summary>
    public bool AutoExecuteReadOnly { get; set; }
}

public sealed class KnowledgeOptions
{
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxDocumentsPerTenant { get; set; } = 500;

    public int ChunkSizeChars { get; set; } = 1000;

    public int ChunkOverlapChars { get; set; } = 150;

    public int TopK { get; set; } = 5;

    public double MinScore { get; set; } = 0.12;

    /// <summary>Safety cap on chunks scored per query (in-process similarity).</summary>
    public int MaxChunksScanned { get; set; } = 50000;

    public EmbeddingOptions Embeddings { get; set; } = new();
}

public sealed class EmbeddingOptions
{
    /// <summary><c>local</c> (default, offline hashing embedder) or <c>openai</c> (OpenAI-compatible /embeddings endpoint).</summary>
    public string Provider { get; set; } = "local";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "text-embedding-3-small";
}
