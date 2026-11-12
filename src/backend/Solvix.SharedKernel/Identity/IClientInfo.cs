namespace Solvix.SharedKernel.Identity;

/// <summary>Network facts about the current caller, recorded on sessions and audit events.</summary>
public interface IClientInfo
{
    string? IpAddress { get; }

    string? UserAgent { get; }

    string? CorrelationId { get; }
}
