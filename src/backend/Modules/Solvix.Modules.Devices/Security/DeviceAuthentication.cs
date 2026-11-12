namespace Solvix.Modules.Devices.Security;

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public static class DeviceAuthenticationDefaults
{
    public const string Scheme = "DeviceCertificate";
}

public static class DeviceAuthenticationExtensions
{
    /// <summary>
    /// Registers mutual-TLS device authentication. The handler validates the
    /// chain against the platform device CA only (never the OS trust store),
    /// the validity period and the client-authentication EKU; the
    /// <see cref="DeviceCredentialValidator"/> then checks the certificate
    /// against the database (not revoked, device active, tenant active).
    /// </summary>
    public static AuthenticationBuilder AddDeviceCertificate(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddCertificate(DeviceAuthenticationDefaults.Scheme, options =>
        {
            options.AllowedCertificateTypes = CertificateTypes.Chained;
            options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
            options.RevocationMode = X509RevocationMode.NoCheck; // revocation is enforced against device_credentials
            options.ValidateCertificateUse = true;
            options.ValidateValidityPeriod = true;
            options.Events = new CertificateAuthenticationEvents
            {
                OnCertificateValidated = async context =>
                {
                    var validator = context.HttpContext.RequestServices.GetRequiredService<DeviceCredentialValidator>();
                    var result = await validator.ValidateAsync(context.ClientCertificate, context.HttpContext.RequestAborted);
                    if (result.Principal is null)
                    {
                        context.Fail(result.FailureReason ?? "Device authentication failed.");
                        return;
                    }

                    context.Principal = result.Principal;
                    context.Success();
                },
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DeviceAuthenticationDefaults));
                    DeviceAuthenticationLog.CertificateRejected(logger, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    // An unauthenticated device must re-authenticate (or re-enroll): report 401, not the handler's default 403.
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
            };
        });

        builder.Services
            .AddOptions<CertificateAuthenticationOptions>(DeviceAuthenticationDefaults.Scheme)
            .Configure<IDeviceCertificateAuthority>((options, ca) =>
            {
                options.CustomTrustStore.Clear();
                options.CustomTrustStore.Add(ca.Certificate);
            });
        return builder;
    }
}

internal static partial class DeviceAuthenticationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Client certificate rejected: {Reason}")]
    public static partial void CertificateRejected(ILogger logger, string reason);
}

public sealed record DeviceValidationResult(ClaimsPrincipal? Principal, string? FailureReason)
{
    public static DeviceValidationResult Fail(string reason) => new(null, reason);
}

/// <summary>
/// Second stage of device authentication, after the TLS/chain checks. Binds the
/// request to the tenant named in the (CA-signed) certificate, then verifies the
/// credential row under that tenant's Row-Level Security scope.
/// </summary>
public sealed partial class DeviceCredentialValidator(
    ISolvixDbContext db,
    ITenantDirectory tenants,
    ITenantContext tenantContext,
    ITenantContextMutator tenantMutator,
    IClock clock,
    ILogger<DeviceCredentialValidator> logger)
{
    public async Task<DeviceValidationResult> ValidateAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();

        if (!DeviceCertificateIdentity.TryRead(certificate, out var tenantId, out var deviceId))
        {
            return Reject(thumbprint, "The certificate does not carry a Solvix device identity.");
        }

        var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            return Reject(thumbprint, "The tenant of this device is not active.");
        }

        if (tenantContext.TenantId is { } existing && existing != tenantId)
        {
            return Reject(thumbprint, "The request is already bound to another tenant.");
        }

        tenantMutator.SetTenant(tenantId, tenant.Slug);

        var now = clock.UtcNow;
        var credential = await db.Set<DeviceCredential>().FirstOrDefaultAsync(c => c.Thumbprint == thumbprint, cancellationToken);
        if (credential is null || credential.DeviceId != deviceId)
        {
            return Reject(thumbprint, "Unknown device credential.");
        }

        if (!credential.IsValidAt(now))
        {
            return Reject(thumbprint, credential.IsRevoked ? "The device credential has been revoked." : "The device credential is not valid at this time.");
        }

        var status = await db.Set<Device>().Where(d => d.Id == deviceId).Select(d => (DeviceStatus?)d.Status).FirstOrDefaultAsync(cancellationToken);
        if (status != DeviceStatus.Active)
        {
            return Reject(thumbprint, "The device is not active.");
        }

        if (credential.FirstUsedAtUtc is null)
        {
            // First use of a renewed certificate: the agent has demonstrably switched,
            // so every older certificate of this device can be retired.
            credential.MarkUsed(now);
            var older = await db.Set<DeviceCredential>()
                .Where(c => c.DeviceId == deviceId && c.Id != credential.Id && c.RevokedAtUtc == null && c.CreatedAtUtc <= credential.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            foreach (var previous in older)
            {
                previous.Revoke(CredentialRevocationReason.Superseded, now);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(SolvixClaimTypes.Subject, deviceId.ToString()),
                new Claim(SolvixClaimTypes.DeviceId, deviceId.ToString()),
                new Claim(SolvixClaimTypes.TenantId, tenantId.ToString()),
                new Claim(SolvixClaimTypes.TenantSlug, tenant.Slug),
                new Claim(SolvixClaimTypes.DeviceCredentialId, credential.Id.ToString()),
                new Claim(SolvixClaimTypes.DeviceCredentialExpires, credential.NotAfterUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            DeviceAuthenticationDefaults.Scheme,
            SolvixClaimTypes.DeviceId,
            ClaimsIdentity.DefaultRoleClaimType);
        return new DeviceValidationResult(new ClaimsPrincipal(identity), null);
    }

    private DeviceValidationResult Reject(string thumbprint, string reason)
    {
        LogRejected(logger, thumbprint, reason);
        return DeviceValidationResult.Fail(reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device certificate {Thumbprint} rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string thumbprint, string reason);
}

/// <summary>The device behind the current agent request.</summary>
public interface ICurrentDevice
{
    Guid DeviceId { get; }

    Guid CredentialId { get; }

    DateTimeOffset CredentialNotAfterUtc { get; }
}

internal sealed class HttpCurrentDevice(IHttpContextAccessor accessor) : ICurrentDevice
{
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? throw new UnauthorizedException();

    public Guid DeviceId => ReadGuid(SolvixClaimTypes.DeviceId);

    public Guid CredentialId => ReadGuid(SolvixClaimTypes.DeviceCredentialId);

    public DateTimeOffset CredentialNotAfterUtc =>
        long.TryParse(Principal.FindFirst(SolvixClaimTypes.DeviceCredentialExpires)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : throw new UnauthorizedException("The request is not authenticated as a device.");

    private Guid ReadGuid(string claim) =>
        Guid.TryParse(Principal.FindFirst(claim)?.Value, out var id)
            ? id
            : throw new UnauthorizedException("The request is not authenticated as a device.");
}
