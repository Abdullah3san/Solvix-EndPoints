namespace Solvix.Modules.Billing;

using System.Reflection;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Api;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Payments;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Modules;

/// <summary>Plans, subscriptions, device-limit entitlements, invoices, payments and the renewal worker.</summary>
public sealed class BillingModule : IModule
{
    public string Name => "Billing";

    public Assembly Assembly => typeof(BillingModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BillingOptions>().Bind(configuration.GetSection(BillingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BillingOptions>, BillingOptionsValidator>();

        services.AddScoped<IDeviceEntitlementProvider, EntitlementService>();
        services.AddScoped<ISubscriptionProvisioner, SubscriptionProvisioner>();
        services.AddScoped<InvoiceFactory>();
        services.AddScoped<UsageRecorder>();
        services.AddScoped<SettlementService>();
        services.AddScoped<TenantBillingJob>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IPlatformBillingService, PlatformBillingService>();
        services.AddScoped<IPaymentWebhookProcessor, PaymentWebhookProcessor>();
        services.AddScoped<IBillingJobRunner, BillingJobRunner>();
        services.AddScoped<OperatorKeyFilter>();
        services.AddHostedService<BillingWorker>();

        var provider = (configuration.GetSection(BillingOptions.SectionName)[nameof(BillingOptions.Provider)] ?? "manual").Trim().ToLowerInvariant();
        if (provider == StripePaymentProvider.ProviderName)
        {
            var apiBase = configuration.GetSection(BillingOptions.SectionName).GetSection(nameof(BillingOptions.Stripe))[nameof(StripeOptions.ApiBaseUrl)] ?? "https://api.stripe.com";
            services.AddHttpClient(StripePaymentProvider.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(apiBase);
                client.Timeout = TimeSpan.FromSeconds(15);
            });
            services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
        }
        else
        {
            services.AddSingleton<IPaymentProvider, ManualPaymentProvider>();
        }

        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
        services.AddRateLimiter(options =>
            options.AddPolicy(BillingWebhooksController.RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }
}
