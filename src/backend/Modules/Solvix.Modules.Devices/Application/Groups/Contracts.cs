namespace Solvix.Modules.Devices.Application.Groups;

public sealed record DeviceGroupDto(Guid Id, string Name, string? Description, int DeviceCount, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record CreateDeviceGroupRequest(string Name, string? Description);

public sealed record UpdateDeviceGroupRequest(string Name, string? Description);

public sealed record AddGroupDevicesRequest(IReadOnlyList<Guid> DeviceIds);

public sealed record DeviceGroupListQuery(string? Search, int Page = 1, int PageSize = 50);
