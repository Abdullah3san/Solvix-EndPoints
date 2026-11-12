namespace Solvix.Modules.Assets.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IVendorService
{
    Task<PagedResult<VendorDto>> ListAsync(VendorListQuery query, CancellationToken cancellationToken);

    Task<VendorDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<VendorDto> CreateAsync(VendorRequest request, CancellationToken cancellationToken);

    Task<VendorDto> UpdateAsync(Guid id, VendorRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class VendorService(ISolvixDbContext db, ITenantContext tenant, AssetAuditTrail audit) : IVendorService
{
    public async Task<PagedResult<VendorDto>> ListAsync(VendorListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var vendors = db.Set<Vendor>().AsNoTracking();
        if (!query.IncludeInactive)
        {
            vendors = vendors.Where(v => v.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            vendors = vendors.Where(v => EF.Functions.ILike(v.Name, pattern) || (v.ContactName != null && EF.Functions.ILike(v.ContactName, pattern)) || (v.Email != null && EF.Functions.ILike(v.Email, pattern)));
        }

        var total = await vendors.LongCountAsync(cancellationToken);
        var rows = await vendors.OrderBy(v => v.Name).ThenBy(v => v.Id).Skip(page.Skip).Take(page.SafePageSize)
            .Select(v => new { Vendor = v, Count = db.Set<Asset>().Count(a => a.VendorId == v.Id) })
            .ToListAsync(cancellationToken);
        return new PagedResult<VendorDto>(rows.Select(r => ToDto(r.Vendor, r.Count)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<VendorDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken) ?? throw new NotFoundException("Vendor", id);
        return ToDto(vendor, await db.Set<Asset>().CountAsync(a => a.VendorId == id, cancellationToken));
    }

    public async Task<VendorDto> CreateAsync(VendorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vendor = Vendor.Create(tenant.RequireTenantId(), request.Name, request.ContactName, request.Email, request.Phone, request.Website, request.AccountNumber, request.Notes);
        await EnsureUniqueAsync(vendor, cancellationToken);
        db.Set<Vendor>().Add(vendor);
        audit.Record(AssetAuditTrail.VendorEntity, vendor.Id, null, "vendor.created", $"Vendor {vendor.Name} created.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(vendor.Id, cancellationToken);
    }

    public async Task<VendorDto> UpdateAsync(Guid id, VendorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vendor = await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Id == id, cancellationToken) ?? throw new NotFoundException("Vendor", id);
        vendor.Update(request.Name, request.ContactName, request.Email, request.Phone, request.Website, request.AccountNumber, request.Notes, request.IsActive);
        await EnsureUniqueAsync(vendor, cancellationToken);
        audit.Record(AssetAuditTrail.VendorEntity, vendor.Id, null, "vendor.updated", $"Vendor {vendor.Name} updated.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var vendor = await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Id == id, cancellationToken) ?? throw new NotFoundException("Vendor", id);
        if (await db.Set<Asset>().AnyAsync(a => a.VendorId == id, cancellationToken) || await db.Set<MaintenanceRecord>().AnyAsync(m => m.VendorId == id, cancellationToken))
        {
            throw new ConflictException("The vendor is referenced by assets or maintenance records. Deactivate it instead.", "vendor_in_use");
        }

        db.Set<Vendor>().Remove(vendor);
        audit.Record(AssetAuditTrail.VendorEntity, vendor.Id, null, "vendor.deleted", $"Vendor {vendor.Name} deleted.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
    }

    private async Task EnsureUniqueAsync(Vendor vendor, CancellationToken cancellationToken)
    {
        if (await db.Set<Vendor>().AnyAsync(v => v.Id != vendor.Id && v.NormalizedName == vendor.NormalizedName, cancellationToken))
        {
            throw new ConflictException("A vendor with this name already exists.", "vendor_name_duplicate");
        }
    }

    private static VendorDto ToDto(Vendor v, int assetCount) =>
        new(v.Id, v.Name, v.ContactName, v.Email, v.Phone, v.Website, v.AccountNumber, v.Notes, v.IsActive, assetCount, v.CreatedAtUtc);
}
