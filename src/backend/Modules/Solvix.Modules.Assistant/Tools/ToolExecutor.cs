namespace Solvix.Modules.Assistant.Tools;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Configuration;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Llm;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Exceptions;

public sealed record ToolResult(bool IsError, string Content, string Status, string Summary);

/// <summary>
/// The only path from a model "tool_use" to application code. It (1) resolves the tool from a fixed
/// registry, (2) checks the signed-in user's role again regardless of what the model was offered,
/// (3) lets the tool validate its arguments, (4) redacts and size-limits the result, (5) audits every call.
/// </summary>
public sealed partial class ToolExecutor(
    IEnumerable<IAssistantTool> tools,
    IAssistantAuditLog audit,
    IOptions<AssistantOptions> options,
    ILogger<ToolExecutor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IReadOnlyDictionary<string, IAssistantTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyList<ToolDefinition> DefinitionsFor(AssistantPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return _tools.Values
            .Where(t => principal.HasAtLeast(t.MinimumRole))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))
            .ToList();
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolUseBlock call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(call);
        var stopwatch = Stopwatch.StartNew();
        var inputText = call.Input.ValueKind == JsonValueKind.Undefined ? "{}" : call.Input.GetRawText();

        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(true, $"Unknown tool '{Truncate(call.Name, 60)}'.", "denied", "unknown tool"), AuditStatus.Denied, cancellationToken);
        }

        if (!context.Principal.HasAtLeast(tool.MinimumRole))
        {
            return await FinishAsync(context, call, inputText, stopwatch,
                new ToolResult(true, $"Not permitted: '{call.Name}' requires the {tool.MinimumRole} role.", "denied", "role insufficient"), AuditStatus.Denied, cancellationToken);
        }

        if (inputText.Length > 8000)
        {
            return await FinishAsync(context, call, Truncate(inputText, 500), stopwatch, new ToolResult(true, "Arguments are too large.", "failed", "arguments too large"), AuditStatus.Failed, cancellationToken);
        }

        try
        {
            var result = await tool.ExecuteAsync(context, ToolArgs.Parse(call.Input, AllowedArguments(tool)), cancellationToken);
            var content = SecretRedactor.RedactText(JsonSerializer.Serialize(result, Json));
            var max = options.Value.MaxToolResultChars;
            if (content.Length > max)
            {
                return await FinishAsync(context, call, inputText, stopwatch,
                    new ToolResult(true, $"The result was too large ({content.Length} characters). Narrow the filters or lower the limit and try again.", "failed", "result too large"), AuditStatus.Failed, cancellationToken);
            }

            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(false, content, "ok", $"{content.Length} chars"), AuditStatus.Success, cancellationToken);
        }
        catch (ToolInputException ex)
        {
            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(true, ex.Message, "failed", ex.Message), AuditStatus.Failed, cancellationToken);
        }
        catch (ValidationException ex)
        {
            var detail = ex.Errors.Count > 0 ? string.Join(" ", ex.Errors.SelectMany(e => e.Value)) : ex.Message;
            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(true, detail, "failed", detail), AuditStatus.Failed, cancellationToken);
        }
        catch (ForbiddenException ex)
        {
            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(true, ex.Message, "denied", ex.Message), AuditStatus.Denied, cancellationToken);
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException or ServiceUnavailableException)
        {
            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(true, ex.Message, "failed", ex.Message), AuditStatus.Failed, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogToolFailure(logger, ex, call.Name);
            return await FinishAsync(context, call, inputText, stopwatch, new ToolResult(true, "The tool failed unexpectedly.", "failed", "unexpected error"), AuditStatus.Failed, cancellationToken);
        }
    }

    private static IReadOnlyCollection<string> AllowedArguments(IAssistantTool tool) =>
        tool.InputSchema["properties"] is System.Text.Json.Nodes.JsonObject props ? props.Select(p => p.Key).ToList() : [];

    private async Task<ToolResult> FinishAsync(ToolContext context, ToolUseBlock call, string inputText, Stopwatch stopwatch, ToolResult result, AuditStatus status, CancellationToken cancellationToken)
    {
        context.State.AddCall(new ToolCallRecord(call.Name, result.Status, Truncate(SecretRedactor.RedactText(result.Summary), 200)));
        try
        {
            await audit.RecordAsync(
                new AuditRecord(AuditKinds.ToolCall, Truncate(call.Name, 100), status, context.ConversationId, Input: inputText, Summary: result.Summary, DurationMs: stopwatch.ElapsedMilliseconds),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Read-only tool calls must not break the conversation if the audit sink hiccups; the failure is logged loudly.
            LogAuditFailure(logger, ex, call.Name);
        }

        return result;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Error, Message = "Assistant tool {Tool} failed")]
    private static partial void LogToolFailure(ILogger logger, Exception exception, string tool);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit write failed for assistant tool {Tool}")]
    private static partial void LogAuditFailure(ILogger logger, Exception exception, string tool);
}
