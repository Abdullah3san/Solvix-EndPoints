namespace Solvix.Modules.Mdm.Application.Enterprise;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Resolves the tenant's Android Enterprise binding for other services.</summary>
public interface IEnterpriseDirectory
{
    /// <summary>The bound enterprise name, or null for iOS / when not bound.</summary>
    Task<string?> GetEnterpriseNameAsync(MobilePlatform platform, CancellationToken cancellationToken);
}

public interface IMobileEnterpriseService
{
    Task<EnterpriseDto> GetAsync(MobilePlatform platform, CancellationToken cancellationToken);

    Task<SignupUrlDto> StartAndroidSignupAsync(StartSignupRequest request, CancellationToken cancellationToken);

    Task<EnterpriseDto> CompleteAndroidSignupAsync(CompleteSignupRequest request, CancellationToken cancellationToken);
}

internal sealed class MobileEnterpriseService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAndroidManagementApi android,
    IOptions<MdmOptions> options,
    IMdmProviderRegistry providers) : IMobileEnterpriseService, IEnterpriseDirectory
{
    public async Task<string?> GetEnterpriseNameAsync(MobilePlatform platform, CancellationToken cancellationToken) =>
        platform == MobilePlatform.Android
            ? (await db.Set<MobileEnterprise>().AsNoTracking().FirstOrDefaultAsync(e => e.Platform == platform, cancellationToken))?.EnterpriseName
            : null;

    public async Task<EnterpriseDto> GetAsync(MobilePlatform platform, CancellationToken cancellationToken)
    {
        var configured = platform == MobilePlatform.Android ? options.Value.Android.IsConfigured : options.Value.Apple.IsConfigured;
        if (platform == MobilePlatform.Ios)
        {
            // Apple has no per-tenant binding in this phase: the platform-level push certificate serves every tenant.
            return new EnterpriseDto(platform, configured, null, null, null, null, configured);
        }

        var e = await db.Set<MobileEnterprise>().AsNoTracking().FirstOrDefaultAsync(x => x.Platform == platform, cancellationToken);
        return new EnterpriseDto(platform, e?.IsBound == true, e?.EnterpriseName, e?.DisplayName, e?.BoundAtUtc, e?.LastSyncAtUtc, configured);
    }

    public async Task<SignupUrlDto> StartAndroidSignupAsync(StartSignupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = providers.For(MobilePlatform.Android);
        var existing = await db.Set<MobileEnterprise>().FirstOrDefaultAsync(e => e.Platform == MobilePlatform.Android, cancellationToken);
        if (existing?.IsBound == true)
        {
            throw new ConflictException("This workspace is already bound to an Android Enterprise.");
        }

        var signup = await android.CreateSignupUrlAsync(options.Value.Android.ProjectId!, request.CallbackUrl, cancellationToken);
        if (existing is null)
        {
            db.Set<MobileEnterprise>().Add(MobileEnterprise.StartAndroidSignup(tenantContext.RequireTenantId(), signup.Name));
        }
        else
        {
            existing.RestartSignup(signup.Name);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new SignupUrlDto(signup.Url);
    }

    public async Task<EnterpriseDto> CompleteAndroidSignupAsync(CompleteSignupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = providers.For(MobilePlatform.Android);
        var enterprise = await db.Set<MobileEnterprise>().FirstOrDefaultAsync(e => e.Platform == MobilePlatform.Android, cancellationToken)
            ?? throw new ConflictException("Start the Android Enterprise signup first.");
        if (enterprise.IsBound)
        {
            throw new ConflictException("This workspace is already bound to an Android Enterprise.");
        }

        var signupName = enterprise.PendingSignupUrlName ?? throw new ConflictException("Start the Android Enterprise signup first.");
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "Solvix EndPoints" : request.DisplayName.Trim();
        var created = await android.CreateEnterpriseAsync(options.Value.Android.ProjectId!, signupName, request.EnterpriseToken, displayName, cancellationToken);
        enterprise.CompleteSignup(created.Name, created.EnterpriseDisplayName ?? displayName, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(MobilePlatform.Android, cancellationToken);
    }
}
