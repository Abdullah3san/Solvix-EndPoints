namespace Solvix.Modules.EndpointManagement.Application;

/// <summary>Bound from <c>Solvix:EndpointManagement</c>.</summary>
public sealed class EndpointManagementOptions
{
    public const string SectionName = "Solvix:EndpointManagement";

    /// <summary>Runs the background scheduler (deployment state machine, lease expiry, restart tracking).</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int WorkerIntervalSeconds { get; set; } = 30;

    /// <summary>How long an agent may work on a job without reporting progress before it counts as interrupted.</summary>
    public int JobLeaseMinutes { get; set; } = 30;

    /// <summary>Base delay before retrying a failed/interrupted job (multiplied by the failure count).</summary>
    public int RetryDelayMinutes { get; set; } = 15;

    /// <summary>Minimum finished devices in the broad phase before the failure threshold can halt a deployment.</summary>
    public int MinimumSampleForHalt { get; set; } = 5;

    /// <summary>Restart requests the agent took but never confirmed are retried after this many minutes.</summary>
    public int RestartAckTimeoutMinutes { get; set; } = 60;

    /// <summary>An initiated restart without a new boot time after this many minutes is marked failed.</summary>
    public int RestartCompletionTimeoutMinutes { get; set; } = 180;

    public int AgentPollIntervalSeconds { get; set; } = 60;

    public TimeSpan JobLease => TimeSpan.FromMinutes(Math.Max(1, JobLeaseMinutes));

    public TimeSpan RetryDelay => TimeSpan.FromMinutes(Math.Max(1, RetryDelayMinutes));
}
