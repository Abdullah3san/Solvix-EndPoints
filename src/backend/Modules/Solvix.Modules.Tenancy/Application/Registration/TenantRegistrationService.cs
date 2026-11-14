namespace Solvix.Modules.Tenancy.Application.Registration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Provisioning;
using Solvix.Modules.Organization.Application.Provisioning;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface ITenantRegistrationService
{
    Task<RegisterTenantResponse> RegisterAsync(RegisterTenantRequest request, CancellationToken cancellationToken);

    Task<SlugAvailabilityResponse> CheckSlugAsync(string slug, CancellationToken cancellationToken);
}

/// <summary>
/// Creates a tenant and everything it needs to be usable, atomically. Runs in
/// platform scope because no tenant exists yet when the operation starts; the
/// new tenant's rows are written with an explicit tenant id and the database
/// RLS policy accepts them because the session is flagged as platform scope.
/// </summary>
internal sealed partial class TenantRegistrationService(
    ISolvixDbContext db,
    ITenantContextMutator tenantContextMutator,
    IUserProvisioningService users,
    IOrganizationProvisioningService organization,
    ITokenService tokenService,
    ILogger<TenantRegistrationService> logger) : ITenantRegistrationService
{
    public async Task<RegisterTenantResponse> RegisterAsync(RegisterTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? Slug.FromName(request.CompanyName)
            : Slug.Normalize(request.Slug);

        if (await db.Set<Tenant>().AnyAsync(t => t.Slug == slug, cancellationToken))
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

        var descriptor = result.tenant.ToDescriptor();
        var token = tokenService.Issue(result.owner, descriptor, TenantRole.Owner);
        var tenantDto = new AuthTenantDto(descriptor.Id, descriptor.Slug, descriptor.Name, TenantRole.Owner);
        var auth = new AuthResponse(
            token.AccessToken,
            token.ExpiresAtUtc,
            new UserDto(result.owner.Id, result.owner.Email, result.owner.DisplayName),
            tenantDto,
            [tenantDto]);

        return new RegisterTenantResponse(result.tenant.Id, result.tenant.Slug, result.tenant.Name, auth);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {TenantId} ({Slug}) registered")]
    private static partial void LogTenantRegistered(ILogger logger, Guid tenantId, string slug);
}
