namespace Solvix.Modules.Devices.Application.Devices;

using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Domain;

public enum DeviceConnectivity
{
    Online = 1,
    Offline = 2,
}

public sealed record DeviceSummaryDto(
    Guid Id,
    string Hostname,
    string? DisplayName,
    DevicePlatform Platform,
    DeviceStatus Status,
    DeviceConnectivity Connectivity,
    string? OsName,
    string? OsVersion,
    string? OsDisplayVersion,
    string? OsBuild,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string AgentVersion,
    bool AgentUpdateAvailable,
    AgentHealthStatus? HealthStatus,
    IReadOnlyList<string> IpAddresses,
    Guid? AssignedUserId,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastInventoryAtUtc,
    DateTimeOffset EnrolledAtUtc);

public sealed record DeviceDetailsDto
{
    public required DeviceSummaryDto Summary { get; init; }

    public string? Notes { get; init; }

    public string? HardwareId { get; init; }

    public string? DomainName { get; init; }

    public string? Architecture { get; init; }

    public string? ProcessorName { get; init; }

    public long? TotalMemoryBytes { get; init; }

    public DateTimeOffset? LastBootAtUtc { get; init; }

    public required IReadOnlyList<string> MacAddresses { get; init; }

    public DateTimeOffset? AgentVersionChangedAtUtc { get; init; }

    public AgentHealthReport? Health { get; init; }

    public required IReadOnlyList<DeviceGroupRefDto> Groups { get; init; }

    public required DeviceAssignmentDto Assignment { get; init; }

    public required IReadOnlyList<DeviceCredentialDto> Credentials { get; init; }

    public DateTimeOffset LastEnrolledAtUtc { get; init; }

    public int EnrollmentCount { get; init; }

    public bool InventoryRequested { get; init; }

    public DateTimeOffset? RetiredAtUtc { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record DeviceGroupRefDto(Guid Id, string Name);

public sealed record DeviceAssignmentDto(AssignedUserDto? User, AssignedUnitDto? Branch, AssignedUnitDto? Department);

/// <summary>Assigned user; <see cref="IsMember"/> is false when the user has since left the workspace.</summary>
public sealed record AssignedUserDto(Guid Id, string? DisplayName, string? Email, bool IsMember);

public sealed record AssignedUnitDto(Guid Id, string? Name, string? Code);

public sealed record DeviceCredentialDto(
    Guid Id,
    string Thumbprint,
    string SerialNumber,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? FirstUsedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    CredentialRevocationReason? RevocationReason)
{
    public static DeviceCredentialDto From(DeviceCredential c) =>
        new(c.Id, c.Thumbprint, c.SerialNumber, c.NotBeforeUtc, c.NotAfterUtc, c.CreatedAtUtc, c.FirstUsedAtUtc, c.RevokedAtUtc, c.RevocationReason);
}

public sealed record DeviceInventoryDto(Guid DeviceId, DateTimeOffset CollectedAtUtc, DateTimeOffset ReceivedAtUtc, string? ContentHash, InventoryReport Report);

public sealed record DeviceListQuery(
    string? Search,
    DeviceStatus? Status,
    DeviceConnectivity? Connectivity,
    Guid? GroupId,
    string? AgentVersion,
    Guid? AssignedUserId,
    int Page = 1,
    int PageSize = 25);

public sealed record UpdateDeviceRequest(string? DisplayName, string? Notes);

public sealed record UpdateDeviceAssignmentRequest(Guid? AssignedUserId, Guid? BranchId, Guid? DepartmentId);

public sealed record SetDeviceGroupsRequest(IReadOnlyList<Guid> GroupIds);

public sealed record DeviceStatsDto(
    int Total,
    int Active,
    int Online,
    int Offline,
    int Retired,
    int UpdateAvailable,
    string? LatestAgentVersion,
    IReadOnlyList<AgentVersionCountDto> AgentVersions);

public sealed record AgentVersionCountDto(string Version, int DeviceCount, bool IsLatest, bool UpdateAvailable);
