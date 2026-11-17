namespace Solvix.Agent.Core.Remote;

using System.Text.RegularExpressions;

/// <summary>
/// Decides which paths a remote file session may touch: only inside the folders this computer allows,
/// never through links, never alternate data streams or device names. The server's request is only a
/// request; this is the authority.
/// </summary>
public sealed partial class FileSandbox
{
    private readonly StringComparison _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly List<string> _roots;

    public FileSandbox(IEnumerable<string> roots)
    {
        _roots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathRooted(r))
            .Select(r => TrimEnd(Path.GetFullPath(r)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Resolves a path from the technician to a full path inside an allowed folder.</summary>
    public bool TryResolve(string? path, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = "That path is not available for transfer.";
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) || !Path.IsPathRooted(path))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = TrimEnd(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = _roots.FirstOrDefault(r => candidate.Equals(r, _comparison) || candidate.StartsWith(r + Path.DirectorySeparatorChar, _comparison));
        if (root is null)
        {
            return false;
        }

        // Each name below the root: no streams, no device names, no links.
        var current = root;
        foreach (var part in candidate[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains(':', StringComparison.Ordinal) || part.EndsWith('.') || part.EndsWith(' ') || ReservedName().IsMatch(part))
            {
                return false;
            }

            current = Path.Combine(current, part);
            if (IsLink(current))
            {
                return false;
            }
        }

        if (IsLink(root))
        {
            return false;
        }

        fullPath = candidate;
        error = string.Empty;
        return true;
    }

    /// <summary>True for symbolic links, junctions and other reparse points that could lead out of the folder.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // does not exist (yet)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell: treat as unsafe
        }
    }

    private static string TrimEnd(string path)
    {
        var root = Path.GetPathRoot(path);
        return path.Length > (root?.Length ?? 0) ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
