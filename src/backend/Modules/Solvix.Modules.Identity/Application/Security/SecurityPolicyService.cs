namespace Solvix.Modules.Identity.Application.Security;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>The tenant's security policy plus the platform floor it cannot go below, for display.</summary>
public sealed record SecurityPolicyDto(SecurityPolicySettings Policy, SecurityPolicySettings Baseline, DateTimeOffset UpdatedAtUtc);

public interface ISecurityPolicyService
{
    Task<SecurityPolicyDto> GetAsync(CancellationToken cancellationToken);

    Task<SecurityPolicyDto> UpdateAsync(SecurityPolicySettings settings, CancellationToken cancellationToken);
}

internal sealed class SecurityPolicyService(
    ISolvixDbContext db,
    IAccessCache accessCache,
    IAuthAuditLog audit,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IOptions<IdentitySecurityOptions> options) : ISecurityPolicyService
{
    private readonly SecurityPolicySettings _baseline = options.Value.Baseline();

    public async Task<SecurityPolicyDto> GetAsync(CancellationToken cancellationToken)
    {
        var policy = await LoadOrCreateAsync(cancellationToken);
        return new SecurityPolicyDto(policy.ToSettings(), _baseline, policy.UpdatedAtUtc);
    }

    public async Task<SecurityPolicyDto> UpdateAsync(SecurityPolicySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var policy = await LoadOrCreateAsync(cancellationToken);
        var before = policy.ToSettings();
        policy.Apply(settings, _baseline);

        audit.Record(
            AuditEvents.SecurityPolicyUpdated,
            AuditOutcome.Success,
            policy.TenantId,
            detail: Describe(before, policy.ToSettings()),
            actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);

        // New rules apply to the next request (MFA requirement) and the next sign-in (session lifetimes).
        await accessCache.InvalidatePolicyAsync(policy.TenantId, cancellationToken);
        return new SecurityPolicyDto(policy.ToSettings(), _baseline, policy.UpdatedAtUtc);
    }

    private async Task<TenantSecurityPolicy> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var policy = await db.Set<TenantSecurityPolicy>().FirstOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            policy = TenantSecurityPolicy.CreateDefault(tenantId, _baseline);
            db.Set<TenantSecurityPolicy>().Add(policy);
            await db.SaveChangesAsync(cancellationToken);
        }

        return policy;
    }

    private static string Describe(SecurityPolicySettings before, SecurityPolicySettings after)
    {
        var changes = new List<string>();
        void Compare<T>(string name, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
            {
                changes.Add($"{name}:{a}->{b}");
            }
        }

        Compare("passwordMinLength", before.PasswordMinLength, after.PasswordMinLength);
        Compare("passwordRequireUppercase", before.PasswordRequireUppercase, after.PasswordRequireUppercase);
        Compare("passwordRequireLowercase", before.PasswordRequireLowercase, after.PasswordRequireLowercase);
        Compare("passwordRequireDigit", before.PasswordRequireDigit, after.PasswordRequireDigit);
        Compare("passwordRequireSymbol", before.PasswordRequireSymbol, after.PasswordRequireSymbol);
        Compare("passwordHistoryCount", before.PasswordHistoryCount, after.PasswordHistoryCount);
        Compare("passwordMaxAgeDays", before.PasswordMaxAgeDays, after.PasswordMaxAgeDays);
        Compare("sessionIdleTimeoutMinutes", before.SessionIdleTimeoutMinutes, after.SessionIdleTimeoutMinutes);
        Compare("sessionAbsoluteLifetimeHours", before.SessionAbsoluteLifetimeHours, after.SessionAbsoluteLifetimeHours);
        Compare("requireMfa", before.RequireMfa, after.RequireMfa);
        return changes.Count == 0 ? "no_changes" : string.Join(" ", changes);
    }
}
