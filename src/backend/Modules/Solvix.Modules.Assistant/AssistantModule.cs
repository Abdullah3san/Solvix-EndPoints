namespace Solvix.Modules.Assistant;

using System.Reflection;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Chat;
using Solvix.Modules.Assistant.Configuration;
using Solvix.Modules.Assistant.Demo;
using Solvix.Modules.Assistant.Knowledge;
using Solvix.Modules.Assistant.Knowledge.Embeddings;
using Solvix.Modules.Assistant.Llm;
using Solvix.Modules.Assistant.Remediation;
using Solvix.Modules.Assistant.Reports;
using Solvix.Modules.Assistant.Security;
using Solvix.Modules.Assistant.Tickets;
using Solvix.Modules.Assistant.Tools;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Modules;

/// <summary>
/// Phase 11: the AI IT assistant. The model only ever talks to <see cref="ToolExecutor"/>; tools call the
/// application services of this and other modules as the signed-in user. Fleet data and device commands
/// arrive through the <c>IFleetQueryService</c> / <c>IDeviceCommandGateway</c> ports.
/// </summary>
public sealed class AssistantModule : IModule
{
    public const string ChatRateLimitPolicy = "assistant-chat";

    public string Name => "Assistant";

    public Assembly Assembly => typeof(AssistantModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AssistantOptions>()
            .Bind(configuration.GetSection(AssistantOptions.SectionName))
            .Validate<IHostEnvironment>((o, env) => !(o.UseDemoFleet && env.IsProduction()), "Solvix:Assistant:UseDemoFleet must not be enabled in Production.")
            .Validate(o => o.MaxToolIterations is >= 1 and <= 12, "MaxToolIterations must be between 1 and 12.")
            .Validate(o => o.Knowledge.ChunkSizeChars >= 200 && o.Knowledge.MaxUploadBytes > 0, "Invalid knowledge base limits.")
            .ValidateOnStart();

        var options = configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>() ?? new AssistantOptions();

        // LLM
        services.AddHttpClient<IChatModel, AnthropicChatModel>((sp, http) =>
        {
            var llm = sp.GetRequiredService<IOptions<AssistantOptions>>().Value.Llm;
            http.BaseAddress = new Uri(llm.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds);
        });

        // Embeddings
        if (string.Equals(options.Knowledge.Embeddings.Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IEmbeddingProvider, OpenAiEmbeddingProvider>((sp, http) =>
            {
                var e = sp.GetRequiredService<IOptions<AssistantOptions>>().Value.Knowledge.Embeddings;
                http.BaseAddress = new Uri(e.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(60);
            });
        }
        else
        {
            services.AddSingleton<IEmbeddingProvider, HashingEmbeddingProvider>();
        }

        // Fleet + command ports: demo (dev only) or "not connected". Real device modules register theirs and win.
        if (options.UseDemoFleet)
        {
            services.AddScoped<IFleetQueryService, DemoFleet>();
            services.AddScoped<IDeviceCommandGateway, DemoCommandGateway>();
        }
        else
        {
            services.TryAddSingleton<IFleetQueryService, NotConnectedFleet>();
            services.TryAddSingleton<IDeviceCommandGateway, NotConnectedCommandGateway>();
        }

        services.AddScoped<IAssistantPrincipalAccessor, AssistantPrincipalAccessor>();
        services.AddScoped<IAssistantAuditLog, AssistantAuditLog>();
        services.AddScoped<IAssistantAuditQuery, AssistantAuditQuery>();
        services.AddScoped<IKnowledgeService, KnowledgeService>();
        services.AddScoped<IRemediationService, RemediationService>();
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAssistantChatService, AssistantChatService>();
        services.AddScoped<ScopeResolver>();
        services.AddScoped<ToolExecutor>();

        // Tools: the complete, closed set the model can reach.
        foreach (var tool in new[]
        {
            typeof(ListBranchesTool), typeof(ListDepartmentsTool), typeof(GetFleetSummaryTool), typeof(SearchDevicesTool),
            typeof(GetDeviceDetailsTool), typeof(GetDevicePerformanceTool), typeof(GetHardwareDiagnosticsTool), typeof(FindSoftwareTool),
            typeof(ListDeviceSoftwareTool), typeof(GetSecurityPostureTool), typeof(SearchKnowledgeBaseTool), typeof(CreateTicketTool),
            typeof(ProposeRemediationTool), typeof(GenerateReportTool), typeof(RunTroubleshootingWorkflowTool),
        })
        {
            services.AddScoped(typeof(IAssistantTool), tool);
        }

        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(ChatRateLimitPolicy, httpContext =>
            {
                var tenant = httpContext.User.FindFirst(SolvixClaimTypes.TenantId)?.Value ?? "anon";
                var user = httpContext.User.FindFirst(SolvixClaimTypes.Subject)?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
                return RateLimitPartition.GetFixedWindowLimiter($"{tenant}:{user}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.ChatPermitsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
    }
}
