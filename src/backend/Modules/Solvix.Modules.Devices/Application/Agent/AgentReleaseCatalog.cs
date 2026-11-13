namespace Solvix.Modules.Devices.Application.Agent;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Configuration;

/// <summary>Agent releases offered per channel. Operated by the platform (configuration), not by tenants.</summary>
public interface IAgentReleaseCatalog
{
    AgentReleaseInfo? GetLatest(string channel);

    /// <summary>Whether <paramref name="installedVersion"/> is older than the latest release of the default channel.</summary>
    bool IsUpdateAvailable(string? installedVersion);

    string? LatestVersion { get; }
}

internal sealed partial class AgentReleaseCatalog(IOptionsMonitor<DevicesOptions> options) : IAgentReleaseCatalog
{
    public string? LatestVersion => GetLatest(options.CurrentValue.Agent.DefaultUpdateChannel)?.Version;

    public AgentReleaseInfo? GetLatest(string channel)
    {
        var normalized = NormalizeChannel(channel);
        return options.CurrentValue.Releases
            .Where(r => string.Equals(NormalizeChannel(r.Channel), normalized, StringComparison.Ordinal) && r.DownloadUrl is not null)
            .Select(r => (Release: r, Version: AgentVersion.TryParse(r.Version, out var v) ? v : null))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => new AgentReleaseInfo
            {
                Version = x.Version!.ToString(),
                Channel = normalized,
                DownloadUrl = x.Release.DownloadUrl!,
                Sha256 = x.Release.Sha256.ToLowerInvariant(),
                SizeBytes = x.Release.SizeBytes,
                Mandatory = x.Release.Mandatory,
                PublishedAtUtc = x.Release.PublishedAtUtc,
                ReleaseNotesUrl = x.Release.ReleaseNotesUrl,
            })
            .FirstOrDefault();
    }

    public bool IsUpdateAvailable(string? installedVersion)
    {
        var latest = LatestVersion;
        return latest is not null
            && AgentVersion.TryParse(installedVersion, out var installed)
            && AgentVersion.Parse(latest) > installed;
    }

    public static string NormalizeChannel(string? channel)
    {
        var value = string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant();
        return ChannelPattern().IsMatch(value) ? value : "stable";
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex ChannelPattern();
}

/// <summary>Rejects release entries an agent would refuse anyway, at start-up.</summary>
internal sealed partial class DevicesOptionsValidator : IValidateOptions<DevicesOptions>
{
    public ValidateOptionsResult Validate(string? name, DevicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (!options.CertificateAuthority.IsConfigured)
        {
            failures.Add("Solvix:Devices:CertificateAuthority requires PfxPath, PfxBase64 or AutoGenerate=true.");
        }

        Validate(options.CertificateAuthority, "CertificateAuthority", failures);
        Validate(options.Enrollment, "Enrollment", failures);
        Validate(options.Agent, "Agent", failures);
        Validate(options.Monitoring, "Monitoring", failures);
        var monitoring = options.Monitoring;
        foreach (var error in Monitoring.MonitoringSettings.Validate(options.Agent.HeartbeatIntervalSeconds, monitoring.DefaultWarningAfterSeconds, monitoring.DefaultOfflineAfterSeconds).Values)
        {
            failures.Add("Monitoring defaults: " + error);
        }

        if (options.Enrollment.DefaultTokenLifetimeHours > options.Enrollment.MaxTokenLifetimeHours)
        {
            failures.Add("Enrollment:DefaultTokenLifetimeHours cannot exceed MaxTokenLifetimeHours.");
        }

        if (options.CertificateAuthority.RenewalWindowDays >= options.CertificateAuthority.DeviceCertificateLifetimeDays)
        {
            failures.Add("CertificateAuthority:RenewalWindowDays must be shorter than DeviceCertificateLifetimeDays.");
        }

        for (var i = 0; i < options.Releases.Count; i++)
        {
            var release = options.Releases[i];
            if (!AgentVersion.TryParse(release.Version, out _))
            {
                failures.Add($"Releases[{i}].Version '{release.Version}' is not a valid version.");
            }

            if (release.DownloadUrl is not { IsAbsoluteUri: true } url || url.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add($"Releases[{i}].DownloadUrl must be an absolute https URL.");
            }

            if (!Sha256Pattern().IsMatch(release.Sha256 ?? string.Empty))
            {
                failures.Add($"Releases[{i}].Sha256 must be 64 hexadecimal characters.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Validate(object section, string prefix, List<string> failures)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(section, new System.ComponentModel.DataAnnotations.ValidationContext(section), results, true))
        {
            failures.AddRange(results.Select(r => $"{prefix}: {r.ErrorMessage}"));
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
