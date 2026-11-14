namespace Solvix.Modules.Mdm.Application.Apps;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IMobileAppService
{
    Task<PagedResult<AppDto>> ListAsync(MobilePlatform? platform, string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<AppDto> CreateAsync(CreateAppRequest request, CancellationToken cancellationToken);

    Task<AppDto> UpdateAsync(Guid id, UpdateAppRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class MobileAppService(ISolvixDbContext db, ITenantContext tenantContext) : IMobileAppService
{
    public async Task<PagedResult<AppDto>> ListAsync(MobilePlatform? platform, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var request = new PageRequest(page, pageSize);
        var query = db.Set<MobileApp>().AsNoTracking();
        if (platform is { } p)
        {
            query = query.Where(a => a.Platform == p);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern) || EF.Functions.ILike(a.Identifier, pattern));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(a => a.Name).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AppDto>(items.Select(AppDto.From).ToList(), request.SafePage, request.SafePageSize, total);
    }

    public async Task<AppDto> CreateAsync(CreateAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identifier = request.Identifier.Trim();
        if (await db.Set<MobileApp>().AnyAsync(a => a.Platform == request.Platform && a.Identifier == identifier, cancellationToken))
        {
            throw new ConflictException($"'{identifier}' is already in the catalogue.");
        }

        var app = MobileApp.Create(tenantContext.RequireTenantId(), request.Platform, request.Name, identifier, request.StoreId);
        db.Set<MobileApp>().Add(app);
        await db.SaveChangesAsync(cancellationToken);
        return AppDto.From(app);
    }

    public async Task<AppDto> UpdateAsync(Guid id, UpdateAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var app = await Find(id, cancellationToken);
        app.Rename(request.Name, request.StoreId);
        await db.SaveChangesAsync(cancellationToken);
        return AppDto.From(app);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var app = await Find(id, cancellationToken);
        if (await db.Set<MobilePolicyApp>().AnyAsync(a => a.AppId == id, cancellationToken))
        {
            throw new ConflictException("The application is assigned to a policy. Remove it from the policies first.");
        }

        db.Set<MobileApp>().Remove(app);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<MobileApp> Find(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MobileApp>().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw new NotFoundException("Application", id);
}
