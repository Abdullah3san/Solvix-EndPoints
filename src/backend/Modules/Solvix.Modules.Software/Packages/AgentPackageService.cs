namespace Solvix.Modules.Software.Packages;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Configuration;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

public sealed record PackageDownload(string Path, string Sha256, long SizeBytes);

public interface IAgentPackageService
{
    Task<PackageDownload> AuthorizeDownloadAsync(Guid executionId, CancellationToken cancellationToken);
}

/// <summary>
/// Hands package bytes to a device. Possession of a device certificate is not enough: the device
/// must name one of its own software commands that it has verified and accepted, the command must
/// name this package and its checksum, and the stored file must still match that checksum.
/// </summary>
internal sealed class AgentPackageService(
    ISolvixDbContext db,
    IHttpContextAccessor httpContext,
    IRemoteCommandDispatcher dispatcher,
    IPackageStorage storage,
    PackageService packages,
    SoftwareAuditTrail audit,
    IOptions<SoftwareOptions> options,
    IClock clock) : IAgentPackageService
{
    public async Task<PackageDownload> AuthorizeDownloadAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var deviceId = Guid.TryParse(httpContext.HttpContext?.User.FindFirst(SolvixClaimTypes.DeviceId)?.Value, out var id)
            ? id
            : throw new UnauthorizedException("A device certificate is required.");
        var command = await dispatcher.FindForDeviceAsync(executionId, deviceId, cancellationToken);
        if (command is null || command.CommandType is not (CommandTypes.SoftwareInstall or CommandTypes.SoftwareUninstall))
        {
            throw new NotFoundException("Software command", executionId);
        }

        if (command.Phase != RemoteCommandPhase.Running)
        {
            await RefuseAsync(null, deviceId, $"command {executionId:D} is {command.Phase}", cancellationToken);
            throw new ConflictException("The package can only be downloaded while its command is being executed.", "command_not_running");
        }

        if (!Guid.TryParse(command.Parameters.GetValueOrDefault("packageId"), out var packageId) || command.Parameters.GetValueOrDefault("sha256") is not { } expected)
        {
            throw new NotFoundException("Software package", executionId);
        }

        var package = await db.Set<SoftwarePackage>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken);
        if (package is null || package.Status != PackageStatus.Ready || !string.Equals(package.Sha256, expected, StringComparison.Ordinal))
        {
            await RefuseAsync(packageId, deviceId, $"package {(package is null ? "missing" : package.Status.ToString())}", cancellationToken);
            throw new ConflictException("The package is no longer available.", "package_unavailable");
        }

        if (package.LastVerifiedAtUtc < clock.UtcNow.AddMinutes(-Math.Max(0, options.Value.ReverifyAfterMinutes)))
        {
            var actual = await storage.HashAsync(package.TenantId, package.StorageKey, cancellationToken);
            var intact = actual is { } a && a.Sha256 == package.Sha256 && a.Length == package.SizeBytes;
            await packages.RecordVerificationAsync(package.Id, intact, actual?.Sha256, cancellationToken);
            if (!intact)
            {
                throw new ConflictException("The stored package failed its integrity check.", PackageValidationCodes.Corrupted);
            }
        }

        var path = storage.Locate(package.TenantId, package.StorageKey);
        if (path is null)
        {
            await packages.RecordVerificationAsync(package.Id, false, null, cancellationToken);
            throw new ConflictException("The stored package is missing.", PackageValidationCodes.Corrupted);
        }

        var range = httpContext.HttpContext?.Request.Headers.Range.ToString();
        await audit.WriteAsync(
            new SoftwareAuditEntry(SoftwareAuditEvents.PackageServed, SoftwareAuditOutcome.Success, package.Id, DeviceId: deviceId,
                Detail: $"command {executionId:D}{(string.IsNullOrEmpty(range) ? string.Empty : $"; {range}")}"),
            cancellationToken);
        return new PackageDownload(path, package.Sha256, package.SizeBytes);
    }

    private Task RefuseAsync(Guid? packageId, Guid deviceId, string detail, CancellationToken cancellationToken) =>
        audit.WriteAsync(new SoftwareAuditEntry(SoftwareAuditEvents.PackageServeRefused, SoftwareAuditOutcome.Denied, packageId, DeviceId: deviceId, Detail: detail), cancellationToken);
}
