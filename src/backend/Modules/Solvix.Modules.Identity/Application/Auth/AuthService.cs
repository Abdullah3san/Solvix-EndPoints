namespace Solvix.Modules.Identity.Application.Auth;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<AuthResponse> SwitchTenantAsync(Guid userId, string tenantSlug, CancellationToken cancellationToken);

    Task<CurrentUserDto> GetCurrentAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Login and tenant switching. These are the only operations in the system that
/// legitimately look across tenants (a user may belong to several), so they run
/// inside an explicit platform scope.
/// </summary>
internal sealed partial class AuthService(
    ISolvixDbContext db,
    IPasswordHasher<User> passwordHasher,
    ITokenService tokenService,
    ITenantDirectory tenantDirectory,
    ITenantContextMutator tenantContextMutator,
    IClock clock,
    ILogger<AuthService> logger) : IAuthService
{
    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var normalized = User.NormalizeEmail(request.Email);
        var user = await db.Set<User>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        // Constant-shaped failure path: do not reveal whether the e-mail exists.
        if (user is null || !user.IsActive)
        {
            LogFailedLogin(logger, normalized);
            throw new UnauthorizedException("Invalid e-mail or password.");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            LogFailedLogin(logger, normalized);
            throw new UnauthorizedException("Invalid e-mail or password.");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.SetPasswordHash(passwordHasher.HashPassword(user, request.Password));
        }

        var memberships = await LoadMembershipsAsync(user.Id, cancellationToken);
        if (memberships.Count == 0)
        {
            throw new ForbiddenException("This account is not a member of any active workspace.");
        }

        var selected = SelectTenant(memberships, request.TenantSlug);
        user.RecordLogin(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        return BuildResponse(user, selected, memberships);
    }

    public async Task<AuthResponse> SwitchTenantAsync(Guid userId, string tenantSlug, CancellationToken cancellationToken)
    {
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken)
            ?? throw new UnauthorizedException();

        var memberships = await LoadMembershipsAsync(user.Id, cancellationToken);
        var selected = memberships.FirstOrDefault(m => m.Tenant.Slug == tenantSlug.Trim().ToLowerInvariant())
            ?? throw new ForbiddenException("You are not a member of that workspace.");

        return BuildResponse(user, selected, memberships);
    }

    public async Task<CurrentUserDto> GetCurrentAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        // Runs inside the resolved tenant scope: the membership query is tenant-filtered.
        var membership = await db.Set<TenantMembership>()
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.UserId == userId && m.IsActive, cancellationToken)
            ?? throw new ForbiddenException("Membership not found in the current tenant.");

        var tenant = await tenantDirectory.FindByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);

        var user = membership.User!;
        return new CurrentUserDto(
            new UserDto(user.Id, user.Email, user.DisplayName),
            new AuthTenantDto(tenant.Id, tenant.Slug, tenant.Name, membership.Role));
    }

    private async Task<List<ResolvedMembership>> LoadMembershipsAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Platform scope: the tenant query filter is explicitly ignored because we
        // are enumerating ALL tenants this user belongs to.
        var memberships = await db.Set<TenantMembership>()
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.IsActive)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var resolved = new List<ResolvedMembership>(memberships.Count);
        foreach (var membership in memberships)
        {
            var tenant = await tenantDirectory.FindByIdAsync(membership.TenantId, cancellationToken);
            if (tenant is { IsActive: true })
            {
                resolved.Add(new ResolvedMembership(tenant, membership.Role));
            }
        }

        return resolved;
    }

    private static ResolvedMembership SelectTenant(IReadOnlyList<ResolvedMembership> memberships, string? requestedSlug)
    {
        if (string.IsNullOrWhiteSpace(requestedSlug))
        {
            return memberships[0];
        }

        var slug = requestedSlug.Trim().ToLowerInvariant();
        return memberships.FirstOrDefault(m => m.Tenant.Slug == slug)
            ?? throw new ForbiddenException("You are not a member of the requested workspace.");
    }

    private AuthResponse BuildResponse(User user, ResolvedMembership selected, IReadOnlyList<ResolvedMembership> all)
    {
        var token = tokenService.Issue(user, selected.Tenant, selected.Role);
        return new AuthResponse(
            token.AccessToken,
            token.ExpiresAtUtc,
            new UserDto(user.Id, user.Email, user.DisplayName),
            ToDto(selected),
            all.Select(ToDto).ToList());
    }

    private static AuthTenantDto ToDto(ResolvedMembership m) =>
        new(m.Tenant.Id, m.Tenant.Slug, m.Tenant.Name, m.Role);

    private sealed record ResolvedMembership(TenantDescriptor Tenant, TenantRole Role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed login attempt for {NormalizedEmail}")]
    private static partial void LogFailedLogin(ILogger logger, string normalizedEmail);
}
