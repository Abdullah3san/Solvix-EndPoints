namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>An application in the tenant's catalogue (Managed Google Play package or App Store app).</summary>
public sealed class MobileApp : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int IdentifierMaxLength = 256;

    private MobileApp()
    {
    }

    private MobileApp(Guid tenantId, MobilePlatform platform, string name, string identifier)
        : base(tenantId)
    {
        Platform = platform;
        Name = name;
        Identifier = identifier;
        Source = platform == MobilePlatform.Android ? AppStoreSource.ManagedGooglePlay : AppStoreSource.AppStoreVpp;
    }

    public MobilePlatform Platform { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Android package name or iOS bundle identifier.</summary>
    public string Identifier { get; private set; } = string.Empty;

    public AppStoreSource Source { get; private set; }

    /// <summary>iOS only: the numeric App Store identifier used by InstallApplication.</summary>
    public long? StoreId { get; private set; }

    public static MobileApp Create(Guid tenantId, MobilePlatform platform, string name, string identifier, long? storeId)
    {
        var app = new MobileApp(tenantId, platform, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), Guard.NotNullOrWhiteSpace(identifier, "identifier", IdentifierMaxLength));
        app.StoreId = platform == MobilePlatform.Ios ? storeId : null;
        return app;
    }

    public void Rename(string name, long? storeId)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        StoreId = Platform == MobilePlatform.Ios ? storeId : null;
    }
}

/// <summary>Assignment of an application to a policy, including per-app managed configuration.</summary>
public sealed class MobilePolicyApp : TenantEntity
{
    private MobilePolicyApp()
    {
    }

    public MobilePolicyApp(Guid tenantId, Guid policyId, Guid appId, AppInstallType installType, string? managedConfigurationJson)
        : base(tenantId)
    {
        PolicyId = policyId;
        AppId = appId;
        InstallType = installType;
        ManagedConfigurationJson = managedConfigurationJson;
    }

    public Guid PolicyId { get; private set; }

    public Guid AppId { get; private set; }

    public AppInstallType InstallType { get; private set; }

    /// <summary>
    /// App restrictions: Android managed configuration / iOS managed app configuration,
    /// a flat JSON object of string, number or boolean values.
    /// </summary>
    public string? ManagedConfigurationJson { get; private set; }

    public void Update(AppInstallType installType, string? managedConfigurationJson)
    {
        InstallType = installType;
        ManagedConfigurationJson = managedConfigurationJson;
    }
}
