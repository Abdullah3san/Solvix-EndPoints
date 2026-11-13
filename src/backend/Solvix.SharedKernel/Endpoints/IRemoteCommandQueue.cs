namespace Solvix.SharedKernel.Endpoints;

/// <summary>
/// Lets the agent heartbeat (Devices module) tell the agent that signed commands are waiting,
/// without depending on the remote management module. Implemented by RemoteManagement.
/// </summary>
public interface IRemoteCommandQueue
{
    /// <summary>Commands the device can fetch now (runs in the device's tenant scope).</summary>
    Task<int> CountPendingAsync(Guid deviceId, CancellationToken cancellationToken);
}
