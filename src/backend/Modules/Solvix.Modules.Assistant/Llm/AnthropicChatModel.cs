namespace Solvix.Modules.Assistant.Llm;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Configuration;

/// <summary>Claude Messages API client with tool use. Only the minimum needed is implemented; the API key is never logged.</summary>
internal sealed partial class AnthropicChatModel(HttpClient http, IOptions<AssistantOptions> options, ILogger<AnthropicChatModel> logger) : IChatModel
{
    private const int MaxAttempts = 3;
    private readonly LlmOptions _options = options.Value.Llm;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string ModelName => _options.Model;

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConfigured)
        {
            throw new ChatModelException("The AI model is not configured.");
        }

        var body = BuildBody(request);
        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            message.Headers.Add("x-api-key", _options.ApiKey);
            message.Headers.Add("anthropic-version", "2023-06-01");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(message, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                LogTransient(logger, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ChatModelException("The AI model could not be reached.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ChatModelException("The AI model timed out.", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    return Parse(json);
                }

                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)529;
                if (retryable && attempt < MaxAttempts)
                {
                    LogTransient(logger, attempt, response.StatusCode.ToString());
                    await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), cancellationToken);
                    continue;
                }

                LogFailure(logger, (int)response.StatusCode);
                throw new ChatModelException($"The AI model returned HTTP {(int)response.StatusCode}.");
            }
        }
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var content = new JsonArray();
            foreach (var block in m.Content)
            {
                content.Add(block switch
                {
                    TextBlock t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
                    ToolUseBlock u => new JsonObject { ["type"] = "tool_use", ["id"] = u.Id, ["name"] = u.Name, ["input"] = JsonNode.Parse(u.Input.GetRawText()) },
                    ToolResultBlock r => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = r.ToolUseId, ["content"] = r.Content, ["is_error"] = r.IsError },
                    _ => throw new NotSupportedException(),
                });
            }

            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = request.System,
            ["messages"] = messages,
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(t.InputSchema.ToJsonString()) });
            }

            body["tools"] = tools;
        }

        return body;
    }

    internal static ChatResponse Parse(JsonElement json)
    {
        var blocks = new List<ContentBlock>();
        if (json.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                switch (item.GetProperty("type").GetString())
                {
                    case "text":
                        blocks.Add(new TextBlock(item.GetProperty("text").GetString() ?? string.Empty));
                        break;
                    case "tool_use":
                        blocks.Add(new ToolUseBlock(
                            item.GetProperty("id").GetString() ?? string.Empty,
                            item.GetProperty("name").GetString() ?? string.Empty,
                            item.GetProperty("input").Clone()));
                        break;
                }
            }
        }

        var usage = json.TryGetProperty("usage", out var u)
            ? new ChatUsage(
                u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0,
                u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0)
            : new ChatUsage(0, 0);
        return new ChatResponse(
            blocks,
            json.TryGetProperty("stop_reason", out var s) ? s.GetString() ?? string.Empty : string.Empty,
            usage,
            json.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI model call failed transiently (attempt {Attempt}): {Reason}")]
    private static partial void LogTransient(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI model call failed with HTTP {Status}")]
    private static partial void LogFailure(ILogger logger, int status);
}
