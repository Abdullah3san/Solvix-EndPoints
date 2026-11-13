namespace Solvix.Agent.Contracts.Software;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

public enum InstallerType
{
    Msi = 1,
    Exe = 2,
}

/// <summary>
/// Parameters of a signed software command. Built once by the server from the deployment's frozen
/// package snapshot, carried inside the signed envelope, and parsed with the same code by the
/// unprivileged agent and by the privileged executor, so all three agree on what is allowed.
/// Nothing in here is ever passed to a shell: arguments become individual process arguments of a
/// command line whose executable and fixed switches are chosen locally by the executor.
/// </summary>
public sealed record SoftwareOperation
{
    public const int MaxArguments = 32;
    public const int MaxArgumentLength = 260;
    public const int MaxTotalArgumentLength = 2048;
    public const int MinTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 4 * 60 * 60;

    public required Guid DeploymentId { get; init; }

    public required Guid TargetId { get; init; }

    public required Guid PackageId { get; init; }

    public required string PackageName { get; init; }

    public string? PackageVersion { get; init; }

    public required InstallerType InstallerType { get; init; }

    /// <summary>Whether the package file must be downloaded (installs, and EXE uninstallers).</summary>
    public bool RequiresPackage { get; init; }

    /// <summary>Lower-case hex SHA-256 of the package file.</summary>
    public string? Sha256 { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>
    /// MSI: public properties (<c>NAME=value</c>). EXE: the installer's own switches (silent mode etc.).
    /// For an uninstall: the uninstall switches of an EXE package; empty for MSI.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public IReadOnlyList<int> SuccessExitCodes { get; init; } = [0];

    public IReadOnlyList<int> RebootExitCodes { get; init; } = [3010, 1641];

    public DetectionRule? Detection { get; init; }

    /// <summary>SHA-256 thumbprint of the Authenticode signer seen when the package was uploaded, if it was signed.</summary>
    public string? ExpectedSignerThumbprint { get; init; }

    /// <summary>The device must refuse the package unless it carries a valid signature from <see cref="ExpectedSignerThumbprint"/>.</summary>
    public bool RequireSignature { get; init; }

    public int TimeoutSeconds { get; init; } = 3600;

    public IReadOnlyDictionary<string, string> ToParameters()
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deploymentId"] = DeploymentId.ToString("D"),
            ["targetId"] = TargetId.ToString("D"),
            ["packageId"] = PackageId.ToString("D"),
            ["packageName"] = PackageName,
            ["installerType"] = InstallerType.ToString(),
            ["requiresPackage"] = RequiresPackage ? "true" : "false",
            ["arguments"] = JsonSerializer.Serialize(Arguments, AgentJsonContext.Default.IReadOnlyListString),
            ["successExitCodes"] = string.Join(',', SuccessExitCodes.Select(c => c.ToString(CultureInfo.InvariantCulture))),
            ["rebootExitCodes"] = string.Join(',', RebootExitCodes.Select(c => c.ToString(CultureInfo.InvariantCulture))),
            ["requireSignature"] = RequireSignature ? "true" : "false",
            ["timeoutSeconds"] = TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };
        if (PackageVersion is not null)
        {
            p["packageVersion"] = PackageVersion;
        }

        if (Sha256 is not null)
        {
            p["sha256"] = Sha256;
            p["sizeBytes"] = SizeBytes.ToString(CultureInfo.InvariantCulture);
        }

        if (Detection is not null)
        {
            p["detection"] = JsonSerializer.Serialize(Detection, AgentJsonContext.Default.DetectionRule);
        }

        if (ExpectedSignerThumbprint is not null)
        {
            p["signer"] = ExpectedSignerThumbprint;
        }

