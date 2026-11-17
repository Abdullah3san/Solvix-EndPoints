namespace Solvix.SharedKernel.Endpoints;

/// <summary>
/// Lets modules that target device groups (patch deployments, configuration policies) stop a group from
/// being deleted while it is in use, without the Devices module depending on them.
/// </summary>
public interface IDeviceGroupUsageCheck
{
    /// <summary>Why the group cannot be deleted, or null when this module does not use it.</summary>
    Task<string?> DescribeUsageAsync(Guid deviceGroupId, CancellationToken cancellationToken);
}
