namespace Solvix.Modules.RemoteManagement.CommandBroker;

using System.Collections.Concurrent;

/// <summary>
/// Wakes a waiting agent poll when a command is queued for its device. In-process only: with
/// several API nodes a poll held by another node returns at its timeout (≤ 30 s) and finds the
/// command on its next request, so delivery is delayed but never lost.
/// </summary>
public sealed class CommandSignal
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waiters = new();

    public void Notify(Guid deviceId)
    {
        if (_waiters.TryRemove(deviceId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>Returns when <see cref="Notify"/> is called for the device or the timeout elapses.</summary>
    public async Task WaitAsync(Guid deviceId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = _waiters.GetOrAdd(deviceId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await waiter.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Nothing arrived; the caller looks again.
        }
    }
}
