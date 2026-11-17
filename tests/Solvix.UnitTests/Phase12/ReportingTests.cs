namespace Solvix.UnitTests.Phase12;

using Solvix.Modules.Reporting.Application;

public sealed class CsvExporterTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("=HYPERLINK(\"http://x\",\"y\")", "\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tfoo", "'\tfoo")]
    public void Formula_like_text_is_neutralised(string input, string expected) =>
        Assert.Equal(expected, CsvExporter.Cell(input));

    [Fact]
    public void Numbers_are_not_prefixed_even_when_negative()
    {
        Assert.Equal("-5", CsvExporter.Cell(-5));
        Assert.Equal("-1.5", CsvExporter.Cell(-1.5d));
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("plain", "plain")]
    public void Cells_are_quoted_per_rfc_4180(string input, string expected) =>
        Assert.Equal(expected, CsvExporter.Cell(input));

    [Fact]
    public void Values_are_formatted_culture_independently()
    {
        Assert.Equal("2026-11-16T10:30:00Z", CsvExporter.Cell(new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero)));
        Assert.Equal("2026-11-16", CsvExporter.Cell(new DateOnly(2026, 11, 16)));
        Assert.Equal("true", CsvExporter.Cell(true));
        Assert.Equal("12.5", CsvExporter.Cell(12.5d));
        Assert.Equal(string.Empty, CsvExporter.Cell(null));
    }

    [Fact]
    public void A_report_becomes_header_plus_rows()
    {
        var report = new ReportResult("r", "R", DateTimeOffset.UnixEpoch, [], [new ReportColumn("a", "Name", "string"), new ReportColumn("b", "Count", "number")],
            [new object?[] { "=evil", 3 }, new object?[] { "ok", null }], false, null);
        Assert.Equal("Name,Count\r\n'=evil,3\r\nok,\r\n", CsvExporter.ToCsv(report));
    }
}
