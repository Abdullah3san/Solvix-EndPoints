namespace Solvix.Modules.Mdm;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Application.Apple;
using Solvix.Modules.Mdm.Application.Apps;
using Solvix.Modules.Mdm.Application.Commands;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Application.Enrollment;
using Solvix.Modules.Mdm.Application.Enterprise;
using Solvix.Modules.Mdm.Application.Maintenance;
using Solvix.Modules.Mdm.Application.Policies;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.Modules.Mdm.Providers.Apple;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Modules;

/// <summary>
/// Mobile device management. Android and Apple are separate providers behind one contract; nothing is shared with
/// any desktop endpoint agent.
/// </summary>
public sealed class MdmModule : IModule
{
    public string Name => "Mdm";

    public Assembly Assembly => typeof(MdmModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MdmOptions>().Bind(configuration.GetSection(MdmOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IValidateOptions<MdmOptions>, MdmOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        // ---- Android Enterprise (Android Management API)
        services.AddHttpClient<IGoogleAccessTokenProvider, GoogleServiceAccountTokenProvider>();
        services.AddHttpClient<IAndroidManagementApi, HttpAndroidManagementApi>();
        services.AddSingleton<IMdmProvider, AndroidMdmProvider>();

        // ---- Apple MDM
        services.AddHttpClient(ApnsPushClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ApnsPushClient.CreateHandler(sp.GetRequiredService<IOptions<MdmOptions>>().Value));
        services.AddSingleton<IApplePushClient, ApnsPushClient>();
        services.AddSingleton<IAppleDeviceSignatureVerifier, AppleDeviceSignatureVerifier>();
        services.AddSingleton<IMdmProvider, AppleMdmProvider>();
        services.AddScoped<AppleDeviceSession>();
        services.AddScoped<IAppleDeviceProtocolService, AppleDeviceProtocolService>();

        services.AddSingleton<IMdmProviderRegistry, MdmProviderRegistry>();

        // ---- application services
        services.AddScoped<MobileEnterpriseService>();
        services.AddScoped<IMobileEnterpriseService>(sp => sp.GetRequiredService<MobileEnterpriseService>());
        services.AddScoped<IEnterpriseDirectory>(sp => sp.GetRequiredService<MobileEnterpriseService>());
        services.AddScoped<IMobileAppService, MobileAppService>();
        services.AddScoped<IMobilePolicyService, MobilePolicyService>();
        services.AddScoped<IPolicyPublisher, PolicyPublisher>();
        services.AddScoped<IEnrollmentService, EnrollmentService>();
        services.AddScoped<IMobileDeviceService, MobileDeviceService>();
        services.AddScoped<IDeviceSyncService, DeviceSyncService>();
        services.AddScoped<DeviceCommandService>();
        services.AddScoped<IDeviceCommandService>(sp => sp.GetRequiredService<DeviceCommandService>());
        services.AddScoped<IDeviceCommandQueue>(sp => sp.GetRequiredService<DeviceCommandService>());
        services.AddScoped<IComplianceService, ComplianceService>();
        services.AddHostedService<MdmSyncWorker>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
