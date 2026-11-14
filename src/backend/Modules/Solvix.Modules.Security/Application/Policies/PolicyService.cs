namespace Solvix.Modules.Security.Application.Policies;

using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IPolicyService
{
    Task<IReadOnlyList<PolicyDto>> ListAsync(CancellationToken cancellationToken);

    Task<PolicyDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<EffectivePolicyDto> GetEffectiveAsync(CancellationToken cancellationToken);

    Task<PolicyDto> CreateAsync(SavePolicyRequest request, CancellationToken cancellationToken);

    Task<PolicyDto> ReviseAsync(Guid id, CancellationToken cancellationToken);

    Task<PolicyDto> UpdateDraftAsync(Guid id, SavePolicyRequest request, CancellationToken cancellationToken);

    Task<PolicyDto> SubmitAsync(Guid id, CancellationToken cancellationToken);

    Task<PolicyDto> WithdrawAsync(Guid id, CancellationToken cancellationToken);

    Task<PolicyDto> ApproveAsync(Guid id, NoteRequest request, CancellationToken cancellationToken);

    Task<PolicyDto> RejectAsync(Guid id, NoteRequest request, CancellationToken cancellationToken);

    Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class PolicyService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IClock clock,
    IPolicyContext policyContext,
    IPostureProcessor processor,
    IAuditTrail audit) : IPolicyService
{
    public async Task<IReadOnlyList<PolicyDto>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Set<SecurityPolicy>().AsNoTracking().OrderByDescending(p => p.Version).Take(200).ToListAsync(cancellationToken))
        .Select(PolicyDto.From).ToList();

    public async Task<PolicyDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        PolicyDto.From(await FindAsync(id, cancellationToken, track: false));

    public async Task<EffectivePolicyDto> GetEffectiveAsync(CancellationToken cancellationToken)
    {
        var effective = await policyContext.GetAsync(cancellationToken);
        return new EffectivePolicyDto(effective.PolicyId, effective.Version, effective.Name, effective.IsBuiltIn, effective.Settings, effective.Advisories.Count);
    }

    public async Task<PolicyDto> CreateAsync(SavePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await NewDraftAsync(request.Name, request.Description, request.Settings, null, cancellationToken);
        audit.Record("policy.created", nameof(SecurityPolicy), policy.Id, $"Created draft policy '{policy.Name}' (v{policy.Version}).", new { policy.Version, settings = policy.Settings });
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task<PolicyDto> ReviseAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await FindAsync(id, cancellationToken, track: false);
        var policy = await NewDraftAsync(source.Name, source.Description, source.Settings, source.Id, cancellationToken);
        audit.Record("policy.revised", nameof(SecurityPolicy), policy.Id, $"Created draft v{policy.Version} from v{source.Version} of '{source.Name}'.", new { basedOn = source.Id });
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task<PolicyDto> UpdateDraftAsync(Guid id, SavePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await FindAsync(id, cancellationToken);
        if (policy.AuthorUserId != user.RequireUserId())
        {
            throw new ForbiddenException("Only the author can edit a draft policy. Create a new version to propose different settings.");
        }

        var before = policy.Settings;
        policy.UpdateDraft(request.Name, request.Description, request.Settings);
        audit.Record("policy.updated", nameof(SecurityPolicy), policy.Id, $"Edited draft policy '{policy.Name}' (v{policy.Version}).", new { before, after = policy.Settings });
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task<PolicyDto> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await FindAsync(id, cancellationToken);
        policy.Submit(user.RequireUserId(), clock.UtcNow);
        audit.Record(
            "policy.submitted", nameof(SecurityPolicy), policy.Id,
            $"Submitted policy '{policy.Name}' (v{policy.Version}) for approval. Enforcement: {policy.Settings.EnforcementMode}.",
            new { policy.Version, enforcement = policy.Settings.EnforcementMode.ToString(), actions = policy.Settings.AllowedRemediationActions });
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task<PolicyDto> WithdrawAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await FindAsync(id, cancellationToken);
        policy.Withdraw(user.RequireUserId());
        audit.Record("policy.withdrawn", nameof(SecurityPolicy), policy.Id, $"Withdrew the submission of '{policy.Name}' (v{policy.Version}).");
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task<PolicyDto> ApproveAsync(Guid id, NoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A policy decides what Solvix may do to devices, so only owners approve; the domain additionally
        // refuses the author and the submitter.
        if (user.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("Only a workspace owner can approve a security policy.");
        }

        var approver = user.RequireUserId();
        var policy = await FindAsync(id, cancellationToken);
        var now = clock.UtcNow;
        var current = await db.Set<SecurityPolicy>().FirstOrDefaultAsync(p => p.Status == PolicyStatus.Active, cancellationToken);

        // Validate before touching the active policy so a refused approval changes nothing.
        policy.EnsureApprovableBy(approver);

        // The "one active policy" index is not deferrable, so retire the old one first, in its own statement.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (current is not null)
            {
                current.Retire();
                await db.SaveChangesAsync(cancellationToken);
            }

            policy.Approve(approver, request.Note, now);
            audit.Record(
                "policy.approved", nameof(SecurityPolicy), policy.Id,
                $"Approved and activated policy '{policy.Name}' (v{policy.Version}); enforcement is now {policy.Settings.EnforcementMode}."
                + (current is null ? string.Empty : $" Retired v{current.Version}."),
                new { policy.Version, enforcement = policy.Settings.EnforcementMode.ToString(), actions = policy.Settings.AllowedRemediationActions, retired = current?.Id, request.Note });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        // Devices are re-assessed against the new policy immediately instead of waiting for their next report.
        policyContext.Invalidate();
        var reevaluated = await processor.ReevaluateAllAsync(cancellationToken);
        audit.Record("policy.applied", nameof(SecurityPolicy), policy.Id, $"Re-evaluated {reevaluated} device(s) against policy v{policy.Version}.");
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task<PolicyDto> RejectAsync(Guid id, NoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (user.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("Only a workspace owner can decide on a security policy.");
        }

        var policy = await FindAsync(id, cancellationToken);
        policy.Reject(user.RequireUserId(), request.Note ?? string.Empty, clock.UtcNow);
        audit.Record("policy.rejected", nameof(SecurityPolicy), policy.Id, $"Rejected policy '{policy.Name}' (v{policy.Version}).", new { request.Note });
        await SaveAsync(cancellationToken);
        return PolicyDto.From(policy);
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await FindAsync(id, cancellationToken);
        if (policy.Status is not (PolicyStatus.Draft or PolicyStatus.Rejected))
        {
            throw new ConflictException("Only draft or rejected policies can be deleted. Approved policies are kept for the record.");
        }

        if (policy.AuthorUserId != user.RequireUserId() && user.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("Only the author or an owner can delete this draft.");
        }

        db.Set<SecurityPolicy>().Remove(policy);
        audit.Record("policy.deleted", nameof(SecurityPolicy), policy.Id, $"Deleted {policy.Status} policy '{policy.Name}' (v{policy.Version}).");
        await SaveAsync(cancellationToken);
    }

    private async Task<SecurityPolicy> NewDraftAsync(string name, string? description, PolicySettings settings, Guid? basedOn, CancellationToken cancellationToken)
    {
        var nextVersion = (await db.Set<SecurityPolicy>().MaxAsync(p => (int?)p.Version, cancellationToken) ?? 0) + 1;
        var policy = new SecurityPolicy(tenant.RequireTenantId(), nextVersion, name, description, settings, user.RequireUserId(), basedOn);
        db.Set<SecurityPolicy>().Add(policy);
        return policy;
    }

    private async Task<SecurityPolicy> FindAsync(Guid id, CancellationToken cancellationToken, bool track = true)
    {
        var query = track ? db.Set<SecurityPolicy>() : db.Set<SecurityPolicy>().AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new NotFoundException("Policy", id);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The policy was changed by someone else at the same time. Reload and try again.");
        }
    }
}

public sealed class SavePolicyRequestValidator : AbstractValidator<SavePolicyRequest>
{
    public SavePolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(SecurityPolicy.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(SecurityPolicy.DescriptionMaxLength);
        RuleFor(x => x.Settings).NotNull();
    }
}

public sealed class NoteRequestValidator : AbstractValidator<NoteRequest>
{
    public NoteRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

