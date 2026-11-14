namespace Solvix.Modules.Assistant.Knowledge;

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Solvix.SharedKernel.Exceptions;

public sealed record TextSegment(string Text, int? Page);

/// <summary>
/// Extracts plain text from uploads. Format is decided from the file extension AND verified against
/// the content (magic bytes / valid UTF-8, no NULs), never from the client-supplied content type.
/// </summary>
public static partial class TextExtractor
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".txt", ".md", ".markdown", ".log", ".csv", ".json", ".html", ".htm", ".docx"];

    private const long MaxDocxXmlBytes = 25 * 1024 * 1024;

    public static string ContentTypeFor(string extension) => extension switch
    {
        ".md" or ".markdown" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "text/plain",
    };

    public static IReadOnlyList<TextSegment> Extract(string fileName, ReadOnlySpan<byte> bytes)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new ValidationException("file", $"Unsupported file type '{extension}'. Supported: {string.Join(", ", SupportedExtensions)}.");
        }

        if (bytes.Length == 0)
        {
            throw new ValidationException("file", "The file is empty.");
        }

        var text = extension == ".docx" ? ExtractDocx(bytes) : ExtractText(extension, bytes);
        text = Normalize(text);
        if (text.Length == 0)
        {
            throw new ValidationException("file", "No readable text was found in the file.");
        }

        return [new TextSegment(text, null)];
    }

    private static string ExtractText(string extension, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            throw new ValidationException("file", "The file content is a binary archive, not text.");
        }

        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            throw new ValidationException("file", "PDF files are not supported yet. Export the document as text, markdown, HTML or DOCX.");
        }

        string text;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            text = (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes[2..]);
        }
        else
        {
            if (bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0))
            {
                throw new ValidationException("file", "The file appears to be binary, not text.");
            }

            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new ValidationException("file", "The file is not valid UTF-8 text.");
            }
        }

        return extension is ".html" or ".htm" ? HtmlToText(text) : text;
    }

    private static string HtmlToText(string html)
    {
        html = ScriptStylePattern().Replace(html, " ");
        html = HeadingOpenPattern().Replace(html, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " ");
        html = BlockBreakPattern().Replace(html, "\n");
        html = TagPattern().Replace(html, string.Empty);
        return WebUtility.HtmlDecode(html);
    }

    private static string ExtractDocx(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
        {
            throw new ValidationException("file", "The file is not a valid DOCX document.");
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml") ?? throw new ValidationException("file", "The file is not a valid DOCX document.");
            if (entry.Length > MaxDocxXmlBytes)
            {
                throw new ValidationException("file", "The document is too large to process.");
            }

            using var xml = XmlReader.Create(
                entry.Open(),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxDocxXmlBytes });
            var sb = new StringBuilder();
            var headingLevel = 0;
            var paragraph = new StringBuilder();
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI.EndsWith("wordprocessingml/2006/main", StringComparison.Ordinal))
                {
                    switch (xml.LocalName)
                    {
                        case "p":
                            headingLevel = 0;
                            paragraph.Clear();
                            break;
                        case "pStyle":
                            var style = xml.GetAttribute("val", xml.NamespaceURI) ?? string.Empty;
                            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out var level))
                            {
                                headingLevel = Math.Clamp(level, 1, 6);
                            }

                            break;
                        case "t":
                            paragraph.Append(xml.ReadElementContentAsString());
                            break;
                        case "tab":
                            paragraph.Append('\t');
                            break;
                        case "br":
                            paragraph.Append('\n');
                            break;
                    }
                }
                else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p" && xml.NamespaceURI.EndsWith("wordprocessingml/2006/main", StringComparison.Ordinal))
                {
                    if (paragraph.Length > 0)
                    {
                        sb.Append(headingLevel > 0 ? new string('#', headingLevel) + " " : string.Empty).Append(paragraph).Append("\n\n");
                    }
                }
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            throw new ValidationException("file", "The file is not a valid DOCX document.");
        }
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\0", string.Empty, StringComparison.Ordinal).Replace("﻿", string.Empty, StringComparison.Ordinal);
        text = ExcessBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptStylePattern();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingOpenPattern();

    [GeneratedRegex(@"</?(p|div|br|li|tr|h[1-6]|ul|ol|table)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockBreakPattern();

    [GeneratedRegex(@"<[^>]{0,2000}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExcessBlankLines();
}
