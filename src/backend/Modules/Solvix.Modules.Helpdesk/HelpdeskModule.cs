namespace Solvix.Modules.Helpdesk;

using System.Reflection;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvix.Modules.Helpdesk.Api;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Configuration;
using Solvix.SharedKernel.Modules;

/// <summary>Tickets, categories, SLA policies, escalation, technicians, e-mail intake and AI summaries.</summary>
public sealed class HelpdeskModule : IModule
{
    public string Name => "Helpdesk";

    public Assembly Assembly => typeof(HelpdeskModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HelpdeskOptions>().Bind(configuration.GetSection(HelpdeskOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<HelpdeskOptions>, HelpdeskOptionsValidator>();

        services.AddScoped<Solvix.SharedKernel.Tenancy.ITenantProvisioningStep, DefaultsProvisioner>();
        services.AddScoped<HelpdeskActor>();
        services.AddScoped<TicketFactory>();
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IEmailIntakeService, EmailIntakeService>();
        services.AddScoped<TenantEscalationJob>();
        services.AddScoped<IHelpdeskJobRunner, HelpdeskJobRunner>();
        services.AddHostedService<HelpdeskWorker>();

        services.AddSingleton<ExtractiveTicketSummarizer>();
        var section = configuration.GetSection(HelpdeskOptions.SectionName).GetSection(nameof(HelpdeskOptions.Ai));
        var provider = (section[nameof(AiOptions.Provider)] ?? "none").Trim().ToLowerInvariant();
        if (provider == "anthropic")
        {
            services.AddHttpClient(AnthropicTicketSummarizer.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(section[nameof(AiOptions.Endpoint)] ?? "https://api.anthropic.com");
                client.Timeout = TimeSpan.FromSeconds(25);
            });
            services.AddSingleton<ITicketSummarizer, AnthropicTicketSummarizer>();
        }
        else
        {
            services.AddSingleton<ITicketSummarizer>(sp => sp.GetRequiredService<ExtractiveTicketSummarizer>());
        }

        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
        services.AddRateLimiter(o => o.AddPolicy(InboundEmailController.RateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }
}
