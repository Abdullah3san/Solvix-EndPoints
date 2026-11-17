namespace Solvix.Modules.Tenancy.Application.Platform;

using Solvix.SharedKernel.Tenancy;

public sealed record RegistrationStatusDto(bool Open);

/// <summary>A customer workspace as the provider sees it.</summary>
public sealed record PlatformTenantDto(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SuspendedAtUtc,
    int MemberCount,
    int DeviceCount,
    IReadOnlyList<string> OwnerEmails,
    bool IsCurrent);

public sealed record PlatformTenantListQuery(string? Search, TenantStatus? Status, int Page = 1, int PageSize = 25);

/// <summary>Creates a workspace for a subscribed company together with its first owner account.</summary>
public sealed record CreatePlatformTenantRequest(
    string CompanyName,
    string? Slug,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);

public sealed record PlatformSummaryDto(int Total, int Active, int Suspended, bool PublicRegistrationOpen);
