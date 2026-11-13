namespace Solvix.Modules.RemoteManagement.Authorization;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.CommandBroker;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record RemoteAccessPolicyDto(
    IReadOnlyList<string> ApprovalRequiredCommands,
    int CommandTtlMinutes,
    int ApprovalTtlMinutes,
    string TimeZoneId,
    int? AllowedFromMinute,
    int? AllowedToMinute,
    int AllowedDaysMask,
    IReadOnlyList<string> AvailableCommands)
{
    internal static RemoteAccessPolicyDto From(RemoteAccessPolicy p) => new(
        p.ApprovalRequiredCommands, p.CommandTtlMinutes, p.ApprovalTtlMinutes, p.TimeZoneId, p.AllowedFromMinute, p.AllowedToMinute,
        p.AllowedDaysMask, CommandDefinitions.All.Select(d => d.Type).Order(StringComparer.Ordinal).ToList());
}

public sealed record UpdateRemoteAccessPolicyRequest(
    IReadOnlyList<string> ApprovalRequiredCommands,
    int CommandTtlMinutes,
    int ApprovalTtlMinutes,
    string TimeZoneId,
    int? AllowedFromMinute,
    int? AllowedToMinute,
    int AllowedDaysMask);

public sealed record RemoteAccessScopeDto(Guid UserId, IReadOnlyList<Guid>? DeviceGroupIds, IReadOnlyList<Guid>? BranchIds);

public sealed record UpdateRemoteAccessScopeRequest(IReadOnlyList<Guid>? DeviceGroupIds, IReadOnlyList<Guid>? BranchIds);

public interface IRemoteAccessAdministration
{
    Task<RemoteAccessPolicyDto> GetPolicyAsync(CancellationToken cancellationToken);

