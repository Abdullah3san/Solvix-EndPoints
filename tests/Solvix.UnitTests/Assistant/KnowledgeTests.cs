namespace Solvix.UnitTests.Assistant;

using System.IO.Compression;
using System.Text;
using Solvix.Modules.Assistant.Knowledge;
using Solvix.Modules.Assistant.Knowledge.Embeddings;
using Solvix.SharedKernel.Exceptions;

public sealed class ChunkerTests
{
    [Fact]
    public void Short_text_is_one_chunk()
    {
        var chunks = Chunker.Split([new TextSegment("Restart the spooler.", null)], 1000, 100);
        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Ordinal);
    }

    [Fact]
    public void Long_text_is_split_within_size_with_sequential_ordinals_and_sections()
    {
        var sb = new StringBuilder("# Printer troubleshooting\n\n");
        for (var i = 0; i < 40; i++)
        {
            sb.Append("Paragraph ").Append(i).Append(" explains how to clear the print queue and restart the spooler service safely. ").AppendLine().AppendLine();
        }

        sb.Append("# Network\n\nCheck the cable.\n\n");
        var chunks = Chunker.Split([new TextSegment(sb.ToString(), null)], 500, 80);
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 700, $"chunk too long: {c.Content.Length}"));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        Assert.Equal("Printer troubleshooting", chunks[0].Section);
        Assert.Contains(chunks, c => c.Section == "Network");
    }

    [Fact]
    public void Giant_paragraph_without_breaks_is_windowed()
    {
        var chunks = Chunker.Split([new TextSegment(string.Join(' ', Enumerable.Repeat("word", 2000)), null)], 400, 50);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 600));
    }

    [Fact]
    public void Page_numbers_are_preserved_and_chunks_do_not_span_pages()
    {
        var chunks = Chunker.Split([new TextSegment("Page one text.", 1), new TextSegment("Page two text.", 2)], 1000, 100);
        Assert.Equal([1, 2], chunks.Select(c => c.Page));
    }
}

public sealed class EmbeddingTests
{
    [Fact]
    public void Vectors_are_normalised_and_deterministic()
    {
        var a = HashingEmbeddingProvider.Embed("Restart the print spooler service");
        var b = HashingEmbeddingProvider.Embed("Restart the print spooler service");
        Assert.Equal(a, b);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => (double)x * x)), 5);
    }

    [Fact]
    public void Related_text_scores_higher_than_unrelated()
    {
        var doc1 = HashingEmbeddingProvider.Embed("To fix a stuck print queue, restart the Print Spooler service and clear pending jobs.");
        var doc2 = HashingEmbeddingProvider.Embed("Quarterly budget approval requires sign-off from the finance director.");
        var query = HashingEmbeddingProvider.Embed("how do I restart the spooler when printing is stuck");
        Assert.True(HashingEmbeddingProvider.Cosine(query, doc1) > HashingEmbeddingProvider.Cosine(query, doc2) + 0.1);
    }

    [Fact]
    public void Empty_text_gives_zero_vector_without_throwing()
    {
        Assert.All(HashingEmbeddingProvider.Embed("   "), v => Assert.Equal(0f, v));
    }
}

public sealed class TextExtractorTests
{
    [Fact]
    public void Plain_text_and_markdown_are_extracted()
    {
        var segments = TextExtractor.Extract("runbook.md", Encoding.UTF8.GetBytes("# Title\r\n\r\nBody"));
        Assert.Equal("# Title\n\nBody", Assert.Single(segments).Text);
    }

    [Fact]
    public void Html_is_stripped_of_scripts_and_tags()
    {
        var html = "<html><head><style>p{color:red}</style><script>alert('x')</script></head><body><h1>Printers</h1><p>Restart &amp; retry</p></body></html>";
        var text = Assert.Single(TextExtractor.Extract("a.html", Encoding.UTF8.GetBytes(html))).Text;
        Assert.Contains("# Printers", text, StringComparison.Ordinal);
        Assert.Contains("Restart & retry", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Docx_text_and_headings_are_extracted()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>VPN setup</w:t></w:r></w:p>
              <w:p><w:r><w:t>Install the client and sign in.</w:t></w:r></w:p>
            </w:body></w:document>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write(xml);
        }

        var text = Assert.Single(TextExtractor.Extract("guide.docx", ms.ToArray())).Text;
        Assert.Contains("# VPN setup", text, StringComparison.Ordinal);
        Assert.Contains("Install the client", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("archive.zip")]
    [InlineData("doc.pdf")]
    [InlineData("noextension")]
    public void Unsupported_extensions_are_rejected(string name) =>
        Assert.Throws<ValidationException>(() => TextExtractor.Extract(name, [65, 66, 67]));

    [Fact]
    public void Content_must_match_the_extension()
    {
        Assert.Throws<ValidationException>(() => TextExtractor.Extract("a.txt", [0x50, 0x4B, 0x03, 0x04, 1, 2]));      // zip disguised as txt
        Assert.Throws<ValidationException>(() => TextExtractor.Extract("a.txt", [0x25, 0x50, 0x44, 0x46, 0x2D]));      // PDF disguised as txt
        Assert.Throws<ValidationException>(() => TextExtractor.Extract("a.txt", [65, 0, 66, 0, 67]));                  // NULs
        Assert.Throws<ValidationException>(() => TextExtractor.Extract("a.txt", [0xC3, 0x28, 0xA0, 0xA1]));            // invalid UTF-8
        Assert.Throws<ValidationException>(() => TextExtractor.Extract("a.docx", Encoding.UTF8.GetBytes("not a zip")));
        Assert.Throws<ValidationException>(() => TextExtractor.Extract("a.txt", []));
    }
}
