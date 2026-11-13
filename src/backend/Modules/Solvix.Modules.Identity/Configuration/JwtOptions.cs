namespace Solvix.Modules.Identity.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class JwtOptions
{
    public const string SectionName = "Solvix:Jwt";

    [Required]
    [MinLength(32, ErrorMessage = "The JWT signing key must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "solvix-endpoints";

    [Required]
    public string Audience { get; set; } = "solvix-endpoints-web";

    /// <summary>
    /// Lifetime of an access token. Kept short on purpose: the refresh token and
    /// the server-side session carry the long-lived state.
    /// </summary>
    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;
}
