namespace Solvix.Modules.Security.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>Maps an immutable record to a PostgreSQL <c>jsonb</c> column, compared by its serialised value.</summary>
internal static class JsonColumn
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PropertyBuilder<T> HasJsonbConversion<T>(this PropertyBuilder<T> property)
        where T : class?
    {
        ArgumentNullException.ThrowIfNull(property);
        var comparer = new ValueComparer<T>(
            (a, b) => Serialize(a) == Serialize(b),
            v => Serialize(v).GetHashCode(StringComparison.Ordinal),
            v => Deserialize<T>(Serialize(v))!);
        property.HasColumnType("jsonb");
        property.HasConversion(
            v => Serialize(v),
            v => Deserialize<T>(v)!,
            comparer);
        return property;
    }

    private static string Serialize<T>(T? value) => JsonSerializer.Serialize(value, Options);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
