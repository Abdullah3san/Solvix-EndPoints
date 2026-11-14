namespace Solvix.Modules.Security.Application.Policies;

using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;
using Severity = Solvix.Modules.Security.Domain.Severity;

public interface IAdvisoryService
{
    Task<IReadOnlyList<AdvisoryDto>> ListAsync(CancellationToken cancellationToken);

    Task<AdvisoryDto> CreateAsync(SaveAdvisoryRequest request, CancellationToken cancellationToken);

    Task<AdvisoryDto> UpdateAsync(Guid id, SaveAdvisoryRequest request, CancellationToken cancellationToken);

    Task<AdvisoryDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class AdvisoryService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IPolicyContext policyContext,
    IPostureProcessor processor,
    IAuditTrail audit) : IAdvisoryService
{
    public const int MaxAdvisories = 1000;

    public async Task<IReadOnlyList<AdvisoryDto>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Set<SoftwareAdvisory>().AsNoTracking().OrderBy(a => a.Title).ToListAsync(cancellationToken)).Select(AdvisoryDto.From).ToList();

    public async Task<AdvisoryDto> CreateAsync(SaveAdvisoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await db.Set<SoftwareAdvisory>().CountAsync(cancellationToken) >= MaxAdvisories)
        {
            throw new ConflictException($"A workspace can keep at most {MaxAdvisories} advisories.");
        }

        var advisory = new SoftwareAdvisory(
            tenant.RequireTenantId(), request.Title, request.ProductPattern, request.Publisher, request.AffectedFromVersion,
            request.FixedVersion, request.Severity, request.Reference, user.RequireUserId());
        db.Set<SoftwareAdvisory>().Add(advisory);
        audit.Record("advisory.created", nameof(SoftwareAdvisory), advisory.Id, $"Added vulnerability advisory '{advisory.Title}' ({advisory.Severity}).", Snapshot(advisory));
        await db.SaveChangesAsync(cancellationToken);
        await ReapplyAsync(advisory.Id, cancellationToken);
        return AdvisoryDto.From(advisory);
    }

    public async Task<AdvisoryDto> UpdateAsync(Guid id, SaveAdvisoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var advisory = await FindAsync(id, cancellationToken);
        var before = Snapshot(advisory);
        advisory.Update(request.Title, request.ProductPattern, request.Publisher, request.AffectedFromVersion, request.FixedVersion, request.Severity, request.Reference);
        audit.Record("advisory.updated", nameof(SoftwareAdvisory), advisory.Id, $"Changed vulnerability advisory '{advisory.Title}'.", new { before, after = Snapshot(advisory) });
        await db.SaveChangesAsync(cancellationToken);
        await ReapplyAsync(advisory.Id, cancellationToken);
        return AdvisoryDto.From(advisory);
    }

    public async Task<AdvisoryDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var advisory = await FindAsync(id, cancellationToken);
        advisory.SetEnabled(enabled);
        audit.Record(enabled ? "advisory.enabled" : "advisory.disabled", nameof(SoftwareAdvisory), advisory.Id, $"{(enabled ? "Enabled" : "Disabled")} advisory '{advisory.Title}'.");
        await db.SaveChangesAsync(cancellationToken);
        await ReapplyAsync(advisory.Id, cancellationToken);
        return AdvisoryDto.From(advisory);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var advisory = await FindAsync(id, cancellationToken);
        db.Set<SoftwareAdvisory>().Remove(advisory);
        audit.Record("advisory.deleted", nameof(SoftwareAdvisory), advisory.Id, $"Deleted advisory '{advisory.Title}'.", Snapshot(advisory));
        await db.SaveChangesAsync(cancellationToken);
        await ReapplyAsync(advisory.Id, cancellationToken);
    }

    /// <summary>Changing the advisory list changes every device's result, so devices are re-assessed straight away.</summary>
    private async Task ReapplyAsync(Guid advisoryId, CancellationToken cancellationToken)
    {
        policyContext.Invalidate();
        var count = await processor.ReevaluateAllAsync(cancellationToken);
        audit.Record("advisory.applied", nameof(SoftwareAdvisory), advisoryId, $"Re-evaluated {count} device(s) after the advisory list changed.");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SoftwareAdvisory> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<SoftwareAdvisory>().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw new NotFoundException("Advisory", id);

    private static object Snapshot(SoftwareAdvisory a) => new
    {
        a.Title,
        a.ProductPattern,
        a.Publisher,
        a.AffectedFromVersion,
        a.FixedVersion,
        severity = a.Severity.ToString(),
        a.Reference,
        a.IsEnabled,
    };
}

public sealed class SaveAdvisoryRequestValidator : AbstractValidator<SaveAdvisoryRequest>
{
    public SaveAdvisoryRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(SoftwareAdvisory.TitleMaxLength);
        RuleFor(x => x.ProductPattern).NotEmpty().MinimumLength(SoftwareAdvisory.PatternMinLength).MaximumLength(SoftwareAdvisory.PatternMaxLength);
        RuleFor(x => x.Publisher).MaximumLength(200);
        RuleFor(x => x.FixedVersion).NotEmpty().MaximumLength(SoftwareAdvisory.VersionMaxLength);
        RuleFor(x => x.AffectedFromVersion).MaximumLength(SoftwareAdvisory.VersionMaxLength);
        RuleFor(x => x.Severity).IsInEnum().NotEqual(Severity.Info).WithMessage("Choose Low, Medium, High or Critical.");
        RuleFor(x => x.Reference).MaximumLength(SoftwareAdvisory.ReferenceMaxLength);
    }
}
