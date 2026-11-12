namespace Solvix.Agent.Windows.Inventory;

using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

/// <summary>Read-only WMI queries (local machine, fixed WQL, bounded time).</summary>
[SupportedOSPlatform("windows")]
internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string className, params string[] properties)
    {
        var wql = $"SELECT {string.Join(", ", properties)} FROM {className}";
        var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(30), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        using var results = searcher.Get();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in results)
        {
            using (item)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in properties)
                {
                    row[property] = item[property];
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    public static string? Text(this IReadOnlyDictionary<string, object?> row, string name)
    {
        var value = row.TryGetValue(name, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static long? Long(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is not null && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    public static int? Int(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.Long(name) is { } l and >= int.MinValue and <= int.MaxValue ? (int)l : null;

    public static bool? Bool(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is bool b ? b : null;

    public static DateTimeOffset? Date(this IReadOnlyDictionary<string, object?> row, string name)
    {
        var text = row.Text(name);
        if (text is null)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text)).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
