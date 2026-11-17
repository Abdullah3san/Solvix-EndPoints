namespace Solvix.Agent.Core.Enrollment;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;

/// <summary>A validated enrollment request: where to enroll, with which one-time token.</summary>
public sealed record EnrollmentRequestSettings(
    Uri ServerUrl,
    string Token,
    string? ServerCertificateSha256,
    RemoteAccessMode? RemoteAccess = null,
    RemoteFeatures RemoteFeatures = RemoteFeatures.Shell,
    IReadOnlyList<string>? FileFolders = null)
{
    /// <summary>Reads and validates <c>enrollment.json</c>; returns an error message instead of throwing.</summary>
    public static EnrollmentRequestSettings? TryLoad(string path, DateTimeOffset now, out string? error)
    {
        error = null;
        AgentEnrollmentFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.AgentEnrollmentFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"{Path.GetFileName(path)} could not be read: {ex.Message}";
            return null;
        }

        if (file is null || file.FormatVersion != AgentEnrollmentFile.CurrentFormatVersion)
        {
            error = "Unsupported enrollment file format.";
            return null;
        }

        if (file.TokenExpiresAtUtc is { } expires && expires <= now)
        {
            error = $"The enrollment token in this package expired on {expires:u}. Download a new package from Solvix.";
            return null;
        }

        return Validate(file.ServerUrl, file.EnrollmentToken, file.ServerCertificateSha256, out error);
    }

    public static EnrollmentRequestSettings? Validate(string? serverUrl, string? token, string? pin, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            error = "The server URL must be an absolute https URL.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(token) || !token.Trim().StartsWith("svxe_", StringComparison.Ordinal))
        {
            error = "The enrollment token is missing or malformed.";
            return null;
        }

        var normalizedPin = pin?.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        if (!string.IsNullOrEmpty(normalizedPin) && (normalizedPin.Length != 64 || !normalizedPin.All(Uri.IsHexDigit)))
        {
            error = "The server certificate pin must be a SHA-256 thumbprint (64 hex characters).";
            return null;
        }

        return new EnrollmentRequestSettings(url, token.Trim(), string.IsNullOrEmpty(normalizedPin) ? null : normalizedPin.ToUpperInvariant());
    }
}

/// <summary>
/// Persists what the agent needs to enroll on its next start: non-secret machine settings
/// (<c>agent.json</c>) and the one-time token, encrypted at rest (DPAPI on Windows).
/// </summary>
public sealed class EnrollmentSetup(IOptions<AgentOptions> options, BootstrapTokenStore bootstrap)
{
    public const string MachineSettingsFileName = "agent.json";

    public string MachineSettingsPath => Path.Combine(options.Value.ResolveDataDirectory(), MachineSettingsFileName);

    public void Apply(EnrollmentRequestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var section = new JsonObject
        {
            [nameof(AgentOptions.ServerUrl)] = settings.ServerUrl.AbsoluteUri,
            [nameof(AgentOptions.TrustedServerCertificateSha256)] = settings.ServerCertificateSha256 is null ? new JsonArray() : new JsonArray(settings.ServerCertificateSha256),
        };
        if (settings.RemoteAccess is { } mode && mode != RemoteAccessMode.Disabled)
        {
            // A local decision taken by whoever installs the agent; it never comes from the server.
            var remote = new JsonObject
            {
                [nameof(RemoteAccessOptions.Mode)] = mode.ToString(),
                [nameof(RemoteAccessOptions.Features)] = settings.RemoteFeatures.ToString(),
            };
            if (settings.FileFolders is { Count: > 0 })
            {
                remote[nameof(RemoteAccessOptions.FileFolders)] = new JsonArray(settings.FileFolders.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
            }

            section[nameof(AgentOptions.RemoteAccess)] = remote;
        }

        var json = new JsonObject { [AgentOptions.SectionName] = section };
        AtomicFile.WriteAllText(MachineSettingsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        bootstrap.Save(settings.Token);
    }
}


/// <summary>Parses the installer's <c>--remote-access</c> choice.</summary>
public static class RemoteAccessChoice
{
    public const string Usage = "--remote-access consent|unattended";

    /// <summary>No value means disabled. Returns false for an unrecognised value.</summary>
    public static bool TryParse(string? value, out RemoteAccessMode mode)
    {
        mode = RemoteAccessMode.Disabled;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "consent":
                mode = RemoteAccessMode.ConsentRequired;
                return true;
            case "unattended":
                mode = RemoteAccessMode.Unattended;
                return true;
            case "off" or "disabled" or "none":
                return true;
            default:
                return false;
        }
    }
}


/// <summary>Parses the installer's <c>--remote-features</c> and <c>--files-folder</c> choices.</summary>
public static class RemoteFeatureChoice
{
    public const string Usage = "--remote-features screen,files,control   (a shell is always included when remote access is on)";

    /// <summary>A comma-separated list of shell, screen, files, control. Empty keeps the default (shell only).</summary>
    public static bool TryParse(string? value, out RemoteFeatures features, out string? error)
    {
        features = RemoteFeatures.Shell;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "shell":
                    features |= RemoteFeatures.Shell;
                    break;
                case "screen":
                    features |= RemoteFeatures.Screen;
                    break;
                case "files":
                    features |= RemoteFeatures.Files;
                    break;
                case "control":
                    features |= RemoteFeatures.Screen | RemoteFeatures.Control;
                    break;
                default:
                    error = $"Unknown remote feature '{part}'. Use {Usage}.";
                    return false;
            }
        }

        return true;
    }

    /// <summary>Folders must be absolute, existing-or-creatable paths; a drive root or the Windows folder is refused.</summary>
    public static bool TryValidateFolder(string? folder, out string full, out string? error)
    {
        full = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
        {
            error = "A transfer folder must be a full path such as C:\\Users\\Public\\Solvix.";
            return false;
        }

        var absolute = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(absolute) ?? string.Empty;
        full = absolute.Length > root.Length ? absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : absolute;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (absolute.Length <= root.Length
            || (!string.IsNullOrEmpty(system) && full.StartsWith(system, StringComparison.OrdinalIgnoreCase)))
        {
            error = "A whole drive or the Windows folder cannot be offered for transfer.";
            return false;
        }

        return true;
    }
}
