namespace Solvix.Modules.Tenancy.Application.Registration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Provisioning;
using Solvix.Modules.Identity.Domain;
using Solvix.Modules.Tenancy.Application.Platform;
using Solvix.Modules.Tenancy.Configuration;
using Solvix.Modules.Organization.Application.Provisioning;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface ITenantRegistrationService
{
    /// <summary>Public self-service sign-up. Refused unless the provider opened registration.</summary>
    Task<RegisterTenantResponse> RegisterAsync(RegisterTenantRequest request, CancellationToken cancellationToken);

    RegistrationStatusDto GetStatus();

    Task<SlugAvailabilityResponse> CheckSlugAsync(string slug, CancellationToken cancellationToken);
}

/// <summary>
/// Creates a tenant and everything it needs to be usable, atomically. Runs in
/// platform scope because no tenant exists yet when the operation starts; the
/// new tenant's rows are written with an explicit tenant id and the database
/// RLS policy accepts them because the session is flagged as platform scope.
/// </summary>
/// <summary>
/// Creates workspaces without any caller check. Internal to the module: callers
/// (public registration, the provider console, the operator seeder) authorize first.
/// </summary>
internal interface ITenantProvisioner
{
    Task<CreatedTenant> CreateAsync(RegisterTenantRequest request, CancellationToken cancellationToken, bool uniqueSlug = false);

    /// <summary>Creates a workspace owned by an account that already exists.</summary>
    Task<Tenant> CreateForExistingOwnerAsync(string companyName, User owner, CancellationToken cancellationToken);
}

internal sealed record CreatedTenant(Tenant Tenant, User Owner);

internal sealed partial class TenantRegistrationService(
    ISolvixDbContext db,
    ITenantContextMutator tenantContextMutator,
    IUserProvisioningService users,
    IOrganizationProvisioningService organization,
    ITokenService tokenService,
    IOptions<PlatformOptions> platformOptions,
    ILogger<TenantRegistrationService> logger) : ITenantRegistrationService, ITenantProvisioner
{
    public RegistrationStatusDto GetStatus() => new(platformOptions.Value.AllowPublicRegistration);

    public async Task<RegisterTenantResponse> RegisterAsync(RegisterTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!platformOptions.Value.AllowPublicRegistration)
        {
            LogClosedRegistrationAttempt(logger);
            throw new ForbiddenException("Registration is closed. Workspaces are created by the service provider.");
        }

        var result = await CreateAsync(request, cancellationToken);
        var descriptor = result.Tenant.ToDescriptor();
        var token = tokenService.Issue(result.Owner, descriptor, TenantRole.Owner);
        var tenantDto = new AuthTenantDto(descriptor.Id, descriptor.Slug, descriptor.Name, TenantRole.Owner);
        var auth = new AuthResponse(
            token.AccessToken,
            token.ExpiresAtUtc,
            new UserDto(result.Owner.Id, result.Owner.Email, result.Owner.DisplayName),
            tenantDto,
            [tenantDto]);

        return new RegisterTenantResponse(result.Tenant.Id, result.Tenant.Slug, result.Tenant.Name, auth);
    }

    public async Task<Tenant> CreateForExistingOwnerAsync(string companyName, User owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var slug = await UniqueSlugAsync(Slug.FromName(companyName), cancellationToken);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var tenant = Tenant.Create(companyName, slug);
            db.Set<Tenant>().Add(tenant);
            await users.AddMembershipAsync(tenant.Id, owner.Id, TenantRole.Owner, ct);
            await organization.CreateCompanyProfileAsync(tenant.Id, companyName, ct);
            await organization.CreateHeadquartersAsync(tenant.Id, "Headquarters", ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return tenant;
        }, cancellationToken);
    }

    public async Task<CreatedTenant> CreateAsync(RegisterTenantRequest request, CancellationToken cancellationToken, bool uniqueSlug = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? Slug.FromName(request.CompanyName)
            : Slug.Normalize(request.Slug);

        if (uniqueSlug)
        {
            slug = await UniqueSlugAsync(slug, cancellationToken);
        }
        else if (await db.Set<Tenant>().AnyAsync(t => t.Slug == slug, cancellationToken))
        {
            throw new ConflictException($"The workspace id '{slug}' is already taken.");
        }

        if (await users.EmailExistsAsync(request.OwnerEmail, cancellationToken))
        {
            throw new ConflictException("An account with this e-mail address already exists. Sign in instead.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var tenant = Tenant.Create(request.CompanyName, slug);
            db.Set<Tenant>().Add(tenant);

            var owner = await users.CreateUserAsync(request.OwnerEmail, request.OwnerDisplayName, request.OwnerPassword, ct);
            await users.AddMembershipAsync(tenant.Id, owner.Id, TenantRole.Owner, ct);
            await organization.CreateCompanyProfileAsync(tenant.Id, request.CompanyName, ct);
            await organization.CreateHeadquartersAsync(tenant.Id, "Headquarters", ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (tenant, owner);
        }, cancellationToken);

        LogTenantRegistered(logger, result.tenant.Id, result.tenant.Slug);
        return new CreatedTenant(result.tenant, result.owner);
    }

    private async Task<string> UniqueSlugAsync(string slug, CancellationToken cancellationToken)
    {
        var candidate = slug;
        for (var suffix = 2; await db.Set<Tenant>().AnyAsync(t => t.Slug == candidate, cancellationToken); suffix++)
        {
            candidate = $"{slug}-{suffix}";
        }

        return candidate;
    }

    public async Task<SlugAvailabilityResponse> CheckSlugAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var normalized = slug.Trim().ToLowerInvariant();
        if (!Slug.IsValid(normalized))
        {
            return new SlugAvailabilityResponse(normalized, false);
        }

        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var taken = await db.Set<Tenant>().AnyAsync(t => t.Slug == normalized, cancellationToken);
        return new SlugAvailabilityResponse(normalized, !taken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a public registration attempt: registration is closed")]
    private static partial void LogClosedRegistrationAttempt(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {TenantId} ({Slug}) registered")]
    private static partial void LogTenantRegistered(ILogger logger, Guid tenantId, string slug);
}
