namespace Solvix.Modules.Devices.Application.Enrollment;

using Solvix.Modules.Devices.Domain;

public sealed record DeploymentProfileDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? BranchId,
    string? BranchName,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? DeviceGroupId,
    string? DeviceGroupName,
    bool IsEnabled,
    int CodeVersion,
    int PendingCount,
    int EnrolledCount,
    DateTimeOffset? LastRequestAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record SaveDeploymentProfileRequest(string Name, string? Description, Guid? BranchId, Guid? DepartmentId, Guid? DeviceGroupId);

public sealed record PendingEnrollmentDto(
    Guid Id,
    Guid ProfileId,
    string? ProfileName,
    PendingEnrollmentStatus Status,
    string Hostname,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? OsName,
    string? OsVersion,
    string? AgentVersion,
    string? SourceAddress,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsExpired,
    string? BranchName,
    string? DepartmentName,
    string? DeviceGroupName,
    Guid? ExistingDeviceId,
    string? ExistingDeviceHostname,
    Guid? DeviceId,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote);

public sealed record PendingEnrollmentListQuery(PendingEnrollmentStatus? Status, Guid? ProfileId, int Page = 1, int PageSize = 50);

public sealed record DecideEnrollmentRequest(string? Note);

public sealed record BulkDecideEnrollmentsRequest(IReadOnlyList<Guid> Ids, string? Note);

public sealed record BulkDecisionResultDto(int Succeeded, IReadOnlyList<BulkDecisionFailureDto> Failed);

public sealed record BulkDecisionFailureDto(Guid Id, string Code, string Message);

public sealed record PendingEnrollmentCountDto(int Pending);
