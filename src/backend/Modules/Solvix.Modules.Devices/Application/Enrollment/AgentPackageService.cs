namespace Solvix.Modules.Devices.Application.Enrollment;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record AgentPackageRequest(string Token);

/// <summary>A ready-to-send zip: the unmodified agent exe plus an <c>enrollment.json</c> for one token.</summary>
public sealed class AgentPackage(string fileName, string path) : IAsyncDisposable
{
    public string FileName { get; } = fileName;

    public long Length => new FileInfo(path).Length;

    public Stream OpenRead() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort: the temp file lives in the OS temp directory
        }

        return ValueTask.CompletedTask;
    }
}

public interface IAgentPackageService
{
    /// <summary>
    /// Builds the package for a token the caller has just created. The plaintext token is only known to
    /// the creator (the server stores a hash), so the caller must present it; it is verified in constant
    /// time against the hash, inside the caller's tenant.
    /// </summary>
    Task<AgentPackage> BuildAsync(AgentPackageRequest request, CancellationToken cancellationToken);
}

internal sealed class AgentPackageService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ITenantDirectory tenants,
    IOrganizationDirectory organization,
    IOptions<DevicesOptions> options,
    IConfiguration configuration,
    IClock clock) : IAgentPackageService
{
    public const string ExeName = "Solvix.Agent.exe";
    public const string UnavailableCode = "agent_package_unavailable";

    public async Task<AgentPackage> BuildAsync(AgentPackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentTokenCodec.TryParse(request.Token, out var tenantId, out var tokenId, out var secret))
        {
            throw new ValidationException("token", "The enrollment token is malformed.");
        }

        try
        {
            // Another tenant's token (or an unknown id) is "not found", exactly like any other resource.
            if (tenantContext.RequireTenantId() != tenantId)
            {
                throw new NotFoundException("Enrollment token", tokenId);
            }

            var token = await db.Set<EnrollmentToken>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
            if (token is null || !token.SecretMatches(secret))
            {
                throw new NotFoundException("Enrollment token", tokenId);
            }

            var now = clock.UtcNow;
            if (token.StateAt(now) != EnrollmentTokenState.Active)
            {
                throw new ConflictException("The enrollment token is no longer active.", "enrollment_token_not_active");
            }

            var package = options.Value.AgentPackage;
            var exe = package.Directory is { Length: > 0 } directory ? Path.Combine(directory, ExeName) : null;
            if (exe is null || !File.Exists(exe) || package.PublicServerUrl is not { } serverUrl)
            {
                throw new ConflictException(
                    "The agent package is not configured on this server (AgentPackage:Directory and AgentPackage:PublicServerUrl).",
                    UnavailableCode);
            }

            var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
            var file = new AgentEnrollmentFile
            {
                ServerUrl = serverUrl.AbsoluteUri,
                EnrollmentToken = request.Token.Trim(),
                ServerCertificateSha256 = ResolvePin(package),
                TokenExpiresAtUtc = token.ExpiresAtUtc,
                WorkspaceName = tenant?.Name,
                Placement = await PlacementAsync(token, cancellationToken),
            };

            var tempPath = Path.Combine(Path.GetTempPath(), $"solvix-agent-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    // The exe is copied byte-for-byte: modifying it would invalidate its code signature.
                    await AddFileAsync(zip, exe, ExeName, cancellationToken);
                    var settings = Path.Combine(Path.GetDirectoryName(exe)!, "appsettings.json");
                    if (File.Exists(settings))
                    {
                        await AddFileAsync(zip, settings, "appsettings.json", cancellationToken);
                    }

                    var enrollment = zip.CreateEntry(AgentEnrollmentFile.FileName, CompressionLevel.Optimal);
                    await using (var entry = enrollment.Open())
                    {
                        await JsonSerializer.SerializeAsync(entry, file, AgentJsonContext.Default.AgentEnrollmentFile, cancellationToken);
                    }

                    var readme = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
                    await using var readmeStream = readme.Open();
                    await readmeStream.WriteAsync(Encoding.UTF8.GetBytes(Readme(file)), cancellationToken);
                }

                return new AgentPackage(PackageFileName(tenant?.Slug, token.Name), tempPath);
            }
            catch
            {
                File.Delete(tempPath);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<string?> PlacementAsync(EnrollmentToken token, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        if (token.BranchId is { } branchId && (await organization.FindBranchesAsync([branchId], cancellationToken)).TryGetValue(branchId, out var branch))
        {
            parts.Add(branch.Name);
        }

        if (token.DepartmentId is { } departmentId && (await organization.FindDepartmentsAsync([departmentId], cancellationToken)).TryGetValue(departmentId, out var department))
        {
            parts.Add(department.Name);
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    /// <summary>
    /// The configured pin, or — for the development self-signed agent certificate generated at
    /// start-up — the thumbprint of that certificate, so a development package works without manual steps.
    /// </summary>
    private string? ResolvePin(AgentPackageOptions package)
    {
        if (!string.IsNullOrWhiteSpace(package.ServerCertificateSha256))
        {
            return package.ServerCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }

        var path = configuration["Kestrel:Endpoints:Agents:Certificate:Path"];
        if (!configuration.GetValue<bool>("Solvix:Devices:GenerateDevelopmentTlsCertificate") || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration["Kestrel:Endpoints:Agents:Certificate:Password"]);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private static async Task AddFileAsync(ZipArchive zip, string path, string entryName, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(entryName, entryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.Fastest : CompressionLevel.Optimal);
        await using var target = entry.Open();
        await using var source = File.OpenRead(path);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string PackageFileName(string? slug, string tokenName)
    {
        static string Clean(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        var name = Clean(tokenName.Replace(' ', '-'));
        return $"SolvixAgent-{Clean(slug ?? "workspace")}{(name.Length > 0 ? "-" + name : string.Empty)}.zip";
    }

    private static string Readme(AgentEnrollmentFile file) =>
        $"""
        Solvix Agent - {file.WorkspaceName}{(file.Placement is null ? string.Empty : " (" + file.Placement + ")")}

        1. Extract this whole zip to a folder on the Windows computer.
        2. Run Solvix.Agent.exe and approve the Windows administrator prompt.
        3. The agent installs itself as a service and enrolls. The computer then appears in Solvix under
           Endpoints > Devices. enrollment.json is deleted automatically after use.

        For deployment tools (Intune, SCCM, GPO), run silently from the extracted folder:
           Solvix.Agent.exe install --silent

        enrollment.json holds a short-lived, limited-use enrollment token (valid until {file.TokenExpiresAtUtc:u}).
        Treat this zip like a password: do not share it, and delete it after the rollout. The token can be
        revoked in Solvix at any time.

        Supported: Windows 10, Windows 11, Windows Server 2016/2019/2022/2025 (64-bit).
        """;
}
