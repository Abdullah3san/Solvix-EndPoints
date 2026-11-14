namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// The tenant's binding to Android Enterprise (a managed Google Play enterprise).
/// Created in two steps: a signup URL is issued, the administrator binds a
/// Google account, and the signup completes with the returned enterprise token.
/// </summary>
public sealed class MobileEnterprise : TenantEntity
{
    private MobileEnterprise()
    {
    }

    private MobileEnterprise(Guid tenantId, MobilePlatform platform)
        : base(tenantId)
    {
        Platform = platform;
    }

    public MobilePlatform Platform { get; private set; }

    /// <summary>Android signup URL name (<c>signupUrls/...</c>) while the signup is in progress.</summary>
    public string? PendingSignupUrlName { get; private set; }

    /// <summary>Android Management API enterprise name, for example <c>enterprises/LC00abcd</c>.</summary>
    public string? EnterpriseName { get; private set; }

    public string? DisplayName { get; private set; }

    public DateTimeOffset? BoundAtUtc { get; private set; }

    public DateTimeOffset? LastSyncAtUtc { get; private set; }

    public bool IsBound => EnterpriseName is not null;

    public static MobileEnterprise StartAndroidSignup(Guid tenantId, string signupUrlName) =>
        new(tenantId, MobilePlatform.Android) { PendingSignupUrlName = signupUrlName };

    public void RestartSignup(string signupUrlName)
    {
        if (IsBound)
        {
            throw new ConflictException("This tenant is already bound to an Android Enterprise.");
        }

        PendingSignupUrlName = signupUrlName;
    }

    public void CompleteSignup(string enterpriseName, string? displayName, DateTimeOffset now)
    {
        EnterpriseName = enterpriseName;
        DisplayName = displayName;
        PendingSignupUrlName = null;
        BoundAtUtc = now;
    }

    public void MarkSynced(DateTimeOffset now) => LastSyncAtUtc = now;
}
