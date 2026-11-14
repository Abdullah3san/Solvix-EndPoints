namespace Solvix.Modules.Identity.Application.Auth;

using Solvix.SharedKernel.Identity;

public sealed record LoginRequest(string Email, string Password, string? TenantSlug);

public sealed record SwitchTenantRequest(string TenantSlug);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    UserDto User,
    AuthTenantDto Tenant,
    IReadOnlyList<AuthTenantDto> AvailableTenants);

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record AuthTenantDto(Guid Id, string Slug, string Name, TenantRole Role);

public sealed record CurrentUserDto(UserDto User, AuthTenantDto Tenant);
