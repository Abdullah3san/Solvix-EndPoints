namespace Solvix.Modules.Assets.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Assets.Domain;

internal static class Money
{
    public const string ColumnType = "numeric(14,2)";
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets", t =>
        {
            t.HasCheckConstraint("ck_assets_amounts_non_negative", "(purchase_cost IS NULL OR purchase_cost >= 0) AND (salvage_value IS NULL OR salvage_value >= 0) AND (disposal_value IS NULL OR disposal_value >= 0)");
            t.HasCheckConstraint("ck_assets_salvage_within_cost", "salvage_value IS NULL OR purchase_cost IS NULL OR salvage_value <= purchase_cost");
            t.HasCheckConstraint("ck_assets_warranty_period", "warranty_start_date IS NULL OR warranty_end_date IS NULL OR warranty_end_date >= warranty_start_date");
            t.HasCheckConstraint("ck_assets_useful_life", "useful_life_months IS NULL OR useful_life_months BETWEEN 1 AND 600");
            t.HasCheckConstraint("ck_assets_depreciation_inputs", "depreciation_method = 'None' OR (useful_life_months IS NOT NULL AND purchase_cost IS NOT NULL AND purchase_date IS NOT NULL)");
            t.HasCheckConstraint("ck_assets_in_use_is_assigned", "status <> 'InUse' OR assigned_user_id IS NOT NULL OR assignee_name IS NOT NULL");
            t.HasCheckConstraint("ck_assets_single_assignee", "assigned_user_id IS NULL OR assignee_name IS NULL");
            t.HasCheckConstraint("ck_assets_retired_has_date", "status NOT IN ('Retired', 'Disposed') OR retired_at_utc IS NOT NULL");
            t.HasCheckConstraint("ck_assets_disposed_has_details", "status <> 'Disposed' OR (disposed_at_utc IS NOT NULL AND disposal_method IS NOT NULL)");
            t.HasCheckConstraint("ck_assets_tag_format", "asset_tag = upper(asset_tag) AND length(asset_tag) BETWEEN 1 AND 40");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.AssetTag).HasMaxLength(Asset.TagMaxLength).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(Asset.NameMaxLength).IsRequired();
        builder.Property(a => a.Category).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Condition).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Manufacturer).HasMaxLength(Asset.ShortTextMaxLength);
        builder.Property(a => a.ManufacturerKey).HasMaxLength(Asset.ShortTextMaxLength).IsRequired();
        builder.Property(a => a.Model).HasMaxLength(Asset.ShortTextMaxLength);
        builder.Property(a => a.SerialNumber).HasMaxLength(Asset.ShortTextMaxLength);
        builder.Property(a => a.SerialNumberKey).HasMaxLength(Asset.ShortTextMaxLength);
        builder.Property(a => a.Description).HasMaxLength(Asset.DescriptionMaxLength);
        builder.Property(a => a.Location).HasMaxLength(Asset.LocationMaxLength);
        builder.Property(a => a.AssigneeName).HasMaxLength(Assignee.NameMaxLength);
        builder.Property(a => a.EmployeeNumber).HasMaxLength(Assignee.EmployeeNumberMaxLength);
        builder.Property(a => a.PurchaseCost).HasColumnType(Money.ColumnType);
        builder.Property(a => a.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(a => a.PurchaseOrderNumber).HasMaxLength(Asset.ReferenceMaxLength);
        builder.Property(a => a.InvoiceNumber).HasMaxLength(Asset.ReferenceMaxLength);
        builder.Property(a => a.WarrantyProvider).HasMaxLength(Asset.ShortTextMaxLength);
        builder.Property(a => a.WarrantyReference).HasMaxLength(Asset.ReferenceMaxLength);
        builder.Property(a => a.DepreciationMethod).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.SalvageValue).HasColumnType(Money.ColumnType);
        builder.Property(a => a.StatusBeforeMaintenance).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.RetirementReason).HasMaxLength(Asset.ReasonMaxLength);
        builder.Property(a => a.DisposalMethod).HasConversion<string>().HasMaxLength(24);
        builder.Property(a => a.DisposalValue).HasColumnType(Money.ColumnType);
        builder.Property(a => a.DisposalReference).HasMaxLength(Asset.ReferenceMaxLength);
        builder.Property(a => a.DisposalNotes).HasMaxLength(Asset.ReasonMaxLength);
        builder.Property(a => a.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.Ignore(a => a.IsAssigned);

        // Children reference assets through (tenant_id, id): the database guarantees they never cross tenants.
        builder.HasAlternateKey(a => new { a.TenantId, a.Id });

        builder.HasOne<Vendor>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.VendorId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.TenantId, a.AssetTag }).IsUnique().HasDatabaseName("ux_assets_tenant_id_asset_tag");

        // Duplicate serial numbers: one asset per (manufacturer, serial) and tenant. Different manufacturers may
        // legitimately share a serial string; such pairs are flagged as ambiguous by the matcher instead.
        builder.HasIndex(a => new { a.TenantId, a.ManufacturerKey, a.SerialNumberKey })
            .IsUnique()
            .HasFilter("serial_number_key IS NOT NULL")
            .HasDatabaseName("ux_assets_tenant_id_manufacturer_key_serial_number_key");
        builder.HasIndex(a => new { a.TenantId, a.SerialNumberKey }).HasFilter("serial_number_key IS NOT NULL");
        builder.HasIndex(a => new { a.TenantId, a.Status, a.Category });
        builder.HasIndex(a => new { a.TenantId, a.Category });
        builder.HasIndex(a => new { a.TenantId, a.AssignedUserId }).HasFilter("assigned_user_id IS NOT NULL");
        builder.HasIndex(a => new { a.TenantId, a.WarrantyEndDate }).HasFilter("warranty_end_date IS NOT NULL");
        builder.HasIndex(a => new { a.TenantId, a.VendorId }).HasFilter("vendor_id IS NOT NULL");
    }
}

