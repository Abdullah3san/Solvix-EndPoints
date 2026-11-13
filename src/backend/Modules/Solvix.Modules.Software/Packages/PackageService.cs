namespace Solvix.Modules.Software.Packages;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Configuration;
using Solvix.Modules.Software.Domain;
using Solvix.Modules.Software.Policies;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public sealed record PackageDto(
    Guid Id,
    string Name,
    string? Publisher,
    string? Version,
    string? Description,
    InstallerType InstallerType,
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    PackageStatus Status,
    SignatureState SignatureState,
    string? SignerSubject,
    string? SignerThumbprint,
    bool SignerTrusted,
    bool SignatureContentVerified,
    string? Architecture,
    bool IsSensitive,
    SilentPreset SilentPreset,
    IReadOnlyList<string> InstallArguments,
    IReadOnlyList<string> UninstallArguments,
    IReadOnlyList<int> SuccessExitCodes,
    IReadOnlyList<int> RebootExitCodes,
    int TimeoutMinutes,
    DetectionRule? Detection,
    bool IsConfigured,
    bool CanUninstall,
    int ConfigurationRevision,
    Guid UploadedBy,
    DateTimeOffset UploadedAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    string? BlockedByRule)
{
    internal static PackageDto From(SoftwarePackage p, SoftwareDeploymentPolicy policy, string? blockedBy) => new(
        p.Id, p.Name, p.Publisher, p.Version, p.Description, p.InstallerType, p.OriginalFileName, p.SizeBytes, p.Sha256, p.Status,
        p.SignatureState, p.SignerSubject, p.SignerThumbprint, policy.IsTrustedPublisher(p.SignerThumbprint), p.SignatureContentVerified,
        p.Architecture, p.IsSensitive, p.SilentPreset, p.InstallArguments, p.UninstallArguments, p.SuccessExitCodes, p.RebootExitCodes,
        p.TimeoutMinutes, p.Detection, p.IsConfigured, p.CanUninstall, p.ConfigurationRevision, p.UploadedBy, p.UploadedAtUtc,
        p.LastVerifiedAtUtc, p.RetiredAtUtc, blockedBy);
}

/// <summary>"Configure installation parameters": description, silent switches, exit codes, timeout and detection.</summary>
public sealed record ConfigurePackageRequest
{
    public required string Name { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public bool IsSensitive { get; init; }

    public SilentPreset SilentPreset { get; init; } = SilentPreset.Custom;

    /// <summary>Null: the preset's defaults.</summary>
    public IReadOnlyList<string>? InstallArguments { get; init; }

    /// <summary>Null: the preset's defaults. EXE packages without uninstall switches cannot be uninstalled.</summary>
    public IReadOnlyList<string>? UninstallArguments { get; init; }

    public IReadOnlyList<int>? SuccessExitCodes { get; init; }

    public IReadOnlyList<int>? RebootExitCodes { get; init; }

    public int TimeoutMinutes { get; init; } = 60;

    public required DetectionRule Detection { get; init; }
}

public sealed record PackageListQuery
{
    public string? Search { get; init; }

    public PackageStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public sealed record PackageVerification(Guid PackageId, bool Intact, string ExpectedSha256, string? ActualSha256, PackageStatus Status);

public interface IPackageService
{
    Task<PackageDto> UploadAsync(Stream content, string fileName, CancellationToken cancellationToken);

    Task<PagedResult<PackageDto>> ListAsync(PackageListQuery query, CancellationToken cancellationToken);

    Task<PackageDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PackageDto> ConfigureAsync(Guid id, ConfigurePackageRequest request, CancellationToken cancellationToken);

    Task<PackageVerification> VerifyAsync(Guid id, CancellationToken cancellationToken);

