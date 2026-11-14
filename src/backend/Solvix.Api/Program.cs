using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Solvix.Api.Infrastructure;
using Solvix.Api.Middleware;
using Solvix.Infrastructure.DependencyInjection;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Tenancy;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.Identity.Configuration;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;
using Solvix.SharedKernel.Web;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ---------------------------------------------------------------- logging
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("Application", "solvix-api");

        var format = context.Configuration["Solvix:Logging:Format"];
        if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
        {
            configuration.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
        }
        else
        {
            configuration.WriteTo.Console(new CompactJsonFormatter());
        }
    });

    // ------------------------------------------------- agent TLS (mutual TLS)
    // Agents connect to a dedicated HTTPS endpoint named "Agents"
    // (Kestrel:Endpoints:Agents). The TLS layer asks for a client certificate but
    // accepts any: chain (device CA only), validity, EKU and revocation are all
    // enforced by the DeviceCertificate authentication handler.
    ConfigureAgentTls(builder);

    // --------------------------------------------------------- infrastructure
    builder.Services.AddSolvixInfrastructure(builder.Configuration);
    builder.Services.AddOptions<CorsOptions>().Bind(builder.Configuration.GetSection(CorsOptions.SectionName));

    // ------------------------------------------------------------------- MVC
    builder.Services
        .AddControllers(options =>
        {
            options.Filters.Add<ValidationActionFilter>();
            // [controller] tokens become kebab-case so routes and OpenAPI paths are stable and lowercase.
            options.Conventions.Add(new RouteTokenTransformerConvention(new KebabCaseParameterTransformer()));
        })
        .ConfigureApplicationPartManager(manager =>
        {
            foreach (var module in SolvixModules.All)
            {
                manager.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(module.Assembly));
            }
        })
        .ConfigureApiBehaviorOptions(options =>
        {
            // Model-binding failures are reported in the same problem shape as FluentValidation errors.
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(kv => kv.Value?.Errors.Count > 0)
                    .ToDictionary(
                        kv => string.IsNullOrEmpty(kv.Key) ? "request" : char.ToLowerInvariant(kv.Key[0]) + kv.Key[1..],
                        kv => kv.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray());
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation failed",
                    Instance = context.HttpContext.Request.Path,
                };
                problem.Extensions["code"] = "validation_failed";
                problem.Extensions["errors"] = errors;
                return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
            };
        });
    builder.Services.AddScoped<ValidationActionFilter>();
    builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions.TryAdd("traceId", ctx.HttpContext.TraceIdentifier);
            if (ctx.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlationId))
            {
                ctx.ProblemDetails.Extensions.TryAdd("correlationId", correlationId);
            }
        };
    });
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // -------------------------------------------------------- API versioning
    builder.Services
        .AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = false;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddMvc()
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        })
        .AddOpenApi(options =>
        {
            // One OpenAPI document per API version (e.g. /openapi/v1.json).
            options.Document.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });

    // ------------------------------------------------------- authentication
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddDeviceCertificate()
        .AddJwtBearer(options =>
        {
            var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = SolvixClaimTypes.Name,
                RoleClaimType = SolvixClaimTypes.Role,
            };
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(Policies.TenantMember, p => p.RequireAuthenticatedUser().RequireClaim(SolvixClaimTypes.TenantId))
        .AddPolicy(Policies.TenantAdmin, p => p.RequireAuthenticatedUser().RequireClaim(SolvixClaimTypes.TenantId).RequireRole(nameof(TenantRole.Owner), nameof(TenantRole.Admin)))
        .AddPolicy(Policies.TenantOwner, p => p.RequireAuthenticatedUser().RequireClaim(SolvixClaimTypes.TenantId).RequireRole(nameof(TenantRole.Owner)))
        .AddPolicy(Policies.Device, p => p
            .AddAuthenticationSchemes(DeviceAuthenticationDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(SolvixClaimTypes.DeviceId)
            .RequireClaim(SolvixClaimTypes.TenantId));

    // Behind a TLS-terminating proxy the client certificate arrives in a header.
    // Only enabled explicitly; the proxy MUST overwrite the header on every request.
    var forwardedCertificateHeader = builder.Configuration[$"{DevicesOptions.SectionName}:{nameof(DevicesOptions.ForwardedClientCertificateHeader)}"];
    if (!string.IsNullOrWhiteSpace(forwardedCertificateHeader))
    {
        builder.Services.AddCertificateForwarding(options =>
        {
            options.CertificateHeader = forwardedCertificateHeader;
            options.HeaderConverter = value => ClientCertificateHeader.Parse(value)!;
        });

        // The proxy terminated TLS: honour its X-Forwarded-Proto/For so the request is
        // seen as HTTPS (required by certificate authentication) and rate limits use the
        // real client address. The deployment contract for this mode is that the API is
        // reachable only through that proxy.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    // ------------------------------------------------------------------ CORS
    builder.Services.AddCors(options =>
    {
        var origins = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>()?.AllowedOrigins ?? [];
        options.AddPolicy(CorsOptions.PolicyName, policy =>
        {
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(CorrelationIdMiddleware.HeaderName);
            }
        });
    });

    var app = builder.Build();

    // ------------------------------------------------------ dev migrations
    var databaseOptions = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    if (databaseOptions.MigrateOnStartup)
    {
        await MigrateAsync(app);
    }

    // -------------------------------------------------------------- pipeline
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnostic, httpContext) =>
        {
            diagnostic.Set("RequestHost", httpContext.Request.Host.Value);
            diagnostic.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
            var tenant = httpContext.RequestServices.GetService<ITenantContext>();
            if (tenant?.TenantId is { } tenantId)
            {
                diagnostic.Set("TenantId", tenantId);
            }
        };
    });
    app.UseCors(CorsOptions.PolicyName);
    if (!string.IsNullOrWhiteSpace(forwardedCertificateHeader))
    {
        app.UseForwardedHeaders();
        app.UseCertificateForwarding();
    }

    app.UseAuthentication();
    app.UseMiddleware<TenantResolutionMiddleware>();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();
    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference(options => options.WithTitle("Solvix EndPoints API"));
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
    app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Solvix API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task MigrateAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
    var db = scope.ServiceProvider.GetRequiredService<SolvixDbContext>();
    var migrator = app.Configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.PostgresMigratorConnectionName);
    if (!string.IsNullOrWhiteSpace(migrator))
    {
        db.Database.SetConnectionString(migrator);
    }

    await db.Database.MigrateAsync();
}

