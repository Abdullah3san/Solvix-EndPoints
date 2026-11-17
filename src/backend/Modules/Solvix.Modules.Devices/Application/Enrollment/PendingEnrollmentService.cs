namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IPendingEnrollmentService
{
    Task<PagedResult<PendingEnrollmentDto>> ListAsync(PendingEnrollmentListQuery query, CancellationToken cancellationToken);

    Task<PendingEnrollmentCountDto> CountAsync(CancellationToken cancellationToken);

    Task<PendingEnrollmentDto> ApproveAsync(Guid id, string? note, CancellationToken cancellationToken);

    Task<PendingEnrollmentDto> RejectAsync(Guid id, string? note, CancellationToken cancellationToken);

    Task<BulkDecisionResultDto> ApproveManyAsync(IReadOnlyList<Guid> ids, string? note, CancellationToken cancellationToken);

    Task<BulkDecisionResultDto> RejectManyAsync(IReadOnlyList<Guid> ids, string? note, CancellationToken cancellationToken);
}

/// <summary>Administrator side of the approval queue.</summary>
internal sealed class PendingEnrollmentService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    DeviceRegistrar registrar,
    IOrganizationDirectory organization,
    IAuditTrail audit,
    IClock clock) : IPendingEnrollmentService
{
    private const int MaxBulk = 200;

    public async Task<PagedResult<PendingEnrollmentDto>> ListAsync(PendingEnrollmentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var status = query.Status ?? PendingEnrollmentStatus.Pending;
        var requests = db.Set<PendingEnrollment>().AsNoTracking().Where(p => p.Status == status);
        if (query.ProfileId is { } profileId)
        {
            requests = requests.Where(p => p.ProfileId == profileId);
        }

        var total = await requests.LongCountAsync(cancellationToken);
        var items = await requests.OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<PendingEnrollmentDto>(await ToDtosAsync(items, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<PendingEnrollmentCountDto> CountAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return new PendingEnrollmentCountDto(await db.Set<PendingEnrollment>().CountAsync(p => p.Status == PendingEnrollmentStatus.Pending && p.ExpiresAtUtc > now, cancellationToken));
    }

    public async Task<PendingEnrollmentDto> ApproveAsync(Guid id, string? note, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        RegisteredDevice? registered = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Row lock: two administrators clicking Approve at once cannot both issue a certificate.
                var pending = await db.Set<PendingEnrollment>()
                    .FromSqlInterpolated($"SELECT * FROM pending_enrollments WHERE id = {id} FOR UPDATE")
                    .FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Enrollment request", id);
                var profile = await db.Set<DeploymentProfile>().FirstOrDefaultAsync(p => p.Id == pending.ProfileId, ct)
                    ?? throw new NotFoundException("Deployment profile", pending.ProfileId);

                var identity = JsonSerializer.Deserialize<DeviceIdentityInfo>(pending.IdentityJson, DevicesJson.Options)
                    ?? throw new InvalidOperationException("The stored device identity could not be read.");
                var publicKey = System.Security.Cryptography.X509Certificates.PublicKey.CreateFromSubjectPublicKeyInfo(pending.PublicKeySpki, out _);

                // Decide first: an already decided or expired request fails here, before anything is issued.
                registered = await registrar.RegisterAsync(
                    new DeviceRegistration(tenantContext.RequireTenantId(), publicKey, identity, pending.HardwareFingerprint, profile.Id, profile.BranchId, profile.DepartmentId, profile.DeviceGroupId),
                    now,
                    ct);
                pending.Approve(userId, registered.Device.Id, registered.Certificate.Pem, note, now);
                audit.Record(
                    "enrollment.approved",
                    "Device",
                    registered.Device.Id,
                    $"Computer '{pending.Hostname}' approved{(registered.ReEnrolled ? " (re-enrollment of an existing device)" : string.Empty)}",
                    new { pendingId = id, profileId = profile.Id, reEnrolled = registered.ReEnrolled });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            },
            cancellationToken);

        await registrar.CompletedAsync(registered!, now, cancellationToken);
        return await GetDtoAsync(id, cancellationToken);
    }

    public async Task<PendingEnrollmentDto> RejectAsync(Guid id, string? note, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var pending = await db.Set<PendingEnrollment>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Enrollment request", id);
        pending.Reject(userId, note, clock.UtcNow);
        audit.Record("enrollment.rejected", "PendingEnrollment", id, $"Computer '{pending.Hostname}' rejected", new { pendingId = id, profileId = pending.ProfileId, note });
        await db.SaveChangesAsync(cancellationToken);
        return await GetDtoAsync(id, cancellationToken);
    }

    public Task<BulkDecisionResultDto> ApproveManyAsync(IReadOnlyList<Guid> ids, string? note, CancellationToken cancellationToken) =>
        DecideManyAsync(ids, id => ApproveAsync(id, note, cancellationToken));

    public Task<BulkDecisionResultDto> RejectManyAsync(IReadOnlyList<Guid> ids, string? note, CancellationToken cancellationToken) =>
        DecideManyAsync(ids, id => RejectAsync(id, note, cancellationToken));

    private async Task<BulkDecisionResultDto> DecideManyAsync(IReadOnlyList<Guid> ids, Func<Guid, Task> decide)
    {
        var distinct = (ids ?? []).Distinct().ToList();
        if (distinct.Count == 0 || distinct.Count > MaxBulk)
        {
            throw new ValidationException("ids", $"Select between 1 and {MaxBulk} requests.");
        }

        var succeeded = 0;
        var failed = new List<BulkDecisionFailureDto>();
        foreach (var id in distinct)
        {
            try
            {
                await decide(id);
                succeeded++;
            }
            catch (DomainException ex)
            {
                failed.Add(new BulkDecisionFailureDto(id, ex.Code, ex.Message));
                (db as DbContext)?.ChangeTracker.Clear(); // a failed attempt must not poison the next one
            }
        }

        return new BulkDecisionResultDto(succeeded, failed);
    }

    private async Task<PendingEnrollmentDto> GetDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var pending = await db.Set<PendingEnrollment>().AsNoTracking().FirstAsync(p => p.Id == id, cancellationToken);
        return (await ToDtosAsync([pending], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<PendingEnrollmentDto>> ToDtosAsync(IReadOnlyList<PendingEnrollment> items, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var profileIds = items.Select(p => p.ProfileId).Distinct().ToList();
        var profiles = await db.Set<DeploymentProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var branches = await organization.FindBranchesAsync(profiles.Values.Select(p => p.BranchId).OfType<Guid>().Distinct().ToList(), cancellationToken);
        var departments = await organization.FindDepartmentsAsync(profiles.Values.Select(p => p.DepartmentId).OfType<Guid>().Distinct().ToList(), cancellationToken);
        var groupIds = profiles.Values.Select(p => p.DeviceGroupId).OfType<Guid>().Distinct().ToList();
        var groups = groupIds.Count == 0
            ? []
            : await db.Set<DeviceGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var existingIds = items.Select(p => p.ExistingDeviceId).OfType<Guid>().Distinct().ToList();
        var existing = existingIds.Count == 0
            ? []
            : await db.Set<Device>().AsNoTracking().Where(d => existingIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Hostname, cancellationToken);

        return items.Select(p =>
        {
            profiles.TryGetValue(p.ProfileId, out var profile);
            var identity = JsonSerializer.Deserialize<DeviceIdentityInfo>(p.IdentityJson, DevicesJson.Options);
            return new PendingEnrollmentDto(
                p.Id,
                p.ProfileId,
                profile?.Name,
                p.Status,
                p.Hostname,
                identity?.Manufacturer,
                identity?.Model,
                identity?.SerialNumber,
                identity?.OsName,
                identity?.OsVersion,
                identity?.AgentVersion,
                p.SourceAddress,
                p.CreatedAtUtc,
                p.ExpiresAtUtc,
                p.IsExpiredAt(now),
                profile?.BranchId is { } b && branches.TryGetValue(b, out var branch) ? branch.Name : null,
                profile?.DepartmentId is { } d && departments.TryGetValue(d, out var department) ? department.Name : null,
                profile?.DeviceGroupId is { } g && groups.TryGetValue(g, out var groupName) ? groupName : null,
                p.ExistingDeviceId,
                p.ExistingDeviceId is { } e && existing.TryGetValue(e, out var hostname) ? hostname : null,
                p.DeviceId,
                p.DecidedAtUtc,
                p.DecisionNote);
        }).ToList();
    }
}
