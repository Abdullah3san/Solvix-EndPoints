namespace Solvix.Modules.Tenancy.Application.Registration;

using Solvix.Modules.Identity.Application.Auth;

/// <summary>Self-service sign-up: creates the tenant, its company profile, an HQ branch and the owner account.</summary>
public sealed record RegisterTenantRequest(
    string CompanyName,
    string? Slug,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);

public sealed record RegisterTenantResponse(Guid TenantId, string Slug, string Name, AuthResponse Auth);

public sealed record SlugAvailabilityResponse(string Slug, bool IsAvailable);