internal sealed class AssetAssignmentConfiguration : IEntityTypeConfiguration<AssetAssignment>
{
    public void Configure(EntityTypeBuilder<AssetAssignment> builder)
    {
        builder.ToTable("asset_assignments", t =>
        {
            t.HasCheckConstraint("ck_asset_assignments_assignee", "assigned_user_id IS NOT NULL OR assignee_name IS NOT NULL");
            t.HasCheckConstraint("ck_asset_assignments_period", "returned_at_utc IS NULL OR returned_at_utc >= assigned_at_utc");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.AssigneeName).HasMaxLength(Assignee.NameMaxLength);
        builder.Property(a => a.EmployeeNumber).HasMaxLength(Assignee.EmployeeNumberMaxLength);
        builder.Property(a => a.Notes).HasMaxLength(AssetAssignment.NotesMaxLength);
        builder.Property(a => a.ReturnNotes).HasMaxLength(AssetAssignment.NotesMaxLength);
        builder.Property(a => a.ReturnCondition).HasConversion<string>().HasMaxLength(16);
        builder.HasOne<Asset>().WithMany().HasForeignKey(a => new { a.TenantId, a.AssetId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);

        // At most one open assignment per asset.
        builder.HasIndex(a => new { a.TenantId, a.AssetId }).IsUnique().HasFilter("returned_at_utc IS NULL").HasDatabaseName("ux_asset_assignments_open");
        builder.HasIndex(a => new { a.TenantId, a.AssetId, a.AssignedAtUtc });
        builder.HasIndex(a => new { a.TenantId, a.AssignedUserId }).HasFilter("assigned_user_id IS NOT NULL");
    }
}

internal sealed class AssetTransferConfiguration : IEntityTypeConfiguration<AssetTransfer>
{
    public void Configure(EntityTypeBuilder<AssetTransfer> builder)
    {
        builder.ToTable("asset_transfers");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.FromLocation).HasMaxLength(Asset.LocationMaxLength);
        builder.Property(t => t.ToLocation).HasMaxLength(Asset.LocationMaxLength);
        builder.Property(t => t.Reason).HasMaxLength(Asset.ReasonMaxLength);
        builder.HasOne<Asset>().WithMany().HasForeignKey(t => new { t.TenantId, t.AssetId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.TenantId, t.AssetId, t.TransferredAtUtc });
    }
}

internal sealed class MaintenanceRecordConfiguration : IEntityTypeConfiguration<MaintenanceRecord>
{
    public void Configure(EntityTypeBuilder<MaintenanceRecord> builder)
    {
        builder.ToTable("asset_maintenance_records", t => t.HasCheckConstraint("ck_asset_maintenance_records_cost", "cost IS NULL OR cost >= 0"));
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.Title).HasMaxLength(MaintenanceRecord.TitleMaxLength).IsRequired();
        builder.Property(m => m.Description).HasMaxLength(MaintenanceRecord.TextMaxLength);
        builder.Property(m => m.Resolution).HasMaxLength(MaintenanceRecord.TextMaxLength);
        builder.Property(m => m.PerformedBy).HasMaxLength(Asset.ShortTextMaxLength);
        builder.Property(m => m.Cost).HasColumnType(Money.ColumnType);
        builder.Ignore(m => m.IsOpen);
        builder.HasOne<Asset>().WithMany().HasForeignKey(m => new { m.TenantId, m.AssetId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Vendor>().WithMany().HasForeignKey(m => new { m.TenantId, m.VendorId }).HasPrincipalKey(v => new { v.TenantId, v.Id }).OnDelete(DeleteBehavior.Restrict);

        // One piece of work in progress per asset.
        builder.HasIndex(m => new { m.TenantId, m.AssetId }).IsUnique().HasFilter("status = 'InProgress'").HasDatabaseName("ux_asset_maintenance_records_in_progress");
        builder.HasIndex(m => new { m.TenantId, m.AssetId, m.CreatedAtUtc });
        builder.HasIndex(m => new { m.TenantId, m.Status, m.ScheduledFor });
    }
}

internal sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> builder)
    {
        builder.ToTable("asset_vendors");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Name).HasMaxLength(Vendor.NameMaxLength).IsRequired();
        builder.Property(v => v.NormalizedName).HasMaxLength(Vendor.NameMaxLength).IsRequired();
        builder.Property(v => v.ContactName).HasMaxLength(Vendor.TextMaxLength);
        builder.Property(v => v.Email).HasMaxLength(Vendor.TextMaxLength);
        builder.Property(v => v.Phone).HasMaxLength(50);
        builder.Property(v => v.Website).HasMaxLength(Vendor.TextMaxLength);
        builder.Property(v => v.AccountNumber).HasMaxLength(100);
        builder.Property(v => v.Notes).HasMaxLength(Vendor.NotesMaxLength);
        builder.HasAlternateKey(v => new { v.TenantId, v.Id });
        builder.HasIndex(v => new { v.TenantId, v.NormalizedName }).IsUnique().HasDatabaseName("ux_asset_vendors_tenant_id_normalized_name");
    }
}

