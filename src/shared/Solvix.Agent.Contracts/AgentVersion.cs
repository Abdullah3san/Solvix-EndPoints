namespace Solvix.Agent.Contracts;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

/// <summary>
/// Minimal semantic version (<c>major.minor.patch[.revision][-prerelease][+build]</c>)
/// used for agent version tracking and update decisions. A pre-release sorts
/// before the corresponding release; build metadata is ignored.
/// </summary>
public sealed class AgentVersion : IComparable<AgentVersion>, IEquatable<AgentVersion>
{
    public const int MaxLength = 64;

    private AgentVersion(int major, int minor, int patch, int revision, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public int Revision { get; }

    public string? PreRelease { get; }

    public static bool operator ==(AgentVersion? left, AgentVersion? right) => Equals(left, right);

    public static bool operator !=(AgentVersion? left, AgentVersion? right) => !Equals(left, right);

    public static bool operator <(AgentVersion? left, AgentVersion? right) => Compare(left, right) < 0;

    public static bool operator >(AgentVersion? left, AgentVersion? right) => Compare(left, right) > 0;

    public static bool operator <=(AgentVersion? left, AgentVersion? right) => Compare(left, right) <= 0;

    public static bool operator >=(AgentVersion? left, AgentVersion? right) => Compare(left, right) >= 0;

    public static AgentVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException($"'{value}' is not a valid agent version.");

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out AgentVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string? preRelease = null;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];
            if (preRelease.Length == 0 || !preRelease.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0
                || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new AgentVersion(numbers[0], numbers[1], numbers[2], numbers[3], preRelease);
        return true;
    }

    public static int Compare(AgentVersion? left, AgentVersion? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        return right is null ? 1 : left.CompareTo(right);
    }

    public int CompareTo(AgentVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        if (result == 0)
        {
            result = Revision.CompareTo(other.Revision);
        }

        if (result != 0)
        {
            return result;
        }

        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(PreRelease, other.PreRelease),
        };
    }

    public bool Equals(AgentVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AgentVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Revision, PreRelease);

    public override string ToString()
    {
        var core = Revision == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Revision}");
        return PreRelease is null ? core : $"{core}-{PreRelease}";
    }
}
