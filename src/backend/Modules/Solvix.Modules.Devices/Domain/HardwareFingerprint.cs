namespace Solvix.Modules.Devices.Domain;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Derives the duplicate-detection key of a machine from its SMBIOS UUID and
/// serial number. Many OEM boards and cloned VM images report placeholder
/// values ("To be filled by O.E.M.", all-zero or all-F UUIDs); such values are
/// ignored so unrelated machines are never merged into one record.
/// </summary>
public static class HardwareFingerprint
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        "0", "00000000", "0123456789", "123456789", "1234567890",
        "none", "null", "n/a", "na", "unknown", "not available", "not applicable", "not specified",
        "default string", "to be filled by o.e.m.", "to be filled by oem", "system serial number",
        "chassis serial number", "o.e.m.", "oem", "invalid", "serial", "default",
    };

    public static string? Compute(string? hardwareId, string? serialNumber)
    {
        var uuid = NormalizeUuid(hardwareId);
        var serial = NormalizeSerial(serialNumber);
        if (uuid is null && serial is null)
        {
            return null;
        }

        // A UUID alone is a reasonable identity; a serial alone is too weak
        // (shared by all VMs of a hypervisor template), so require the UUID.
        if (uuid is null)
        {
            return null;
        }

        var material = $"v1|{uuid}|{serial ?? string.Empty}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    internal static string? NormalizeUuid(string? value)
    {
        if (!Guid.TryParse(value?.Trim(), out var guid))
        {
            return null;
        }

        var text = guid.ToString("N");
        if (text.All(c => c == '0') || text.All(c => c == 'f') || PlaceholderGuids.Contains(text))
        {
            return null;
        }

        return text;
    }

    internal static string? NormalizeSerial(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || PlaceholderValues.Contains(trimmed) || trimmed.All(c => c is '0' or ' ' or '-' or '.'))
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }

    private static readonly HashSet<string> PlaceholderGuids = new(StringComparer.Ordinal)
    {
        "03000200040005000006000700080009", // common AMI placeholder
        "12345678123412341234123456789012",
    };
}
