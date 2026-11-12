namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>One check-out period of an asset. Open while <see cref="ReturnedAtUtc"/> is null (at most one per asset, enforced by a partial unique index).</summary>
public sealed class AssetAssignment : TenantEntity
{
    public const int NotesMaxLength = 1000;

    private AssetAssignment()
    {
    }

    private AssetAssignment(Guid tenantId, Guid assetId)
        : base(tenantId)
    {
        AssetId = assetId;
    }

    public Guid AssetId { get; private set; }

    public Guid? AssignedUserId { get; private set; }

    public string? AssigneeName { get; private set; }

    public string? EmployeeNumber { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public DateTimeOffset AssignedAtUtc { get; private set; }

    public Guid? AssignedByUserId { get; private set; }

    public DateOnly? ExpectedReturnDate { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset? ReturnedAtUtc { get; private set; }

    public Guid? ReturnedByUserId { get; private set; }

    public string? ReturnNotes { get; private set; }

    public AssetCondition? ReturnCondition { get; private set; }

    public static AssetAssignment Open(
        Asset asset, Assignee assignee, DateTimeOffset now, Guid? actor, DateOnly? expectedReturn, string? notes)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assignee);
        return new AssetAssignment(asset.TenantId, asset.Id)
        {
            AssignedUserId = assignee.UserId,
            AssigneeName = assignee.Name,
            EmployeeNumber = assignee.EmployeeNumber,
            BranchId = asset.BranchId,
            DepartmentId = asset.DepartmentId,
            AssignedAtUtc = now,
            AssignedByUserId = actor,
            ExpectedReturnDate = expectedReturn,
            Notes = Guard.Optional(notes, "notes", NotesMaxLength),
        };
    }

    public void Close(DateTimeOffset now, Guid? actor, string? notes, AssetCondition? condition)
    {
        ReturnedAtUtc = now;
        ReturnedByUserId = actor;
        ReturnNotes = Guard.Optional(notes, "notes", NotesMaxLength);
        ReturnCondition = condition;
    }
}

/// <summary>Movement of an asset between branches, departments or locations.</summary>
public sealed class AssetTransfer : TenantEntity
{
    private AssetTransfer()
    {
    }

    private AssetTransfer(Guid tenantId, Guid assetId)
        : base(tenantId)
    {
        AssetId = assetId;
    }

    public Guid AssetId { get; private set; }

    public Guid? FromBranchId { get; private set; }

    public Guid? FromDepartmentId { get; private set; }

    public string? FromLocation { get; private set; }

    public Guid? ToBranchId { get; private set; }

    public Guid? ToDepartmentId { get; private set; }

    public string? ToLocation { get; private set; }

    public DateTimeOffset TransferredAtUtc { get; private set; }

    public Guid? TransferredByUserId { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Captures the origin before <see cref="Asset.Transfer"/> is applied, and the destination after.</summary>
    public static AssetTransfer Record(
        Asset asset, Guid? fromBranch, Guid? fromDepartment, string? fromLocation, DateTimeOffset now, Guid? actor, string? reason)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new AssetTransfer(asset.TenantId, asset.Id)
        {
            FromBranchId = fromBranch,
            FromDepartmentId = fromDepartment,
            FromLocation = fromLocation,
            ToBranchId = asset.BranchId,
            ToDepartmentId = asset.DepartmentId,
            ToLocation = asset.Location,
            TransferredAtUtc = now,
            TransferredByUserId = actor,
            Reason = Guard.Optional(reason, "reason", Asset.ReasonMaxLength),
        };
    }
}

/// <summary>Maintenance, repair, upgrade or inspection work on an asset.</summary>
public sealed class MaintenanceRecord : TenantEntity
{
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 4000;

    private MaintenanceRecord()
    {
    }

    private MaintenanceRecord(Guid tenantId, Guid assetId)
        : base(tenantId)
    {
        AssetId = assetId;
    }

    public Guid AssetId { get; private set; }

    public MaintenanceType Type { get; private set; }

    public MaintenanceStatus Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid? VendorId { get; private set; }

    public string? PerformedBy { get; private set; }

    public decimal? Cost { get; private set; }

    public DateOnly? ScheduledFor { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? Resolution { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    public bool IsOpen => Status is MaintenanceStatus.Scheduled or MaintenanceStatus.InProgress;

    public static MaintenanceRecord Create(
        Asset asset, MaintenanceType type, string title, string? description, Guid? vendorId, string? performedBy, DateOnly? scheduledFor, Guid? actor)
    {
        ArgumentNullException.ThrowIfNull(asset);
        asset.EnsureNotDisposed();
        return new MaintenanceRecord(asset.TenantId, asset.Id)
        {
            Type = Enum.IsDefined(type) ? type : throw new Solvix.SharedKernel.Exceptions.ValidationException("type", "Unknown maintenance type."),
            Status = MaintenanceStatus.Scheduled,
            Title = Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength),
            Description = Guard.Optional(description, "description", TextMaxLength),
            VendorId = vendorId,
            PerformedBy = Guard.Optional(performedBy, "performedBy", Asset.ShortTextMaxLength),
            ScheduledFor = scheduledFor,
            CreatedByUserId = actor,
        };
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != MaintenanceStatus.Scheduled)
        {
            throw new Solvix.SharedKernel.Exceptions.ConflictException("Only scheduled maintenance can be started.", "maintenance_status_conflict");
        }

        Status = MaintenanceStatus.InProgress;
        StartedAtUtc = now;
    }

    public void Complete(DateTimeOffset now, decimal? cost, string? resolution)
    {
        if (!IsOpen)
        {
            throw new Solvix.SharedKernel.Exceptions.ConflictException("The maintenance record is already closed.", "maintenance_status_conflict");
        }

        if (cost is < 0 or > Asset.MaxAmount)
        {
            throw new Solvix.SharedKernel.Exceptions.ValidationException("cost", "The cost is out of range.");
        }

        StartedAtUtc ??= now;
        Status = MaintenanceStatus.Completed;
        CompletedAtUtc = now;
        Cost = cost is null ? null : decimal.Round(cost.Value, 2, MidpointRounding.AwayFromZero);
        Resolution = Guard.Optional(resolution, "resolution", TextMaxLength);
    }

    public void Cancel(DateTimeOffset now, string? reason)
    {
        if (!IsOpen)
        {
            throw new Solvix.SharedKernel.Exceptions.ConflictException("The maintenance record is already closed.", "maintenance_status_conflict");
        }

        Status = MaintenanceStatus.Cancelled;
        CompletedAtUtc = now;
        Resolution = Guard.Optional(reason, "reason", TextMaxLength);
    }
}