    Task<PackageDto> RetireAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class PackageService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IPackageStorage storage,
    DeploymentPolicyProvider policies,
    SoftwareAuditTrail audit,
    IOptions<SoftwareOptions> options,
    IClock clock) : IPackageService
{
    public async Task<PackageDto> UploadAsync(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = SanitizeFileName(fileName);
        var policy = await policies.GetAsync(cancellationToken);
        var tenantId = tenantContext.RequireTenantId();
        var temporary = storage.CreateTemporaryPath();
        try
        {
            var declared = PackageInspector.TypeFromFileName(name);
            if (declared is null || (declared == InstallerType.Exe && !policy.AllowExeInstallers))
            {
                throw new PackageValidationException(
                    PackageValidationCodes.UnsupportedType,
                    declared is null ? "Only .msi and .exe installers can be uploaded." : "Your organisation only allows MSI packages.");
            }

            var (sha256, size) = await ReceiveAsync(content, temporary, options.Value.MaxPackageBytes, cancellationToken);
            var inspection = PackageInspector.Inspect(temporary, name);
            if (!inspection.Signed && policy.RequireSignedPackages)
            {
                throw new PackageValidationException(PackageValidationCodes.UnsignedNotAllowed, "Your organisation only allows signed packages.");
            }

            if (await db.Set<SoftwarePackage>().AsNoTracking().FirstOrDefaultAsync(p => p.Sha256 == sha256 && p.Status != PackageStatus.Retired, cancellationToken) is { } duplicate)
            {
                throw new PackageValidationException(PackageValidationCodes.Duplicate, $"This file is already in the repository as '{duplicate.Name}' ({duplicate.Id}).");
            }

            var package = new SoftwarePackage(tenantId, Path.GetFileNameWithoutExtension(name), inspection.InstallerType, name, size, sha256, inspection,
                currentUser.RequireUserId(), clock.UtcNow);
            await storage.CommitAsync(tenantId, package.StorageKey, temporary, cancellationToken);
            try
            {
                await audit.AtomicAsync(
                    _ =>
                    {
                        db.Set<SoftwarePackage>().Add(package);
                        audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.PackageUploaded, SoftwareAuditOutcome.Success, package.Id,
                            Detail: $"{name}; {package.InstallerType}; {size} bytes; sha256 {sha256}; {(inspection.Signed ? $"signed by {inspection.SignerSubject} ({inspection.SignerThumbprint})" : "unsigned")}"));
                        return Task.FromResult(true);
                    },
                    cancellationToken);
            }
            catch
            {
                storage.Delete(tenantId, package.StorageKey);
                throw;
            }

            return PackageDto.From(package, policy, null);
        }
        catch (PackageValidationException ex)
        {
            await audit.WriteAsync(new SoftwareAuditEntry(SoftwareAuditEvents.PackageRejected, SoftwareAuditOutcome.Denied, Detail: $"{name}: {ex.Code} – {ex.Message}"), CancellationToken.None);
            if (ex.Code == PackageValidationCodes.Duplicate)
            {
                throw new ConflictException(ex.Message, ex.Code);
            }

            throw new ValidationException("file", ex.Message, ex.Code);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<PagedResult<PackageDto>> ListAsync(PackageListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var packages = db.Set<SoftwarePackage>().AsNoTracking();
        packages = query.Status is { } status ? packages.Where(p => p.Status == status) : packages.Where(p => p.Status != PackageStatus.Retired);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            packages = packages.Where(p => EF.Functions.ILike(p.Name, pattern) || (p.Publisher != null && EF.Functions.ILike(p.Publisher, pattern)) || EF.Functions.ILike(p.OriginalFileName, pattern));
        }

        var total = await packages.LongCountAsync(cancellationToken);
        var rows = await packages.OrderBy(p => p.Name).ThenByDescending(p => p.UploadedAtUtc).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var policy = await policies.GetAsync(cancellationToken);
        var rules = await BlockingRulesAsync(cancellationToken);
        return new PagedResult<PackageDto>(rows.Select(p => PackageDto.From(p, policy, BlockedBy(p, rules)?.Name)).ToList(), page, size, total);
    }

    public async Task<PackageDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var package = await FindAsync(id, tracked: false, cancellationToken);
        return PackageDto.From(package, await policies.GetAsync(cancellationToken), BlockedBy(package, await BlockingRulesAsync(cancellationToken))?.Name);
    }

