namespace Solvix.Modules.Mdm.Application.Apple;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Mdm.Application.Enrollment;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers.Apple;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Entry point for the anonymous device-facing Apple MDM endpoints. A device proves itself with the signature
/// of its identity certificate, not with a user token, so the tenant is found from the (random) device handle in the
/// URL under an explicit platform scope and all work then runs in a scope bound to exactly that tenant.
/// </summary>
public interface IAppleDeviceProtocolService
{
    Task<(int StatusCode, byte[]? Body)> CheckInAsync(string handle, byte[] body, string? signature, CancellationToken cancellationToken);

    Task<(int StatusCode, byte[]? Body)> ServerAsync(string handle, byte[] body, string? signature, CancellationToken cancellationToken);

    /// <summary>Redeems an enrollment secret and returns the enrollment profile for the device.</summary>
    Task<byte[]> RedeemEnrollmentAsync(string secret, CancellationToken cancellationToken);
}

internal sealed class AppleDeviceProtocolService(
    IServiceScopeFactory rootScopes,
    ITenantScopeFactory tenantScopes,
    ITenantDirectory tenants,
    IAppleDeviceSignatureVerifier verifier,
    IClock clock,
    ILogger<AppleDeviceProtocolService> logger) : IAppleDeviceProtocolService
{
    public async Task<(int StatusCode, byte[]? Body)> CheckInAsync(string handle, byte[] body, string? signature, CancellationToken cancellationToken)
    {
        var (tenantId, deviceId, message, thumbprint) = await PrepareAsync(handle, body, signature, cancellationToken);
        await using var scope = tenantScopes.CreateScope(tenantId);
        var response = await scope.ServiceProvider.GetRequiredService<AppleDeviceSession>().CheckInAsync(deviceId, message, thumbprint, cancellationToken);
        return (response.StatusCode, response.Body);
    }

    public async Task<(int StatusCode, byte[]? Body)> ServerAsync(string handle, byte[] body, string? signature, CancellationToken cancellationToken)
    {
        var (tenantId, deviceId, message, thumbprint) = await PrepareAsync(handle, body, signature, cancellationToken);
        await using var scope = tenantScopes.CreateScope(tenantId);
        var response = await scope.ServiceProvider.GetRequiredService<AppleDeviceSession>().ServerAsync(deviceId, message, thumbprint, cancellationToken);
        return (response.StatusCode, response.Body);
    }

    public async Task<byte[]> RedeemEnrollmentAsync(string secret, CancellationToken cancellationToken)
    {
        var hash = EnrollmentService.HashSecret(secret);
        Guid tenantId, tokenId;
        await using (var lookup = rootScopes.CreateAsyncScope())
        {
            using var platform = lookup.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
            var db = lookup.ServiceProvider.GetRequiredService<ISolvixDbContext>();
            var row = await db.Set<EnrollmentToken>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.SecretHash == hash)
                .Select(t => new { t.TenantId, t.Id, t.RevokedAtUtc, t.ExpiresAtUtc, t.UseCount, t.MaxUses })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                throw new NotFoundException("Enrollment", "token");
            }

            if (row.RevokedAtUtc is not null || row.ExpiresAtUtc <= clock.UtcNow || row.UseCount >= row.MaxUses)
            {
                throw new ConflictException("This enrollment link is expired, revoked or has already been used.");
            }

            tenantId = row.TenantId;
            tokenId = row.Id;
        }

        await EnsureTenantActiveAsync(tenantId, cancellationToken);
        await using var scope = tenantScopes.CreateScope(tenantId);
        var (profile, deviceId) = await scope.ServiceProvider.GetRequiredService<AppleDeviceSession>().CreateEnrollmentAsync(tokenId, cancellationToken);
        logger.LogInformation("Apple enrollment profile issued for device {DeviceId}", deviceId);
        return profile;
    }

    private async Task<(Guid TenantId, Guid DeviceId, Dictionary<string, object?> Message, string Thumbprint)> PrepareAsync(
        string handle, byte[] body, string? signature, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > MobileDevice.IdentifierMaxLength)
        {
            throw new NotFoundException("Device", "handle");
        }

        // Authenticate the message before anything is read from or written to the database.
        string thumbprint;
        Dictionary<string, object?> message;
        try
        {
            thumbprint = verifier.Verify(body, signature);
            message = ApplePlist.ParseDictionary(body);
        }
        catch (AppleSignatureException ex)
        {
            logger.LogWarning("Rejected Apple MDM message: {Reason}", ex.Message);
            throw new UnauthorizedException("The device signature could not be verified.");
        }
        catch (FormatException ex)
        {
            throw new ValidationException("body", ex.Message);
        }

        await using var lookup = rootScopes.CreateAsyncScope();
        using var platform = lookup.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
        var db = lookup.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var device = await db.Set<MobileDevice>().IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Platform == MobilePlatform.Ios && d.ProviderDeviceId == handle)
            .Select(d => new { d.TenantId, d.Id })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Device", "handle");

        await EnsureTenantActiveAsync(device.TenantId, cancellationToken);
        return (device.TenantId, device.Id, message, thumbprint);
    }

    private async Task EnsureTenantActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            throw new TenantInactiveException(tenant?.Status.ToString() ?? "missing");
        }
    }
}
