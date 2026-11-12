namespace Solvix.SharedKernel.Identity;

/// <summary>Claim names used in Solvix access tokens.</summary>
public static class SolvixClaimTypes
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string TenantId = "tid";
    public const string TenantSlug = "tslug";
    public const string Role = "role";
}
