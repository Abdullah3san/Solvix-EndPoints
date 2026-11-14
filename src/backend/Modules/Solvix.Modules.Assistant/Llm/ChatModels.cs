namespace Solvix.Modules.Assistant.Llm;

using System.Text.Json;
using System.Text.Json.Nodes;

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;

public sealed record ChatMessage(string Role, IReadOnlyList<ContentBlock> Content)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public static ChatMessage User(string text) => new(UserRole, [new TextBlock(text)]);

    public static ChatMessage Assistant(string text) => new(AssistantRole, [new TextBlock(text)]);
}

/// <summary>Provider-neutral tool definition: JSON-schema describing the arguments.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonNode InputSchema);

public sealed record ChatRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    int MaxOutputTokens);

public sealed record ChatUsage(int InputTokens, int OutputTokens);

public sealed record ChatResponse(IReadOnlyList<ContentBlock> Content, string StopReason, ChatUsage Usage, string Model)
{
    public IReadOnlyList<ToolUseBlock> ToolUses => Content.OfType<ToolUseBlock>().ToList();

    public string Text => string.Concat(Content.OfType<TextBlock>().Select(t => t.Text));
}

/// <summary>Raised for provider failures. The message is safe to log; it never contains the API key.</summary>
public sealed class ChatModelException : Exception
{
    public ChatModelException(string message)
        : base(message)
    {
    }

    public ChatModelException()
    {
    }

    public ChatModelException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public interface IChatModel
{
    /// <summary>False when no API key is configured.</summary>
    bool IsConfigured { get; }

    string ModelName { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}
