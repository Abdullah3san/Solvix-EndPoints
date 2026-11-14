namespace Solvix.Modules.Devices.Application.Enrollment;

using Solvix.Modules.Devices.Domain;

public sealed record EnrollmentTokenDto(
    Guid Id,
    string Name,
    EnrollmentTokenState State,
    DateTimeOffset ExpiresAtUtc,
    int MaxUses,
    int UseCount,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    Guid? DeviceGroupId,
    string? DeviceGroupName,
    Guid? BranchId,
    string? BranchName,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc);

/// <summary>Returned once on creation. <see cref="Token"/> is never retrievable again.</summary>
public sealed record CreatedEnrollmentTokenDto(EnrollmentTokenDto EnrollmentToken, string Token);

public sealed record CreateEnrollmentTokenRequest(string Name, int? LifetimeHours, int? MaxUses, Guid? DeviceGroupId, Guid? BranchId = null, Guid? DepartmentId = null);

public sealed record EnrollmentTokenListQuery(bool IncludeInactive = false, int Page = 1, int PageSize = 50);
