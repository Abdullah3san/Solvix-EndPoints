namespace Solvix.Modules.Assets.Application;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

internal static class AssetsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Records audit log entries in the same unit of work as the change they describe.</summary>
internal sealed class AssetAuditTrail(ISolvixDbContext db, ITenantContext tenant, ICurrentUser user, IClock clock, IHttpContextAccessor http)
{
    public const string AssetEntity = "Asset";
    public const string VendorEntity = "Vendor";
    public const string MaintenanceEntity = "Maintenance";
    public const string PhysicalAuditEntity = "PhysicalAudit";
    public const string LinkEntity = "EndpointLink";
    public const string SettingsEntity = "Settings";

    public void Record(string entityType, Guid entityId, Guid? assetId, string action, string summary, IReadOnlyDictionary<string, AuditChangeDto>? changes = null)
    {
        var correlation = http.HttpContext?.Items["X-Correlation-Id"] as string ?? Activity.Current?.TraceId.ToString();
        var actorId = user.IsAuthenticated ? user.UserId : null;
        db.Set<AssetAuditLogEntry>().Add(new AssetAuditLogEntry(
            tenant.RequireTenantId(),
            entityType,
            entityId,
            assetId,
            action,
            summary,
            changes is { Count: > 0 } ? JsonSerializer.Serialize(changes, AssetsJson.Options) : null,
            actorId is null ? AuditActorKind.System : AuditActorKind.User,
            actorId,
            correlation,
            clock.UtcNow));
    }

    public void Asset(Asset asset, string action, string summary, IReadOnlyDictionary<string, AuditChangeDto>? changes = null) =>
        Record(AssetEntity, asset.Id, asset.Id, action, summary, changes);

    public static Dictionary<string, AuditChangeDto> Diff(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
    {
        var changes = new Dictionary<string, AuditChangeDto>(StringComparer.Ordinal);
        foreach (var (key, value) in after)
        {
            var old = before.GetValueOrDefault(key);
            if (!string.Equals(old, value, StringComparison.Ordinal))
            {
                changes[key] = new AuditChangeDto(old, value);
            }
        }

        return changes;
    }
}

/// <summary>Resolves and validates references to members, branches, departments and vendors of the current tenant.</summary>
internal sealed class AssetReferences(ISolvixDbContext db, IMemberDirectory members, IOrganizationDirectory organization)
{
    private readonly Dictionary<Guid, MemberSummary?> _members = [];
    private readonly Dictionary<Guid, BranchSummary?> _branches = [];
    private readonly Dictionary<Guid, DepartmentSummary?> _departments = [];
    private readonly Dictionary<Guid, Vendor?> _vendors = [];

    public async Task<(Guid? BranchId, Guid? DepartmentId)> ValidateOrganizationAsync(Guid? branchId, Guid? departmentId, CancellationToken cancellationToken)
    {
        if (departmentId is { } dep)
        {
            await PreloadAsync([], [], [dep], [], cancellationToken);
            var department = _departments.GetValueOrDefault(dep) ?? throw new NotFoundException("Department", dep);
            if (branchId is not null && branchId != department.BranchId)
            {
                throw new ValidationException("departmentId", "The department does not belong to the selected branch.");
            }

            branchId = department.BranchId;
        }

        if (branchId is { } b)
        {
            await PreloadAsync([], [b], [], [], cancellationToken);
            if (_branches.GetValueOrDefault(b) is null)
            {
                throw new NotFoundException("Branch", b);
            }
        }

        return (branchId, departmentId);
    }

    public async Task ValidateVendorAsync(Guid? vendorId, CancellationToken cancellationToken)
    {
        if (vendorId is { } v)
        {
            await PreloadAsync([], [], [], [v], cancellationToken);
            if (_vendors.GetValueOrDefault(v) is null)
            {
                throw new NotFoundException("Vendor", v);
            }
        }
    }

    public async Task<Domain.Assignee> ValidateAssigneeAsync(AssignAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var assignee = Domain.Assignee.Create(request.AssignedUserId, request.AssigneeName, request.EmployeeNumber);
        if (assignee.UserId is { } userId)
        {
            await PreloadAsync([userId], [], [], [], cancellationToken);
            var member = _members.GetValueOrDefault(userId) ?? throw new NotFoundException("Member", userId);
            if (!member.IsActive)
            {
                throw new ValidationException("assignedUserId", "The user is not an active member of this workspace.");
            }
        }

        return assignee;
    }