internal sealed class AssetSettingsConfiguration : IEntityTypeConfiguration<AssetSettings>
{
    public void Configure(EntityTypeBuilder<AssetSettings> builder)
    {
        builder.ToTable("asset_settings", t => t.HasCheckConstraint("ck_asset_settings_warranty_alert_days", "warranty_alert_days BETWEEN 1 AND 365"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.TagPrefix).HasMaxLength(10).IsRequired();
        builder.Property(s => s.DefaultCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.HasIndex(s => s.TenantId).IsUnique();
    }
}

internal sealed class AssetTagSequenceConfiguration : IEntityTypeConfiguration<AssetTagSequence>
{
    public void Configure(EntityTypeBuilder<AssetTagSequence> builder)
    {
        builder.ToTable("asset_tag_sequences", t => t.HasCheckConstraint("ck_asset_tag_sequences_positive", "last_value >= 0"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Scope).HasMaxLength(32).IsRequired();
        builder.HasIndex(s => new { s.TenantId, s.Scope }).IsUnique();
    }
}

internal sealed class PhysicalAuditConfiguration : IEntityTypeConfiguration<PhysicalAudit>
{
    public void Configure(EntityTypeBuilder<PhysicalAudit> builder)
    {
        builder.ToTable("asset_physical_audits");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).HasMaxLength(PhysicalAudit.NameMaxLength).IsRequired();
        builder.Property(a => a.Category).HasConversion<string>().HasMaxLength(24);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(PhysicalAudit.NotesMaxLength);
        builder.HasAlternateKey(a => new { a.TenantId, a.Id });
        builder.HasIndex(a => new { a.TenantId, a.Status, a.StartedAtUtc });
    }
}

internal sealed class PhysicalAuditItemConfiguration : IEntityTypeConfiguration<PhysicalAuditItem>
{
    public void Configure(EntityTypeBuilder<PhysicalAuditItem> builder)
    {
        builder.ToTable("asset_physical_audit_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Result).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.ExpectedLocation).HasMaxLength(Asset.LocationMaxLength);
        builder.Property(i => i.ObservedLocation).HasMaxLength(Asset.LocationMaxLength);
        builder.Property(i => i.ObservedCondition).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.Notes).HasMaxLength(PhysicalAuditItem.NotesMaxLength);
        builder.HasOne<PhysicalAudit>().WithMany().HasForeignKey(i => new { i.TenantId, i.PhysicalAuditId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Asset>().WithMany().HasForeignKey(i => new { i.TenantId, i.AssetId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => new { i.TenantId, i.PhysicalAuditId, i.AssetId }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.PhysicalAuditId, i.Result });
    }
}

internal sealed class AssetDeviceLinkConfiguration : IEntityTypeConfiguration<AssetDeviceLink>
{
    public void Configure(EntityTypeBuilder<AssetDeviceLink> builder)
    {
        builder.ToTable("asset_device_links");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.Confidence).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.EvidenceJson).HasColumnType("jsonb").IsRequired();
        builder.Property(l => l.DecisionNote).HasMaxLength(AssetDeviceLink.NoteMaxLength);
        builder.HasOne<Asset>().WithMany().HasForeignKey(l => new { l.TenantId, l.AssetId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);

        // The device FK (tenant_id, device_id) → devices crosses modules and is declared in the security migration.
        builder.HasIndex(l => new { l.TenantId, l.AssetId, l.DeviceId }).IsUnique();
        builder.HasIndex(l => new { l.TenantId, l.AssetId }).IsUnique().HasFilter("status = 'Approved'").HasDatabaseName("ux_asset_device_links_approved_asset");
        builder.HasIndex(l => new { l.TenantId, l.DeviceId }).IsUnique().HasFilter("status = 'Approved'").HasDatabaseName("ux_asset_device_links_approved_device");
        builder.HasIndex(l => new { l.TenantId, l.Status, l.DetectedAtUtc });
        builder.HasIndex(l => new { l.TenantId, l.DeviceId });
    }
}

internal sealed class AssetAuditLogEntryConfiguration : IEntityTypeConfiguration<AssetAuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AssetAuditLogEntry> builder)
    {
        builder.ToTable("asset_audit_log");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EntityType).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(AssetAuditLogEntry.ActionMaxLength).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(AssetAuditLogEntry.SummaryMaxLength).IsRequired();
        builder.Property(e => e.ChangesJson).HasColumnType("jsonb");
        builder.Property(e => e.ActorKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(128);

        // No FK to assets on purpose: the log outlives the records it describes.
        builder.HasIndex(e => new { e.TenantId, e.AssetId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId });
    }
}
