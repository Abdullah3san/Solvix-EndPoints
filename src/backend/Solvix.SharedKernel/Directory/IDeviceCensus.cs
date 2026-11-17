namespace Solvix.SharedKernel.Directory;

/// <summary>
/// Device counts per tenant for provider-level reporting. Must be called
/// inside an explicit platform scope: it deliberately reads across tenants.
/// </summary>
public interface IDeviceCensus
{
    /// <summary>Number of active (not retired) devices for each of <paramref name="tenantIds"/>.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountActiveAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);
}
