namespace Solvix.Modules.Security.Domain;

using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A vulnerable-software rule maintained by the tenant: "products whose name contains X (and optionally publisher Y)
/// at a version from A up to but excluding F are vulnerable". Solvix does not bundle or synchronise a vulnerability
/// database; the quality of this list is the quality of the check.
/// </summary>
public sealed class SoftwareAdvisory : TenantEntity
{
    public const int TitleMaxLength = 200;
    public const int PatternMinLength = 3;
    public const int PatternMaxLength = 200;
    public const int VersionMaxLength = 64;
    public const int ReferenceMaxLength = 500;

    private SoftwareAdvisory()
    {
    }

    public SoftwareAdvisory(
        Guid tenantId, string title, string productPattern, string? publisher, string? affectedFromVersion, string fixedVersion,
        Severity severity, string? reference, Guid createdByUserId)
        : base(tenantId)
    {
        CreatedByUserId = createdByUserId;
        IsEnabled = true;
        Apply(title, productPattern, publisher, affectedFromVersion, fixedVersion, severity, reference);
    }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Case-insensitive substring of the product's display name. Plain text, never a regular expression.</summary>
    public string ProductPattern { get; private set; } = string.Empty;

    public string? Publisher { get; private set; }

    public string? AffectedFromVersion { get; private set; }

    /// <summary>The first version that is no longer vulnerable.</summary>
    public string FixedVersion { get; private set; } = string.Empty;

    public Severity Severity { get; private set; }

    public string? Reference { get; private set; }

    public bool IsEnabled { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public void Update(string title, string productPattern, string? publisher, string? affectedFromVersion, string fixedVersion, Severity severity, string? reference) =>
        Apply(title, productPattern, publisher, affectedFromVersion, fixedVersion, severity, reference);

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public AdvisoryRule ToRule() => new(Id, Title, ProductPattern, Publisher, AffectedFromVersion, FixedVersion, Severity, Reference);

    private void Apply(string title, string productPattern, string? publisher, string? affectedFromVersion, string fixedVersion, Severity severity, string? reference)
    {
        var pattern = Guard.NotNullOrWhiteSpace(productPattern, "productPattern", PatternMaxLength);
        if (pattern.Length < PatternMinLength)
        {
            throw new ValidationException("productPattern", $"Use at least {PatternMinLength} characters so the rule does not match unrelated products.");
        }

        var fixedText = Guard.NotNullOrWhiteSpace(fixedVersion, "fixedVersion", VersionMaxLength);
        if (!VersionComparer.TryParse(fixedText, out _))
        {
            throw new ValidationException("fixedVersion", "Use a dotted numeric version such as 12.0.4.");
        }

        var from = Guard.Optional(affectedFromVersion, "affectedFromVersion", VersionMaxLength);
        if (from is not null)
        {
            if (!VersionComparer.TryParse(from, out var fromParts))
            {
                throw new ValidationException("affectedFromVersion", "Use a dotted numeric version such as 10.0.");
            }

            VersionComparer.TryParse(fixedText, out var fixedParts);
            if (VersionComparer.Compare(fromParts, fixedParts) >= 0)
            {
                throw new ValidationException("affectedFromVersion", "The affected range must start below the fixed version.");
            }
        }

        if (!Enum.IsDefined(severity) || severity == Severity.Info)
        {
            throw new ValidationException("severity", "Severity must be Low, Medium, High or Critical.");
        }

        Title = Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength);
        ProductPattern = pattern;
        Publisher = Guard.Optional(publisher, "publisher", 200);
        AffectedFromVersion = from;
        FixedVersion = fixedText;
        Severity = severity;
        Reference = Guard.Optional(reference, "reference", ReferenceMaxLength);
    }
}
