namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IEnrollmentTokenService
{
    Task<PagedResult<EnrollmentTokenDto>> ListAsync(EnrollmentTokenListQuery query, CancellationToken cancellationToken);

    Task<CreatedEnrollmentTokenDto> CreateAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken);

    Task<EnrollmentTokenDto> RevokeAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class EnrollmentTokenService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IOptions<DevicesOptions> options,
    IClock clock) : IEnrollmentTokenService
{
    public async Task<PagedResult<EnrollmentTokenDto>> ListAsync(EnrollmentTokenListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var now = clock.UtcNow;
        var tokens = db.Set<EnrollmentToken>().AsNoTracking();
        if (!query.IncludeInactive)
        {
            tokens = tokens.Where(t => t.RevokedAtUtc == null && t.ExpiresAtUtc > now && t.UseCount < t.MaxUses);
        }

        var total = await tokens.LongCountAsync(cancellationToken);
        var items = await tokens
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        var groupNames = await GroupNamesAsync(items.Select(t => t.DeviceGroupId), cancellationToken);
        return new PagedResult<EnrollmentTokenDto>(
            items.Select(t => ToDto(t, groupNames, now)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<CreatedEnrollmentTokenDto> CreateAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var settings = options.Value.Enrollment;
        var lifetime = request.LifetimeHours ?? settings.DefaultTokenLifetimeHours;
        if (lifetime < 1 || lifetime > settings.MaxTokenLifetimeHours)
        {
            throw new ValidationException("lifetimeHours", $"Must be between 1 and {settings.MaxTokenLifetimeHours} hours.");
        }

        var maxUses = request.MaxUses ?? 1;
        if (maxUses < 1 || maxUses > settings.MaxTokenUses)
        {
            throw new ValidationException("maxUses", $"Must be between 1 and {settings.MaxTokenUses}.");
        }

        if (request.DeviceGroupId is { } groupId && !await db.Set<DeviceGroup>().AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            throw new NotFoundException("Device group", groupId);
        }

        var now = clock.UtcNow;
        var tokenId = Guid.CreateVersion7();
        var (plaintext, secret) = EnrollmentTokenCodec.Create(tenantId, tokenId);
        try
        {
            var token = EnrollmentToken.Create(
                tenantId, tokenId, request.Name, secret, now.AddHours(lifetime), maxUses, currentUser.RequireUserId(), request.DeviceGroupId);
            db.Set<EnrollmentToken>().Add(token);
            await db.SaveChangesAsync(cancellationToken);
            var groupNames = await GroupNamesAsync([token.DeviceGroupId], cancellationToken);
            return new CreatedEnrollmentTokenDto(ToDto(token, groupNames, now), plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<EnrollmentTokenDto> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.Set<EnrollmentToken>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Enrollment token", id);
        var now = clock.UtcNow;
        token.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
        var groupNames = await GroupNamesAsync([token.DeviceGroupId], cancellationToken);
        return ToDto(token, groupNames, now);
    }

    private async Task<Dictionary<Guid, string>> GroupNamesAsync(IEnumerable<Guid?> groupIds, CancellationToken cancellationToken)
    {
        var ids = groupIds.OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Set<DeviceGroup>().AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
    }

    private static EnrollmentTokenDto ToDto(EnrollmentToken t, Dictionary<Guid, string> groupNames, DateTimeOffset now) =>
        new(
            t.Id,
            t.Name,
            t.StateAt(now),
            t.ExpiresAtUtc,
            t.MaxUses,
            t.UseCount,
            t.LastUsedAtUtc,
            t.RevokedAtUtc,
            t.DeviceGroupId,
            t.DeviceGroupId is { } g && groupNames.TryGetValue(g, out var name) ? name : null,
            t.CreatedByUserId,
            t.CreatedAtUtc);
}
