namespace Solvix.UnitTests.Domain;

using Solvix.Modules.Identity.Domain;
using Solvix.Modules.Organization.Domain;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Tenancy;

public sealed class EntityTests
{
    [Fact]
    public void Tenant_entities_cannot_change_tenant_once_assigned()
    {
        var tenant = Guid.NewGuid();
        var branch = Branch.Create(tenant, "HQ", "hq");
        Assert.Equal(tenant, branch.TenantId);
        Assert.Equal("HQ", branch.Code);
        branch.AssignTenant(tenant); // same tenant is a no-op
        Assert.Throws<InvalidOperationException>(() => branch.AssignTenant(Guid.NewGuid()));
    }

    [Fact]
    public void Ids_are_version7_guids()
    {
        var tenant = Tenant.Create("Acme", "acme");
        Assert.NotEqual(Guid.Empty, tenant.Id);
        Assert.Equal(7, tenant.Id.Version);
    }

    [Fact]
    public void Tenant_normalises_slug_and_starts_active()
    {
        var tenant = Tenant.Create(" Acme ", " ACME-Corp ");
        Assert.Equal("Acme", tenant.Name);
        Assert.Equal("acme-corp", tenant.Slug);
        Assert.Equal(TenantStatus.Active, tenant.Status);

        tenant.Suspend(DateTimeOffset.UtcNow);
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.False(tenant.ToDescriptor().IsActive);
    }

    [Fact]
    public void Tenant_rejects_blank_or_overlong_names()
    {
        Assert.Throws<ValidationException>(() => Tenant.Create("  ", "acme"));
        Assert.Throws<ValidationException>(() => Tenant.Create(new string('x', Tenant.NameMaxLength + 1), "acme"));
    }

    [Fact]
    public void User_normalises_email_for_uniqueness()
    {
        var user = User.Create(" Owner@Acme.io ", "Owner", "hash");
        Assert.Equal("Owner@Acme.io", user.Email);
        Assert.Equal("OWNER@ACME.IO", user.NormalizedEmail);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void Department_cannot_be_its_own_parent()
    {
        var tenant = Guid.NewGuid();
        var department = Department.Create(tenant, Guid.NewGuid(), null, "Engineering", "eng");
        Assert.Equal("ENG", department.Code);
        Assert.Throws<ValidationException>(() => department.MoveUnder(department.Id));
    }

    [Fact]
    public void Address_validates_country_code()
    {
        var address = Address.Create(" 1 Main St ", null, "Springfield", null, "12345", "us");
        Assert.Equal("1 Main St", address.Line1);
        Assert.Equal("US", address.CountryCode);
        Assert.False(address.IsEmpty);
        Assert.True(Address.Empty.IsEmpty);
        Assert.Throws<ValidationException>(() => Address.Create(null, null, null, null, null, "USA"));
    }

    [Fact]
    public void Company_profile_update_trims_and_bounds_fields()
    {
        var profile = CompanyProfile.Create(Guid.NewGuid(), "Acme");
        profile.Update(" Acme Inc ", " Acme ", null, null, "Software", "https://acme.io", null, null, "Europe/Berlin", Address.Empty);
        Assert.Equal("Acme Inc", profile.LegalName);
        Assert.Equal("Acme", profile.TradeName);
        Assert.Equal("Europe/Berlin", profile.TimeZone);
        Assert.Throws<ValidationException>(() =>
            profile.Update(string.Empty, null, null, null, null, null, null, null, "UTC", Address.Empty));
    }
}
