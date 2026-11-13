namespace Solvix.Modules.Mdm.Providers.Apple;

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Minimal XML property-list codec for the Apple MDM protocol. Supported types:
/// dictionary (<c>Dictionary&lt;string, object?&gt;</c>), array (<c>List&lt;object?&gt;</c>), string,
/// integer (<c>long</c>), real (<c>double</c>), bool, date (<c>DateTimeOffset</c>) and data (<c>byte[]</c>).
/// DTDs are ignored and never resolved, so a hostile device cannot trigger external entity expansion.
/// </summary>
public static class ApplePlist
{
    private const string Doctype = "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">";

    public static Dictionary<string, object?> ParseDictionary(ReadOnlySpan<byte> xml)
    {
        if (Parse(xml) is Dictionary<string, object?> dict)
        {
            return dict;
        }

        throw new FormatException("The property list root is not a dictionary.");
    }

    public static object? Parse(ReadOnlySpan<byte> xml)
    {
        if (xml.IsEmpty)
        {
            throw new FormatException("Empty property list.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = 8 * 1024 * 1024,
        };
        try
        {
            using var stream = new MemoryStream(xml.ToArray());
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "plist" || root.Elements().FirstOrDefault() is not { } value)
            {
                throw new FormatException("Not a property list.");
            }

            return ReadValue(value, 0);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Malformed property list.", ex);
        }
    }

    public static byte[] Serialize(object? root)
    {
        var plist = new XElement("plist", new XAttribute("version", "1.0"), WriteValue(root));
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n").Append(Doctype).Append('\n');
        builder.Append(plist.ToString(SaveOptions.DisableFormatting));
        builder.Append('\n');
        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    private static object? ReadValue(XElement element, int depth)
    {
        if (depth > 32)
        {
            throw new FormatException("Property list is nested too deeply.");
        }

        switch (element.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                var children = element.Elements().ToList();
                if (children.Count % 2 != 0)
                {
                    throw new FormatException("Dictionary has an unpaired key.");
                }

                for (var i = 0; i < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName != "key")
                    {
                        throw new FormatException("Dictionary expects a key element.");
                    }

                    dict[children[i].Value] = ReadValue(children[i + 1], depth + 1);
                }

                return dict;
            case "array":
                return element.Elements().Select(e => ReadValue(e, depth + 1)).ToList();
            case "string":
                return element.Value;
            case "integer":
                return long.Parse(element.Value, CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(element.Value, CultureInfo.InvariantCulture);
            case "true":
                return true;
            case "false":
                return false;
            case "date":
                return DateTimeOffset.Parse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            case "data":
                return Convert.FromBase64String(string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c))));
            default:
                throw new FormatException($"Unsupported property list element '{element.Name.LocalName}'.");
        }
    }

    private static XElement WriteValue(object? value) => value switch
    {
        null => throw new ArgumentException("Property lists cannot contain null values."),
        string s => new XElement("string", s),
        bool b => new XElement(b ? "true" : "false"),
        int i => new XElement("integer", i.ToString(CultureInfo.InvariantCulture)),
        long l => new XElement("integer", l.ToString(CultureInfo.InvariantCulture)),
        double d => new XElement("real", d.ToString("R", CultureInfo.InvariantCulture)),
        DateTimeOffset t => new XElement("date", t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
        byte[] bytes => new XElement("data", Convert.ToBase64String(bytes)),
        IReadOnlyDictionary<string, object?> dict => new XElement(
            "dict",
            dict.Where(kv => kv.Value is not null).SelectMany(kv => new[] { new XElement("key", kv.Key), WriteValue(kv.Value) })),
        System.Collections.IEnumerable list => new XElement("array", list.Cast<object?>().Select(WriteValue)),
        _ => throw new ArgumentException($"Unsupported property list type {value.GetType().Name}."),
    };
}
