namespace Solvix.Modules.Assistant.Tools;

using System.Text.Json;
using System.Text.Json.Nodes;
using Solvix.Modules.Assistant.Knowledge;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Identity;

/// <summary>A problem with the model's arguments. The message is returned to the model so it can correct the call.</summary>
public sealed class ToolInputException : Exception
{
    public ToolInputException(string message)
        : base(message)
    {
    }

    public ToolInputException()
    {
    }

    public ToolInputException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed record SourceRef(string Id, Guid DocumentId, string DocumentTitle, Guid ChunkId, int Ordinal, string? Section, int? Page, double Score);

public sealed record ArtifactRef(string Type, Guid Id, string Label);

public sealed record ToolCallRecord(string Name, string Status, string Summary);

/// <summary>Mutable per-turn collector so the final answer can carry source references and created artifacts.</summary>
public sealed class TurnState
{
    private readonly List<SourceRef> _sources = [];
    private readonly List<ArtifactRef> _artifacts = [];
    private readonly List<ToolCallRecord> _calls = [];

    public IReadOnlyList<SourceRef> Sources => _sources;

    public IReadOnlyList<ArtifactRef> Artifacts => _artifacts;

    public IReadOnlyList<ToolCallRecord> Calls => _calls;

    public SourceRef AddSource(KnowledgeHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var existing = _sources.FirstOrDefault(s => s.ChunkId == hit.ChunkId);
        if (existing is not null)
        {
            return existing;
        }

        var source = new SourceRef($"S{_sources.Count + 1}", hit.DocumentId, hit.DocumentTitle, hit.ChunkId, hit.Ordinal, hit.Section, hit.Page, hit.Score);
        _sources.Add(source);
        return source;
    }

    public void AddArtifact(ArtifactRef artifact) => _artifacts.Add(artifact);

    public void AddCall(ToolCallRecord call) => _calls.Add(call);
}

public sealed record ToolContext(AssistantPrincipal Principal, Guid? ConversationId, TurnState State);

public interface IAssistantTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>The caller must hold at least this role for the tool to be offered to the model or executed.</summary>
    TenantRole MinimumRole { get; }

    JsonNode InputSchema { get; }

    /// <summary>Returns a JSON-serialisable result. Throw <see cref="ToolInputException"/> or a domain exception for failures the model can act on.</summary>
    Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken);
}

/// <summary>Strict reader over the model-supplied arguments: unknown properties are rejected.</summary>
public sealed class ToolArgs
{
    private readonly JsonElement _root;

    private ToolArgs(JsonElement root) => _root = root;

    public static ToolArgs Parse(JsonElement input, IReadOnlyCollection<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new ToolArgs(JsonDocument.Parse("{}").RootElement);
        }

        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ToolInputException("Arguments must be a JSON object.");
        }

        foreach (var property in input.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ToolInputException($"Unknown argument '{property.Name}'. Allowed: {string.Join(", ", allowed)}.");
            }
        }

        return new ToolArgs(input);
    }

    public bool Has(string name) => _root.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public string? OptionalString(string name, int maxLength = 500)
    {
        if (!Has(name))
        {
            return null;
        }

        var element = _root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ToolInputException($"'{name}' must be a string.");
        }

        var value = element.GetString()!.Trim();
        if (value.Length > maxLength)
        {
            throw new ToolInputException($"'{name}' must be at most {maxLength} characters.");
        }

        return value.Length == 0 ? null : value;
    }

    public string RequiredString(string name, int maxLength = 500) =>
        OptionalString(name, maxLength) ?? throw new ToolInputException($"'{name}' is required.");

    public int Int(string name, int min, int max, int defaultValue)
    {
        if (!Has(name))
        {
            return defaultValue;
        }

        var element = _root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new ToolInputException($"'{name}' must be an integer.");
        }

        if (value < min || value > max)
        {
            throw new ToolInputException($"'{name}' must be between {min} and {max}.");
        }

        return value;
    }

    public double? OptionalNumber(string name, double min, double max)
    {
        if (!Has(name))
        {
            return null;
        }

        var element = _root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
        {
            throw new ToolInputException($"'{name}' must be a number.");
        }

        if (value < min || value > max)
        {
            throw new ToolInputException($"'{name}' must be between {min} and {max}.");
        }

        return value;
    }

    public bool? OptionalBool(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        var element = _root.GetProperty(name);
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw new ToolInputException($"'{name}' must be true or false.");
    }

    public TEnum? OptionalEnum<TEnum>(string name)
        where TEnum : struct, Enum
    {
        var raw = OptionalString(name, 50);
        if (raw is null)
        {
            return null;
        }

        var normalized = raw.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(value.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new ToolInputException($"'{name}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    public TEnum RequiredEnum<TEnum>(string name)
        where TEnum : struct, Enum =>
        OptionalEnum<TEnum>(name) ?? throw new ToolInputException($"'{name}' is required.");

    public IReadOnlyList<string> StringList(string name, int maxCount, int maxLength = 100)
    {
        if (!Has(name))
        {
            return [];
        }

        var element = _root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ToolInputException($"'{name}' must be an array of strings.");
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > maxLength)
            {
                throw new ToolInputException($"'{name}' must contain non-empty strings of at most {maxLength} characters.");
            }

            list.Add(item.GetString()!.Trim());
        }

        return list.Count > maxCount ? throw new ToolInputException($"'{name}' may contain at most {maxCount} items.") : list;
    }

    public IReadOnlyDictionary<string, JsonElement> Object(string name)
    {
        if (!Has(name))
        {
            return new Dictionary<string, JsonElement>();
        }

        var element = _root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ToolInputException($"'{name}' must be an object.");
        }

        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}

/// <summary>Tiny JSON-schema builders for tool definitions.</summary>
internal static class Schema
{
    public static JsonObject Object(JsonObject properties, params string[] required) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["additionalProperties"] = false,
        };

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Int(string description, int min, int max) => new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Enum(string description, IEnumerable<string> values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) };

    public static JsonObject StrArray(string description, int maxItems) =>
        new() { ["type"] = "array", ["description"] = description, ["maxItems"] = maxItems, ["items"] = new JsonObject { ["type"] = "string" } };

    public static JsonObject FreeObject(string description) => new() { ["type"] = "object", ["description"] = description };
}
