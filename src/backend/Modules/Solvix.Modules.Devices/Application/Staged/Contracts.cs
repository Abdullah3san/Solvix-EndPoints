namespace Solvix.Modules.Devices.Application.Staged;

using Solvix.Modules.Devices.Domain;

/// <summary>Derived from the device table: no device with this hostname yet, an active one, or only a retired one.</summary>
public enum StagedComputerStatus
{
    Pending = 1,
    Enrolled = 2,
    Retired = 3,
}

public sealed record StagedComputerDto(
    Guid Id,
    string Hostname,
    Guid? BranchId,
    string? BranchName,
    Guid? DepartmentId,
    string? DepartmentName,
    StagedComputerSource Source,
    StagedComputerStatus Status,
    Guid? DeviceId,
    DateTimeOffset? EnrolledAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record StagedComputerListQuery(string? Search, StagedComputerStatus? Status, Guid? BranchId, int Page = 1, int PageSize = 25);

public sealed record StagedComputerSummaryDto(int Total, int Pending, int Enrolled, int Retired);

public sealed record CreateStagedComputerRequest(string Hostname, Guid? BranchId, Guid? DepartmentId);

/// <summary>Bulk add (paste or CSV): one hostname per entry, the same placement for all.</summary>
public sealed record ImportStagedComputersRequest(IReadOnlyList<string> Hostnames, Guid? BranchId, Guid? DepartmentId);

public sealed record ImportRejectedDto(string Value, string Reason);

public sealed record ImportStagedComputersResult(int Created, int AlreadyStaged, IReadOnlyList<ImportRejectedDto> Rejected);

public sealed record RemoveStagedComputersRequest(IReadOnlyList<Guid> Ids);
