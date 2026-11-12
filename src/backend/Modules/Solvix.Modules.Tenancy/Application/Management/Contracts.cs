namespace Solvix.Modules.Tenancy.Application.Management;

using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Tenancy;

public sealed record TenantDto(Guid Id, string Name, string Slug, TenantStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public static TenantDto From(Tenant t) => new(t.Id, t.Name, t.Slug, t.Status, t.CreatedAtUtc, t.UpdatedAtUtc);
}

public sealed record UpdateTenantRequest(string Name);

public sealed record TenantOverviewDto(TenantDto Tenant, int MemberCount, int BranchCount, int DepartmentCount);
