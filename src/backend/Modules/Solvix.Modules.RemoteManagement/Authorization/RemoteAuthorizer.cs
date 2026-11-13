namespace Solvix.Modules.RemoteManagement.Authorization;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public static class RemoteDenialCodes
{
    public const string PermissionDenied = "permission_denied";
    public const string ScopeDenied = "remote_scope_denied";
    public const string OutsideAllowedHours = "remote_outside_allowed_hours";
    public const string DeviceInactive = "device_inactive";
    public const string DeviceNotFound = "device_not_found";
    public const string RequesterInactive = "requester_inactive";
}

/// <summary>
/// The single place remote access is decided. Order: tenant (from the validated token or
/// device certificate, never the request) → device exists in that tenant (query filter + RLS)
/// → device active → permission (resolved on the server) → technician scope (device groups,
/// branches) → tenant time window. Denials are written to the remote audit trail.
/// </summary>
public sealed class RemoteAuthorizer(
    ISolvixDbContext db,
    IEndpointDirectory endpoints,
    ICurrentUser currentUser,
    IAccessResolver accessResolver,
    ITenantContext tenantContext,
    RemoteAccessPolicyProvider policies,
    RemoteAuditTrail audit,
    IClock clock)
{
    /// <summary>Authorizes the signed-in user for <paramref name="permission"/> on <paramref name="deviceId"/> or throws.</summary>
    public async Task<EndpointAccessInfo> AuthorizeAsync(Guid deviceId, string permission, string? commandType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(permission);
        var device = await endpoints.FindForAccessAsync(deviceId, cancellationToken);
        if (device is null)
        {
            // Unknown here, which includes "belongs to another tenant": indistinguishable on purpose.
            await DenyAsync(deviceId, permission, commandType, RemoteDenialCodes.DeviceNotFound, cancellationToken);
            throw new NotFoundException("Device", deviceId);
        }

        var denial = await EvaluateAsync(currentUser.RequireUserId(), currentUser.Permissions, device, permission, cancellationToken);
        if (denial is not null)
        {
            await DenyAsync(deviceId, permission, commandType, denial, cancellationToken);
            throw denial == RemoteDenialCodes.DeviceInactive
                ? new ConflictException("The device is retired and cannot be managed remotely.", denial)
                : new ForbiddenException(Describe(denial), denial);
        }

        return device;
    }

    /// <summary>
    /// Re-checks a user who is not the caller (e.g. the requester of a queued command at
    /// delivery time) using their current roles. Returns a denial code, or <c>null</c> if allowed.
    /// </summary>
    public async Task<string?> ReauthorizeAsync(Guid userId, Guid deviceId, string permission, CancellationToken cancellationToken)
    {
        var device = await endpoints.FindForAccessAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return RemoteDenialCodes.DeviceNotFound;
        }

        var permissions = await accessResolver.ResolveMemberPermissionsAsync(tenantContext.RequireTenantId(), userId, cancellationToken);
        if (permissions.Count == 0)
        {
            return RemoteDenialCodes.RequesterInactive;
        }

        return await EvaluateAsync(userId, permissions, device, permission, cancellationToken);
    }

    private async Task<string?> EvaluateAsync(Guid userId, IReadOnlySet<string> permissions, EndpointAccessInfo device, string permission, CancellationToken cancellationToken)
    {
        if (!device.Endpoint.IsActive)
        {
            return RemoteDenialCodes.DeviceInactive;
        }

        if (!permissions.Contains(permission))
        {
            return RemoteDenialCodes.PermissionDenied;
        }

        var scope = await db.Set<RemoteAccessScope>().AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (scope is not null && !scope.Allows(device.Endpoint.BranchId, device.GroupIds))
        {
            return RemoteDenialCodes.ScopeDenied;
        }

        var policy = await policies.GetAsync(cancellationToken);
        return policy.IsWithinAllowedHours(clock.UtcNow) ? null : RemoteDenialCodes.OutsideAllowedHours;
    }

    private Task DenyAsync(Guid deviceId, string permission, string? commandType, string code, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new RemoteAuditEntry(RemoteAuditEvents.AccessDenied, RemoteAuditOutcome.Denied, deviceId, CommandType: commandType, Permission: permission, Detail: code),
            cancellationToken);

    private static string Describe(string code) => code switch
    {
        RemoteDenialCodes.ScopeDenied => "This device is outside the devices you are allowed to manage remotely.",
        RemoteDenialCodes.OutsideAllowedHours => "Remote administration is not allowed at this time by your organisation's policy.",
        _ => "You do not have permission to perform this action.",
    };
}

/// <summary>Reads the tenant's remote access policy (defaults until an administrator saves one).</summary>
public sealed class RemoteAccessPolicyProvider(ISolvixDbContext db, ITenantContext tenantContext)
{
    public async Task<RemoteAccessPolicy> GetAsync(CancellationToken cancellationToken) =>
        await db.Set<RemoteAccessPolicy>().AsNoTracking().FirstOrDefaultAsync(cancellationToken)
        ?? RemoteAccessPolicy.CreateDefault(tenantContext.RequireTenantId());
}
