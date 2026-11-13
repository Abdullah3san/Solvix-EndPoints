namespace Solvix.Modules.Devices.Application.Devices;

using Solvix.Modules.Devices.Domain;

public sealed record DeviceDto(
    Guid Id,
    string Hostname,
    string? DnsDomain,
    string OsName,
    string OsVersion,
    string OsBuild,
    string AgentVersion,
    Guid? BranchId,
    Guid? DepartmentId,
    DeviceStatus Status,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? LastBootUtc,
    IReadOnlyList<Guid> GroupIds)
{
    public static DeviceDto From(Device d, IReadOnlyList<Guid> groupIds) => new(
        d.Id, d.Hostname, d.DnsDomain, d.OsName, d.OsVersion, d.OsBuild, d.AgentVersion,
        d.BranchId, d.DepartmentId, d.Status, d.EnrolledAtUtc, d.LastSeenAtUtc, d.LastBootUtc, groupIds);
}

public sealed record DeviceListQuery(
    string? Search,
    Guid? BranchId,
    Guid? DepartmentId,
    Guid? GroupId,
    bool IncludeRetired = false,
    int Page = 1,
    int PageSize = 50);

public sealed record PlaceDeviceRequest(Guid? BranchId, Guid? DepartmentId);

public sealed record DeviceGroupDto(Guid Id, string Name, string? Description, int MemberCount, DateTimeOffset CreatedAtUtc);

public sealed record SaveDeviceGroupRequest(string Name, string? Description);

public sealed record DeviceGroupMembersRequest(IReadOnlyList<Guid> DeviceIds);

public sealed record EnrollmentTokenDto(
    Guid Id,
    string Name,
    DateTimeOffset ExpiresAtUtc,
    int? MaxUses,
    int UseCount,
    bool IsRevoked,
    Guid? DefaultBranchId,
    Guid? DefaultDepartmentId,
    Guid? DefaultDeviceGroupId,
    DateTimeOffset CreatedAtUtc)
{
    public static EnrollmentTokenDto From(EnrollmentToken t) => new(
        t.Id, t.Name, t.ExpiresAtUtc, t.MaxUses, t.UseCount, t.RevokedAtUtc is not null,
        t.DefaultBranchId, t.DefaultDepartmentId, t.DefaultDeviceGroupId, t.CreatedAtUtc);
}

public sealed record CreateEnrollmentTokenRequest(
    string Name,
    int ValidForHours,
    int? MaxUses,
    Guid? DefaultBranchId,
    Guid? DefaultDepartmentId,
    Guid? DefaultDeviceGroupId);

/// <summary><see cref="Token"/> is returned only once, at creation.</summary>
public sealed record CreatedEnrollmentTokenDto(EnrollmentTokenDto Info, string Token);
