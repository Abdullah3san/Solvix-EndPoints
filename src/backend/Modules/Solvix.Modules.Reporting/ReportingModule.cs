namespace Solvix.Modules.Reporting;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Reporting.Application;
using Solvix.SharedKernel.Modules;

/// <summary>Read-only operational reports and the availability trend sampler.</summary>
public sealed class ReportingModule : IModule
{
    public string Name => "Reporting";

    public Assembly Assembly => typeof(ReportingModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IReport, DeviceInventoryReport>();
        services.AddScoped<IReport, DeviceAvailabilityReport>();
        services.AddScoped<IReport, AssetLifecycleReport>();
        services.AddScoped<IReport, SoftwareInventoryReport>();
        services.AddScoped<IReport, PatchComplianceReport>();
        services.AddScoped<IReport, SecurityComplianceReport>();
        services.AddScoped<IReport, DeploymentReport>();
        services.AddScoped<IReport, TechnicianActivityReport>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<AvailabilityRecorder>();
        services.AddScoped<IAvailabilityJobRunner, AvailabilityJobRunner>();
        services.AddHostedService<AvailabilityWorker>();
    }
}
