namespace Solvix.Modules.Assistant.Knowledge;

using System.Text.RegularExpressions;

public sealed record ChunkDraft(int Ordinal, string Content, string? Section, int? Page);

/// <summary>Paragraph- and heading-aware splitter with character overlap; chunks never span pages.</summary>
public static partial class Chunker
{
    public static IReadOnlyList<ChunkDraft> Split(IReadOnlyList<TextSegment> segments, int size, int overlap)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (size < 200)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        overlap = Math.Clamp(overlap, 0, size / 2);
        var chunks = new List<ChunkDraft>();

        foreach (var segment in segments)
        {
            string? section = null;
            string? chunkSection = null;
            var buffer = new System.Text.StringBuilder();

            void Flush()
            {
                var content = buffer.ToString().Trim();
                if (content.Length > 0)
                {
                    chunks.Add(new ChunkDraft(chunks.Count, content, chunkSection, segment.Page));
                }

                buffer.Clear();
                if (overlap > 0 && content.Length > 0)
                {
                    buffer.Append(Tail(content, overlap));
                }

                chunkSection = section;
            }

            foreach (var raw in BlankLine().Split(segment.Text))
            {
                var paragraph = raw.Trim();
                if (paragraph.Length == 0)
                {
                    continue;
                }

                var heading = Heading().Match(paragraph);
                if (heading.Success)
                {
                    // A new section starts a new chunk (no overlap across sections).
                    var pending = buffer.ToString().Trim();
                    if (pending.Length > 0 && (chunks.Count == 0 || !chunks[^1].Content.EndsWith(pending, StringComparison.Ordinal)))
                    {
                        chunks.Add(new ChunkDraft(chunks.Count, pending, chunkSection, segment.Page));
                    }

                    buffer.Clear();
                    section = heading.Groups[1].Value.Trim();
                    chunkSection = section;
                }

                chunkSection ??= section;

                foreach (var piece in Windows(paragraph, size))
                {
                    if (buffer.Length > 0 && buffer.Length + piece.Length + 2 > size)
                    {
                        Flush();
                    }

                    if (buffer.Length > 0)
                    {
                        buffer.Append("\n\n");
                    }

                    buffer.Append(piece);
                }
            }

            var finalContent = buffer.ToString().Trim();
            if (finalContent.Length > 0 && (chunks.Count == 0 || !chunks[^1].Content.EndsWith(finalContent, StringComparison.Ordinal)))
            {
                chunks.Add(new ChunkDraft(chunks.Count, finalContent, chunkSection, segment.Page));
            }
        }

        return chunks;
    }

    private static IEnumerable<string> Windows(string paragraph, int size)
    {
        if (paragraph.Length <= size)
        {
            yield return paragraph;
            yield break;
        }

        var start = 0;
        while (start < paragraph.Length)
        {
            var end = Math.Min(start + size, paragraph.Length);
            if (end < paragraph.Length)
            {
                var space = paragraph.LastIndexOf(' ', end - 1, Math.Min(end - start, 200));
                if (space > start)
                {
                    end = space;
                }
            }

            yield return paragraph[start..end].Trim();
            start = end;
            while (start < paragraph.Length && char.IsWhiteSpace(paragraph[start]))
            {
                start++;
            }
        }
    }

    private static string Tail(string text, int overlap)
    {
        if (text.Length <= overlap)
        {
            return text;
        }

        var start = text.Length - overlap;
        var space = text.IndexOf(' ', start);
        return space < 0 ? string.Empty : text[(space + 1)..];
    }

    [GeneratedRegex(@"\n\s*\n", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"^#{1,6}\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Heading();
}
