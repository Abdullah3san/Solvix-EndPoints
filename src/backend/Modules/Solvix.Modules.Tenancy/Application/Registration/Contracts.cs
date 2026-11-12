namespace Solvix.Modules.Tenancy.Application.Registration;

using Solvix.Modules.Identity.Application.Auth;

/// <summary>Self-service sign-up: creates the tenant, its company profile, an HQ branch and the owner account.</summary>
public sealed record RegisterTenantRequest(
    string CompanyName,
    string? Slug,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);

/// <param name="EmailVerificationRequired">
/// When true the owner must confirm their e-mail address before signing in and
/// <paramref name="Session"/> is null.
/// </param>
public sealed record RegisterTenantResponse(
    Guid TenantId,
    string Slug,
    string Name,
    bool EmailVerificationRequired,
    AuthResponse? Session);

public sealed record SlugAvailabilityResponse(string Slug, bool IsAvailable);
