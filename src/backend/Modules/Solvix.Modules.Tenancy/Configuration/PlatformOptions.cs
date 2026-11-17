namespace Solvix.Modules.Tenancy.Configuration;

/// <summary>
/// Provider-level settings: who operates the platform and whether anyone may
/// sign up. Bound from <c>Solvix:Platform</c>.
/// </summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Solvix:Platform";

    /// <summary>
    /// When false (the default) only a platform operator can create workspaces;
    /// the public registration endpoint refuses every request.
    /// </summary>
    public bool AllowPublicRegistration { get; set; }

    public PlatformOperatorOptions Operator { get; set; } = new();
}

public sealed class PlatformOperatorOptions
{
    /// <summary>E-mail of the single platform operator account. Empty: nobody is an operator.</summary>
    public string? Email { get; set; }

    public string DisplayName { get; set; } = "Platform Operator";

    /// <summary>
    /// Used only to create the account when no user with <see cref="Email"/> exists.
    /// It never changes the password of an existing account.
    /// </summary>
    public string? InitialPassword { get; set; }

    /// <summary>Name of the operator's own workspace, created when the account has none.</summary>
    public string WorkspaceName { get; set; } = "Platform";
}