        return p;
    }

    /// <summary>Parses and validates parameters; throws <see cref="FormatException"/> on anything unexpected.</summary>
    public static SoftwareOperation Parse(string commandType, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "deploymentId", "targetId", "packageId", "packageName", "packageVersion", "installerType", "requiresPackage", "sha256", "sizeBytes",
            "arguments", "successExitCodes", "rebootExitCodes", "detection", "signer", "requireSignature", "timeoutSeconds",
        };
        if (parameters.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown)
        {
            throw new FormatException($"Unknown parameter '{unknown}'.");
        }

        var type = Enum.TryParse<InstallerType>(Required(parameters, "installerType"), ignoreCase: false, out var t) && Enum.IsDefined(t)
            ? t
            : throw new FormatException("Invalid installer type.");
        var arguments = JsonSerializer.Deserialize(Required(parameters, "arguments"), AgentJsonContext.Default.IReadOnlyListString)
            ?? throw new FormatException("Invalid arguments.");
        var detection = parameters.TryGetValue("detection", out var d)
            ? JsonSerializer.Deserialize(d, AgentJsonContext.Default.DetectionRule) ?? throw new FormatException("Invalid detection rule.")
            : null;

        var operation = new SoftwareOperation
        {
            DeploymentId = ParseGuid(parameters, "deploymentId"),
            TargetId = ParseGuid(parameters, "targetId"),
            PackageId = ParseGuid(parameters, "packageId"),
            PackageName = Required(parameters, "packageName"),
            PackageVersion = parameters.GetValueOrDefault("packageVersion"),
            InstallerType = type,
            RequiresPackage = ParseBool(parameters, "requiresPackage"),
            Sha256 = parameters.GetValueOrDefault("sha256"),
            SizeBytes = parameters.TryGetValue("sizeBytes", out var size)
                ? long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : throw new FormatException("Invalid size.")
                : 0,
            Arguments = arguments,
            SuccessExitCodes = ParseCodes(parameters, "successExitCodes"),
            RebootExitCodes = ParseCodes(parameters, "rebootExitCodes"),
            Detection = detection,
            ExpectedSignerThumbprint = parameters.GetValueOrDefault("signer"),
            RequireSignature = ParseBool(parameters, "requireSignature"),
            TimeoutSeconds = int.TryParse(Required(parameters, "timeoutSeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out var timeout)
                ? timeout
                : throw new FormatException("Invalid timeout."),
        };

        return operation.Validate(commandType) is { } problem ? throw new FormatException(problem) : operation;
    }

    /// <summary>Returns why this operation is not acceptable for <paramref name="commandType"/>, or null.</summary>
    public string? Validate(string commandType)
    {
        if (string.IsNullOrWhiteSpace(PackageName) || PackageName.Length > 256 || PackageName.Any(char.IsControl))
        {
            return "Invalid package name.";
        }

        if (TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            return $"The timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.";
        }

        if (SuccessExitCodes.Count is 0 or > 16 || RebootExitCodes.Count > 16)
        {
            return "Invalid exit code lists.";
        }

        if (RequiresPackage && (Sha256 is not { Length: 64 } || !Sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') || SizeBytes <= 0))
        {
            return "A package needs a lower-case SHA-256 and a size.";
        }

        if (ExpectedSignerThumbprint is not null && (ExpectedSignerThumbprint.Length != 64 || !ExpectedSignerThumbprint.All(char.IsAsciiHexDigitUpper)))
        {
            return "Invalid signer thumbprint.";
        }

        if (RequireSignature && ExpectedSignerThumbprint is null)
        {
            return "A required signature needs the expected signer.";
        }

        if (InstallerArguments.Validate(InstallerType, Arguments) is { } argumentProblem)
        {
            return argumentProblem;
        }

        if (Detection?.Validate() is { } detectionProblem)
        {
            return detectionProblem;
        }

        return commandType switch
        {
            RemoteManagement.CommandTypes.SoftwareInstall when !RequiresPackage => "An installation needs the package.",
            RemoteManagement.CommandTypes.SoftwareInstall or RemoteManagement.CommandTypes.SoftwareDetect when Detection is null => "A detection rule is required.",
            RemoteManagement.CommandTypes.SoftwareUninstall when InstallerType == InstallerType.Msi && (Detection?.Kind != DetectionKind.MsiProductCode || RequiresPackage || Arguments.Count > 0)
                => "An MSI is removed by its product code, without a package or arguments.",
            RemoteManagement.CommandTypes.SoftwareUninstall when InstallerType == InstallerType.Exe && (!RequiresPackage || Arguments.Count == 0)
                => "An EXE package is removed by running it with its uninstall switches.",
            RemoteManagement.CommandTypes.SoftwareUninstall when Detection is null => "A detection rule is required.",
            RemoteManagement.CommandTypes.SoftwareInstall or RemoteManagement.CommandTypes.SoftwareUninstall or RemoteManagement.CommandTypes.SoftwareDetect => null,
            _ => $"'{commandType}' is not a software command.",
        };
    }

    private static string Required(IReadOnlyDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : throw new FormatException($"Missing parameter '{key}'.");

    private static Guid ParseGuid(IReadOnlyDictionary<string, string> p, string key) =>
        Guid.TryParseExact(Required(p, key), "D", out var id) && id != Guid.Empty ? id : throw new FormatException($"Invalid '{key}'.");

    private static bool ParseBool(IReadOnlyDictionary<string, string> p, string key) => Required(p, key) switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException($"Invalid '{key}'."),
    };

    private static IReadOnlyList<int> ParseCodes(IReadOnlyDictionary<string, string> p, string key)
    {
        var value = p.GetValueOrDefault(key) ?? string.Empty;
        if (value.Length == 0)
        {
            return [];
        }

        return value.Split(',').Select(c => int.TryParse(c, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : throw new FormatException($"Invalid '{key}'.")).ToList();
    }
}

/// <summary>
/// Rules for installer arguments, enforced by the server when a package is configured and again
/// by the agent and the executor before anything runs.
/// </summary>
public static partial class InstallerArguments
{
    /// <summary>Properties the executor sets itself or that would change what msiexec does beyond a plain install.</summary>
    public static readonly IReadOnlySet<string> ReservedMsiProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "REBOOT", "REBOOTPROMPT", "TRANSFORMS", "PATCH", "ACTION", "MSIINSTANCEGUID", "MSINEWINSTANCE", "ADDLOCAL_SOURCE",
        "EXECUTEACTION", "SCRIPTFILE", "LIMITUI", "MSIFASTINSTALL", "MSIRESTARTMANAGERCONTROL", "UILEVEL",
    };

    // Characters a command interpreter would treat specially. Arguments never reach a shell, but some
    // installers re-launch themselves through cmd.exe, so these are refused as defence in depth.
    private static readonly char[] Forbidden = ['"', '|', '&', '<', '>', '^', '%', '`', '\n', '\r', '\t', '\0'];

    public static string? Validate(InstallerType type, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > SoftwareOperation.MaxArguments)
        {
            return $"At most {SoftwareOperation.MaxArguments} arguments are allowed.";
        }

        if (arguments.Sum(a => a?.Length ?? 0) > SoftwareOperation.MaxTotalArgumentLength)
        {
            return $"The arguments may be at most {SoftwareOperation.MaxTotalArgumentLength} characters long in total.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.Length > SoftwareOperation.MaxArgumentLength || argument != argument.Trim())
            {
                return "Arguments must be non-empty, without leading or trailing spaces and at most 260 characters.";
            }

            if (argument.IndexOfAny(Forbidden) >= 0 || argument.Any(char.IsControl))
            {
                return $"The argument '{argument}' contains a character that is not allowed (quotes, |, &, <, >, ^, %, ` or control characters).";
            }

            if (type == InstallerType.Msi)
            {
                var match = MsiProperty().Match(argument);
                if (!match.Success)
                {
                    return $"'{argument}' is not a public MSI property assignment (NAME=value with an upper-case name). msiexec switches are set by the agent.";
                }

                var name = match.Groups["name"].Value;
                if (ReservedMsiProperties.Contains(name))
                {
                    return $"The MSI property {name} is managed by the agent and cannot be set.";
                }

                if (!seen.Add(name))
                {
                    return $"The MSI property {name} is set twice.";
                }
            }
        }

        return null;
    }

    [GeneratedRegex("^(?<name>[A-Z][A-Z0-9_.]{0,63})=(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MsiProperty();
}

/// <summary>Silent-switch presets for common installer frameworks (EXE) and plain MSI.</summary>
public enum SilentPreset
{
    Msi = 1,
    InnoSetup = 2,
    Nsis = 3,
    WixBurn = 4,
    InstallShield = 5,
    Custom = 99,
}

public static class SilentPresets
{
    /// <summary>Default install and uninstall switches of a preset (Custom: none).</summary>
    public static (IReadOnlyList<string> Install, IReadOnlyList<string> Uninstall) Defaults(SilentPreset preset) => preset switch
    {
        SilentPreset.InnoSetup => (["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"], []),
        SilentPreset.Nsis => (["/S"], []),
        SilentPreset.WixBurn => (["/quiet", "/norestart"], ["/uninstall", "/quiet", "/norestart"]),
        SilentPreset.InstallShield => (["/s", "/v/qn"], ["/s", "/x", "/v/qn"]),
        _ => ([], []),
    };
}

public enum DetectionKind
{
    /// <summary>An MSI product code in the Uninstall registry key.</summary>
    MsiProductCode = 1,

    /// <summary>An Add/Remove Programs entry matched by display name (and optionally publisher).</summary>
    UninstallEntry = 2,
}

public enum NameMatch
{
    Exact = 1,
    Prefix = 2,
    Contains = 3,
}

public enum VersionOperator
{
    Any = 1,
    Equal = 2,
    AtLeast = 3,
}

/// <summary>How an installed application is recognised on a device (application version detection).</summary>
public sealed record DetectionRule
{
    public required DetectionKind Kind { get; init; }

    /// <summary>MSI product code, <c>{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</c>.</summary>
    public string? ProductCode { get; init; }

    public string? DisplayName { get; init; }

    public NameMatch NameMatch { get; init; } = NameMatch.Exact;

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public VersionOperator VersionOperator { get; init; } = VersionOperator.Any;

    public string? Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(NameMatch) || !Enum.IsDefined(VersionOperator))
        {
            return "Invalid detection rule.";
        }

        if (Kind == DetectionKind.MsiProductCode && NormalizeProductCode(ProductCode) is null)
        {
            return "The product code must be a GUID in braces, e.g. {12345678-1234-1234-1234-123456789ABC}.";
        }

        if (Kind == DetectionKind.UninstallEntry && (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 256 || DisplayName.Any(char.IsControl)))
        {
            return "Detection by display name needs a name of at most 256 characters.";
        }

        if (Publisher is { Length: > 256 } || Publisher?.Any(char.IsControl) == true)
        {
            return "Invalid publisher.";
        }

        if (VersionOperator != VersionOperator.Any && (string.IsNullOrWhiteSpace(Version) || Version.Length > 64 || !SoftwareVersion.IsValid(Version)))
        {
            return "Version detection needs a version such as 1.2.3.";
        }

        return null;
    }

    /// <summary>Upper-case product code in braces, or null when <paramref name="value"/> is not a GUID.</summary>
    public static string? NormalizeProductCode(string? value) =>
        Guid.TryParseExact(value?.Trim(), "B", out var g) || Guid.TryParseExact(value?.Trim(), "D", out g)
            ? g.ToString("B").ToUpperInvariant()
            : null;
}

/// <summary>An installed application as seen by detection.</summary>
public sealed record DetectedApplication(string Name, string? Version, string? Publisher, string? ProductCode);

public sealed record DetectionResult(bool Installed, bool Satisfied, string? InstalledVersion);

/// <summary>Evaluates a <see cref="DetectionRule"/> against installed applications (agent and server use the same logic).</summary>
public static class DetectionEvaluator
{
    /// <summary>
    /// <c>Installed</c>: some entry matches the identity part of the rule (product code / name / publisher).
    /// <c>Satisfied</c>: one of those also meets the version condition.
    /// </summary>
    public static DetectionResult Evaluate(DetectionRule rule, IEnumerable<DetectedApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(applications);
        var matches = applications.Where(a => Matches(rule, a)).ToList();
        if (matches.Count == 0)
        {
            return new DetectionResult(false, false, null);
        }

        var highest = matches.Select(m => m.Version).OfType<string>().Where(v => !string.IsNullOrWhiteSpace(v)).OrderByDescending(v => v, SoftwareVersion.Comparer).FirstOrDefault();
        var satisfied = rule.VersionOperator switch
        {
            VersionOperator.Equal => matches.Any(m => m.Version is not null && SoftwareVersion.Compare(m.Version, rule.Version!) == 0),
            VersionOperator.AtLeast => matches.Any(m => m.Version is not null && SoftwareVersion.Compare(m.Version, rule.Version!) >= 0),
            _ => true,
        };
        return new DetectionResult(true, satisfied, highest);
    }

    private static bool Matches(DetectionRule rule, DetectedApplication app)
    {
        if (rule.Kind == DetectionKind.MsiProductCode)
        {
            return DetectionRule.NormalizeProductCode(app.ProductCode) is { } code && code == DetectionRule.NormalizeProductCode(rule.ProductCode);
        }

        var name = app.Name.Trim();
        var wanted = rule.DisplayName!.Trim();
        var nameOk = rule.NameMatch switch
        {
            NameMatch.Prefix => name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase),
            NameMatch.Contains => name.Contains(wanted, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase),
        };
        return nameOk && (string.IsNullOrWhiteSpace(rule.Publisher) || string.Equals(app.Publisher?.Trim(), rule.Publisher.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Version comparison for application versions: dot/dash separated segments, numeric segments
/// compared as numbers, missing trailing segments equal to zero ("1.2" == "1.2.0").
/// </summary>
public static class SoftwareVersion
{
    private static readonly char[] Separators = ['.', '-', '_', '+', ' '];

    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    public static bool IsValid(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && char.IsAsciiDigit(value.Trim()[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || Separators.Contains(c));

    public static int Compare(string? left, string? right)
    {
        var a = (left ?? string.Empty).Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var b = (right ?? string.Empty).Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "0";
            var y = i < b.Length ? b[i] : "0";
            int result;
            if (ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var nx) && ulong.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var ny))
            {
                result = nx.CompareTo(ny);
            }
            else
            {
                result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return Math.Sign(result);
            }
        }

        return 0;
    }
}

public enum SoftwareOutcome
{
    Installed = 1,
    AlreadyInstalled = 2,
    Uninstalled = 3,
    NotInstalled = 4,
    Detected = 5,
    NotDetected = 6,
    Failed = 7,
}

/// <summary>Structured output of a software command (JSON in the command result).</summary>
public sealed record SoftwareOperationResult
{
    public const int MaxLogLength = 8 * 1024;

    public required SoftwareOutcome Outcome { get; init; }

    public int? InstallerExitCode { get; init; }

    public bool RebootRequired { get; init; }

    public bool DetectionSatisfied { get; init; }

    public string? DetectedVersion { get; init; }

    public bool PackageVerified { get; init; }

    /// <summary>"Valid", "Unsigned" or the reason a signature was refused.</summary>
    public string? Signature { get; init; }

    /// <summary>download, verify, install, detect.</summary>
    public string? FailedPhase { get; init; }

    /// <summary>Tail of the installer log (MSI verbose log), at most <see cref="MaxLogLength"/> characters.</summary>
    public string? LogExcerpt { get; init; }
}

/// <summary>Error codes of software commands. The server retries the ones in <see cref="Retryable"/>.</summary>
public static class SoftwareErrorCodes
{
    public const string DownloadFailed = "package_download_failed";
    public const string PackageUnavailable = "package_unavailable";
    public const string HashMismatch = "package_hash_mismatch";
    public const string TooLarge = "package_too_large";
    public const string SignatureInvalid = "signature_invalid";
    public const string SignerNotTrusted = "signer_not_trusted";
    public const string UnsignedNotAllowed = "unsigned_not_allowed";
    public const string InstallerTypeNotAllowed = "installer_type_not_allowed";
    public const string DisabledLocally = "software_disabled_locally";
    public const string ExecutorUnavailable = "software_executor_unavailable";
    public const string ExecutorRefused = "software_executor_refused";
    public const string InstallerFailed = "installer_failed";
    public const string InstallerTimeout = "installer_timeout";
    public const string AnotherInstallInProgress = "another_install_in_progress";
    public const string NotDetectedAfterInstall = "not_detected_after_install";
    public const string StillInstalled = "still_installed_after_uninstall";
    public const string InvalidParameters = "invalid_parameters";
    public const string Cancelled = "cancelled";

    public static IReadOnlySet<string> Retryable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        DownloadFailed, AnotherInstallInProgress, ExecutorUnavailable, InstallerTimeout,
    };
}
