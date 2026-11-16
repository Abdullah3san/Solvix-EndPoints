namespace Solvix.Modules.Billing.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Billing.Domain;

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Code).HasMaxLength(32).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Ignore(p => p.IsCustom);
        builder.HasIndex(p => p.Code).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("ck_plans_device_limit_positive", "device_limit IS NULL OR device_limit > 0"));
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.PlanCode).HasMaxLength(32).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.Interval).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.PendingInterval).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.PendingPlanCode).HasMaxLength(32);
        builder.Property(s => s.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(s => s.SuspensionReason).HasMaxLength(500);
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);

        // One subscription per tenant: enforced by the database, not by convention.
        builder.HasIndex(s => s.TenantId).IsUnique();
        builder.HasIndex(s => s.CurrentPeriodEndUtc);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_subscriptions_device_limit_positive", "device_limit > 0");
            t.HasCheckConstraint("ck_subscriptions_tax_rate", "tax_rate_basis_points IS NULL OR (tax_rate_basis_points >= 0 AND tax_rate_basis_points <= 10000)");
        });
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Number).HasMaxLength(32).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.Purpose).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(i => i.TermsPlanCode).HasMaxLength(32).IsRequired();
        builder.Property(i => i.TermsInterval).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.BillToName).HasMaxLength(200);
        builder.Property(i => i.BillToEmail).HasMaxLength(254);
        builder.Property(i => i.BillToAddress).HasMaxLength(1000);
        builder.Property(i => i.BillToTaxId).HasMaxLength(50);
        builder.Property(i => i.ProviderName).HasMaxLength(32);
        builder.Property(i => i.ProviderCheckoutId).HasMaxLength(255);
        builder.Property(i => i.PaymentReference).HasMaxLength(255);
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasAlternateKey(i => new { i.TenantId, i.Id });
        builder.HasIndex(i => new { i.TenantId, i.Number }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.IssuedAtUtc });
        builder.HasIndex(i => i.ProviderCheckoutId);

        // At most one live renewal invoice per period: makes renewal generation idempotent at the database level.
        builder.HasIndex(i => new { i.TenantId, i.PeriodStartUtc })
            .IsUnique()
            .HasFilter("purpose = 'Renewal' AND status <> 'Void'")
            .HasDatabaseName("ux_invoices_one_renewal_per_period");

        builder.HasMany(i => i.Lines)
            .WithOne()
            .HasForeignKey(l => new { l.TenantId, l.InvoiceId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.ToTable(t => t.HasCheckConstraint("ck_invoices_amounts", "subtotal_minor >= 0 AND tax_minor >= 0 AND total_minor = subtotal_minor + tax_minor"));
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("invoice_lines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Description).HasMaxLength(500).IsRequired();
        builder.HasIndex(l => new { l.TenantId, l.InvoiceId, l.Position }).IsUnique();
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Provider).HasMaxLength(32).IsRequired();
        builder.Property(p => p.ProviderReference).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.FailureReason).HasMaxLength(300);

        builder.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.InvoiceId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Global (not per-tenant) uniqueness: a replayed provider reference can never be recorded twice.
        builder.HasIndex(p => new { p.Provider, p.ProviderReference, p.Status }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.OccurredAtUtc });
    }
}

internal sealed class BillingProfileConfiguration : IEntityTypeConfiguration<BillingProfile>
{
    public void Configure(EntityTypeBuilder<BillingProfile> builder)
    {
        builder.ToTable("billing_profiles");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.LegalName).HasMaxLength(BillingProfile.TextMaxLength).IsRequired();
        builder.Property(p => p.BillingEmail).HasMaxLength(254).IsRequired();
        builder.Property(p => p.AddressLine1).HasMaxLength(BillingProfile.TextMaxLength);
        builder.Property(p => p.AddressLine2).HasMaxLength(BillingProfile.TextMaxLength);
        builder.Property(p => p.City).HasMaxLength(100);
        builder.Property(p => p.Region).HasMaxLength(100);
        builder.Property(p => p.PostalCode).HasMaxLength(20);
        builder.Property(p => p.CountryCode).HasMaxLength(2).IsFixedLength();
        builder.Property(p => p.TaxId).HasMaxLength(50);
        builder.HasIndex(p => p.TenantId).IsUnique();
    }
}

internal sealed class SubscriptionEventConfiguration : IEntityTypeConfiguration<SubscriptionEvent>
{
    public void Configure(EntityTypeBuilder<SubscriptionEvent> builder)
    {
        builder.ToTable("subscription_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.Actor).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(1000);
        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
    }
}

internal sealed class UsageSnapshotConfiguration : IEntityTypeConfiguration<UsageSnapshot>
{
    public void Configure(EntityTypeBuilder<UsageSnapshot> builder)
    {
        builder.ToTable("usage_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Day).HasColumnType("date");
        builder.HasIndex(s => new { s.TenantId, s.Day }).IsUnique();
    }
}

internal sealed class InvoiceCounterConfiguration : IEntityTypeConfiguration<InvoiceCounter>
{
    public void Configure(EntityTypeBuilder<InvoiceCounter> builder)
    {
        builder.ToTable("invoice_counters");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.TenantId).IsUnique();
    }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("billing_webhook_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Provider).HasMaxLength(32).IsRequired();
        builder.Property(e => e.EventId).HasMaxLength(255).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(32).IsRequired();
        builder.HasIndex(e => new { e.Provider, e.EventId }).IsUnique();
    }
}
