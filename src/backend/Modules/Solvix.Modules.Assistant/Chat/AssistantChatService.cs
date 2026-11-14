namespace Solvix.Modules.Assistant.Chat;

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Configuration;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Knowledge.Embeddings;
using Solvix.Modules.Assistant.Llm;
using Solvix.Modules.Assistant.Remediation;
using Solvix.Modules.Assistant.Security;
using Solvix.Modules.Assistant.Tools;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IAssistantChatService
{
    Task<ChatReplyDto> SendAsync(SendMessageRequest request, CancellationToken cancellationToken);

    Task<PagedResult<ConversationDto>> ListConversationsAsync(PageRequest page, CancellationToken cancellationToken);

    Task<ConversationDetailDto> GetConversationAsync(Guid id, CancellationToken cancellationToken);

    Task ArchiveConversationAsync(Guid id, CancellationToken cancellationToken);

    AssistantStatusDto GetStatus();
}

/// <summary>
/// Runs the tool-calling loop. The model proposes tool calls; every call is executed by
/// <see cref="ToolExecutor"/> as the signed-in user. The orchestrator itself has no database access
/// to fleet data and no way to execute device commands.
/// </summary>
internal sealed partial class AssistantChatService(
    ISolvixDbContext db,
    IAssistantPrincipalAccessor principals,
    IChatModel model,
    ToolExecutor executor,
    IAssistantAuditLog audit,
    IFleetQueryService fleet,
    IDeviceCommandGateway commands,
    IEmbeddingProvider embeddings,
    IOptions<AssistantOptions> options,
    IClock clock,
    ILogger<AssistantChatService> logger) : IAssistantChatService
{
    private const int MaxCallsPerIteration = 8;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex CitationPattern = CitationRegex();
    private readonly AssistantOptions _options = options.Value;

    public async Task<ChatReplyDto> SendAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var principal = principals.Current;
        var text = request.Message?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > _options.MaxMessageChars)
        {
            throw new ValidationException("message", $"message is required (max {_options.MaxMessageChars} characters).");
        }

        if (!model.IsConfigured)
        {
            throw new ServiceUnavailableException("The AI model is not configured. Ask an administrator to set Solvix:Assistant:Llm:ApiKey.", "assistant_unavailable");
        }

        var redaction = SecretRedactor.Redact(text);
        var conversation = await GetOrStartAsync(principal, request.ConversationId, redaction.Text, cancellationToken);

        var history = await db.Set<AssistantMessage>().AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.Id)
            .Take(_options.MaxHistoryMessages)
            .ToListAsync(cancellationToken);
        history.Reverse();

        db.Set<AssistantMessage>().Add(AssistantMessage.FromUser(principal.TenantId, conversation.Id, redaction.Text));
        conversation.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditRecord(AuditKinds.Request, "chat", AuditStatus.Success, conversation.Id,
                Input: new { message = Truncate(redaction.Text, 500), redactions = redaction.Count }, Model: model.ModelName),
            cancellationToken);

        var messages = history.Select(m => m.Role == MessageRole.User ? ChatMessage.User(m.Content) : ChatMessage.Assistant(m.Content)).ToList();
        messages.Add(ChatMessage.User(redaction.Text));

        var state = new TurnState();
        var context = new ToolContext(principal, conversation.Id, state);
        var tools = executor.DefinitionsFor(principal);
        var system = SystemPrompt.Build(principal, clock.UtcNow);
        int inputTokens = 0, outputTokens = 0;
        string finalText = string.Empty;

        try
        {
            for (var iteration = 0; ; iteration++)
            {
                var offerTools = iteration < _options.MaxToolIterations;
                var response = await model.CompleteAsync(
                    new ChatRequest(system, messages, offerTools ? tools : [], _options.Llm.MaxOutputTokens),
                    cancellationToken);
                inputTokens += response.Usage.InputTokens;
                outputTokens += response.Usage.OutputTokens;

                var calls = response.ToolUses;
                if (calls.Count == 0 || !offerTools)
                {
                    finalText = response.Text;
                    break;
                }

                messages.Add(new ChatMessage(ChatMessage.AssistantRole, response.Content));
                var results = new List<ContentBlock>();
                for (var i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    if (i >= MaxCallsPerIteration)
                    {
                        results.Add(new ToolResultBlock(call.Id, "Too many tool calls in one step; call fewer tools at a time.", true));
                        continue;
                    }

                    var result = await executor.ExecuteAsync(context, call, cancellationToken);
                    results.Add(new ToolResultBlock(call.Id, result.Content, result.IsError));
                }

                messages.Add(new ChatMessage(ChatMessage.UserRole, results));
            }
        }
        catch (ChatModelException ex)
        {
            LogModelFailure(logger, ex.Message);
            await audit.RecordAsync(new AuditRecord(AuditKinds.ModelError, "chat", AuditStatus.Failed, conversation.Id, Summary: ex.Message, Model: model.ModelName), CancellationToken.None);
            throw new ServiceUnavailableException("The AI model is temporarily unavailable. Please try again.", "assistant_model_error");
        }

        finalText = SecretRedactor.RedactText(finalText).Trim();
        if (finalText.Length == 0)
        {
            finalText = "I wasn't able to produce an answer. Please rephrase or narrow the question.";
        }

        var cited = CitationPattern.Matches(finalText).Select(m => "S" + m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var sources = state.Sources.Select(s => new SourceDto(s.Id, s.DocumentId, s.DocumentTitle, s.ChunkId, s.Ordinal, s.Section, s.Page, s.Score, cited.Contains(s.Id))).ToList();

        var assistantMessage = AssistantMessage.FromAssistant(
            principal.TenantId,
            conversation.Id,
            finalText,
            sources.Count == 0 ? null : JsonSerializer.Serialize(sources, Json),
            state.Calls.Count == 0 ? null : JsonSerializer.Serialize(state.Calls, Json),
            state.Artifacts.Count == 0 ? null : JsonSerializer.Serialize(state.Artifacts, Json),
            model.ModelName,
            inputTokens,
            outputTokens);
        db.Set<AssistantMessage>().Add(assistantMessage);
        conversation.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditRecord(AuditKinds.Response, "chat", AuditStatus.Success, conversation.Id,
                Input: new { toolCalls = state.Calls.Count, artifacts = state.Artifacts.Count, inputTokens, outputTokens },
                Summary: Truncate(finalText, 300), Model: model.ModelName),
            cancellationToken);

        return new ChatReplyDto(conversation.Id, ToDto(assistantMessage));
    }

    public async Task<PagedResult<ConversationDto>> ListConversationsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var userId = principals.Current.UserId;
        var query = db.Set<AssistantConversation>().AsNoTracking().Where(c => c.UserId == userId && !c.IsArchived);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(c => c.LastMessageAtUtc).Skip(page.Skip).Take(page.SafePageSize)
            .Select(c => new ConversationDto(c.Id, c.Title, c.LastMessageAtUtc))
            .ToListAsync(cancellationToken);
        return new PagedResult<ConversationDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<ConversationDetailDto> GetConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        var conversation = await FindOwnedAsync(id, asNoTracking: true, cancellationToken);
        var messages = await db.Set<AssistantMessage>().AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Id).ToListAsync(cancellationToken);
        return new ConversationDetailDto(conversation.Id, conversation.Title, messages.Select(ToDto).ToList());
    }

    public async Task ArchiveConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        var conversation = await FindOwnedAsync(id, asNoTracking: false, cancellationToken);
        conversation.Archive();
        await db.SaveChangesAsync(cancellationToken);
    }

    public AssistantStatusDto GetStatus() => new(
        model.IsConfigured,
        model.ModelName,
        embeddings.ModelId,
        fleet.IsAvailable,
        commands.IsAvailable,
        fleet is Demo.DemoFleet,
        executor.DefinitionsFor(principals.Current).Select(t => t.Name).ToList(),
        ActionCatalog.All.Select(a => a.Name).ToList());

    private async Task<AssistantConversation> GetOrStartAsync(AssistantPrincipal principal, Guid? id, string firstMessage, CancellationToken cancellationToken)
    {
        if (id is null)
        {
            var created = AssistantConversation.Start(principal.TenantId, principal.UserId, firstMessage, clock.UtcNow);
            db.Set<AssistantConversation>().Add(created);
            return created;
        }

        return await FindOwnedAsync(id.Value, asNoTracking: false, cancellationToken);
    }

    private async Task<AssistantConversation> FindOwnedAsync(Guid id, bool asNoTracking, CancellationToken cancellationToken)
    {
        var userId = principals.Current.UserId;
        var query = asNoTracking ? db.Set<AssistantConversation>().AsNoTracking() : db.Set<AssistantConversation>();
        return await query.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && !c.IsArchived, cancellationToken)
            ?? throw new NotFoundException("Conversation", id);
    }

    private static MessageDto ToDto(AssistantMessage m) => new(
        m.Id,
        m.Role,
        m.Content,
        m.SourcesJson is null ? [] : JsonSerializer.Deserialize<List<SourceDto>>(m.SourcesJson, Json) ?? [],
        m.ToolCallsJson is null ? [] : JsonSerializer.Deserialize<List<ToolCallRecord>>(m.ToolCallsJson, Json) ?? [],
        m.ArtifactsJson is null ? [] : JsonSerializer.Deserialize<List<ArtifactRef>>(m.ArtifactsJson, Json) ?? [],
        m.CreatedAtUtc);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"\[S(\d{1,3})\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex CitationRegex();

    [LoggerMessage(Level = LogLevel.Error, Message = "AI model call failed: {Reason}")]
    private static partial void LogModelFailure(ILogger logger, string reason);
}
