namespace Solvix.SharedKernel.Endpoints;

using System.Text;

/// <summary>
/// Normalisation of the hardware identity fields used to relate physical assets
/// to managed endpoints. Serial numbers compare case-insensitively after
/// trimming (the same expression PostgreSQL uses for the indexed device key:
/// <c>upper(btrim(serial_number))</c>); placeholder values reported by OEM
/// firmware or cloned VM images are rejected so they can never become evidence.
/// </summary>
public static class HardwareIdentity
{
    private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "0", "00000000", "0123456789", "123456789", "1234567890",
        "none", "null", "n/a", "na", "unknown", "not available", "not applicable", "not specified",
        "default string", "to be filled by o.e.m.", "to be filled by oem", "system serial number",
        "chassis serial number", "o.e.m.", "oem", "invalid", "serial", "default", "none provided",
    };

    private static readonly string[] CorporateSuffixes =
    [
        "incorporated", "corporation", "company", "limited", "technologies", "technology", "international",
        "inc", "corp", "co", "ltd", "llc", "gmbh", "ag", "sa", "bv", "plc", "kk",
    ];

    private static readonly Dictionary<string, string> ManufacturerAliases = new(StringComparer.Ordinal)
    {
        ["hewlettpackard"] = "hp",
        ["hewlettpackardenterprise"] = "hpe",
        ["hpinc"] = "hp",
        ["dellinc"] = "dell",
        ["dellemc"] = "dell",
        ["lenovogroup"] = "lenovo",
        ["microsoftcorporation"] = "microsoft",
        ["asustekcomputer"] = "asus",
        ["asustek"] = "asus",
        ["zebratechnologies"] = "zebra",
        ["ciscosystems"] = "cisco",
        ["vmware"] = "vmware",
        ["samsungelectronics"] = "samsung",
        ["applecomputer"] = "apple",
        ["fujitsuclient"] = "fujitsu",
        ["acerinc"] = "acer",
    };

    /// <summary>Upper-cased, trimmed serial number, or null for empty and placeholder values.</summary>
    public static string? NormalizeSerial(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || PlaceholderSerials.Contains(trimmed)
            || trimmed.All(c => c is '0' or ' ' or '-' or '.' or 'F' or 'f' or 'X' or 'x')
            || trimmed.Length < 3)
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>"Dell Inc." → "dell", "Hewlett-Packard" → "hp", "LENOVO" → "lenovo".</summary>
    public static string? NormalizeManufacturer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var words = Words(value).ToList();
        while (words.Count > 1 && CorporateSuffixes.Contains(words[^1], StringComparer.Ordinal))
        {
            words.RemoveAt(words.Count - 1);
        }

        var key = string.Concat(words);
        if (key.Length == 0 || PlaceholderSerials.Contains(key) || key is "systemmanufacturer" or "tobefilledbyoem")
        {
            return null;
        }

        return ManufacturerAliases.TryGetValue(key, out var alias) ? alias : key;
    }

    /// <summary>Lower-case letters and digits only: "Latitude 7440" → "latitude7440".</summary>
    public static string? NormalizeModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = string.Concat(Words(value));
        return key.Length == 0 || key is "systemproductname" or "tobefilledbyoem" or "default" ? null : key;
    }

    private static IEnumerable<string> Words(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }
}
