namespace Solvix.Api.Infrastructure;

public sealed class CorsOptions
{
    public const string SectionName = "Solvix:Cors";
    public const string PolicyName = "SolvixWeb";

    public string[] AllowedOrigins { get; set; } = [];
}
