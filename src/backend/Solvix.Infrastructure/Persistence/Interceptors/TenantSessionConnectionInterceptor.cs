namespace Solvix.Infrastructure.Persistence.Interceptors;

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Publishes the ambient tenant to PostgreSQL as the session variable
/// <c>app.tenant_id</c> (and <c>app.rls_bypass</c> for platform scope) every
/// time a pooled connection is opened. Row-Level Security policies read these
/// variables, so even raw SQL and hand-written queries cannot cross tenants.
/// Npgsql resets session state (<c>DISCARD ALL</c>) when a connection returns
/// to the pool, so a value can never leak into another request.
/// </summary>
public sealed class TenantSessionConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    public const string TenantSetting = "app.tenant_id";
    public const string BypassSetting = "app.rls_bypass";

    private const string Sql =
        "SELECT set_config('app.tenant_id', @tenant_id, false), set_config('app.rls_bypass', @bypass, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = BuildCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = BuildCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand BuildCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;

        var tenant = command.CreateParameter();
        tenant.ParameterName = "tenant_id";
        tenant.DbType = DbType.String;
        tenant.Value = tenantContext.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(tenant);

        var bypass = command.CreateParameter();
        bypass.ParameterName = "bypass";
        bypass.DbType = DbType.String;
        bypass.Value = tenantContext.IsPlatformScope ? "on" : "off";
        command.Parameters.Add(bypass);

        return command;
    }
}
