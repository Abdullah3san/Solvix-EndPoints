namespace Solvix.Modules.Devices.Domain;

using System.Text.RegularExpressions;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>Where a staged computer entry came from.</summary>
public enum StagedComputerSource
{
    Manual = 1,
    Import = 2,

    /// <summary>Reserved for directory discovery (Active Directory, Entra); entries are created by that integration.</summary>
    Directory = 3,
}

/// <summary>
/// A computer the organisation expects to manage but whose agent has not enrolled yet
/// ("yet to install"). Whether it has enrolled is derived by hostname from the device
/// table at read time, so enrollment needs no extra bookkeeping and cannot drift.
/// </summary>
public sealed partial class StagedComputer : TenantEntity
{
    public const int HostnameMaxLength = Device.HostnameMaxLength;

    private StagedComputer()
    {
    }

    private StagedComputer(Guid tenantId, string hostname, Guid? branchId, Guid? departmentId, StagedComputerSource source)
        : base(tenantId)
    {
        Hostname = hostname;
        NormalizedHostname = Normalize(hostname);
        BranchId = branchId;
        DepartmentId = departmentId;
        Source = source;
    }

    public string Hostname { get; private set; } = string.Empty;

    /// <summary>Upper-cased hostname; unique per tenant and the key matched against enrolled devices.</summary>
    public string NormalizedHostname { get; private set; } = string.Empty;

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public StagedComputerSource Source { get; private set; }

    public static StagedComputer Create(Guid tenantId, string hostname, Guid? branchId, Guid? departmentId, StagedComputerSource source) =>
        new(tenantId, ValidateHostname(hostname), branchId, departmentId, source);

    public static string Normalize(string hostname) => hostname.Trim().ToUpperInvariant();

    /// <summary>Letters, digits, dots, hyphens and underscores (NetBIOS and DNS host names); no spaces or separators.</summary>
    public static bool IsValidHostname(string? hostname) =>
        !string.IsNullOrWhiteSpace(hostname) && hostname.Trim().Length <= HostnameMaxLength && HostnamePattern().IsMatch(hostname.Trim());

    public static string ValidateHostname(string? hostname) =>
        IsValidHostname(hostname)
            ? hostname!.Trim()
            : throw new ValidationException("hostname", "Use letters, digits, dots, hyphens and underscores only (e.g. FIN-LT-0142).");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex HostnamePattern();
}