    public async Task PreloadAsync(IEnumerable<Guid?> userIds, IEnumerable<Guid?> branchIds, IEnumerable<Guid?> departmentIds, IEnumerable<Guid?> vendorIds, CancellationToken cancellationToken)
    {
        var u = userIds.OfType<Guid>().Distinct().Where(id => !_members.ContainsKey(id)).ToList();
        if (u.Count > 0)
        {
            var found = await members.FindAsync(u, cancellationToken);
            foreach (var id in u)
            {
                _members[id] = found.GetValueOrDefault(id);
            }
        }

        var b = branchIds.OfType<Guid>().Distinct().Where(id => !_branches.ContainsKey(id)).ToList();
        if (b.Count > 0)
        {
            var found = await organization.FindBranchesAsync(b, cancellationToken);
            foreach (var id in b)
            {
                _branches[id] = found.GetValueOrDefault(id);
            }
        }

        var d = departmentIds.OfType<Guid>().Distinct().Where(id => !_departments.ContainsKey(id)).ToList();
        if (d.Count > 0)
        {
            var found = await organization.FindDepartmentsAsync(d, cancellationToken);
            foreach (var id in d)
            {
                _departments[id] = found.GetValueOrDefault(id);
            }
        }

        var v = vendorIds.OfType<Guid>().Distinct().Where(id => !_vendors.ContainsKey(id)).ToList();
        if (v.Count > 0)
        {
            var found = await db.Set<Vendor>().AsNoTracking().Where(x => v.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
            foreach (var id in v)
            {
                _vendors[id] = found.GetValueOrDefault(id);
            }
        }
    }

    public RefDto? Branch(Guid? id) => id is { } b ? new RefDto(b, _branches.GetValueOrDefault(b)?.Name, _branches.GetValueOrDefault(b)?.Code) : null;

    public RefDto? Department(Guid? id) => id is { } d ? new RefDto(d, _departments.GetValueOrDefault(d)?.Name, _departments.GetValueOrDefault(d)?.Code) : null;

    public RefDto? Vendor(Guid? id) => id is { } v ? new RefDto(v, _vendors.GetValueOrDefault(v)?.Name) : null;

    public RefDto? User(Guid? id) => id is { } u ? new RefDto(u, _members.GetValueOrDefault(u)?.DisplayName) : null;

    public AssigneeDto? Assignee(Guid? userId, string? name, string? employeeNumber)
    {
        if (userId is { } u)
        {
            var member = _members.GetValueOrDefault(u);
            return new AssigneeDto(u, member?.DisplayName, member?.Email, employeeNumber, member is { IsActive: true });
        }

        return name is null ? null : new AssigneeDto(null, name, null, employeeNumber, false);
    }
}

/// <summary>Generates tags {prefix}-{category code}-{000001} from an atomically incremented per-tenant counter.</summary>
internal sealed class AssetNumbering(ISolvixDbContext db, ITenantContext tenant)
{
    public async Task<string> NextTagAsync(AssetCategory category, string prefix, CancellationToken cancellationToken)
    {
        var scope = $"{prefix}-{AssetCategories.Get(category).Code}";
        var tenantId = tenant.RequireTenantId();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var value = await db.Database
                .SqlQuery<long>($"""
                    INSERT INTO asset_tag_sequences (id, tenant_id, scope, last_value, created_at_utc, updated_at_utc)
                    VALUES ({Guid.CreateVersion7()}, {tenantId}, {scope}, 1, now(), now())
                    ON CONFLICT (tenant_id, scope) DO UPDATE
                        SET last_value = asset_tag_sequences.last_value + 1, updated_at_utc = now()
                    RETURNING last_value AS "Value"
                    """)
                .ToListAsync(cancellationToken);
            var tag = $"{scope}-{value[0]:D6}";

            // A manually entered tag may already use this number: skip it.
            if (!await db.Set<Asset>().AnyAsync(a => a.AssetTag == tag, cancellationToken))
            {
                return tag;
            }
        }

        throw new ConflictException("Could not allocate a free asset tag.", "asset_tag_unavailable");
    }
}

/// <summary>Turns unique-constraint races into the same domain errors the pre-checks produce.</summary>
internal static class AssetPersistence
{
    public static async Task SaveAsync(ISolvixDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException("The record was changed by someone else. Reload it and try again.", "concurrency_conflict", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw pg.ConstraintName switch
            {
                "ux_assets_tenant_id_manufacturer_key_serial_number_key" => new ConflictException("Another asset from this manufacturer already has this serial number.", "asset_serial_duplicate"),
                "ux_assets_tenant_id_asset_tag" => new ConflictException("Another asset already uses this asset tag.", "asset_tag_duplicate"),
                "ux_asset_vendors_tenant_id_normalized_name" => new ConflictException("A vendor with this name already exists.", "vendor_name_duplicate"),
                "ux_asset_device_links_approved_asset" => new ConflictException("The asset is already associated with another endpoint.", "asset_already_linked"),
                "ux_asset_device_links_approved_device" => new ConflictException("The endpoint is already associated with another asset.", "device_already_linked"),
                "ux_asset_assignments_open" or "ux_asset_maintenance_records_in_progress" => new ConflictException("The asset was changed concurrently. Reload it and try again.", "concurrency_conflict"),
                _ => ex,
            };
        }
    }
}
