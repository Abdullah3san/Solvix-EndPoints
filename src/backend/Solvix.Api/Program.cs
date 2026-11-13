using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Solvix.Api.Infrastructure;
using Solvix.Api.Middleware;
using Solvix.Infrastructure.Authorization;
using Solvix.Infrastructure.DependencyInjection;
using Solvix.Infrastructure.Persistence;
using Solvix.Infrastructure.Tenancy;
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

    // --------------------------------------------------------- infrastructure
    builder.Services.AddSolvixInfrastructure(builder.Configuration);
    builder.Services.AddOptions<CorsOptions>().Bind(builder.Configuration.GetSection(CorsOptions.SectionName));

    // ------------------------------------------------------------------- MVC
    builder.Services
        .AddControllers(options =>
        {
            options.Filters.Add<ValidationActionFilter>();
            // Start-up fails if any action does not declare how it is protected.
            options.Conventions.Add(new ExplicitAuthorizationConvention());
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
                RequireExpirationTime = true,
                RequireSignedTokens = true,

                // Only the algorithm we sign with: rules out "alg: none" and algorithm-confusion tokens.
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = SolvixClaimTypes.Name,
            };
        });

    // Base policy on every controller: a valid token is not enough, the request must have been
    // resolved to a live session and an active membership. Permissions are added per action with
    // [RequirePermission], which resolves to "perm:<key>" policies built by PermissionPolicyProvider.
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(Policies.TenantMember, p => p
            .RequireAuthenticatedUser()
            .RequireClaim(SolvixClaimTypes.TenantId)
            .RequireClaim(SolvixClaimTypes.SessionId)
            .AddRequirements(new ActiveTenantSessionRequirement()));
    builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();

    // ---------------------------------------------------------- rate limiting
    builder.Services.AddOptions<RateLimitingOptions>().Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName));
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            var http = context.HttpContext;

            // Sliding windows do not report an exact wait; the length of the policy's window is a safe upper bound.
            var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
            var window = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName switch
            {
                RateLimitPolicies.Email => limits.Email.WindowSeconds,
                RateLimitPolicies.Refresh => limits.Refresh.WindowSeconds,
                RateLimitPolicies.Registration => limits.Registration.WindowSeconds,
                _ => limits.Credentials.WindowSeconds,
            };
            var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                : window;
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Solvix.RateLimiting")
                .LogWarning("Rate limit exceeded for {Path} from {RemoteIp}", http.Request.Path, http.Connection.RemoteIpAddress);

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "Too many attempts. Wait a moment and try again.",
                Instance = http.Request.Path,
            };
            problem.Extensions["code"] = "too_many_requests";
            await http.RequestServices.GetRequiredService<IProblemDetailsService>()
                .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
        };

        AddClientPolicy(options, RateLimitPolicies.Credentials, o => o.Credentials);
        AddClientPolicy(options, RateLimitPolicies.Email, o => o.Email);
        AddClientPolicy(options, RateLimitPolicies.Refresh, o => o.Refresh);
        AddClientPolicy(options, RateLimitPolicies.Registration, o => o.Registration);
    });

    // ------------------------------------------------------ forwarded headers
    var network = builder.Configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = network.ForwardLimit;

        // Nothing is trusted by default (not even loopback): only the proxies listed in configuration.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var entry in network.TrustedProxies.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry.Trim()));
            }
            else
            {
                options.KnownProxies.Add(IPAddress.Parse(entry.Trim()));
            }
        }
    });

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
    if (network.TrustedProxies.Length > 0)
    {
        // First in the pipeline: everything after it (logging, rate limiting, audit) sees the real client address.
        app.UseForwardedHeaders();
    }

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.Use(async (context, next) =>
    {
        // Responses carry tokens and tenant data: never cacheable, never sniffed, never framed.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next(context);
    });
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
    if (app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value.Enabled)
    {
        // Before authentication: abusive callers are turned away as cheaply as possible.
        app.UseRateLimiter();
    }

    app.UseAuthentication();
    app.UseMiddleware<TenantResolutionMiddleware>();
    app.UseAuthorization();

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

// One sliding window per policy and client address. Limits are read from options when a
// partition is created, so they can be tuned by configuration without code changes.
static void AddClientPolicy(RateLimiterOptions options, string policyName, Func<RateLimitingOptions, RateLimitRule> select) =>
    options.AddPolicy(policyName, http =>
    {
        var rule = select(http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetSlidingWindowLimiter(
            $"{policyName}:{client}",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = TimeSpan.FromSeconds(rule.WindowSeconds),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });

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

/// <summary>Entry point marker used by integration tests (WebApplicationFactory).</summary>
public partial class Program
{
}
