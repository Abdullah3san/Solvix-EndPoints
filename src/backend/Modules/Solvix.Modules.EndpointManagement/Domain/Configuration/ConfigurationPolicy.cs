namespace Solvix.Modules.EndpointManagement.Domain.Configuration;

using System.Text.Json;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A named set of desired settings. Content lives in immutable, numbered
/// <see cref="ConfigurationPolicyVersion"/>s; exactly one version at a time is
/// active (published) and delivered to devices.
/// </summary>
public sealed class ConfigurationPolicy : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    private ConfigurationPolicy()
    {
    }

    private ConfigurationPolicy(Guid tenantId, string name)
        : base(tenantId)
    {
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsArchived { get; private set; }

    public Guid? ActiveVersionId { get; private set; }

    public int LatestVersionNumber { get; private set; }

    public uint RowVersion { get; private set; }

    public static ConfigurationPolicy Create(Guid tenantId, string name, string? description)
    {
        var policy = new ConfigurationPolicy(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength));
        policy.Rename(name, description);
        return policy;
    }

    public void Rename(string name, string? description)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
    }

    public int NextVersionNumber() => ++LatestVersionNumber;

    public void Activate(Guid versionId) => ActiveVersionId = versionId;

    public void Archive()
    {
        IsArchived = true;
        ActiveVersionId = null;
    }
}

public enum PolicyVersionStatus
{
    Draft = 1,
    PendingApproval = 2,
    Approved = 3,
    Rejected = 4,

    /// <summary>The active version of its policy.</summary>
    Published = 5,

    /// <summary>Was published, replaced by a newer version; can be re-activated as a fast rollback.</summary>
    Superseded = 6,
}

/// <summary>
/// One immutable revision of a policy's settings. Lifecycle:
/// Draft → PendingApproval → Approved/Rejected → Published → Superseded.
/// The approval records the highest risk the approver accepted
/// (<see cref="AuthorizedRisk"/>); agents refuse anything above it.
/// </summary>
public sealed class ConfigurationPolicyVersion : TenantEntity
{
    public const int NoteMaxLength = 2000;
    public const int MaxSettings = 500;

    private IReadOnlyList<ConfigurationSetting>? _settings;

    private ConfigurationPolicyVersion()
    {
    }

    private ConfigurationPolicyVersion(Guid tenantId, Guid policyId, int number, Guid createdByUserId)
        : base(tenantId)
    {
        PolicyId = policyId;
        Number = number;
        CreatedByUserId = createdByUserId;
        Status = PolicyVersionStatus.Draft;
    }

    public Guid PolicyId { get; private set; }

    public int Number { get; private set; }

    public PolicyVersionStatus Status { get; private set; }

    /// <summary>JSON array of settings (with "kind" discriminators).</summary>
    public string SettingsJson { get; private set; } = "[]";

    public SettingRisk MaxRisk { get; private set; }

    public string ContentHash { get; private set; } = string.Empty;

    public string? ChangeNote { get; private set; }

