namespace Solvix.Modules.Mdm.Application.Enrollment;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Mdm.Application.Enterprise;
using Solvix.Modules.Mdm.Application.Policies;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IEnrollmentService
{
    Task<EnrollmentCreatedDto> CreateAsync(CreateEnrollmentRequest request, CancellationToken cancellationToken);

    Task<PagedResult<EnrollmentDto>> ListAsync(bool includeInactive, int page, int pageSize, CancellationToken cancellationToken);

    Task<EnrollmentDto> RevokeAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class EnrollmentService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IMdmProviderRegistry providers,
    IEnterpriseDirectory enterprises,
    IPolicyPublisher publisher) : IEnrollmentService
{
    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public async Task<EnrollmentCreatedDto> CreateAsync(CreateEnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scenario = new ManagementScenario(request.Platform, request.Mode, request.Ownership).EnsureValid();
        if (request.ValidForHours is < 1 or > 24 * 30)
        {
            throw new ValidationException("validForHours", "An enrollment can be valid for 1 hour to 30 days.");
        }

        if (request.Platform == MobilePlatform.Android && request.MaxUses != 1)
        {
            throw new ValidationException("maxUses", "Android enrollment tokens are single-use so that one token cannot be cloned across devices.");
        }

        var policy = await db.Set<MobilePolicy>().FirstOrDefaultAsync(p => p.Id == request.PolicyId, cancellationToken)
            ?? throw new NotFoundException("Policy", request.PolicyId);
        if (policy.Scenario != scenario)
        {
            throw new ConflictException("The policy targets a different platform, management mode or ownership than this enrollment.");
        }

        var provider = providers.For(request.Platform);
        var enterprise = await enterprises.GetEnterpriseNameAsync(request.Platform, cancellationToken);
        if (request.Platform == MobilePlatform.Android && enterprise is null)
        {
            throw new ConflictException("Bind an Android Enterprise before issuing enrollments.");
        }

        // The platform must already know the policy the device will receive at enrollment.
        await publisher.EnsurePublishedAsync(policy, cancellationToken);

        var now = clock.UtcNow;
        var expires = now.AddHours(request.ValidForHours);
        var secret = request.Platform == MobilePlatform.Ios ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_') : null;
        var token = EnrollmentToken.Create(
            tenantContext.RequireTenantId(), scenario, request.Name, policy.Id, currentUser.RequireUserId(), expires, request.MaxUses,
            secret is null ? null : HashSecret(secret));

        var artifact = await provider.CreateEnrollmentAsync(
            new EnrollmentRequest(token.TenantId, token.Id, token.Name, scenario, policy.Id, enterprise, expires, secret), cancellationToken);
        if (artifact.ProviderReference is not null)
        {
            token.AttachProviderReference(artifact.ProviderReference);
        }

        db.Set<EnrollmentToken>().Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return new EnrollmentCreatedDto(EnrollmentDto.From(token, now), artifact.QrPayload, artifact.TokenValue, artifact.EnrollmentUrl, artifact.Instructions);
    }

    public async Task<PagedResult<EnrollmentDto>> ListAsync(bool includeInactive, int page, int pageSize, CancellationToken cancellationToken)
    {
        var request = new PageRequest(page, pageSize);
        var now = clock.UtcNow;
        var query = db.Set<EnrollmentToken>().AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(t => t.RevokedAtUtc == null && t.ExpiresAtUtc > now && t.UseCount < t.MaxUses);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(t => t.CreatedAtUtc).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<EnrollmentDto>(items.Select(t => EnrollmentDto.From(t, now)).ToList(), request.SafePage, request.SafePageSize, total);
    }

    public async Task<EnrollmentDto> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.Set<EnrollmentToken>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Enrollment", id);
        token.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return EnrollmentDto.From(token, clock.UtcNow);
    }
}
