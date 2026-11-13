namespace Solvix.Modules.Software.Domain;

using Solvix.Agent.Contracts.Software;
using Solvix.SharedKernel.Entities;

public enum PackageStatus
{
    /// <summary>Validated and stored; can be deployed once its installation is configured.</summary>
    Ready = 1,

    /// <summary>The stored file no longer matches its checksum. Never served to devices again.</summary>
    Corrupted = 2,

    /// <summary>Withdrawn by an administrator; the file is deleted, history is kept.</summary>
    Retired = 3,
}

public enum SignatureState
{
    Unsigned = 1,

    /// <summary>A structurally valid Authenticode signature whose CMS signature verifies.</summary>
    Signed = 2,
}

/// <summary>
/// A software package in the tenant's repository: the uploaded installer (stored under an opaque
/// key, never under its uploaded name), what validation found out about it, and how it is
/// installed, removed and detected. Uploading never runs anything: the server only reads bytes.
/// </summary>
public sealed class SoftwarePackage : TenantEntity
{
    private SoftwarePackage()
    {
    }

    internal SoftwarePackage(
        Guid tenantId,
        string name,
        InstallerType installerType,
        string originalFileName,
        long sizeBytes,
        string sha256,
        PackageInspection inspection,
        Guid uploadedBy,
        DateTimeOffset uploadedAtUtc)
        : base(tenantId)
    {
        Name = name;
        InstallerType = installerType;
        OriginalFileName = originalFileName;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = Id.ToString("N");
        SignatureState = inspection.Signed ? SignatureState.Signed : SignatureState.Unsigned;
        SignerSubject = inspection.SignerSubject;
        SignerThumbprint = inspection.SignerThumbprint;
        SignatureContentVerified = inspection.ContentDigestVerified;
        Architecture = inspection.Architecture;
        UploadedBy = uploadedBy;
        UploadedAtUtc = uploadedAtUtc;
        LastVerifiedAtUtc = uploadedAtUtc;
        Status = PackageStatus.Ready;
        SilentPreset = installerType == InstallerType.Msi ? SilentPreset.Msi : SilentPreset.Custom;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Publisher { get; private set; }

    public string? Version { get; private set; }

    public string? Description { get; private set; }

    public InstallerType InstallerType { get; private set; }

    /// <summary>The uploaded file name, sanitised, for display only.</summary>
    public string OriginalFileName { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 computed while the upload was received.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Opaque storage name (the package id); the uploaded name is never used as a path.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public PackageStatus Status { get; private set; }

    public SignatureState SignatureState { get; private set; }

    public string? SignerSubject { get; private set; }

    /// <summary>Upper-case hex SHA-256 of the signing certificate.</summary>
    public string? SignerThumbprint { get; private set; }

    /// <summary>Whether the server also verified the signed digest against the file content (PE files).</summary>
    public bool SignatureContentVerified { get; private set; }

    /// <summary>x86, x64, arm64 for executables; null for MSI.</summary>
    public string? Architecture { get; private set; }

    /// <summary>Administrators mark packages that always need the additional approval.</summary>
    public bool IsSensitive { get; private set; }

    // Installation parameters.
    public SilentPreset SilentPreset { get; private set; }

    public List<string> InstallArguments { get; private set; } = [];

    public List<string> UninstallArguments { get; private set; } = [];

    public List<int> SuccessExitCodes { get; private set; } = [0];

    public List<int> RebootExitCodes { get; private set; } = [3010, 1641];

    public int TimeoutMinutes { get; private set; } = 60;

    // Detection (application version detection).
    public DetectionKind? DetectionKind { get; private set; }

    public string? DetectionProductCode { get; private set; }

    public string? DetectionDisplayName { get; private set; }

    public NameMatch DetectionNameMatch { get; private set; } = NameMatch.Exact;

    public string? DetectionPublisher { get; private set; }

    public string? DetectionVersion { get; private set; }

    public VersionOperator DetectionVersionOperator { get; private set; } = VersionOperator.Any;

    /// <summary>Incremented whenever the installation parameters change; deployments record the revision they froze.</summary>
    public int ConfigurationRevision { get; private set; }

    public Guid UploadedBy { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    public DateTimeOffset LastVerifiedAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public bool IsConfigured => DetectionKind is not null;

    public bool CanUninstall => InstallerType == InstallerType.Msi
        ? DetectionKind == Agent.Contracts.Software.DetectionKind.MsiProductCode
        : UninstallArguments.Count > 0;

    public DetectionRule? Detection => DetectionKind is not { } kind
        ? null
        : new DetectionRule
        {
            Kind = kind,
            ProductCode = DetectionProductCode,
            DisplayName = DetectionDisplayName,
            NameMatch = DetectionNameMatch,
            Publisher = DetectionPublisher,
            Version = DetectionVersion,
            VersionOperator = DetectionVersionOperator,
        };

    public void Describe(string name, string? publisher, string? version, string? description, bool isSensitive)
    {
        Name = name;
        Publisher = publisher;
        Version = version;
        Description = description;
        IsSensitive = isSensitive;
    }

    public void Configure(
        SilentPreset preset,
        IReadOnlyList<string> installArguments,
        IReadOnlyList<string> uninstallArguments,
        IReadOnlyList<int> successExitCodes,
        IReadOnlyList<int> rebootExitCodes,
        int timeoutMinutes,
        DetectionRule detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        SilentPreset = preset;
        InstallArguments = [.. installArguments];
        UninstallArguments = [.. uninstallArguments];
        SuccessExitCodes = [.. successExitCodes.Distinct()];
        RebootExitCodes = [.. rebootExitCodes.Distinct()];
        TimeoutMinutes = timeoutMinutes;
        DetectionKind = detection.Kind;
        DetectionProductCode = DetectionRule.NormalizeProductCode(detection.ProductCode);
        DetectionDisplayName = detection.DisplayName?.Trim();
        DetectionNameMatch = detection.NameMatch;
        DetectionPublisher = string.IsNullOrWhiteSpace(detection.Publisher) ? null : detection.Publisher.Trim();
        DetectionVersion = detection.VersionOperator == VersionOperator.Any ? null : detection.Version?.Trim();
        DetectionVersionOperator = detection.VersionOperator;
        ConfigurationRevision++;
    }

    public void MarkVerified(DateTimeOffset now) => LastVerifiedAtUtc = now;

    public void MarkCorrupted(DateTimeOffset now)
    {
        Status = PackageStatus.Corrupted;
        LastVerifiedAtUtc = now;
    }

    public void Retire(DateTimeOffset now)
    {
        Status = PackageStatus.Retired;
        RetiredAtUtc = now;
    }
}

/// <summary>What validation learned about an uploaded file.</summary>
public sealed record PackageInspection(
    InstallerType InstallerType,
    bool Signed,
    string? SignerSubject,
    string? SignerThumbprint,
    bool ContentDigestVerified,
    string? Architecture);
