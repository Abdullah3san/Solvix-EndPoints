namespace Solvix.SharedKernel.Auditing;

/// <summary>
/// Append-only, tenant-scoped audit history. <see cref="Record"/> only stages the
/// entry in the current unit of work: it is committed by the caller's next
/// <c>SaveChangesAsync</c>, atomically with the change it describes, so an
/// audited action can never succeed without its audit entry (or vice versa).
/// The actor (user, agent or system) and correlation id are taken from the
/// ambient request or job.
/// </summary>
public interface IAuditTrail
{
    void Record(string action, string entityType, Guid? entityId, string summary, object? details = null);
}

public enum AuditActorType
{
    System = 1,
    User = 2,
    Agent = 3,
}
