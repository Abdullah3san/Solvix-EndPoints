namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;

public sealed class AssistantMessage : TenantEntity
{
    private AssistantMessage()
    {
    }

    private AssistantMessage(Guid tenantId, Guid conversationId, MessageRole role, string content)
        : base(tenantId)
    {
        ConversationId = conversationId;
        Role = role;
        Content = content;
    }

    public Guid ConversationId { get; private set; }

    public MessageRole Role { get; private set; }

    /// <summary>Always stored after secret redaction.</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>JSON: knowledge-base sources cited/retrieved while answering.</summary>
    public string? SourcesJson { get; private set; }

    /// <summary>JSON: tools the assistant called while answering (name, status, summary).</summary>
    public string? ToolCallsJson { get; private set; }

    /// <summary>JSON: ids of remediation proposals / tickets / reports created while answering.</summary>
    public string? ArtifactsJson { get; private set; }

    public string? Model { get; private set; }

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public static AssistantMessage FromUser(Guid tenantId, Guid conversationId, string content) =>
        new(tenantId, conversationId, MessageRole.User, content);

    public static AssistantMessage FromAssistant(
        Guid tenantId,
        Guid conversationId,
        string content,
        string? sourcesJson,
        string? toolCallsJson,
        string? artifactsJson,
        string? model,
        int inputTokens,
        int outputTokens) =>
        new(tenantId, conversationId, MessageRole.Assistant, content)
        {
            SourcesJson = sourcesJson,
            ToolCallsJson = toolCallsJson,
            ArtifactsJson = artifactsJson,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };
}
