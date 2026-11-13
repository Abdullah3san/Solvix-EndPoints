namespace Solvix.Modules.Mdm.Domain;

/// <summary>A point-in-time report from a platform provider. Null means "not reported".</summary>
public sealed record DeviceInventory
{
    public string? DisplayName { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? OsVersion { get; init; }

    public string? SecurityPatchLevel { get; init; }

    public bool? IsEncrypted { get; init; }

    public bool? HasPasscode { get; init; }

    public bool? IsSupervised { get; init; }

    public IntegrityState Integrity { get; init; } = IntegrityState.Unknown;

    public IReadOnlyList<InstalledApp>? Apps { get; init; }

    /// <summary>The provider's own compliance verdict, if it has one (Android Management API).</summary>
    public bool? ProviderReportsCompliant { get; init; }

    public DateTimeOffset? LastSeenAtUtc { get; init; }

    /// <summary>Policy name/identifier the device reports it has applied.</summary>
    public string? AppliedPolicyReference { get; init; }
}

public sealed record InstalledApp(string Identifier, string? Name, string? Version, bool IsManaged);

public sealed record ComplianceViolation(string Rule, string Message);
