namespace Solvix.Modules.Security.Application.Reports;

using System.Globalization;
using System.Text;

/// <summary>
/// Minimal RFC 4180 writer with formula-injection protection. Device-supplied text (hostnames, software names,
/// account names) ends up in spreadsheets, where a cell starting with = + - @ (or a tab/CR) can run as a formula.
/// Such cells are prefixed with an apostrophe so they are always treated as text.
/// </summary>
public static class Csv
{
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value;
        if (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    public static string Cell(DateTimeOffset? value) => value?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Cell(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public static void Row(StringBuilder builder, params string[] cells)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cells);
        builder.Append(string.Join(',', cells)).Append("\r\n");
    }

    public static byte[] ToBytes(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }
}
