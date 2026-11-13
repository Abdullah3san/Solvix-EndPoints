namespace Solvix.Modules.Software.Domain;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Solvix.Agent.Contracts.Software;
using Solvix.SharedKernel.Entities;

/// <summary>
/// Tenant-wide rules for packages and deployments. One row per tenant, defaults until saved.
/// </summary>
public sealed class SoftwareDeploymentPolicy : TenantEntity
{
    private SoftwareDeploymentPolicy()
    {
    }

    private SoftwareDeploymentPolicy(Guid tenantId)
        : base(tenantId)
    {
    }

    /// <summary>EXE installers run vendor code with vendor-specific switches; tenants can restrict the repository to MSI.</summary>
    public bool AllowExeInstallers { get; private set; } = true;

    /// <summary>Refuse unsigned packages at upload.</summary>
    public bool RequireSignedPackages { get; private set; }

    /// <summary>Signing certificates (SHA-256 thumbprints) whose packages need no additional approval.</summary>
    public List<string> TrustedPublisherThumbprints { get; private set; } = [];

    /// <summary>Every deployment needs one approval by someone other than the requester.</summary>
    public bool RequireApproval { get; private set; } = true;

    /// <summary>Deployments to more devices than this are sensitive.</summary>
    public int LargeDeploymentThreshold { get; private set; } = 50;

    public bool UninstallIsSensitive { get; private set; } = true;

    public bool ServerTargetsAreSensitive { get; private set; } = true;

    public int ApprovalTtlHours { get; private set; } = 72;

    public static SoftwareDeploymentPolicy CreateDefault(Guid tenantId) => new(tenantId);

    public void Update(
        bool allowExeInstallers,
        bool requireSignedPackages,
        IEnumerable<string> trustedPublisherThumbprints,
        bool requireApproval,
        int largeDeploymentThreshold,
        bool uninstallIsSensitive,
        bool serverTargetsAreSensitive,
        int approvalTtlHours)
    {
        AllowExeInstallers = allowExeInstallers;
        RequireSignedPackages = requireSignedPackages;
        TrustedPublisherThumbprints = trustedPublisherThumbprints.Select(NormalizeThumbprint).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        RequireApproval = requireApproval;
        LargeDeploymentThreshold = largeDeploymentThreshold;
        UninstallIsSensitive = uninstallIsSensitive;
        ServerTargetsAreSensitive = serverTargetsAreSensitive;
        ApprovalTtlHours = approvalTtlHours;
    }

    public bool IsTrustedPublisher(string? thumbprint) =>
        NormalizeThumbprint(thumbprint) is { } t && TrustedPublisherThumbprints.Contains(t, StringComparer.Ordinal);

    /// <summary>Upper-case hex without separators; null unless it is a 64-character SHA-256 thumbprint.</summary>
    public static string? NormalizeThumbprint(string? value)
    {
        var cleaned = value?.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        return cleaned is { Length: 64 } && cleaned.All(char.IsAsciiHexDigit) ? cleaned : null;
    }
}

public enum RestrictionRuleType
{
    /// <summary>Must not be installed (optionally: below a minimum version). Deployments of it are refused.</summary>
    Blocked = 1,

    /// <summary>Must be installed (optionally: at least a minimum version).</summary>
    Required = 2,
}

/// <summary>
/// A software restriction rule. Rules are evaluated against the reported software inventory
/// (compliance) and against packages (blocked software cannot be deployed).
/// </summary>
public sealed class SoftwareRestrictionRule : TenantEntity
{
    private SoftwareRestrictionRule()
    {
    }

    internal SoftwareRestrictionRule(Guid tenantId)
        : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public RestrictionRuleType RuleType { get; private set; }

    /// <summary>Display name with <c>*</c> and <c>?</c> wildcards, case-insensitive.</summary>
    public string NamePattern { get; private set; } = string.Empty;

    public string? Publisher { get; private set; }

    /// <summary>Blocked: only versions below this are violations. Required: installed version must be at least this.</summary>
    public string? MinimumVersion { get; private set; }

    /// <summary>Device groups the rule applies to; null = every device.</summary>
    public List<Guid>? DeviceGroupIds { get; private set; }

    public bool Enabled { get; private set; } = true;

    public void Update(string name, string? description, RestrictionRuleType ruleType, string namePattern, string? publisher, string? minimumVersion, IEnumerable<Guid>? deviceGroupIds, bool enabled)
    {
        Name = name;
        Description = description;
        RuleType = ruleType;
        NamePattern = namePattern.Trim();
        Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim();
        MinimumVersion = string.IsNullOrWhiteSpace(minimumVersion) ? null : minimumVersion.Trim();
        DeviceGroupIds = deviceGroupIds?.Distinct().ToList();
        Enabled = enabled;
    }

    public bool AppliesTo(IReadOnlyCollection<Guid> groupIds) => DeviceGroupIds is null || DeviceGroupIds.Any(groupIds.Contains);

