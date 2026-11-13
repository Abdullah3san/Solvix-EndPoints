namespace Solvix.Modules.Devices.Application.Credentials;

using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Authenticates endpoint agents presenting <c>Authorization: Device sxd_...</c>.
/// The device row is looked up by the id embedded in the credential in an
/// explicit platform scope (the tenant is not known yet), the hash is compared in
/// constant time, and the resulting principal carries the device's tenant in the
/// <c>tid</c> claim so the normal tenant resolution middleware, query filters and
/// RLS apply to every agent request exactly as they do for people.
/// </summary>
internal sealed class DeviceCredentialAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var prefix = AgentAuth.Scheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var credential = header[prefix.Length..].Trim();
        if (!Secrets.TryParse(credential, AgentAuth.DeviceCredentialPrefix, out var deviceId))
        {
            return AuthenticateResult.Fail("Malformed device credential.");
        }

        var cache = Context.RequestServices.GetRequiredService<IDeviceCredentialCache>();
        var record = await cache.GetAsync(deviceId, Context.RequestAborted);
        if (record is null || !record.IsActive || !Secrets.Verify(credential, record.CredentialHash))
        {
            return AuthenticateResult.Fail("Invalid or revoked device credential.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(SolvixClaimTypes.Subject, deviceId.ToString()),
                new Claim(SolvixClaimTypes.TenantId, record.TenantId.ToString()),
                new Claim(SolvixClaimTypes.ActorType, ActorTypes.Agent),
                new Claim(SolvixClaimTypes.Name, record.Hostname),
            ],
            AgentAuth.Scheme,
            SolvixClaimTypes.Name,
            SolvixClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AgentAuth.Scheme));
    }
}

public interface IDeviceCredentialCache
{
    Task<DeviceCredentialRecord?> GetAsync(Guid deviceId, CancellationToken cancellationToken);

    Task InvalidateAsync(Guid deviceId, CancellationToken cancellationToken);
}

public sealed record DeviceCredentialRecord(Guid TenantId, string CredentialHash, bool IsActive, string Hostname);

/// <summary>
/// Short-lived Redis cache of credential hashes so polling agents do not hit the
/// database on every request. Retirement and rotation invalidate the entry
/// immediately; the TTL bounds staleness if invalidation is ever missed.
/// </summary>
internal sealed class DeviceCredentialCache(
    ISolvixDbContext db,
    ITenantContextMutator tenantContextMutator,
    IDistributedCache cache) : IDeviceCredentialCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<DeviceCredentialRecord?> GetAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var key = Key(deviceId);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<DeviceCredentialRecord>(cached);
        }

        DeviceCredentialRecord? record;
        using (tenantContextMutator.BeginPlatformScope())
        {
            record = await db.Set<Device>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(d => d.Id == deviceId)
                .Select(d => new DeviceCredentialRecord(d.TenantId, d.CredentialHash, d.Status == DeviceStatus.Active, d.Hostname))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (record is not null)
        {
            await cache.SetAsync(
                key,
                JsonSerializer.SerializeToUtf8Bytes(record),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                cancellationToken);
        }

        return record;
    }

    public Task InvalidateAsync(Guid deviceId, CancellationToken cancellationToken) =>
        cache.RemoveAsync(Key(deviceId), cancellationToken);

    private static string Key(Guid deviceId) => $"solvix:platform:device-credential:{deviceId}";
}
