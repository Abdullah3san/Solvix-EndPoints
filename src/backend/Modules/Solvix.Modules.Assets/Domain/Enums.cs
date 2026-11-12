namespace Solvix.Modules.Assets.Domain;

/// <summary>Lifecycle state. Registered → in stock ⇄ in use; maintenance and missing are temporary; retired → disposed is the end.</summary>
public enum AssetStatus
{
    InStock = 1,
    InUse = 2,
    InMaintenance = 3,
    Missing = 4,
    Retired = 5,
    Disposed = 6,
}

public enum AssetCondition
{
    New = 1,
    Good = 2,
    Fair = 3,
    Poor = 4,
    Damaged = 5,
}

public enum DepreciationMethod
{
    None = 0,
    StraightLine = 1,

    /// <summary>Double-declining balance, written down to the salvage value in the final month.</summary>
    DecliningBalance = 2,
}

public enum DisposalMethod
{
    Recycled = 1,
    Sold = 2,
    Donated = 3,
    Destroyed = 4,
    ReturnedToVendor = 5,
    TradedIn = 6,
    Lost = 7,
}

public enum WarrantyState
{
    None = 0,
    Active = 1,
    ExpiringSoon = 2,
    Expired = 3,
}

public enum MaintenanceType
{
    Preventive = 1,
    Repair = 2,
    Upgrade = 3,
    Inspection = 4,
    WarrantyClaim = 5,
}

public enum MaintenanceStatus
{
    Scheduled = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
}

public enum PhysicalAuditStatus
{
    InProgress = 1,
    Completed = 2,
    Cancelled = 3,
}

public enum PhysicalAuditResult
{
    Pending = 1,
    Found = 2,
    Missing = 3,

    /// <summary>Scanned during the audit but not expected in its scope (registered elsewhere).</summary>
    Unexpected = 4,
}

public enum AssetDeviceLinkStatus
{
    /// <summary>Proposed by the matcher; waiting for an administrator.</summary>
    Suggested = 1,
    Approved = 2,
    Rejected = 3,

    /// <summary>Another link was approved for the asset or the device.</summary>
    Superseded = 4,

    /// <summary>Was approved, later removed by an administrator.</summary>
    Unlinked = 5,
}

public enum AssetDeviceLinkSource
{
    Automatic = 1,
    Manual = 2,
}

public enum MatchConfidence
{
    Low = 1,
    Medium = 2,
    High = 3,
}
