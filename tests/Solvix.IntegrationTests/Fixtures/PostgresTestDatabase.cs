namespace Solvix.IntegrationTests.Fixtures;

using Npgsql;

/// <summary>
/// Creates a throw-away PostgreSQL database with the same two-role layout as
/// production (schema owner + NOBYPASSRLS runtime role), so Row-Level Security
/// is really enforced in tests. Configure the admin connection with
/// <c>SOLVIX_TEST_PG_ADMIN</c>; defaults target a local server.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    public const string MigratorUser = "solvix_migrator";
    public const string MigratorPassword = "solvix_migrator_dev";
    public const string AppUser = "solvix_app";
    public const string AppPassword = "solvix_app_dev";

    private readonly string _adminConnectionString;

    private PostgresTestDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        DatabaseName = databaseName;
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);
        MigratorConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = admin.Host,
            Port = admin.Port,
            Database = databaseName,
            Username = MigratorUser,
            Password = MigratorPassword,
            IncludeErrorDetail = true,
        }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = admin.Host,
            Port = admin.Port,
            Database = databaseName,
            Username = AppUser,
            Password = AppPassword,
            IncludeErrorDetail = true,
        }.ConnectionString;
    }

    public string DatabaseName { get; }

    public string MigratorConnectionString { get; }

    public string AppConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var admin = Environment.GetEnvironmentVariable("SOLVIX_TEST_PG_ADMIN")
            ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        var name = $"solvix_test_{Guid.NewGuid():N}"[..40];
        var db = new PostgresTestDatabase(admin, name);
        await db.ProvisionAsync();
        return db;
    }

    private async Task ProvisionAsync()
    {
        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{MigratorUser}') THEN
                        CREATE ROLE {MigratorUser} LOGIN PASSWORD '{MigratorPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppUser}') THEN
                        CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                    END IF;
                END $$;
                """);
            await ExecuteAsync(connection, $"CREATE DATABASE \"{DatabaseName}\" OWNER {MigratorUser};");
        }

        var adminOnNewDb = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = DatabaseName }.ConnectionString;
        await using (var connection = new NpgsqlConnection(adminOnNewDb))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"""
                ALTER SCHEMA public OWNER TO {MigratorUser};
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                GRANT USAGE ON SCHEMA public TO {AppUser};
                ALTER DEFAULT PRIVILEGES FOR ROLE {MigratorUser} IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {AppUser};
                ALTER DEFAULT PRIVILEGES FOR ROLE {MigratorUser} IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {AppUser};
                ALTER DEFAULT PRIVILEGES FOR ROLE {MigratorUser} IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO {AppUser};
                """);
        }
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