    public int? RestoredFromNumber { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Guid? SubmittedByUserId { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public string? ReviewNote { get; private set; }

    /// <summary>Risk accepted by the approver; set on approval.</summary>
    public SettingRisk? AuthorizedRisk { get; private set; }

    public bool DestructiveAcknowledged { get; private set; }

    public Guid? PublishedByUserId { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public DateTimeOffset? SupersededAtUtc { get; private set; }

    public ConfigurationPolicy? Policy { get; private set; }

    public static ConfigurationPolicyVersion CreateDraft(
        Guid tenantId,
        ConfigurationPolicy policy,
        IReadOnlyList<ConfigurationSetting> settings,
        string? changeNote,
        Guid createdByUserId,
        int? restoredFrom = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var version = new ConfigurationPolicyVersion(tenantId, policy.Id, policy.NextVersionNumber(), createdByUserId)
        {
            RestoredFromNumber = restoredFrom,
        };
        version.ReplaceSettings(settings, changeNote);
        return version;
    }

    public IReadOnlyList<ConfigurationSetting> GetSettings() =>
        _settings ??= JsonSerializer.Deserialize<List<ConfigurationSetting>>(SettingsJson, ContractJson.Options) ?? [];

    public void ReplaceSettings(IReadOnlyList<ConfigurationSetting> settings, string? changeNote)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Status != PolicyVersionStatus.Draft)
        {
            throw new ConflictException($"Version {Number} is {Status} and can no longer be edited; create a new version.");
        }

        var errors = ValidateSettings(settings);
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        // Canonical order makes the content hash independent of input order.
        var ordered = settings.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        _settings = ordered;
        SettingsJson = JsonSerializer.Serialize<IReadOnlyList<ConfigurationSetting>>(ordered, ContractJson.Options);
        ContentHash = ContractJson.Sha256(SettingsJson);
        MaxRisk = SettingRiskClassifier.Max(ordered);
        ChangeNote = Guard.Optional(changeNote, "changeNote", NoteMaxLength);
    }

    /// <summary>Validates each setting, rejects duplicate targets and anything classified Blocked.</summary>
    public static Dictionary<string, string[]> ValidateSettings(IReadOnlyList<ConfigurationSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new Dictionary<string, string[]>();
        if (settings.Count > MaxSettings)
        {
            errors["settings"] = [$"A policy version can contain at most {MaxSettings} settings."];
            return errors;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < settings.Count; i++)
        {
            var setting = settings[i];
            if (setting is null)
            {
                errors[$"settings[{i}]"] = ["Setting is required."];
                continue;
            }

            var problems = setting.Validate().ToList();
            if (problems.Count == 0)
            {
                if (seen.TryGetValue(setting.Key, out var first))
                {
                    problems.Add($"Duplicates settings[{first}] ({setting.Key}).");
                }
                else
                {
                    seen[setting.Key] = i;
                }

                if (SettingRiskClassifier.Classify(setting) == SettingRisk.Blocked)
                {
                    problems.Add($"Not allowed: {SettingRiskClassifier.Explain(setting)}");
                }
            }

            if (problems.Count > 0)
            {
                errors[$"settings[{i}]"] = [.. problems];
            }
        }

        return errors;
    }

    public void Submit(Guid userId, DateTimeOffset now)
    {
        if (Status != PolicyVersionStatus.Draft)
        {
            throw new ConflictException($"Only drafts can be submitted (version {Number} is {Status}).");
        }

        Status = PolicyVersionStatus.PendingApproval;
        SubmittedByUserId = userId;
        SubmittedAtUtc = now;
    }

    /// <summary>
    /// Approval rules ("never apply destructive configuration without authorisation"):
    /// Low risk – any administrator, including the author;
    /// Elevated – a different administrator than the author/submitter (four-eyes);
    /// Destructive – a different person who is an Owner and explicitly acknowledges it.
    /// </summary>
    public void Approve(Guid reviewerId, bool reviewerIsOwner, bool acknowledgeDestructive, string? note, DateTimeOffset now)
    {
        if (Status != PolicyVersionStatus.PendingApproval)
        {
            throw new ConflictException($"Only versions pending approval can be approved (version {Number} is {Status}).");
        }

        var isAuthor = reviewerId == CreatedByUserId || reviewerId == SubmittedByUserId;
        if (MaxRisk >= SettingRisk.Elevated && isAuthor)
        {
            throw new ForbiddenException($"This version contains {MaxRisk} settings and must be approved by someone other than its author.");
        }

        if (MaxRisk >= SettingRisk.Destructive)
        {
            if (!reviewerIsOwner)
            {
                throw new ForbiddenException("Destructive settings can only be approved by a workspace Owner.");
            }

            if (!acknowledgeDestructive)
            {
                throw new ValidationException("acknowledgeDestructive", "Confirm that you authorise destructive changes on the targeted devices.");
            }

            DestructiveAcknowledged = true;
        }

        Status = PolicyVersionStatus.Approved;
        ReviewedByUserId = reviewerId;
        ReviewedAtUtc = now;
        ReviewNote = Guard.Optional(note, "note", NoteMaxLength);
        AuthorizedRisk = MaxRisk;
    }

    public void Reject(Guid reviewerId, string? note, DateTimeOffset now)
    {
        if (Status != PolicyVersionStatus.PendingApproval)
        {
            throw new ConflictException($"Only versions pending approval can be rejected (version {Number} is {Status}).");
        }

        Status = PolicyVersionStatus.Rejected;
        ReviewedByUserId = reviewerId;
        ReviewedAtUtc = now;
        ReviewNote = Guard.Optional(note, "note", NoteMaxLength);
    }

    public void Publish(Guid userId, DateTimeOffset now)
    {
        if (Status is not (PolicyVersionStatus.Approved or PolicyVersionStatus.Superseded))
        {
            throw new ConflictException($"Only approved (or previously published) versions can be published (version {Number} is {Status}).");
        }

        Status = PolicyVersionStatus.Published;
        PublishedByUserId = userId;
        PublishedAtUtc = now;
        SupersededAtUtc = null;
    }

    public void Supersede(DateTimeOffset now)
    {
        if (Status == PolicyVersionStatus.Published)
        {
            Status = PolicyVersionStatus.Superseded;
            SupersededAtUtc = now;
        }
    }
}

public enum RemovalBehavior
{
    /// <summary>When the setting stops applying, leave the device as it is.</summary>
    Leave = 1,

