namespace Solvix.Modules.Security;

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Security.Application.Alerts;
using Solvix.Modules.Security.Application.Audit;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Application.Endpoints;
using Solvix.Modules.Security.Application.Findings;
using Solvix.Modules.Security.Application.Ingest;
using Solvix.Modules.Security.Application.Policies;
using Solvix.Modules.Security.Application.Remediation;
using Solvix.Modules.Security.Application.Reports;
using Solvix.SharedKernel.Modules;

/// <summary>
/// Endpoint security management: posture collection, evaluation, findings, alerts, policy, approved remediation,
/// reporting and an append-only audit trail. See docs/PHASE-10-SECURITY.md for scope and limits.
/// </summary>
public sealed class SecurityModule : IModule
{
    public string Name => "Security";

    public Assembly Assembly => typeof(SecurityModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.AddScoped<DeviceActorAccessor>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IPolicyContext, PolicyContext>();
        services.AddScoped<IPostureProcessor, PostureProcessor>();
        services.AddScoped<IIngestService, IngestService>();
        services.AddSingleton<IDeviceGateway, DeviceGateway>();
        services.AddScoped<IEndpointService, EndpointService>();
        services.AddScoped<IFindingService, FindingService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IPolicyService, PolicyService>();
        services.AddScoped<IAdvisoryService, AdvisoryService>();
        services.AddScoped<IRemediationService, RemediationService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
