namespace Solvix.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Solvix:Database";

    /// <summary>Apply pending migrations when the API starts. Development convenience only.</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Log parameter values in EF Core logs. Never enable outside local development.</summary>
    public bool EnableSensitiveDataLogging { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 30;
}
