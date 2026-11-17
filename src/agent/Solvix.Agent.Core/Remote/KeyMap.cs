namespace Solvix.Agent.Core.Remote;

/// <summary>Maps browser key names (<c>KeyboardEvent.key</c>) to Windows virtual-key codes.</summary>
public static class KeyMap
{
    private static readonly Dictionary<string, (ushort Vk, bool Extended)> Named = new(StringComparer.Ordinal)
    {
        ["Backspace"] = (0x08, false),
        ["Tab"] = (0x09, false),
        ["Enter"] = (0x0D, false),
        ["Shift"] = (0x10, false),
        ["Control"] = (0x11, false),
        ["Alt"] = (0x12, false),
        ["Escape"] = (0x1B, false),
        [" "] = (0x20, false),
        ["PageUp"] = (0x21, true),
        ["PageDown"] = (0x22, true),
        ["End"] = (0x23, true),
        ["Home"] = (0x24, true),
        ["ArrowLeft"] = (0x25, true),
        ["ArrowUp"] = (0x26, true),
        ["ArrowRight"] = (0x27, true),
        ["ArrowDown"] = (0x28, true),
        ["Insert"] = (0x2D, true),
        ["Delete"] = (0x2E, true),
        ["Meta"] = (0x5B, true),
    };

    /// <summary>Keys that are not printable characters. Printable text is sent as text, not as key presses.</summary>
    public static bool TryGetVirtualKey(string? name, out ushort virtualKey, out bool extended)
    {
        virtualKey = 0;
        extended = false;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (Named.TryGetValue(name, out var known))
        {
            (virtualKey, extended) = known;
            return true;
        }

        // F1..F12
        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 12)
        {
            virtualKey = (ushort)(0x6F + f);
            return true;
        }

        // Single letters/digits when used as shortcuts (Ctrl+C): the virtual key is the upper-case character.
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            virtualKey = char.ToUpperInvariant(name[0]);
            return true;
        }

        return false;
    }
}
