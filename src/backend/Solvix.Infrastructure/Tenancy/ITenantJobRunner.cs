namespace Solvix.Infrastructure.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Entry point for background work. A job never receives a raw DbContext from
/// the root container; it always runs inside a tenant-bound scope, so it sees
/// exactly what an HTTP request for that tenant would see.
/// </summary>
public interface ITenantJobRunner
{
    Task RunForTenantAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> job, CancellationToken cancellationToken);

    Task<TResult> RunForTenantAsync<TResult>(Guid tenantId, Func<IServiceProvider, CancellationToken, Task<TResult>> job, CancellationToken cancellationToken);
}

internal sealed partial class TenantJobRunner(ITenantScopeFactory scopeFactory, ILogger<TenantJobRunner> logger) : ITenantJobRunner
{
    public async Task RunForTenantAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await RunForTenantAsync<object?>(
            tenantId,
            async (sp, ct) =>
            {
                await job(sp, ct);
                return null;
            },
            cancellationToken);
    }

    public async Task<TResult> RunForTenantAsync<TResult>(Guid tenantId, Func<IServiceProvider, CancellationToken, Task<TResult>> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var scope = scopeFactory.CreateScope(tenantId);
        using (LogContext.PushProperty("TenantId", tenantId))
        {
            LogJobStarted(logger, tenantId);
            try
            {
                return await job(scope.ServiceProvider, cancellationToken);
            }
            catch (Exception ex)
            {
                LogJobFailed(logger, ex, tenantId);
                throw;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tenant job started for {TenantId}")]
    private static partial void LogJobStarted(ILogger logger, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tenant job failed for {TenantId}")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, Guid tenantId);
}
