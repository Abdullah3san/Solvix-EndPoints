namespace Solvix.Modules.Assistant.Chat;

using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Tools;

public sealed record SendMessageRequest(Guid? ConversationId, string Message);

public sealed record SourceDto(string Id, Guid DocumentId, string DocumentTitle, Guid ChunkId, int Ordinal, string? Section, int? Page, double Score, bool Cited);

public sealed record MessageDto(
    Guid Id,
    MessageRole Role,
    string Content,
    IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<ArtifactRef> Artifacts,
    DateTimeOffset CreatedAtUtc);

public sealed record ChatReplyDto(Guid ConversationId, MessageDto Message);

public sealed record ConversationDto(Guid Id, string Title, DateTimeOffset LastMessageAtUtc);

public sealed record ConversationDetailDto(Guid Id, string Title, IReadOnlyList<MessageDto> Messages);

public sealed record AssistantStatusDto(
    bool ModelConfigured,
    string Model,
    string EmbeddingModel,
    bool FleetConnected,
    bool CommandsConnected,
    bool DemoData,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Actions);
