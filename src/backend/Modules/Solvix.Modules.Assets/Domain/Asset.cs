namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>Descriptive, purchase, warranty and depreciation data of an asset (everything an administrator edits directly).</summary>
public sealed record AssetDetails(
    string Name,
    AssetCategory Category,
    AssetCondition Condition,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Description,
    string? Location,
    Guid? VendorId,
    DateOnly? PurchaseDate,
    decimal? PurchaseCost,
    string? Currency,
    string? PurchaseOrderNumber,
    string? InvoiceNumber,
    DateOnly? WarrantyStartDate,
    DateOnly? WarrantyEndDate,
    string? WarrantyProvider,
    string? WarrantyReference,
    DepreciationMethod DepreciationMethod,
    int? UsefulLifeMonths,
    decimal? SalvageValue);

/// <summary>Who holds an asset: a workspace member, or an employee without a login (name + employee number).</summary>
public sealed record Assignee(Guid? UserId, string? Name, string? EmployeeNumber)
{
    public const int NameMaxLength = 200;
    public const int EmployeeNumberMaxLength = 50;

    public static Assignee Create(Guid? userId, string? name, string? employeeNumber)
    {
        var cleanName = Guard.Optional(name, "assigneeName", NameMaxLength);
        var number = Guard.Optional(employeeNumber, "employeeNumber", EmployeeNumberMaxLength);
        if (userId is null && cleanName is null)
        {
            throw new ValidationException("assignedUserId", "Choose a workspace member or enter the employee's name.");
        }

        return new Assignee(userId, userId is null ? cleanName : null, number);
    }
}

/// <summary>
/// A physical IT asset. Deliberately separate from the managed endpoint
/// (<c>devices</c>): an asset exists whether or not an agent is installed, and
/// the relation to an endpoint is an explicit, reviewed <see cref="AssetDeviceLink"/>.
/// </summary>
public sealed class Asset : TenantEntity
{
    public const int TagMaxLength = 40;
    public const int NameMaxLength = 200;
    public const int ShortTextMaxLength = 128;
    public const int DescriptionMaxLength = 4000;
    public const int LocationMaxLength = 200;
    public const int ReferenceMaxLength = 100;
    public const int ReasonMaxLength = 1000;
    public const decimal MaxAmount = 999_999_999_999.99m;

    private Asset()
    {
    }

    private Asset(Guid tenantId, string assetTag)
        : base(tenantId)
    {
        AssetTag = assetTag;
        Status = AssetStatus.InStock;
    }