    Task<RemoteAccessPolicyDto> UpdatePolicyAsync(UpdateRemoteAccessPolicyRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteAccessScopeDto>> ListScopesAsync(CancellationToken cancellationToken);

    Task<RemoteAccessScopeDto> SetScopeAsync(Guid userId, UpdateRemoteAccessScopeRequest request, CancellationToken cancellationToken);

    Task RemoveScopeAsync(Guid userId, CancellationToken cancellationToken);
}

internal sealed class RemoteAccessAdministration(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IMemberDirectory members,
    RemoteAccessPolicyProvider policies,
    RemoteAuditTrail audit) : IRemoteAccessAdministration
{
    public async Task<RemoteAccessPolicyDto> GetPolicyAsync(CancellationToken cancellationToken) =>
        RemoteAccessPolicyDto.From(await policies.GetAsync(cancellationToken));

    public async Task<RemoteAccessPolicyDto> UpdatePolicyAsync(UpdateRemoteAccessPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        return await audit.AtomicAsync(
            async ct =>
            {
                var policy = await db.Set<RemoteAccessPolicy>().FirstOrDefaultAsync(ct);
                if (policy is null)
                {
                    policy = RemoteAccessPolicy.CreateDefault(tenantContext.RequireTenantId());
                    db.Set<RemoteAccessPolicy>().Add(policy);
                }

                policy.Update(request.ApprovalRequiredCommands, request.CommandTtlMinutes, request.ApprovalTtlMinutes, request.TimeZoneId.Trim(),
                    request.AllowedFromMinute, request.AllowedToMinute, request.AllowedDaysMask);
                audit.Record(new RemoteAuditEntry(RemoteAuditEvents.PolicyChanged, RemoteAuditOutcome.Success,
                    Detail: $"approval=[{string.Join(',', policy.ApprovalRequiredCommands)}]; ttl={policy.CommandTtlMinutes}; approvalTtl={policy.ApprovalTtlMinutes}; tz={policy.TimeZoneId}; window={policy.AllowedFromMinute}-{policy.AllowedToMinute}; days={policy.AllowedDaysMask}"));
                return RemoteAccessPolicyDto.From(policy);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteAccessScopeDto>> ListScopesAsync(CancellationToken cancellationToken) =>
        (await db.Set<RemoteAccessScope>().AsNoTracking().ToListAsync(cancellationToken))
            .Select(s => new RemoteAccessScopeDto(s.UserId, s.DeviceGroupIds, s.BranchIds))
            .ToList();

    public async Task<RemoteAccessScopeDto> SetScopeAsync(Guid userId, UpdateRemoteAccessScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceGroupIds is { Count: > 200 } || request.BranchIds is { Count: > 200 })
        {
            throw new ValidationException("scope", "At most 200 groups and 200 branches.");
        }

        // Only members of this tenant can be scoped (a user of another tenant is "not found").
        if (!(await members.FindAsync([userId], cancellationToken)).ContainsKey(userId))
        {
            throw new NotFoundException("User", userId);
        }

        return await audit.AtomicAsync(
            async ct =>
            {
                var scope = await db.Set<RemoteAccessScope>().FirstOrDefaultAsync(s => s.UserId == userId, ct);
                if (scope is null)
                {
                    scope = RemoteAccessScope.Create(tenantContext.RequireTenantId(), userId);
                    db.Set<RemoteAccessScope>().Add(scope);
                }

                scope.Update(request.DeviceGroupIds, request.BranchIds);
                audit.Record(new RemoteAuditEntry(RemoteAuditEvents.ScopeChanged, RemoteAuditOutcome.Success,
                    Detail: $"user={userId:D}; groups={Format(scope.DeviceGroupIds)}; branches={Format(scope.BranchIds)}"));
                return new RemoteAccessScopeDto(scope.UserId, scope.DeviceGroupIds, scope.BranchIds);
            },
            cancellationToken);
    }

    public async Task RemoveScopeAsync(Guid userId, CancellationToken cancellationToken) =>
        await audit.AtomicAsync(
            async ct =>
            {
                var scope = await db.Set<RemoteAccessScope>().FirstOrDefaultAsync(s => s.UserId == userId, ct)
                    ?? throw new NotFoundException("Scope", userId);
                db.Set<RemoteAccessScope>().Remove(scope);
                audit.Record(new RemoteAuditEntry(RemoteAuditEvents.ScopeChanged, RemoteAuditOutcome.Success, Detail: $"user={userId:D}; unrestricted"));
                return true;
            },
            cancellationToken);

    private static void Validate(UpdateRemoteAccessPolicyRequest r)
    {
        if (r.ApprovalRequiredCommands is null || r.ApprovalRequiredCommands.Any(c => !CommandDefinitions.IsKnown(c)))
        {
            throw new ValidationException(nameof(r.ApprovalRequiredCommands), "Only known operations can require approval.");
        }

        if (r.CommandTtlMinutes is < RemoteAccessPolicy.MinCommandTtlMinutes or > RemoteAccessPolicy.MaxCommandTtlMinutes
            || r.ApprovalTtlMinutes is < RemoteAccessPolicy.MinCommandTtlMinutes or > RemoteAccessPolicy.MaxCommandTtlMinutes)
        {
            throw new ValidationException(nameof(r.CommandTtlMinutes), "Lifetimes must be between 5 minutes and 7 days.");
        }

        if (string.IsNullOrWhiteSpace(r.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZoneId.Trim(), out _))
        {
            throw new ValidationException(nameof(r.TimeZoneId), "Unknown time zone.");
        }

        if ((r.AllowedFromMinute is null) != (r.AllowedToMinute is null)
            || r.AllowedFromMinute is < 0 or > 1439
            || r.AllowedToMinute is < 0 or > 1440
            || (r.AllowedFromMinute is not null && r.AllowedFromMinute == r.AllowedToMinute))
        {
            throw new ValidationException(nameof(r.AllowedFromMinute), "Give both ends of the allowed window (minutes after midnight), or neither.");
        }

        if (r.AllowedDaysMask is < 1 or > 0b111_1111)
        {
            throw new ValidationException(nameof(r.AllowedDaysMask), "Allow at least one day.");
        }
    }

    private static string Format(List<Guid>? ids) => ids is null ? "*" : $"[{string.Join(',', ids)}]";
}
