namespace Solvix.Modules.Tenancy.Application.Platform;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Domain;
using Solvix.Modules.Tenancy.Application.Directory;
using Solvix.Modules.Tenancy.Application.Registration;
using Solvix.Modules.Tenancy.Configuration;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IPlatformTenantService
{
    Task<PlatformSummaryDto> GetSummaryAsync(CancellationToken cancellationToken);

    Task<PagedResult<PlatformTenantDto>> ListAsync(PlatformTenantListQuery query, CancellationToken cancellationToken);

    Task<PlatformTenantDto> CreateAsync(CreatePlatformTenantRequest request, CancellationToken cancellationToken);

    Task<PlatformTenantDto> SuspendAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<PlatformTenantDto> ReactivateAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Provider console: the only place that deliberately works across tenants on
/// behalf of a person. Every method first verifies, against the database, that
/// the caller is still a platform operator (a token claim alone is not enough),
/// then enters an explicit platform scope.
/// </summary>
internal sealed partial class PlatformTenantService(
    ISolvixDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    ITenantContextMutator tenantContextMutator,
    ITenantProvisioner registration,
    ITenantDirectoryCache directoryCache,
    IDeviceCensus deviceCensus,
    IOptions<PlatformOptions> options,
    IClock clock,
    ILogger<PlatformTenantService> logger) : IPlatformTenantService
{
    public async Task<PlatformSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        using var scope = await EnterAsync(cancellationToken);
        var tenants = db.Set<Tenant>().AsNoTracking();
        var total = await tenants.CountAsync(cancellationToken);
        var suspended = await tenants.CountAsync(t => t.Status == TenantStatus.Suspended, cancellationToken);
        var active = await tenants.CountAsync(t => t.Status == TenantStatus.Active, cancellationToken);
        return new PlatformSummaryDto(total, active, suspended, options.Value.AllowPublicRegistration);
    }

    public async Task<PagedResult<PlatformTenantDto>> ListAsync(PlatformTenantListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var scope = await EnterAsync(cancellationToken);
        var page = new PageRequest(query.Page, query.PageSize);
        var tenants = db.Set<Tenant>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            tenants = tenants.Where(t => EF.Functions.ILike(t.Name, pattern) || EF.Functions.ILike(t.Slug, pattern));
        }

        if (query.Status is { } status)
        {
            tenants = tenants.Where(t => t.Status == status);
        }

        var total = await tenants.LongCountAsync(cancellationToken);
        var items = await tenants.OrderBy(t => t.Name).ThenBy(t => t.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<PlatformTenantDto>(await DescribeAsync(items, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<PlatformTenantDto> CreateAsync(CreatePlatformTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = await EnterAsync(cancellationToken);
        var created = await registration.CreateAsync(
            new RegisterTenantRequest(request.CompanyName, request.Slug, request.OwnerEmail, request.OwnerDisplayName, request.OwnerPassword),
            cancellationToken);
        LogTenantCreated(logger, created.Tenant.Id, created.Tenant.Slug, currentUser.RequireUserId());
        return (await DescribeAsync([created.Tenant], cancellationToken))[0];
    }

    public async Task<PlatformTenantDto> SuspendAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var scope = await EnterAsync(cancellationToken);
        if (tenantId == CurrentTenantId)
        {
            throw new ConflictException("You cannot suspend the workspace you are signed in to.", "cannot_suspend_own_workspace");
        }

        var tenant = await FindAsync(tenantId, cancellationToken);
        if (tenant.Status != TenantStatus.Suspended)
        {
            tenant.Suspend(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await directoryCache.InvalidateAsync(tenant.Id, tenant.Slug, cancellationToken);
            LogTenantStatusChanged(logger, tenant.Id, tenant.Slug, "suspended", currentUser.RequireUserId());
        }

        return (await DescribeAsync([tenant], cancellationToken))[0];
    }

    public async Task<PlatformTenantDto> ReactivateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var scope = await EnterAsync(cancellationToken);
        var tenant = await FindAsync(tenantId, cancellationToken);
        if (tenant.Status != TenantStatus.Active)
        {
            tenant.Reactivate();
            await db.SaveChangesAsync(cancellationToken);
            await directoryCache.InvalidateAsync(tenant.Id, tenant.Slug, cancellationToken);
            LogTenantStatusChanged(logger, tenant.Id, tenant.Slug, "reactivated", currentUser.RequireUserId());
        }

        return (await DescribeAsync([tenant], cancellationToken))[0];
    }

    // The request's own tenant, captured before the platform scope hides it.
    private Guid? CurrentTenantId { get; set; }

    /// <summary>Verifies the caller against the database, then enters platform scope.</summary>
    private async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        CurrentTenantId ??= tenantContext.TenantId;

        // Users are a platform-level table (no tenant filter): this read does not need platform scope.
        var isOperator = await db.Set<User>().AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive && u.IsPlatformAdmin, cancellationToken);
        if (!isOperator)
        {
            LogOperatorRejected(logger, userId);
            throw new ForbiddenException("Platform operator access is required.");
        }

        return tenantContextMutator.BeginPlatformScope();
    }

    private async Task<Tenant> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Set<Tenant>().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
        ?? throw new NotFoundException("Tenant", tenantId);

    private async Task<List<PlatformTenantDto>> DescribeAsync(IReadOnlyList<Tenant> tenants, CancellationToken cancellationToken)
    {
        var ids = tenants.Select(t => t.Id).ToList();
        var memberships = await db.Set<TenantMembership>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => ids.Contains(m.TenantId) && m.IsActive)
            .Select(m => new { m.TenantId, m.Role, m.User!.Email })
            .ToListAsync(cancellationToken);
        var devices = await deviceCensus.CountActiveAsync(ids, cancellationToken);

        return tenants
            .Select(t =>
            {
                var members = memberships.Where(m => m.TenantId == t.Id).ToList();
                return new PlatformTenantDto(
                    t.Id,
                    t.Name,
                    t.Slug,
                    t.Status,
                    t.CreatedAtUtc,
                    t.SuspendedAtUtc,
                    members.Count,
                    devices.GetValueOrDefault(t.Id),
                    members.Where(m => m.Role == TenantRole.Owner).Select(m => m.Email).Order().ToList(),
                    t.Id == CurrentTenantId);
            })
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform operation rejected: user {UserId} is not a platform operator")]
    private static partial void LogOperatorRejected(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {TenantId} ({Slug}) created by platform operator {OperatorId}")]
    private static partial void LogTenantCreated(ILogger logger, Guid tenantId, string slug, Guid operatorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} ({Slug}) {Action} by platform operator {OperatorId}")]
    private static partial void LogTenantStatusChanged(ILogger logger, Guid tenantId, string slug, string action, Guid operatorId);
}
