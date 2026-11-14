namespace Solvix.Api.Infrastructure;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

/// <summary>Adds the JWT bearer scheme to the generated OpenAPI document.</summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the access token returned by /api/v1/auth/login.",
        };

        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Solvix EndPoints API";
        document.Info.Version = "v1";
        document.Info.Description = "Multi-tenant SaaS platform API. All tenant-scoped endpoints require a bearer token issued for that tenant.";
        return Task.CompletedTask;
    }
}
