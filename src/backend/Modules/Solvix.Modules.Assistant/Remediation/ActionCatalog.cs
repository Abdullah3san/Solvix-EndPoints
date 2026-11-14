namespace Solvix.Modules.Assistant.Remediation;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Solvix.Modules.Assistant.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;

public enum ParameterKind
{
    /// <summary>A short identifier such as a Windows service name or package id. Letters, digits and <c>._-$</c> only.</summary>
    Identifier = 1,

    /// <summary>A version string such as <c>1.2.3</c>.</summary>
    Version = 2,

    Integer = 3,
}

public sealed record ParameterSpec(string Name, ParameterKind Kind, bool Required, string Description, int Min = 0, int Max = 0, string? Default = null);

public sealed record ActionDefinition(
    string Name,
    DeviceCommandType Type,
    RiskLevel BaseRisk,
    bool ReadOnly,
    string Description,
    IReadOnlyList<ParameterSpec> Parameters);

/// <summary>
/// The closed catalog of actions the assistant may propose. Anything not listed here is rejected.
/// There is no parameter that can carry a script or command line; string parameters are
/// restricted to short identifier patterns and are handed to fixed handlers by the command module.
/// </summary>
public static class ActionCatalog
{
    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.$-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex VersionPattern = new(@"^[0-9][0-9A-Za-z._+-]{0,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<ActionDefinition> All { get; } =
    [
        new("refresh_inventory", DeviceCommandType.RefreshInventory, RiskLevel.Low, true, "Ask the agent to re-collect hardware and software inventory.", []),
        new("collect_diagnostics", DeviceCommandType.CollectDiagnostics, RiskLevel.Low, true, "Collect a read-only diagnostics bundle (performance counters, event log summary).", []),
        new("clear_temp_files", DeviceCommandType.ClearTempFiles, RiskLevel.Low, false, "Delete temporary files and caches using the platform's standard cleanup handler.", []),
        new("restart_service", DeviceCommandType.RestartService, RiskLevel.Medium, false, "Restart one named service.",
            [new("serviceName", ParameterKind.Identifier, true, "Service name (identifier only, e.g. 'Spooler').")]),
        new("restart_device", DeviceCommandType.RestartDevice, RiskLevel.Medium, false, "Restart the computer after an optional delay.",
            [new("delayMinutes", ParameterKind.Integer, false, "Minutes to wait before restarting (0-1440).", 0, 1440, "5")]),
        new("update_software", DeviceCommandType.UpdateSoftware, RiskLevel.Medium, false, "Update an already-installed package from the approved software catalog.",
            [
                new("packageId", ParameterKind.Identifier, true, "Approved catalog package id."),
                new("version", ParameterKind.Version, false, "Target version; latest approved if omitted."),
            ]),
        new("deploy_software", DeviceCommandType.DeploySoftware, RiskLevel.High, false, "Install a package from the approved software catalog.",
            [
                new("packageId", ParameterKind.Identifier, true, "Approved catalog package id."),
                new("version", ParameterKind.Version, false, "Target version; latest approved if omitted."),
            ]),
        new("uninstall_software", DeviceCommandType.UninstallSoftware, RiskLevel.High, false, "Remove a package that was installed from the approved software catalog.",
            [new("packageId", ParameterKind.Identifier, true, "Approved catalog package id.")]),
    ];

    public static ActionDefinition? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates model-supplied parameters against the action's spec. Unknown parameters are errors
    /// (the model cannot smuggle extra fields through). Returns the normalised string map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NormalizeParameters(ActionDefinition action, IReadOnlyDictionary<string, JsonElement>? supplied)
    {
        ArgumentNullException.ThrowIfNull(action);
        supplied ??= new Dictionary<string, JsonElement>();
        var errors = new Dictionary<string, string[]>();
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in supplied.Keys)
        {
            if (!action.Parameters.Any(p => string.Equals(p.Name, key, StringComparison.Ordinal)))
            {
                errors[key] = [$"Unknown parameter '{key}' for action '{action.Name}'."];
            }
        }

        foreach (var spec in action.Parameters)
        {
            supplied.TryGetValue(spec.Name, out var element);
            var raw = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.Undefined or JsonValueKind.Null => null,
                _ => "\u0000invalid",
            };

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (spec.Default is not null)
                {
                    result[spec.Name] = spec.Default;
                }
                else if (spec.Required)
                {
                    errors[spec.Name] = [$"Parameter '{spec.Name}' is required."];
                }

                continue;
            }

            raw = raw.Trim();
            var error = spec.Kind switch
            {
                ParameterKind.Identifier => IdentifierPattern.IsMatch(raw) ? null : "must be a short identifier (letters, digits, '.', '_', '-', '$').",
                ParameterKind.Version => VersionPattern.IsMatch(raw) ? null : "must be a version string such as 1.2.3.",
                ParameterKind.Integer => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= spec.Min && n <= spec.Max
                    ? null
                    : $"must be an integer between {spec.Min} and {spec.Max}.",
                _ => "unsupported.",
            };
            if (error is not null)
            {
                errors[spec.Name] = [$"Parameter '{spec.Name}' {error}"];
                continue;
            }

            result[spec.Name] = raw;
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return result;
    }
}
