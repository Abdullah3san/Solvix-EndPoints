namespace Solvix.Modules.Devices.Application.Insights;

/// <summary>A labelled count for a chart. <see cref="Key"/> is stable (filterable); <see cref="Label"/> is for display.</summary>
public sealed record NamedCountDto(string Key, string Label, int Count);

public sealed record BranchFleetDto(Guid? BranchId, string Name, int Total, int Online, int Offline);

/// <summary>Breakdowns of the active fleet for the agent overview. Retired devices are excluded.</summary>
public sealed record DeviceOverviewDto(
    IReadOnlyList<NamedCountDto> Platforms,
    IReadOnlyList<NamedCountDto> OperatingSystems,
    IReadOnlyList<NamedCountDto> Health,
    IReadOnlyList<BranchFleetDto> Branches);

public enum AlertSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3,
}

public sealed record AlertDeviceDto(Guid Id, string Name);

/// <summary>
/// A condition that needs attention, derived from current data (nothing is stored).
/// <see cref="Devices"/> is a sample of the affected devices (at most 5); <see cref="Count"/> is the full count.
/// </summary>
public sealed record DeviceAlertDto(string Code, AlertSeverity Severity, string Title, string Message, int Count, IReadOnlyList<AlertDeviceDto> Devices);
