namespace Solvix.Agent.Core.Enrollment;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;

/// <summary>A validated enrollment request: where to enroll, with which one-time token.</summary>
public sealed record EnrollmentRequestSettings(Uri ServerUrl, string Token, string? ServerCertificateSha256)
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
        var json = new JsonObject
        {
            [AgentOptions.SectionName] = new JsonObject
            {
                [nameof(AgentOptions.ServerUrl)] = settings.ServerUrl.AbsoluteUri,
                [nameof(AgentOptions.TrustedServerCertificateSha256)] = settings.ServerCertificateSha256 is null ? new JsonArray() : new JsonArray(settings.ServerCertificateSha256),
            },
        };
        AtomicFile.WriteAllText(MachineSettingsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        bootstrap.Save(settings.Token);
    }
}
