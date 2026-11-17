namespace Solvix.Modules.Helpdesk.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;
using Solvix.SharedKernel.Tenancy;

/// <summary>Pure escalation decision, separated from persistence so it can be tested exhaustively.</summary>
public static class EscalationEvaluator
{
    public static double ElapsedPercent(DateTimeOffset created, DateTimeOffset due, DateTimeOffset now)
    {
        var window = (due - created).TotalSeconds;
        return window <= 0 ? 100 : (now - created).TotalSeconds / window * 100;
    }

    public static bool ShouldApply(EscalationRule rule, Ticket ticket, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(ticket);
        if (!rule.IsActive || !ticket.IsOpen || ticket.SlaPausedAtUtc is not null)
        {
            return false;
        }

        if (rule.MinPriority is { } min && ticket.Priority < min)
        {
            return false;
        }

        return rule.Trigger switch
        {
            EscalationTrigger.FirstResponseElapsedPercent =>
                ticket.FirstRespondedAtUtc is null && ticket.FirstResponseDueAtUtc is { } due
                && ElapsedPercent(ticket.CreatedAtUtc, due, now) >= rule.ThresholdPercent,
            EscalationTrigger.ResolutionElapsedPercent =>
                ticket.ResolutionDueAtUtc is { } resolutionDue
                && ElapsedPercent(ticket.CreatedAtUtc, resolutionDue, now) >= rule.ThresholdPercent,
            _ => false,
        };
    }
}

public interface IHelpdeskJobRunner
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Finds tenants with open SLA-bound tickets (the one cross-tenant read) and processes each in its own tenant scope.</summary>
internal sealed partial class HelpdeskJobRunner(IServiceScopeFactory rootScopes, ITenantScopeFactory tenantScopes, ILogger<HelpdeskJobRunner> logger) : IHelpdeskJobRunner
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenantIds;
        await using (var scope = rootScopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
            tenantIds = await db.Set<Ticket>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && (t.ResolutionDueAtUtc != null || t.FirstResponseDueAtUtc != null))
                .Select(t => t.TenantId).Distinct().ToListAsync(cancellationToken);
        }

        var processed = 0;
        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = tenantScopes.CreateScope(tenantId);
                processed += await scope.ServiceProvider.GetRequiredService<TenantEscalationJob>().ProcessAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex, tenantId);
            }
        }

        return processed;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Escalation job failed for tenant {TenantId}")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenantId);
}

internal sealed class TenantEscalationJob(ISolvixDbContext db, IClock clock)
{
    private const int BatchSize = 500;

    /// <summary>Returns the number of tickets changed.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var rules = await db.Set<EscalationRule>().AsNoTracking().Where(r => r.IsActive).ToListAsync(cancellationToken);
        var tickets = await db.Set<Ticket>()
            .Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && t.SlaPausedAtUtc == null)
            .OrderBy(t => t.ResolutionDueAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (tickets.Count == 0)
        {
            return 0;
        }

        var ticketIds = tickets.Select(t => t.Id).ToList();
        var applied = (await db.Set<AppliedEscalation>().AsNoTracking().Where(a => ticketIds.Contains(a.TicketId)).Select(a => new { a.TicketId, a.RuleId }).ToListAsync(cancellationToken))
            .Select(a => (a.TicketId, a.RuleId)).ToHashSet();
        var activeTechnicians = (await db.Set<Technician>().AsNoTracking().Where(t => t.IsActive).Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();

        var changed = 0;
        foreach (var ticket in tickets)
        {
            var touched = false;

            if (ticket.ResolutionDueAtUtc is { } resolutionDue && now > resolutionDue && ticket.MarkResolutionBreached())
            {
                db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.SlaBreached, null, "system", null, null, "resolution target missed", now));
                SolvixMetrics.SlaBreaches.Add(1, new KeyValuePair<string, object?>("kind", "resolution"));
                touched = true;
            }

            if (ticket.FirstRespondedAtUtc is null && ticket.FirstResponseDueAtUtc is { } responseDue && now > responseDue && ticket.MarkFirstResponseBreached())
            {
                db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.SlaBreached, null, "system", null, null, "first response target missed", now));
                SolvixMetrics.SlaBreaches.Add(1, new KeyValuePair<string, object?>("kind", "first_response"));
                touched = true;
            }

            foreach (var rule in rules.OrderBy(r => r.ThresholdPercent))
            {
                if (applied.Contains((ticket.Id, rule.Id)) || !EscalationEvaluator.ShouldApply(rule, ticket, now))
                {
                    continue;
                }

                Apply(rule, ticket, activeTechnicians, now);
                db.Set<AppliedEscalation>().Add(AppliedEscalation.Create(ticket.TenantId, ticket.Id, rule.Id, now));
                SolvixMetrics.TicketsEscalated.Add(1, new KeyValuePair<string, object?>("action", rule.Action.ToString()));
                touched = true;
            }

            if (touched)
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private void Apply(EscalationRule rule, Ticket ticket, HashSet<Guid> activeTechnicians, DateTimeOffset now)
    {
        var detail = $"rule '{rule.Name}' ({rule.Trigger} >= {rule.ThresholdPercent}%)";
        switch (rule.Action)
        {
            case EscalationAction.Reassign when rule.TargetTechnicianId is { } target && activeTechnicians.Contains(target):
                var previous = ticket.AssigneeTechnicianId;
                ticket.Assign(target, now);
                db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.Assigned, null, "escalation", previous?.ToString(), target.ToString(), detail, now));
                break;
            case EscalationAction.RaisePriority when ticket.Priority < TicketPriority.Critical:
                var before = ticket.Priority;
                ticket.SetPriority(before + 1, now);
                db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.PriorityChanged, null, "escalation", before.ToString(), ticket.Priority.ToString(), detail, now));
                break;
            default:
                break;
        }

        ticket.Escalate(now);
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.Escalated, null, "escalation", null, ticket.EscalationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), detail, now));
    }
}
