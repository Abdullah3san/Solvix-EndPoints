namespace Solvix.Modules.Organization.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Organization.Domain;

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).HasMaxLength(Department.NameMaxLength).IsRequired();
        builder.Property(d => d.Code).HasMaxLength(Department.CodeMaxLength).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(Department.DescriptionMaxLength);
        builder.Property(d => d.IsActive).IsRequired();

        builder.HasAlternateKey(d => new { d.TenantId, d.Id });

        // Tenant-safe foreign keys: the referenced row must carry the same tenant_id.
        builder.HasOne(d => d.Branch)
            .WithMany()
            .HasForeignKey(d => new { d.TenantId, d.BranchId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.ParentDepartment)
            .WithMany()
            .HasForeignKey(d => new { d.TenantId, d.ParentDepartmentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.TenantId, d.BranchId, d.Code }).IsUnique();
        builder.HasIndex(d => new { d.TenantId, d.ParentDepartmentId });
    }
}
