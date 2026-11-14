namespace Solvix.Modules.Devices.Application;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Serializer settings for JSON documents stored by the module (same conventions as the API).</summary>
internal static class DevicesJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
