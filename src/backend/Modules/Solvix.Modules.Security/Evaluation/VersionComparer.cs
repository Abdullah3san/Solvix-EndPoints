namespace Solvix.Modules.Security.Evaluation;

/// <summary>
/// Compares dotted numeric versions ("10.2.5", "1.0.0.1234"). Anything after the first
/// non-numeric segment (build labels, "-beta") is ignored. Unparseable input never matches an
/// advisory: Solvix prefers to miss a match over reporting a false one it cannot justify.
/// </summary>
public static class VersionComparer
{
    private const int MaxParts = 6;
    private const int MaxDigits = 9;

    public static bool TryParse(string? text, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        var result = new List<int>(MaxParts);
        foreach (var segmentRange in span.ToString().Split('.'))
        {
            var digits = 0;
            while (digits < segmentRange.Length && char.IsAsciiDigit(segmentRange[digits]))
            {
                digits++;
            }

            if (digits == 0 || digits > MaxDigits)
            {
                break;
            }

            result.Add(int.Parse(segmentRange.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture));
            if (digits != segmentRange.Length || result.Count == MaxParts)
            {
                break;
            }
        }

        if (result.Count == 0)
        {
            return false;
        }

        parts = [.. result];
        return true;
    }

    /// <summary>Negative when <paramref name="a"/> is older than <paramref name="b"/>. Missing parts count as zero.</summary>
    public static int Compare(int[] a, int[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }
}
