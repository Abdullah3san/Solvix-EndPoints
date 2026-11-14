namespace Solvix.Modules.Mdm.Application.Devices;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Mdm.Application.Commands;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Application.Policies;
using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IMobileDeviceService
{
    Task<PagedResult<DeviceDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken);

    Task<DeviceDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceDto> AssignPolicyAsync(Guid id, AssignPolicyRequest request, CancellationToken cancellationToken);

    /// <summary>Evaluates compliance for one device on demand.</summary>
    Task<DeviceDetailDto> EvaluateComplianceAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class MobileDeviceService(
    ISolvixDbContext db,
    ICurrentUser currentUser,
    IPolicyPublisher publisher,
    IDeviceCommandQueue commands,
    IComplianceService compliance) : IMobileDeviceService
{
    public async Task<PagedResult<DeviceDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var devices = db.Set<MobileDevice>().AsNoTracking().AsQueryable();
        if (query.Platform is { } platform)
        {
            devices = devices.Where(d => d.Platform == platform);
        }

        if (query.State is { } state)
        {
            devices = devices.Where(d => d.State == state);
        }
        else
        {
            devices = devices.Where(d => d.State != DeviceState.Pending);
        }

        if (query.Compliance is { } c)
        {
            devices = devices.Where(d => d.Compliance == c);
        }

        if (query.Ownership is { } ownership)
        {
            devices = devices.Where(d => d.Ownership == ownership);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            devices = devices.Where(d => EF.Functions.ILike(d.DisplayName ?? string.Empty, pattern)
                || EF.Functions.ILike(d.Model ?? string.Empty, pattern)
                || EF.Functions.ILike(d.SerialNumber ?? string.Empty, pattern));
        }

        var total = await devices.LongCountAsync(cancellationToken);
        var items = await devices.OrderByDescending(d => d.LastSeenAtUtc).ThenBy(d => d.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<DeviceDto>(items.Select(DeviceDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<DeviceDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, cancellationToken);
        return await ToDetailAsync(device, cancellationToken);
    }

    public async Task<DeviceDto> AssignPolicyAsync(Guid id, AssignPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await FindAsync(id, cancellationToken);
        if (!device.IsActive)
        {
            throw new ConflictException($"A {device.State} device cannot be assigned a policy.");
        }

        if (request.PolicyId is null)
        {
            device.AssignPolicy(null);
            await db.SaveChangesAsync(cancellationToken);
            return DeviceDto.From(device);
        }

        var policy = await db.Set<MobilePolicy>().FirstOrDefaultAsync(p => p.Id == request.PolicyId, cancellationToken)
            ?? throw new NotFoundException("Policy", request.PolicyId.Value);
        device.AssignPolicy(policy);
        await publisher.EnsurePublishedAsync(policy, cancellationToken);
        DeviceLifecycle.Record(db, device, DeviceEventType.PolicyApplied, $"Policy '{policy.Name}' assigned.", currentUser.UserId);
        await commands.EnqueueSystemAsync(device, DeviceCommandType.ApplyPolicy, $"Policy '{policy.Name}' assigned.", new CommandParameters(PolicyVersion: policy.Version).ToJson(), cancellationToken);
        await compliance.EvaluateAsync(device, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return DeviceDto.From(device);
    }

    public async Task<DeviceDetailDto> EvaluateComplianceAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, cancellationToken);
        await compliance.EvaluateAsync(device, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(device, cancellationToken);
    }

    private async Task<DeviceDetailDto> ToDetailAsync(MobileDevice device, CancellationToken cancellationToken)
    {
        var events = await db.Set<DeviceEvent>().AsNoTracking().Where(e => e.DeviceId == device.Id)
            .OrderByDescending(e => e.CreatedAtUtc).Take(50).ToListAsync(cancellationToken);
        var recent = await db.Set<DeviceCommand>().AsNoTracking().Where(c => c.DeviceId == device.Id)
            .OrderByDescending(c => c.CreatedAtUtc).Take(20).ToListAsync(cancellationToken);
        return new DeviceDetailDto(
            DeviceDto.From(device),
            device.InstalledApps,
            device.ComplianceViolations,
            events.Select(DeviceEventDto.From).ToList(),
            recent.Select(CommandDto.From).ToList(),
            CapabilitiesDto.From(MdmCapabilities.For(device.Scenario)));
    }

    private async Task<MobileDevice> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MobileDevice>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw new NotFoundException("Device", id);
}