    /// <summary>When the setting stops applying, restore the value captured before Solvix first changed it.</summary>
    Revert = 2,
}

/// <summary>
/// Assigns a policy to a branch, department (with sub-departments), device group,
/// single device or all devices — or excludes one of those from the policy.
/// Lower <see cref="Priority"/> wins when two policies set the same thing
/// differently; on a tie the more specific target wins.
/// </summary>
public sealed class ConfigurationAssignment : TenantEntity
{
    public const int MinPriority = 1;
    public const int MaxPriority = 1000;

    private ConfigurationAssignment()
    {
    }

    public ConfigurationAssignment(
        Guid tenantId,
        Guid policyId,
        Targeting.TargetType targetType,
        Guid? targetId,
        bool isExclusion,
        EnforcementMode mode,
        int priority,
        Guid? maintenanceWindowId,
        RemovalBehavior removalBehavior,
        Guid createdByUserId)
        : base(tenantId)
    {
        PolicyId = policyId;
        TargetType = targetType;
        TargetId = targetType == Targeting.TargetType.AllDevices ? null : targetId;
        IsExclusion = isExclusion;
        CreatedByUserId = createdByUserId;
        Update(mode, priority, maintenanceWindowId, removalBehavior);
    }

    public Guid PolicyId { get; private set; }

    public Targeting.TargetType TargetType { get; private set; }

    public Guid? TargetId { get; private set; }

    public bool IsExclusion { get; private set; }

    public EnforcementMode Mode { get; private set; }

    public int Priority { get; private set; }

    /// <summary>When set, changes (apply/remediate) only happen inside the window; evaluation always happens.</summary>
    public Guid? MaintenanceWindowId { get; private set; }

    public RemovalBehavior RemovalBehavior { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Targeting.TargetRef ToRef() => new(TargetType, TargetId, IsExclusion);

    public void Update(EnforcementMode mode, int priority, Guid? maintenanceWindowId, RemovalBehavior removalBehavior)
    {
        if (priority is < MinPriority or > MaxPriority)
        {
            throw new ValidationException("priority", $"Priority must be between {MinPriority} and {MaxPriority}.");
        }

        Mode = mode;
        Priority = priority;
        MaintenanceWindowId = maintenanceWindowId;
        RemovalBehavior = removalBehavior;
    }
}
