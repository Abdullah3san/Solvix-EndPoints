namespace Solvix.Agent.Core.Communication;

/// <summary>Tracks connectivity to the server for backoff decisions and health reporting.</summary>
public sealed class ConnectionMonitor(TimeProvider time)
{
    private readonly Lock _gate = new();

    public int ConsecutiveFailures { get; private set; }

    public int ConsecutiveAuthenticationFailures { get; private set; }

    public DateTimeOffset? LastSuccessAtUtc { get; private set; }

    public DateTimeOffset? DisconnectedSinceUtc { get; private set; }

    public string? LastError { get; private set; }

    public bool IsConnected => ConsecutiveFailures == 0 && LastSuccessAtUtc is not null;

    /// <summary>Records success; returns the number of failures that preceded it (0 if none).</summary>
    public int RecordSuccess()
    {
        lock (_gate)
        {
            var previous = ConsecutiveFailures;
            ConsecutiveFailures = 0;
            ConsecutiveAuthenticationFailures = 0;
            DisconnectedSinceUtc = null;
            LastError = null;
            LastSuccessAtUtc = time.GetUtcNow();
            return previous;
        }
    }

    public void RecordFailure(AgentApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            ConsecutiveFailures++;
            if (exception.Failure == AgentApiFailure.Authentication)
            {
                ConsecutiveAuthenticationFailures++;
            }

            DisconnectedSinceUtc ??= time.GetUtcNow();
            LastError = exception.Message;
        }
    }
}
