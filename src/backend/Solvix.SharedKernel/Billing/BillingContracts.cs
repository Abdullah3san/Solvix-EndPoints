namespace Solvix.SharedKernel.Billing;

/// <summary>
/// What a tenant's subscription currently allows. Implemented by the Billing module and consumed
/// by modules that must enforce plan limits on the server (never trust the client to do it).
/// </summary>
public interface IDeviceEntitlementProvider
{
    /// <summary>Evaluates the tenant's subscription right now. Fails closed: no subscription means not entitled.</summary>
    Task<DeviceEntitlement> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <param name="IsEntitled">False when the subscription is missing, expired, cancelled or suspended.</param>
/// <param name="DeviceLimit">Maximum number of billable (active) devices; meaningful when entitled.</param>
/// <param name="DenyCode">Stable machine-readable reason when not entitled.</param>
/// <param name="DenyMessage">Human readable reason when not entitled.</param>
public sealed record DeviceEntitlement(bool IsEntitled, int DeviceLimit, string? DenyCode = null, string? DenyMessage = null)
{
    public static DeviceEntitlement None(string code, string message) => new(false, 0, code, message);
}

/// <summary>Counts what a tenant is billed for. Implemented by the Devices module; always evaluated inside a tenant scope.</summary>
public interface IDeviceUsageReader
{
    /// <summary>Number of active (non-retired) devices of the ambient tenant.</summary>
    Task<int> CountBillableDevicesAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the initial (trial) subscription as part of tenant registration, inside its transaction.</summary>
public interface ISubscriptionProvisioner
{
    Task StartTrialAsync(Guid tenantId, CancellationToken cancellationToken);
}
