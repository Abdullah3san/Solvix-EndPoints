namespace Solvix.Modules.Reporting.Application;

using System.Globalization;
using System.Text;

public sealed record ReportColumn(string Key, string Label, string Type);

public sealed record ReportMetric(string Label, object? Value);

/// <summary>Uniform tabular result: the UI renders it generically and the CSV export is just another view of it.</summary>
public sealed record ReportResult(
    string Report,
    string Title,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ReportMetric> Summary,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    string? Notes);

public sealed record ReportInfo(string Key, string Title, string Description, IReadOnlyList<string> Filters);

public sealed record ReportQuery(Guid? BranchId = null, int? Days = null, string? Search = null);

public interface IReport
{
    string Key { get; }

    string Title { get; }

    string Description { get; }

    IReadOnlyList<string> Filters { get; }

    Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken);
}

public static class ReportLimits
{
    public const int MaxRows = 20_000;
}

/// <summary>RFC 4180 CSV with spreadsheet-formula injection protection: text that a spreadsheet would execute is neutralised.</summary>
public static class CsvExporter
{
    public static string ToCsv(ReportResult report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append(string.Join(',', report.Columns.Select(c => Cell(c.Label)))).Append("\r\n");
        foreach (var row in report.Rows)
        {
            sb.Append(string.Join(',', row.Select(v => Cell(v)))).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string Cell(object? value)
    {
        string text = value switch
        {
            null => string.Empty,
            DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (value is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