    public string AssetTag { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public AssetCategory Category { get; private set; }

    public AssetStatus Status { get; private set; }

    public AssetCondition Condition { get; private set; }

    public string? Manufacturer { get; private set; }

    /// <summary><see cref="HardwareIdentity.NormalizeManufacturer"/>; part of the serial uniqueness key ("" when unknown).</summary>
    public string ManufacturerKey { get; private set; } = string.Empty;

    public string? Model { get; private set; }

    public string? SerialNumber { get; private set; }

    /// <summary><see cref="HardwareIdentity.NormalizeSerial"/>; null for missing or placeholder serials.</summary>
    public string? SerialNumberKey { get; private set; }

    public string? Description { get; private set; }

    public string? Location { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public Guid? AssignedUserId { get; private set; }

    public string? AssigneeName { get; private set; }

    public string? EmployeeNumber { get; private set; }

    public DateTimeOffset? AssignedAtUtc { get; private set; }

    public Guid? VendorId { get; private set; }

    public DateOnly? PurchaseDate { get; private set; }

    public decimal? PurchaseCost { get; private set; }

    public string? Currency { get; private set; }

    public string? PurchaseOrderNumber { get; private set; }

    public string? InvoiceNumber { get; private set; }

    public DateOnly? WarrantyStartDate { get; private set; }

    public DateOnly? WarrantyEndDate { get; private set; }

    public string? WarrantyProvider { get; private set; }

    public string? WarrantyReference { get; private set; }

    public DepreciationMethod DepreciationMethod { get; private set; }

    public int? UsefulLifeMonths { get; private set; }

    public decimal? SalvageValue { get; private set; }

    /// <summary>Status to return to when the open maintenance ends.</summary>
    public AssetStatus? StatusBeforeMaintenance { get; private set; }

    public DateTimeOffset? LastAuditedAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public string? RetirementReason { get; private set; }

    public DateTimeOffset? DisposedAtUtc { get; private set; }

    public DisposalMethod? DisposalMethod { get; private set; }

    public decimal? DisposalValue { get; private set; }

    public string? DisposalReference { get; private set; }

    public string? DisposalNotes { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>: optimistic concurrency token.</summary>
    public uint Version { get; private set; }

    public bool IsAssigned => AssignedUserId is not null || AssigneeName is not null;

    public static Asset Register(Guid tenantId, string assetTag, AssetDetails details, Guid? branchId, Guid? departmentId)
    {
        var asset = new Asset(tenantId, NormalizeTag(assetTag));
        asset.Update(details);
        asset.BranchId = branchId;
        asset.DepartmentId = departmentId;
        return asset;
    }

    public static string NormalizeTag(string tag)
    {
        var value = Guard.NotNullOrWhiteSpace(tag, "assetTag", TagMaxLength).ToUpperInvariant();
        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))
        {
            throw new ValidationException("assetTag", "The asset tag may only contain letters, digits and - _ . /");
        }

        return value;
    }

    public static WarrantyState WarrantyStateOn(DateOnly? warrantyEnd, DateOnly today, int expiringWithinDays = 60) =>
        warrantyEnd is not { } end ? WarrantyState.None
        : end < today ? WarrantyState.Expired
        : end <= today.AddDays(expiringWithinDays) ? WarrantyState.ExpiringSoon
        : WarrantyState.Active;

    public void Update(AssetDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        EnsureNotDisposed();
        if (!Enum.IsDefined(details.Category))
        {
            throw new ValidationException("category", "Unknown category.");
        }

        Name = Guard.NotNullOrWhiteSpace(details.Name, "name", NameMaxLength);
        Category = details.Category;
        Condition = Enum.IsDefined(details.Condition) ? details.Condition : throw new ValidationException("condition", "Unknown condition.");
        Manufacturer = Guard.Optional(details.Manufacturer, "manufacturer", ShortTextMaxLength);
        ManufacturerKey = HardwareIdentity.NormalizeManufacturer(Manufacturer) ?? string.Empty;
        Model = Guard.Optional(details.Model, "model", ShortTextMaxLength);
        SerialNumber = Guard.Optional(details.SerialNumber, "serialNumber", ShortTextMaxLength);
        SerialNumberKey = HardwareIdentity.NormalizeSerial(SerialNumber);
        Description = Guard.Optional(details.Description, "description", DescriptionMaxLength);
        Location = Guard.Optional(details.Location, "location", LocationMaxLength);
        VendorId = details.VendorId;
        PurchaseDate = details.PurchaseDate;
        PurchaseCost = Amount(details.PurchaseCost, "purchaseCost");
        Currency = NormalizeCurrency(details.Currency);
        PurchaseOrderNumber = Guard.Optional(details.PurchaseOrderNumber, "purchaseOrderNumber", ReferenceMaxLength);
        InvoiceNumber = Guard.Optional(details.InvoiceNumber, "invoiceNumber", ReferenceMaxLength);
        WarrantyStartDate = details.WarrantyStartDate;
        WarrantyEndDate = details.WarrantyEndDate;
        if (WarrantyStartDate is { } ws && WarrantyEndDate is { } we && we < ws)
        {
            throw new ValidationException("warrantyEndDate", "The warranty cannot end before it starts.");
        }

        WarrantyProvider = Guard.Optional(details.WarrantyProvider, "warrantyProvider", ShortTextMaxLength);
        WarrantyReference = Guard.Optional(details.WarrantyReference, "warrantyReference", ReferenceMaxLength);

        DepreciationMethod = Enum.IsDefined(details.DepreciationMethod) ? details.DepreciationMethod : throw new ValidationException("depreciationMethod", "Unknown method.");
        UsefulLifeMonths = details.UsefulLifeMonths;
        SalvageValue = Amount(details.SalvageValue, "salvageValue");
        if (DepreciationMethod != DepreciationMethod.None)
        {
            if (UsefulLifeMonths is not (>= 1 and <= 600))
            {
                throw new ValidationException("usefulLifeMonths", "Useful life must be between 1 and 600 months to depreciate the asset.");
            }

            if (PurchaseCost is null || PurchaseDate is null)
            {
                throw new ValidationException("purchaseCost", "Depreciation needs a purchase cost and purchase date.");
            }
        }

        if (SalvageValue is { } salvage && PurchaseCost is { } cost && salvage > cost)
        {
            throw new ValidationException("salvageValue", "The salvage value cannot exceed the purchase cost.");
        }
    }

    /// <summary>Checks the asset out to an employee. Re-assigning closes the previous assignment (caller records history).</summary>
    public void Assign(Assignee assignee, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assignee);
        if (Status is not (AssetStatus.InStock or AssetStatus.InUse))
        {
            throw new ConflictException($"An asset that is {Describe(Status)} cannot be assigned.", "asset_not_assignable");
        }

        AssignedUserId = assignee.UserId;
        AssigneeName = assignee.Name;
        EmployeeNumber = assignee.EmployeeNumber;
        AssignedAtUtc = now;
        Status = AssetStatus.InUse;
    }

