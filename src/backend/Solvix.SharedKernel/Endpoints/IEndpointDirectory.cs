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

    /// <summary>
    /// One device with the attributes access decisions need (branch, groups, state), or
    /// <c>null</c> when it does not exist in the current tenant.
    /// </summary>
    Task<EndpointAccessInfo?> FindForAccessAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Every device (active or not) that is listed in <paramref name="deviceIds"/>, a member of one of
    /// <paramref name="groupIds"/> or assigned to one of <paramref name="branchIds"/>, with its groups.
    /// Used to resolve deployment targets; bounded by <c>MaxBatch</c> devices.
    /// </summary>
    Task<IReadOnlyList<EndpointAccessInfo>> ResolveAsync(
        IReadOnlyCollection<Guid> deviceIds, IReadOnlyCollection<Guid> groupIds, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken);
}

/// <summary>A device as seen by authorization: who it belongs to organisationally and whether it can be reached.</summary>
public sealed record EndpointAccessInfo(EndpointSummary Endpoint, IReadOnlyList<Guid> GroupIds);

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
