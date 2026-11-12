namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>Supplier, reseller, service or warranty provider.</summary>
public sealed class Vendor : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int TextMaxLength = 200;
    public const int NotesMaxLength = 2000;

    private Vendor()
    {
    }

    private Vendor(Guid tenantId)
        : base(tenantId)
    {
        IsActive = true;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Upper-cased name, unique per tenant.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public string? ContactName { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Website { get; private set; }

    public string? AccountNumber { get; private set; }

    public string? Notes { get; private set; }

    public bool IsActive { get; private set; }

    public static Vendor Create(Guid tenantId, string name, string? contactName, string? email, string? phone, string? website, string? accountNumber, string? notes)
    {
        var vendor = new Vendor(tenantId);
        vendor.Update(name, contactName, email, phone, website, accountNumber, notes, true);
        return vendor;
    }

    public void Update(string name, string? contactName, string? email, string? phone, string? website, string? accountNumber, string? notes, bool isActive)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        NormalizedName = Name.ToUpperInvariant();
        ContactName = Guard.Optional(contactName, "contactName", TextMaxLength);
        Email = Guard.Optional(email, "email", TextMaxLength);
        if (Email is not null && !System.Net.Mail.MailAddress.TryCreate(Email, out _))
        {
            throw new ValidationException("email", "Enter a valid e-mail address.");
        }

        Phone = Guard.Optional(phone, "phone", 50);
        Website = Guard.Optional(website, "website", TextMaxLength);
        if (Website is not null && !(Uri.TryCreate(Website, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw new ValidationException("website", "Enter an http(s) address.");
        }

        AccountNumber = Guard.Optional(accountNumber, "accountNumber", 100);
        Notes = Guard.Optional(notes, "notes", NotesMaxLength);
        IsActive = isActive;
    }
}

/// <summary>Per-tenant asset management settings (one row per tenant, created on first use).</summary>
public sealed class AssetSettings : TenantEntity
{
    public const string DefaultTagPrefix = "AST";

    private AssetSettings()
    {
    }

    private AssetSettings(Guid tenantId)
        : base(tenantId)
    {
    }

    /// <summary>Prefix of automatically generated asset tags: {prefix}-{category code}-{000001}.</summary>
    public string TagPrefix { get; private set; } = DefaultTagPrefix;

    public string DefaultCurrency { get; private set; } = "USD";

    /// <summary>
    /// When enabled, an endpoint/asset pair is linked without review only if the
    /// match is High confidence and unambiguous on both sides. Off by default:
    /// every association is reviewed by an administrator.
    /// </summary>
    public bool AutoApproveHighConfidenceMatches { get; private set; }

    /// <summary>Warranty end dates within this many days are flagged as expiring soon.</summary>
    public int WarrantyAlertDays { get; private set; } = 60;

    public static AssetSettings CreateDefault(Guid tenantId) => new(tenantId);

    public void Update(string tagPrefix, string defaultCurrency, bool autoApprove, int warrantyAlertDays)
    {
        var prefix = Guard.NotNullOrWhiteSpace(tagPrefix, "tagPrefix", 10).ToUpperInvariant();
        if (!prefix.All(char.IsAsciiLetterOrDigit))
        {
            throw new ValidationException("tagPrefix", "The prefix may only contain letters and digits.");
        }

        var currency = Guard.NotNullOrWhiteSpace(defaultCurrency, "defaultCurrency", 3).ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ValidationException("defaultCurrency", "Use a three-letter ISO 4217 currency code.");
        }

        if (warrantyAlertDays is < 1 or > 365)
        {
            throw new ValidationException("warrantyAlertDays", "Choose between 1 and 365 days.");
        }

        TagPrefix = prefix;
        DefaultCurrency = currency;
        AutoApproveHighConfidenceMatches = autoApprove;
        WarrantyAlertDays = warrantyAlertDays;
    }
}

/// <summary>Counter behind automatic asset numbering; incremented atomically with an upsert.</summary>
public sealed class AssetTagSequence : TenantEntity
{
    private AssetTagSequence()
    {
    }

    public string Scope { get; private set; } = string.Empty;

    public long LastValue { get; private set; }
}