    /// <summary>Checks the asset back in.</summary>
    public void Return(AssetCondition? condition)
    {
        if (!IsAssigned)
        {
            throw new ConflictException("The asset is not assigned to anyone.", "asset_not_assigned");
        }

        EnsureNotDisposed();
        ClearAssignment();
        if (condition is { } c)
        {
            Condition = c;
        }

        if (Status == AssetStatus.InUse)
        {
            Status = AssetStatus.InStock;
        }
        else if (Status == AssetStatus.InMaintenance && StatusBeforeMaintenance == AssetStatus.InUse)
        {
            StatusBeforeMaintenance = AssetStatus.InStock;
        }
    }

    public void Transfer(Guid? branchId, Guid? departmentId, string? location)
    {
        EnsureNotDisposed();
        var newLocation = Guard.Optional(location, "location", LocationMaxLength);
        if (branchId == BranchId && departmentId == DepartmentId && string.Equals(newLocation, Location, StringComparison.Ordinal))
        {
            throw new ConflictException("The asset is already at this branch, department and location.", "asset_transfer_no_change");
        }

        BranchId = branchId;
        DepartmentId = departmentId;
        Location = newLocation;
    }

    public void StartMaintenance()
    {
        if (Status is AssetStatus.Retired or AssetStatus.Disposed or AssetStatus.InMaintenance)
        {
            throw new ConflictException($"Maintenance cannot start while the asset is {Describe(Status)}.", "asset_maintenance_not_allowed");
        }

        StatusBeforeMaintenance = Status;
        Status = AssetStatus.InMaintenance;
    }

    public void EndMaintenance()
    {
        if (Status != AssetStatus.InMaintenance)
        {
            return;
        }

        Status = StatusBeforeMaintenance is AssetStatus.InUse && IsAssigned ? AssetStatus.InUse
            : StatusBeforeMaintenance is AssetStatus.Missing ? AssetStatus.Missing
            : IsAssigned ? AssetStatus.InUse : AssetStatus.InStock;
        StatusBeforeMaintenance = null;
    }

    public void MarkMissing()
    {
        if (Status is AssetStatus.Retired or AssetStatus.Disposed or AssetStatus.Missing)
        {
            throw new ConflictException($"An asset that is {Describe(Status)} cannot be marked missing.", "asset_status_conflict");
        }

        if (Status == AssetStatus.InMaintenance)
        {
            throw new ConflictException("Complete or cancel the open maintenance first.", "asset_maintenance_open");
        }

        Status = AssetStatus.Missing;
    }

    public void MarkFound(DateTimeOffset now)
    {
        if (Status != AssetStatus.Missing)
        {
            throw new ConflictException("The asset is not marked missing.", "asset_status_conflict");
        }

        Status = IsAssigned ? AssetStatus.InUse : AssetStatus.InStock;
        LastAuditedAtUtc = now;
    }

    public void ChangeCondition(AssetCondition condition)
    {
        EnsureNotDisposed();
        Condition = Enum.IsDefined(condition) ? condition : throw new ValidationException("condition", "Unknown condition.");
    }

    public void RecordAudit(DateTimeOffset now, AssetCondition? observedCondition)
    {
        LastAuditedAtUtc = now;
        if (observedCondition is { } c && Status != AssetStatus.Disposed)
        {
            Condition = c;
        }
    }

    /// <summary>Takes the asset out of service. Any open assignment is closed by the caller first.</summary>
    public void Retire(string reason, DateTimeOffset now)
    {
        if (Status is AssetStatus.Retired or AssetStatus.Disposed)
        {
            throw new ConflictException($"The asset is already {Describe(Status)}.", "asset_status_conflict");
        }

        if (Status == AssetStatus.InMaintenance)
        {
            throw new ConflictException("Complete or cancel the open maintenance before retiring the asset.", "asset_maintenance_open");
        }

        RetirementReason = Guard.NotNullOrWhiteSpace(reason, "reason", ReasonMaxLength);
        ClearAssignment();
        Status = AssetStatus.Retired;
        RetiredAtUtc = now;
    }

