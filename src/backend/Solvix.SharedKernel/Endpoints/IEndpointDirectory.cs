namespace Solvix.SharedKernel.Endpoints;

/// <summary>
/// Read-only view of the managed endpoints (enrolled devices) of the current
/// tenant, for modules that relate their own records to devices without
/// depending on the Devices module. Implemented by Devices; tenant-scoped like
/// any query (other tenants' devices are never returned).
/// </summary>
public interface IEndpointDirectory
{
    Task<IReadOnlyDictionary<Guid, EndpointSummary>> FindAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken);

    /// <summary>Devices whose normalised serial number (<see cref="HardwareIdentity.NormalizeSerial"/>) is one of <paramref name="serialKeys"/>.</summary>
    Task<IReadOnlyList<EndpointSummary>> FindBySerialKeysAsync(IReadOnlyCollection<string> serialKeys, CancellationToken cancellationToken);

    /// <summary>Pages through devices (hostname order), optionally filtered by a search term and excluding <paramref name="excludedDeviceIds"/>.</summary>
    Task<EndpointPage> SearchAsync(string? search, IReadOnlyCollection<Guid> excludedDeviceIds, bool activeOnly, int skip, int take, CancellationToken cancellationToken);

    Task<int> CountActiveAsync(CancellationToken cancellationToken);
}

public sealed record EndpointPage(IReadOnlyList<EndpointSummary> Items, long TotalCount);

/// <summary>Identity and state of a managed endpoint as needed by other modules.</summary>
public sealed record EndpointSummary(
    Guid Id,
    string Hostname,
    string? DisplayName,
    bool IsActive,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string? OsName,
    string Connectivity,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset EnrolledAtUtc,
    Guid? AssignedUserId,
    Guid? BranchId,
    Guid? DepartmentId);

public enum EndpointIdentityChangeKind
{
    Enrolled = 1,
    ReEnrolled = 2,

    /// <summary>The serial number, manufacturer or model reported by the agent changed.</summary>
    HardwareChanged = 3,
}

/// <summary>
/// Notified (after the change is committed, inside the device's tenant scope)
/// whenever an endpoint's hardware identity becomes known or changes. Observers
/// must never make enrollment fail: the publisher logs and swallows their errors.
/// </summary>
public interface IEndpointIdentityObserver
{
    Task OnEndpointIdentityChangedAsync(Guid deviceId, EndpointIdentityChangeKind kind, CancellationToken cancellationToken);
}
