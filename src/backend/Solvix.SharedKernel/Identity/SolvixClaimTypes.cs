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

    /// <summary>Kind of principal: absent or <see cref="ActorTypes.User"/> for people, <see cref="ActorTypes.Agent"/> for endpoint agents.</summary>
    public const string ActorType = "act";
}

/// <summary>Values of the <see cref="SolvixClaimTypes.ActorType"/> claim.</summary>
public static class ActorTypes
{
    public const string User = "user";
    public const string Agent = "agent";
}
