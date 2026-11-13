namespace Solvix.Modules.Assets.Application;

using Solvix.Modules.Assets.Domain;

// ---------------------------------------------------------------- register

public enum EndpointLinkState
{
    /// <summary>The category does not run the agent and nothing is linked.</summary>
    NotApplicable = 0,
    Unlinked = 1,
    Suggested = 2,
    Linked = 3,
}

public enum AssetSortField
{
    AssetTag = 0,
    Name,
    Category,
    Status,
    Manufacturer,
    Model,
    SerialNumber,
    PurchaseDate,
    PurchaseCost,
    WarrantyEndDate,
    LastAuditedAt,
    CreatedAt,
    UpdatedAt,
}

public enum SortDirection
{
    Asc = 0,
    Desc = 1,
}

/// <summary>Filters of the asset register (list and export).</summary>
public sealed record AssetListQuery
{
    public string? Search { get; init; }

    public IReadOnlyList<AssetCategory>? Category { get; init; }

    public IReadOnlyList<AssetStatus>? Status { get; init; }

    public AssetCondition? Condition { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? VendorId { get; init; }

    public Guid? AssignedUserId { get; init; }

    /// <summary>true: assigned to anyone; false: unassigned.</summary>
    public bool? Assigned { get; init; }

    public WarrantyState? Warranty { get; init; }

    public EndpointLinkState? EndpointLink { get; init; }

    public DateOnly? PurchasedFrom { get; init; }

    public DateOnly? PurchasedTo { get; init; }

    /// <summary>Not physically audited in the last N days (or never).</summary>
    public int? NotAuditedForDays { get; init; }

    public AssetSortField Sort { get; init; } = AssetSortField.AssetTag;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public sealed record RefDto(Guid Id, string? Name, string? Code = null);

/// <summary>Holder of an asset; <see cref="IsMember"/> false for an employee without a login or a former member.</summary>
public sealed record AssigneeDto(Guid? UserId, string? DisplayName, string? Email, string? EmployeeNumber, bool IsMember);

public sealed record AssetSummaryDto(
    Guid Id,
    string AssetTag,
    string Name,
    AssetCategory Category,
    AssetStatus Status,
    AssetCondition Condition,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    RefDto? Branch,
    RefDto? Department,
    string? Location,
    AssigneeDto? Assignee,
    RefDto? Vendor,
    DateOnly? PurchaseDate,
    decimal? PurchaseCost,
    string? Currency,
    DateOnly? WarrantyEndDate,
    WarrantyState Warranty,
    EndpointLinkState EndpointLink,
    Guid? LinkedDeviceId,
    DateTimeOffset? LastAuditedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AssetDetailsDto
{
    public required AssetSummaryDto Summary { get; init; }

    public uint Version { get; init; }

    public string? Description { get; init; }

    public string? PurchaseOrderNumber { get; init; }

    public string? InvoiceNumber { get; init; }

    public DateOnly? WarrantyStartDate { get; init; }

    public string? WarrantyProvider { get; init; }

    public string? WarrantyReference { get; init; }

    public DepreciationMethod DepreciationMethod { get; init; }

    public int? UsefulLifeMonths { get; init; }

    public decimal? SalvageValue { get; init; }

    public DepreciationResult? Depreciation { get; init; }

    public DateTimeOffset? AssignedAtUtc { get; init; }

    public DateTimeOffset? RetiredAtUtc { get; init; }

    public string? RetirementReason { get; init; }

    public DateTimeOffset? DisposedAtUtc { get; init; }

    public DisposalMethod? DisposalMethod { get; init; }

    public decimal? DisposalValue { get; init; }

    public string? DisposalReference { get; init; }

    public string? DisposalNotes { get; init; }

    public bool EndpointManageable { get; init; }

    public AssetEndpointLinkDto? EndpointLink { get; init; }

    public int PendingSuggestions { get; init; }

    public MaintenanceRecordDto? OpenMaintenance { get; init; }

    /// <summary>Text encoded in the asset's QR code.</summary>
    public required string QrPayload { get; init; }
}

public sealed record AssetInput
{
    public string? AssetTag { get; init; }

    public string Name { get; init; } = string.Empty;

    public AssetCategory Category { get; init; }

    public AssetCondition Condition { get; init; } = AssetCondition.Good;

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? Description { get; init; }

    public string? Location { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? VendorId { get; init; }

    public DateOnly? PurchaseDate { get; init; }

    public decimal? PurchaseCost { get; init; }

    public string? Currency { get; init; }

    public string? PurchaseOrderNumber { get; init; }

    public string? InvoiceNumber { get; init; }

    public DateOnly? WarrantyStartDate { get; init; }

    public DateOnly? WarrantyEndDate { get; init; }

    public string? WarrantyProvider { get; init; }

    public string? WarrantyReference { get; init; }

    public DepreciationMethod DepreciationMethod { get; init; }

    public int? UsefulLifeMonths { get; init; }

    public decimal? SalvageValue { get; init; }
}

public sealed record CreateAssetRequest
{
    public required AssetInput Asset { get; init; }

    /// <summary>Optional initial assignment.</summary>
    public AssignAssetRequest? AssignTo { get; init; }
}

/// <summary>Branch, department and location are changed through transfers, not here.</summary>
public sealed record UpdateAssetRequest
{
    public required AssetInput Asset { get; init; }

    /// <summary>When supplied, the update fails with 409 if the asset changed since it was read.</summary>
    public uint? Version { get; init; }
}

public sealed record AssignAssetRequest(Guid? AssignedUserId, string? AssigneeName, string? EmployeeNumber, DateOnly? ExpectedReturnDate, string? Notes);

public sealed record ReturnAssetRequest(AssetCondition? Condition, string? Notes);

public sealed record TransferAssetRequest(Guid? BranchId, Guid? DepartmentId, string? Location, string? Reason);

public sealed record RetireAssetRequest(string Reason);

public sealed record DisposeAssetRequest(DisposalMethod Method, DateTimeOffset? DisposedAtUtc, decimal? Value, string? Reference, string? Notes);

public enum BulkAssetAction
{
    Transfer = 1,
    Return = 2,
    Retire = 3,
    SetCondition = 4,
    MarkMissing = 5,
}

public sealed record BulkAssetRequest
{
    public IReadOnlyList<Guid> AssetIds { get; init; } = [];

    public BulkAssetAction Action { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Location { get; init; }

    public string? Reason { get; init; }

    public AssetCondition? Condition { get; init; }
}

public sealed record BulkAssetResultDto(BulkAssetAction Action, int Processed);

public sealed record AssetStatsDto(
    int Total,
    int InStock,
    int InUse,
    int InMaintenance,
    int Missing,
    int Retired,
    int Disposed,
    int WarrantyExpiringSoon,
    int PendingEndpointMatches);

public sealed record AssignmentDto(
    Guid Id,
    AssigneeDto Assignee,
    RefDto? Branch,
    RefDto? Department,
    DateTimeOffset AssignedAtUtc,
    RefDto? AssignedBy,
    DateOnly? ExpectedReturnDate,
    string? Notes,
    DateTimeOffset? ReturnedAtUtc,
    RefDto? ReturnedBy,
    string? ReturnNotes,
    AssetCondition? ReturnCondition);

public sealed record TransferDto(
    Guid Id,
    RefDto? FromBranch,
    RefDto? FromDepartment,
    string? FromLocation,
    RefDto? ToBranch,
    RefDto? ToDepartment,
    string? ToLocation,
    DateTimeOffset TransferredAtUtc,
    RefDto? TransferredBy,
    string? Reason);

public sealed record AuditLogEntryDto(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string EntityType,
    Guid EntityId,
    Guid? AssetId,
    string Action,
    string Summary,
    IReadOnlyDictionary<string, AuditChangeDto>? Changes,
    AuditActorKind ActorKind,
    RefDto? Actor,
    string? CorrelationId);

public sealed record AuditChangeDto(string? From, string? To);

public sealed record AuditLogQuery(Guid? AssetId, string? EntityType, string? Action, int Page = 1, int PageSize = 50);

/// <summary>An asset as its holder sees it (no financial data).</summary>
public sealed record MyAssetDto(
    Guid Id,
    string AssetTag,
    string Name,
    AssetCategory Category,
    AssetStatus Status,
    AssetCondition Condition,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    DateTimeOffset? AssignedAtUtc,
    DateOnly? WarrantyEndDate,
    string? Location);

public sealed record AssetLookupDto(Guid Id, string AssetTag, string Name, AssetStatus Status, string MatchedBy);

// ---------------------------------------------------------------- vendors

public sealed record VendorDto(
    Guid Id,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? Website,
    string? AccountNumber,
    string? Notes,
    bool IsActive,
    int AssetCount,
    DateTimeOffset CreatedAtUtc);

public sealed record VendorRequest(string Name, string? ContactName, string? Email, string? Phone, string? Website, string? AccountNumber, string? Notes, bool IsActive = true);

public sealed record VendorListQuery(string? Search, bool IncludeInactive = false, int Page = 1, int PageSize = 50);

// ---------------------------------------------------------------- maintenance

public sealed record MaintenanceRecordDto(
    Guid Id,
    Guid AssetId,
    string? AssetTag,
    string? AssetName,
    MaintenanceType Type,
    MaintenanceStatus Status,
    string Title,
    string? Description,
    RefDto? Vendor,
    string? PerformedBy,
    decimal? Cost,
    DateOnly? ScheduledFor,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Resolution,
    RefDto? CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateMaintenanceRequest(
    MaintenanceType Type, string Title, string? Description, Guid? VendorId, string? PerformedBy, DateOnly? ScheduledFor, bool StartNow = false);

public sealed record CompleteMaintenanceRequest(decimal? Cost, string? Resolution);

public sealed record CancelMaintenanceRequest(string? Reason);

public sealed record MaintenanceListQuery(MaintenanceStatus? Status, Guid? AssetId, int Page = 1, int PageSize = 50);

// ---------------------------------------------------------------- physical audits

public sealed record PhysicalAuditDto(
    Guid Id,
    string Name,
    RefDto? Branch,
    RefDto? Department,
    AssetCategory? Category,
    PhysicalAuditStatus Status,
    DateTimeOffset StartedAtUtc,
    RefDto? StartedBy,
    DateTimeOffset? CompletedAtUtc,
    RefDto? CompletedBy,
    string? Notes,
    int Expected,
    int Found,
    int Missing,
    int Pending,
    int Unexpected);

public sealed record StartPhysicalAuditRequest(string Name, Guid? BranchId, Guid? DepartmentId, AssetCategory? Category, string? Notes);

public sealed record PhysicalAuditItemDto(
    Guid Id,
    Guid AssetId,
    string AssetTag,
    string AssetName,
    AssetCategory Category,
    AssetStatus AssetStatus,
    bool Expected,
    PhysicalAuditResult Result,
    RefDto? ExpectedBranch,
    string? ExpectedLocation,
    string? ObservedLocation,
    AssetCondition? ObservedCondition,
    DateTimeOffset? ScannedAtUtc,
    string? Notes);

public sealed record PhysicalAuditItemQuery(PhysicalAuditResult? Result, string? Search, int Page = 1, int PageSize = 100);

public sealed record ScanRequest(string Code, string? ObservedLocation, AssetCondition? Condition, string? Notes);

public sealed record ScanResultDto(PhysicalAuditItemDto Item, bool AlreadyScanned, PhysicalAuditDto Audit);

public sealed record UpdateAuditItemRequest(PhysicalAuditResult Result, AssetCondition? Condition, string? Notes);

public sealed record CompletePhysicalAuditRequest(bool MarkMissingAssets, string? Notes);

// ---------------------------------------------------------------- endpoint matching

public sealed record EndpointDto(
    Guid Id,
    string Hostname,
    string? DisplayName,
    bool IsActive,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string? OsName,
    string Connectivity,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset EnrolledAtUtc);

public sealed record AssetRefDto(Guid Id, string AssetTag, string Name, AssetCategory Category, AssetStatus Status, string? Manufacturer, string? Model, string? SerialNumber);

/// <summary>Differences between the two records that an administrator may want to reconcile (they are never synchronised automatically).</summary>
public sealed record LinkDiscrepancyDto(string Field, string? Asset, string? Endpoint);

public sealed record AssetDeviceLinkDto(
    Guid Id,
    AssetRefDto Asset,
    EndpointDto? Endpoint,
    AssetDeviceLinkStatus Status,
    AssetDeviceLinkSource Source,
    MatchConfidence Confidence,
    int Score,
    bool IsAmbiguous,
    IReadOnlyList<MatchEvidence> Evidence,
    IReadOnlyList<LinkDiscrepancyDto> Discrepancies,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    RefDto? DecidedBy,
    string? DecisionNote);

public sealed record AssetEndpointLinkDto(Guid LinkId, EndpointDto Endpoint, AssetDeviceLinkSource Source, MatchConfidence Confidence, DateTimeOffset? ApprovedAtUtc, IReadOnlyList<LinkDiscrepancyDto> Discrepancies);

public sealed record LinkQuery(AssetDeviceLinkStatus? Status = AssetDeviceLinkStatus.Suggested, Guid? AssetId = null, Guid? DeviceId = null, int Page = 1, int PageSize = 50);

public sealed record LinkDecisionRequest(string? Note);

public sealed record ManualLinkRequest(Guid DeviceId, string? Note);

public sealed record RegisterEndpointRequest(Guid DeviceId, AssetCategory Category, string? Name, string? AssetTag);

public sealed record DeviceAssetDto(AssetDeviceLinkDto? Approved, IReadOnlyList<AssetDeviceLinkDto> Suggestions);

public sealed record RescanResultDto(int EndpointsEvaluated, int SuggestionsCreated, int AutoApproved);

// ---------------------------------------------------------------- reports & settings

public sealed record CountValueDto(string Key, string? Label, int Count, decimal Cost);

public sealed record AssetSummaryReportDto(
    DateOnly AsOf,
    int TotalAssets,
    int ActiveAssets,
    decimal TotalPurchaseCost,
    decimal TotalBookValue,
    IReadOnlyList<CountValueDto> ByStatus,
    IReadOnlyList<CountValueDto> ByCategory,
    IReadOnlyList<CountValueDto> ByBranch,
    int WarrantyActive,
    int WarrantyExpiringSoon,
    int WarrantyExpired,
    int WarrantyNone,
    int OpenMaintenance,
    decimal MaintenanceCostThisYear,
    int NotAuditedIn365Days,
    EndpointCoverageDto EndpointCoverage);

public sealed record EndpointCoverageDto(int ManageableAssets, int LinkedAssets, int PendingSuggestions, int ActiveEndpoints, int EndpointsWithoutAsset);

public sealed record DepreciationReportRowDto(AssetCategory Category, int Assets, decimal Cost, decimal AccumulatedDepreciation, decimal BookValue);

public sealed record DepreciationReportDto(DateOnly AsOf, IReadOnlyList<DepreciationReportRowDto> Rows, decimal Cost, decimal AccumulatedDepreciation, decimal BookValue);

public sealed record AssetSettingsDto(string TagPrefix, string DefaultCurrency, bool AutoApproveHighConfidenceMatches, int WarrantyAlertDays);

public sealed record CategoryDto(AssetCategory Category, string Code, string DisplayName, bool EndpointManageable, int DefaultUsefulLifeMonths);