    /// <summary>Returns a retired (not disposed) asset to stock.</summary>
    public void Reinstate()
    {
        if (Status != AssetStatus.Retired)
        {
            throw new ConflictException("Only retired assets can be reinstated.", "asset_status_conflict");
        }

        Status = AssetStatus.InStock;
        RetiredAtUtc = null;
        RetirementReason = null;
    }

    /// <summary>Final step of the lifecycle; the record becomes read-only.</summary>
    public void RecordDisposal(DisposalMethod method, DateTimeOffset disposedAt, decimal? value, string? reference, string? notes)
    {
        if (Status != AssetStatus.Retired)
        {
            throw new ConflictException("Retire the asset before disposing of it.", "asset_not_retired");
        }

        if (!Enum.IsDefined(method))
        {
            throw new ValidationException("method", "Unknown disposal method.");
        }

        if (RetiredAtUtc is { } retired && disposedAt < retired.AddDays(-1))
        {
            throw new ValidationException("disposedAtUtc", "The disposal date cannot be before the asset was retired.");
        }

        DisposalMethod = method;
        DisposedAtUtc = disposedAt;
        DisposalValue = Amount(value, "value");
        DisposalReference = Guard.Optional(reference, "reference", ReferenceMaxLength);
        DisposalNotes = Guard.Optional(notes, "notes", ReasonMaxLength);
        Status = AssetStatus.Disposed;
    }

    public void EnsureNotDisposed()
    {
        if (Status == AssetStatus.Disposed)
        {
            throw new ConflictException("The asset has been disposed of; its record is read-only.", "asset_disposed");
        }
    }

    /// <summary>Flat view of the audited fields, used to compute change sets for the audit log.</summary>
    public IReadOnlyDictionary<string, string?> Snapshot() => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["assetTag"] = AssetTag,
        ["name"] = Name,
        ["category"] = Category.ToString(),
        ["status"] = Status.ToString(),
        ["condition"] = Condition.ToString(),
        ["manufacturer"] = Manufacturer,
        ["model"] = Model,
        ["serialNumber"] = SerialNumber,
        ["description"] = Description,
        ["location"] = Location,
        ["branchId"] = BranchId?.ToString(),
        ["departmentId"] = DepartmentId?.ToString(),
        ["assignedUserId"] = AssignedUserId?.ToString(),
        ["assigneeName"] = AssigneeName,
        ["employeeNumber"] = EmployeeNumber,
        ["vendorId"] = VendorId?.ToString(),
        ["purchaseDate"] = PurchaseDate?.ToString("O"),
        ["purchaseCost"] = PurchaseCost?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["currency"] = Currency,
        ["purchaseOrderNumber"] = PurchaseOrderNumber,
        ["invoiceNumber"] = InvoiceNumber,
        ["warrantyStartDate"] = WarrantyStartDate?.ToString("O"),
        ["warrantyEndDate"] = WarrantyEndDate?.ToString("O"),
        ["warrantyProvider"] = WarrantyProvider,
        ["warrantyReference"] = WarrantyReference,
        ["depreciationMethod"] = DepreciationMethod.ToString(),
        ["usefulLifeMonths"] = UsefulLifeMonths?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["salvageValue"] = SalvageValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["retirementReason"] = RetirementReason,
        ["disposalMethod"] = DisposalMethod?.ToString(),
        ["disposalValue"] = DisposalValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["disposalReference"] = DisposalReference,
    };

    internal static string Describe(AssetStatus status) => status switch
    {
        AssetStatus.InStock => "in stock",
        AssetStatus.InUse => "in use",
        AssetStatus.InMaintenance => "in maintenance",
        AssetStatus.Missing => "missing",
        AssetStatus.Retired => "retired",
        AssetStatus.Disposed => "disposed",
        _ => status.ToString(),
    };

    private static decimal? Amount(decimal? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (value < 0 || value > MaxAmount)
        {
            throw new ValidationException(name, $"{name} must be between 0 and {MaxAmount}.");
        }

        return decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static string? NormalizeCurrency(string? currency)
    {
        var value = Guard.Optional(currency, "currency", 3)?.ToUpperInvariant();
        if (value is not null && (value.Length != 3 || !value.All(char.IsAsciiLetterUpper)))
        {
            throw new ValidationException("currency", "Use a three-letter ISO 4217 currency code.");
        }

        return value;
    }

    private void ClearAssignment()
    {
        AssignedUserId = null;
        AssigneeName = null;
        EmployeeNumber = null;
        AssignedAtUtc = null;
    }
}
