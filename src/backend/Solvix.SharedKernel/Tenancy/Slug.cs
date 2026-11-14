namespace Solvix.SharedKernel.Tenancy;

using System.Text.RegularExpressions;
using Solvix.SharedKernel.Exceptions;

/// <summary>URL-safe tenant identifier: lowercase letters, digits and single hyphens, 3-63 chars.</summary>
public static partial class Slug
{
    public const int MinLength = 3;
    public const int MaxLength = 63;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "api", "www", "admin", "app", "auth", "login", "register", "health", "static", "assets", "solvix", "platform", "system",
    };

    public static string Normalize(string value, string propertyName = "slug")
    {
        ArgumentNullException.ThrowIfNull(value);
        var slug = value.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug) || slug.Contains("--", StringComparison.Ordinal))
        {
            throw new ValidationException(
                propertyName,
                $"Must be {MinLength}-{MaxLength} characters of lowercase letters, digits and single hyphens, starting and ending with a letter or digit.");
        }

        if (Reserved.Contains(slug))
        {
            throw new ValidationException(propertyName, $"'{slug}' is reserved.");
        }

        return slug;
    }

    public static bool IsValid(string? value) =>
        value is not null
        && SlugPattern().IsMatch(value)
        && !value.Contains("--", StringComparison.Ordinal)
        && !Reserved.Contains(value);

    /// <summary>Derives a candidate slug from a free-form name (e.g. company name).</summary>
    public static string FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lowered = name.Trim().ToLowerInvariant();
        var replaced = NonSlugChars().Replace(lowered, "-");
        var collapsed = MultiHyphen().Replace(replaced, "-").Trim('-');
        if (collapsed.Length > MaxLength)
        {
            collapsed = collapsed[..MaxLength].TrimEnd('-');
        }

        if (collapsed.Length < MinLength)
        {
            collapsed = (collapsed + "-org").PadRight(MinLength, 'x');
        }

        return Reserved.Contains(collapsed) ? collapsed + "-org" : collapsed;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiHyphen();
}