    public bool MatchesIdentity(string name, string? publisher) =>
        WildcardMatch(NamePattern, name) && (Publisher is null || string.Equals(Publisher, publisher?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an installed (or to-be-installed) version of a matching application violates a Blocked rule.</summary>
    public bool BlocksVersion(string? version) =>
        MinimumVersion is null || version is null || SoftwareVersion.Compare(version, MinimumVersion) < 0;

    public static bool WildcardMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}

/// <summary>One installed application on one device, from the latest inventory.</summary>
public sealed class SoftwareInventoryItem : TenantEntity
{
    private SoftwareInventoryItem()
    {
    }

    internal SoftwareInventoryItem(Guid tenantId, Guid deviceId, string name, string? version, string? publisher, string? scope, DateOnly? installDate, DateTimeOffset seenAtUtc)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Name = name;
        NameKey = KeyOf(name);
        Version = version;
        Publisher = publisher;
        Scope = scope;
        InstallDate = installDate;
        FirstSeenAtUtc = seenAtUtc;
        LastSeenAtUtc = seenAtUtc;
    }

    public Guid DeviceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Lower-cased, trimmed name used to group the same product across devices.</summary>
    public string NameKey { get; private set; } = string.Empty;

    public string? Version { get; private set; }

    public string? Publisher { get; private set; }

    public string? Scope { get; private set; }

    public DateOnly? InstallDate { get; private set; }

    public DateTimeOffset FirstSeenAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public void Seen(string? publisher, DateOnly? installDate, DateTimeOffset now)
    {
        Publisher = publisher;
        InstallDate = installDate;
        LastSeenAtUtc = now;
    }

    public static string KeyOf(string name) => name.Trim().ToLowerInvariant();
}

public enum SoftwareAuditOutcome
{
    Success = 1,
    Denied = 2,
    Failure = 3,
}

public static class SoftwareAuditEvents
{
    public const string PackageUploaded = "package.uploaded";
    public const string PackageRejected = "package.rejected";
    public const string PackageConfigured = "package.configured";
    public const string PackageVerified = "package.verified";
    public const string PackageCorrupted = "package.corrupted";
    public const string PackageRetired = "package.retired";
    public const string PackageServed = "package.served";
    public const string PackageServeRefused = "package.serve_refused";
    public const string DeploymentRequested = "deployment.requested";
    public const string DeploymentApproved = "deployment.approved";
    public const string DeploymentApprovalRecorded = "deployment.approval_recorded";
    public const string DeploymentRejected = "deployment.rejected";
    public const string DeploymentStarted = "deployment.started";
    public const string DeploymentCancelled = "deployment.cancelled";
    public const string DeploymentExpired = "deployment.expired";
    public const string DeploymentCompleted = "deployment.completed";
    public const string TargetDispatched = "target.dispatched";
    public const string TargetFinished = "target.finished";
    public const string TargetRetryScheduled = "target.retry_scheduled";
    public const string DetectionRequested = "detection.requested";
    public const string PolicyChanged = "policy.changed";
    public const string RuleChanged = "restriction_rule.changed";
    public const string GroupChanged = "deployment_group.changed";
}

/// <summary>
/// Append-only, hash-chained audit trail of software management (same scheme as the remote
/// administration trail): UPDATE/DELETE/TRUNCATE are rejected by triggers and every row carries
/// the SHA-256 of its content chained to the previous row of the tenant.
/// </summary>
public sealed class SoftwareAuditEvent : TenantEntity
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private SoftwareAuditEvent()
    {
    }

    internal SoftwareAuditEvent(
        Guid tenantId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        string eventType,
        SoftwareAuditOutcome outcome,
        Guid? actorUserId,
        Guid? packageId,
        Guid? deploymentId,
        Guid? deviceId,
        string? sourceIp,
        string? detail,
        string previousHash)
        : base(tenantId)
    {
        Sequence = sequence;
        OccurredAtUtc = new DateTimeOffset(occurredAtUtc.UtcTicks - (occurredAtUtc.UtcTicks % 10), TimeSpan.Zero);
        EventType = eventType;
        Outcome = outcome;
        ActorUserId = actorUserId;
        PackageId = packageId;
        DeploymentId = deploymentId;
        DeviceId = deviceId;
        SourceIp = sourceIp;
        Detail = detail;
        PreviousHash = previousHash;
        Hash = ComputeHash();
    }

    public long Sequence { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public SoftwareAuditOutcome Outcome { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public Guid? PackageId { get; private set; }

    public Guid? DeploymentId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public string? SourceIp { get; private set; }

    public string? Detail { get; private set; }

    public string PreviousHash { get; private set; } = GenesisHash;

    public string Hash { get; private set; } = string.Empty;

    public string ComputeHash()
    {
        var text = string.Join(
            '\u001f',
            PreviousHash,
            TenantId.ToString("D"),
            Id.ToString("D"),
            Sequence.ToString(CultureInfo.InvariantCulture),
            OccurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            EventType,
            ((int)Outcome).ToString(CultureInfo.InvariantCulture),
            ActorUserId?.ToString("D") ?? string.Empty,
            PackageId?.ToString("D") ?? string.Empty,
            DeploymentId?.ToString("D") ?? string.Empty,
            DeviceId?.ToString("D") ?? string.Empty,
            SourceIp ?? string.Empty,
            Detail ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