static void ConfigureAgentTls(WebApplicationBuilder builder)
{
    var agentEndpoint = builder.Configuration.GetSection("Kestrel:Endpoints:Agents");
    if (!agentEndpoint.Exists())
    {
        return;
    }

    // Development / evaluation only: create a self-signed server certificate on first start.
    var certificatePath = agentEndpoint["Certificate:Path"];
    if (builder.Configuration.GetValue<bool>("Solvix:Devices:GenerateDevelopmentTlsCertificate") && !string.IsNullOrWhiteSpace(certificatePath))
    {
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException("GenerateDevelopmentTlsCertificate must not be used in Production.");
        }

        var hosts = (builder.Configuration["Solvix:Devices:DevelopmentTlsHostNames"] ?? "localhost")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DevelopmentServerCertificate.EnsureExists(certificatePath, agentEndpoint["Certificate:Password"], hosts);
    }

    builder.WebHost.ConfigureKestrel((context, kestrel) =>
        kestrel.Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: false)
            .Endpoint("Agents", endpoint =>
            {
                endpoint.HttpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                endpoint.HttpsOptions.CheckCertificateRevocation = false;
                endpoint.HttpsOptions.ClientCertificateValidation = static (_, _, _) => true;
            }));
}

/// <summary>Entry point marker used by integration tests (WebApplicationFactory).</summary>
public partial class Program
{
}
