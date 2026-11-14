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

    [Range(5, 24 * 60)]
    public int AccessTokenMinutes { get; set; } = 60;
}
