namespace Solvix.Modules.Tenancy.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// The account / isolation boundary. This is a platform-level table (it is
/// not itself tenant-owned); every tenant-owned table references it.
/// </summary>
public sealed class Tenant : Entity
{
    public const int NameMaxLength = 200;

    private Tenant()
    {
    }

    private Tenant(string name, string slug)
    {
        Name = name;
        Slug = slug;
        Status = TenantStatus.Active;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Immutable, globally unique, URL-safe identifier.</summary>
    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public DateTimeOffset? SuspendedAtUtc { get; private set; }

    public static Tenant Create(string name, string slug) =>
        new(Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), SharedKernel.Tenancy.Slug.Normalize(slug));

    public void Rename(string name) => Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);

    public void Suspend(DateTimeOffset at)
    {
        Status = TenantStatus.Suspended;
        SuspendedAtUtc = at;
    }

    public void Reactivate()
    {
        Status = TenantStatus.Active;
        SuspendedAtUtc = null;
    }

    public TenantDescriptor ToDescriptor() => new(Id, Slug, Name, Status);
}
