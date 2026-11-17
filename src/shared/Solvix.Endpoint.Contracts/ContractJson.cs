namespace Solvix.Endpoint.Contracts;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solvix.Endpoint.Contracts.Configuration;

/// <summary>
/// JSON conventions for everything exchanged between server and agent: web
/// defaults (camelCase), enums as strings, nulls omitted. The same options are
/// used to compute setting hashes, so both sides agree on "same value".
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Serialises a setting with its discriminator, in canonical form.</summary>
    public static string SerializeSetting(ConfigurationSetting setting) =>
        JsonSerializer.Serialize(setting, Options);

    public static ConfigurationSetting DeserializeSetting(string json) =>
        JsonSerializer.Deserialize<ConfigurationSetting>(json, Options)
        ?? throw new JsonException("Setting payload is empty.");

    /// <summary>Lower-case hex SHA-256 of the canonical setting JSON.</summary>
    public static string Hash(ConfigurationSetting setting) => Sha256(SerializeSetting(setting));

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