    public async Task<PackageDto> ConfigureAsync(Guid id, ConfigurePackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = SoftwarePolicyService.Text(request.Name, 1, 200, nameof(request.Name));
        var publisher = SoftwarePolicyService.Optional(request.Publisher, 200, nameof(request.Publisher));
        var version = SoftwarePolicyService.Optional(request.Version, 64, nameof(request.Version));
        var description = SoftwarePolicyService.Optional(request.Description, 2000, nameof(request.Description));
        if (!Enum.IsDefined(request.SilentPreset))
        {
            throw new ValidationException(nameof(request.SilentPreset), "Unknown preset.");
        }

        if (request.TimeoutMinutes is < 1 or > SoftwareOperation.MaxTimeoutSeconds / 60)
        {
            throw new ValidationException(nameof(request.TimeoutMinutes), $"The timeout must be between 1 and {SoftwareOperation.MaxTimeoutSeconds / 60} minutes.");
        }

        if (request.Detection?.Validate() is { } detectionProblem)
        {
            throw new ValidationException(nameof(request.Detection), detectionProblem);
        }

        var success = request.SuccessExitCodes ?? [0];
        var reboot = request.RebootExitCodes ?? [3010, 1641];
        if (success.Count is 0 or > 16 || reboot.Count > 16)
        {
            throw new ValidationException(nameof(request.SuccessExitCodes), "Give 1–16 success exit codes and at most 16 reboot exit codes.");
        }

        return await audit.AtomicAsync(
            async ct =>
            {
                var package = await FindAsync(id, tracked: true, ct);
                if (package.Status != PackageStatus.Ready)
                {
                    throw new ConflictException("Only packages that are ready can be configured.", "package_not_ready");
                }

                if (package.InstallerType == InstallerType.Msi && request.SilentPreset is not (SilentPreset.Msi or SilentPreset.Custom))
                {
                    throw new ValidationException(nameof(request.SilentPreset), "MSI packages use the MSI preset (msiexec /qn is applied by the agent).");
                }

                var defaults = SilentPresets.Defaults(request.SilentPreset);
                var install = request.InstallArguments ?? defaults.Install;
                var uninstall = package.InstallerType == InstallerType.Msi ? [] : request.UninstallArguments ?? defaults.Uninstall;
                if (InstallerArguments.Validate(package.InstallerType, install) is { } installProblem)
                {
                    throw new ValidationException(nameof(request.InstallArguments), installProblem);
                }

                if (InstallerArguments.Validate(package.InstallerType, uninstall) is { } uninstallProblem)
                {
                    throw new ValidationException(nameof(request.UninstallArguments), uninstallProblem);
                }

                if (package.InstallerType == InstallerType.Exe && install.Count == 0)
                {
                    throw new ValidationException(nameof(request.InstallArguments), "EXE installers need their silent switches (choose a preset or enter them) so they never wait for input.");
                }

                package.Describe(name, publisher, version, description, request.IsSensitive);
                package.Configure(request.SilentPreset, install, uninstall, success, reboot, request.TimeoutMinutes, request.Detection!);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.PackageConfigured, SoftwareAuditOutcome.Success, package.Id,
                    Detail: $"revision {package.ConfigurationRevision.ToString(CultureInfo.InvariantCulture)}; preset {package.SilentPreset}; install [{string.Join(' ', install)}]; uninstall [{string.Join(' ', uninstall)}]; detection {package.DetectionKind} {package.DetectionProductCode ?? package.DetectionDisplayName} {package.DetectionVersionOperator} {package.DetectionVersion}; sensitive={package.IsSensitive}"));
                return PackageDto.From(package, await policies.GetAsync(ct), BlockedBy(package, await BlockingRulesAsync(ct))?.Name);
            },
            cancellationToken);
    }

    /// <summary>Re-hashes the stored file; a mismatch marks the package corrupted so it is never served again.</summary>
    public async Task<PackageVerification> VerifyAsync(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await FindAsync(id, tracked: false, cancellationToken);
        if (snapshot.Status == PackageStatus.Retired)
        {
            throw new ConflictException("The package has been retired.", "package_retired");
        }

        var actual = await storage.HashAsync(snapshot.TenantId, snapshot.StorageKey, cancellationToken);
        var intact = actual is { } a && a.Sha256 == snapshot.Sha256 && a.Length == snapshot.SizeBytes;
        var status = await RecordVerificationAsync(id, intact, actual?.Sha256, cancellationToken);
        return new PackageVerification(id, intact, snapshot.Sha256, actual?.Sha256, status);
    }

    internal async Task<PackageStatus> RecordVerificationAsync(Guid id, bool intact, string? actualSha256, CancellationToken cancellationToken) =>
        await audit.AtomicAsync(
            async ct =>
            {
                var package = await FindAsync(id, tracked: true, ct);
                if (intact)
                {
                    package.MarkVerified(clock.UtcNow);
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.PackageVerified, SoftwareAuditOutcome.Success, package.Id, Detail: package.Sha256));
                }
                else if (package.Status != PackageStatus.Corrupted)
                {
                    package.MarkCorrupted(clock.UtcNow);
                    audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.PackageCorrupted, SoftwareAuditOutcome.Failure, package.Id,
                        Detail: $"expected {package.Sha256}, found {actualSha256 ?? "no file"}"));
                }

                return package.Status;
            },
            cancellationToken);

    public async Task<PackageDto> RetireAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await audit.AtomicAsync(
            async ct =>
            {
                var package = await FindAsync(id, tracked: true, ct);
                if (package.Status == PackageStatus.Retired)
                {
                    throw new ConflictException("The package has already been retired.", "package_retired");
                }

                var active = await db.Set<DeploymentTarget>().AnyAsync(
                    t => t.PackageId == id && (t.Status == TargetStatus.Pending || t.Status == TargetStatus.Dispatched || t.Status == TargetStatus.Running), ct);
                if (active)
                {
                    throw new ConflictException("The package is used by a deployment that has not finished. Cancel it first.", "package_in_use");
                }

                package.Retire(clock.UtcNow);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.PackageRetired, SoftwareAuditOutcome.Success, package.Id, Detail: package.Name));
                return package;
            },
            cancellationToken);
        storage.Delete(result.TenantId, result.StorageKey);
        return PackageDto.From(result, await policies.GetAsync(cancellationToken), null);
    }

    internal async Task<IReadOnlyList<SoftwareRestrictionRule>> BlockingRulesAsync(CancellationToken cancellationToken) =>
        await db.Set<SoftwareRestrictionRule>().AsNoTracking().Where(r => r.Enabled && r.RuleType == RestrictionRuleType.Blocked).ToListAsync(cancellationToken);

    /// <summary>The blocked-software rule an installation of this package would violate, if any (rules scoped to device groups are checked per device).</summary>
    internal static SoftwareRestrictionRule? BlockedBy(SoftwarePackage package, IEnumerable<SoftwareRestrictionRule> rules, IReadOnlyCollection<Guid>? deviceGroups = null) =>
        rules.FirstOrDefault(r =>
            r.Enabled && r.RuleType == RestrictionRuleType.Blocked
            && (deviceGroups is null ? r.DeviceGroupIds is null : r.AppliesTo(deviceGroups))
            && (r.MatchesIdentity(package.Name, package.Publisher) || (package.DetectionDisplayName is { } display && r.MatchesIdentity(display, package.DetectionPublisher ?? package.Publisher)))
            && r.BlocksVersion(package.Version ?? package.DetectionVersion));

    private async Task<SoftwarePackage> FindAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var packages = tracked ? db.Set<SoftwarePackage>() : db.Set<SoftwarePackage>().AsNoTracking();
        return await packages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new NotFoundException("Package", id);
    }

    /// <summary>Streams the upload to a private temporary file, hashing it and enforcing the size limit as it arrives.</summary>
    private static async Task<(string Sha256, long Size)> ReceiveAsync(Stream content, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var target = new FileStream(path, streamOptions);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new PackageValidationException(PackageValidationCodes.TooLarge, $"The package exceeds the maximum size of {maxBytes / (1024 * 1024)} MB.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw new PackageValidationException(PackageValidationCodes.Empty, "The file is empty.");
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    /// <summary>Display name only: last path segment, printable characters, bounded length.</summary>
    internal static string SanitizeFileName(string? fileName)
    {
        var last = (fileName ?? string.Empty).Replace('\\', '/').Split('/').Last().Trim();
        var builder = new StringBuilder();
        foreach (var c in last.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?'))
            {
                builder.Append(c);
            }
        }

        var cleaned = builder.ToString().Trim().TrimStart('.');
        if (cleaned.Length == 0)
        {
            throw new ValidationException("fileName", "A file name ending in .msi or .exe is required.", PackageValidationCodes.UnsupportedType);
        }

        return cleaned.Length <= 200 ? cleaned : string.Concat(cleaned.AsSpan(0, 190), Path.GetExtension(cleaned).AsSpan());
    }
}
