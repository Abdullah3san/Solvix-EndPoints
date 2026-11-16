namespace Solvix.SharedKernel.Tenancy;

/// <summary>
/// A module's contribution to creating a new workspace (default data). Runs inside the registration transaction in
/// platform scope, with the new tenant id passed explicitly.
/// </summary>
public interface ITenantProvisioningStep
{
    Task ProvisionAsync(Guid tenantId, CancellationToken cancellationToken);
}
