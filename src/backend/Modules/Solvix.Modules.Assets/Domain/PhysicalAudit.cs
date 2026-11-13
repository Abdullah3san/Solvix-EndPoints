namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A physical stock-take. Starting it snapshots the assets expected in its scope
/// (branch / department / category); auditors scan tags; completing it marks
/// everything not found as missing.
/// </summary>
public sealed class PhysicalAudit : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int NotesMaxLength = 2000;

    private PhysicalAudit()
    {
    }

    private PhysicalAudit(Guid tenantId)
        : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public AssetCategory? Category { get; private set; }

    public PhysicalAuditStatus Status { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public Guid? StartedByUserId { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Guid? CompletedByUserId { get; private set; }

    public string? Notes { get; private set; }

    public static PhysicalAudit Start(Guid tenantId, string name, Guid? branchId, Guid? departmentId, AssetCategory? category, string? notes, DateTimeOffset now, Guid? actor) =>
        new(tenantId)
        {
            Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength),
            BranchId = branchId,
            DepartmentId = departmentId,
            Category = category,
            Notes = Guard.Optional(notes, "notes", NotesMaxLength),
            Status = PhysicalAuditStatus.InProgress,
            StartedAtUtc = now,
            StartedByUserId = actor,
        };

    public void EnsureInProgress()
    {
        if (Status != PhysicalAuditStatus.InProgress)
        {
            throw new ConflictException("The audit is closed.", "audit_closed");
        }
    }

    public void Complete(DateTimeOffset now, Guid? actor, string? notes)
    {
        EnsureInProgress();
        Status = PhysicalAuditStatus.Completed;
        CompletedAtUtc = now;
        CompletedByUserId = actor;
        Notes = Guard.Optional(notes, "notes", NotesMaxLength) ?? Notes;
    }

    public void Cancel(DateTimeOffset now, Guid? actor)
    {
        EnsureInProgress();
        Status = PhysicalAuditStatus.Cancelled;
        CompletedAtUtc = now;
        CompletedByUserId = actor;
    }
}

public sealed class PhysicalAuditItem : TenantEntity
{
    public const int NotesMaxLength = 1000;

    private PhysicalAuditItem()
    {
    }

    private PhysicalAuditItem(Guid tenantId, Guid auditId, Guid assetId)
        : base(tenantId)
    {
        PhysicalAuditId = auditId;
        AssetId = assetId;
    }

    public Guid PhysicalAuditId { get; private set; }

    public Guid AssetId { get; private set; }

    /// <summary>In the audit's scope when it started.</summary>
    public bool Expected { get; private set; }

    public PhysicalAuditResult Result { get; private set; }

    /// <summary>Location of the asset in the register when the audit started.</summary>
    public Guid? ExpectedBranchId { get; private set; }

    public string? ExpectedLocation { get; private set; }

    public string? ObservedLocation { get; private set; }

    public AssetCondition? ObservedCondition { get; private set; }

    public DateTimeOffset? ScannedAtUtc { get; private set; }

    public Guid? ScannedByUserId { get; private set; }

    public string? Notes { get; private set; }

    public static PhysicalAuditItem Expect(PhysicalAudit audit, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(asset);
        return new PhysicalAuditItem(audit.TenantId, audit.Id, asset.Id)
        {
            Expected = true,
            Result = PhysicalAuditResult.Pending,
            ExpectedBranchId = asset.BranchId,
            ExpectedLocation = asset.Location,
        };
    }

    public static PhysicalAuditItem Unexpected(PhysicalAudit audit, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(asset);
        return new PhysicalAuditItem(audit.TenantId, audit.Id, asset.Id)
        {
            Expected = false,
            Result = PhysicalAuditResult.Unexpected,
            ExpectedBranchId = asset.BranchId,
            ExpectedLocation = asset.Location,
        };
    }

    public void Record(PhysicalAuditResult result, DateTimeOffset now, Guid? actor, string? observedLocation, AssetCondition? condition, string? notes)
    {
        if (result == PhysicalAuditResult.Pending)
        {
            ScannedAtUtc = null;
            ScannedByUserId = null;
        }
        else
        {
            ScannedAtUtc = now;
            ScannedByUserId = actor;
        }

        Result = !Expected && result == PhysicalAuditResult.Found ? PhysicalAuditResult.Unexpected : result;
        ObservedLocation = Guard.Optional(observedLocation, "observedLocation", Asset.LocationMaxLength) ?? ObservedLocation;
        ObservedCondition = condition ?? ObservedCondition;
        Notes = Guard.Optional(notes, "notes", NotesMaxLength) ?? Notes;
    }

    public void MarkMissingIfPending()
    {
        if (Result == PhysicalAuditResult.Pending)
        {
            Result = PhysicalAuditResult.Missing;
        }
    }
}
