namespace Solvix.Modules.Organization.Application.Branches;

using Solvix.Modules.Organization.Domain;

public sealed record BranchDto(
    Guid Id,
    string Name,
    string Code,
    bool IsHeadquarters,
    string? Phone,
    string? Email,
    string? TimeZone,
    bool IsActive,
    AddressDto Address,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static BranchDto From(Branch b) => new(
        b.Id, b.Name, b.Code, b.IsHeadquarters, b.Phone, b.Email, b.TimeZone, b.IsActive,
        AddressDto.From(b.Address), b.CreatedAtUtc, b.UpdatedAtUtc);
}

public sealed record CreateBranchRequest(
    string Name,
    string Code,
    bool IsHeadquarters,
    string? Phone,
    string? Email,
    string? TimeZone,
    AddressDto? Address);

public sealed record UpdateBranchRequest(
    string Name,
    string Code,
    bool IsHeadquarters,
    string? Phone,
    string? Email,
    string? TimeZone,
    AddressDto? Address);

public sealed record BranchListQuery(string? Search, bool IncludeInactive, int Page = 1, int PageSize = 25);
