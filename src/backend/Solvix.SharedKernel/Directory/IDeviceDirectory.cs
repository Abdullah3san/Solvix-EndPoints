namespace Solvix.SharedKernel.Directory;

/// <summary>Read-only view of devices for modules that only need to reference or label them (e.g. tickets).</summary>
public interface IDeviceDirectory
{
    /// <summary>Resolves devices of the ambient tenant; ids of other tenants are simply absent.</summary>
    Task<IReadOnlyDictionary<Guid, DeviceSummary>> FindAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken);
}

public sealed record DeviceSummary(Guid Id, string Hostname, string? DisplayName);
