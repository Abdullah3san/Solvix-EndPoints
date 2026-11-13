namespace Solvix.Modules.Mdm;

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>Configuration section <c>Solvix:Mdm</c>.</summary>
public sealed class MdmOptions
{
    public const string SectionName = "Solvix:Mdm";

    /// <summary>
    /// Base64 AES-256 key (32 bytes) protecting secrets stored in policies and devices
    /// (Wi-Fi/VPN pre-shared keys, Apple unlock tokens). Required outside Development/Testing.
    /// </summary>
    public string? SecretEncryptionKey { get; set; }

    /// <summary>Public https base URL of the API, used in enrollment profiles and callbacks.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Hours a queued or unapproved command stays valid.</summary>
    [Range(1, 24 * 14)]
    public int CommandTimeToLiveHours { get; set; } = 24;

    /// <summary>
    /// Allow the requester of a destructive command to approve it. Off by default
    /// (four-eyes principle); enable only for single-administrator workspaces.
    /// </summary>
    public bool AllowSelfApproval { get; set; }

    public AndroidMdmOptions Android { get; set; } = new();

    public AppleMdmOptions Apple { get; set; } = new();

    public MdmSyncOptions Sync { get; set; } = new();
}

public sealed class AndroidMdmOptions
{
    /// <summary>Google Cloud project id that owns the Android Management API enterprises.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Service account key JSON. Keep in a secret store; never in source control.</summary>
    public string? ServiceAccountJson { get; set; }

    public string ApiBaseUrl { get; set; } = "https://androidmanagement.googleapis.com/v1/";

    public string TokenUrl { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>Optional Cloud Pub/Sub topic for device notifications.</summary>
    public string? PubSubTopic { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(ServiceAccountJson);
}

public sealed class AppleMdmOptions
{
    /// <summary>MDM push topic (the UID of the APNs MDM certificate), e.g. <c>com.apple.mgmt.External.xxxxxxxx</c>.</summary>
    public string? PushTopic { get; set; }

    /// <summary>Path to the PKCS#12 APNs MDM push certificate.</summary>
    public string? PushCertificatePath { get; set; }

    public string? PushCertificatePassword { get; set; }

    public string ApnsHost { get; set; } = "https://api.push.apple.com";

    /// <summary>PEM of the CA that issues device identity certificates (SCEP). Required to accept devices in production.</summary>
    public string? IdentityCaCertificatePem { get; set; }

    /// <summary>SCEP enrollment URL placed in the enrollment profile.</summary>
    public string? ScepUrl { get; set; }

    /// <summary>SCEP challenge placed in the enrollment profile.</summary>
    public string? ScepChallenge { get; set; }

    /// <summary>Organisation name shown on the device when installing the profile.</summary>
    public string OrganizationName { get; set; } = "Solvix EndPoints";

    /// <summary>
    /// Development only: accept device signatures without chaining them to <see cref="IdentityCaCertificatePem"/>.
    /// Never enable in production.
    /// </summary>
    public bool AllowUntrustedDeviceIdentity { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PushTopic);
}

public sealed class MdmSyncOptions
{
    /// <summary>Run the periodic inventory / compliance / expiry worker.</summary>
    public bool Enabled { get; set; }

    [Range(1, 24 * 60)]
    public int IntervalMinutes { get; set; } = 15;
}

/// <summary>Startup validation: misconfiguration must stop the host, not surface as a 500 on first use.</summary>
internal sealed class MdmOptionsValidator(IHostEnvironment environment) : IValidateOptions<MdmOptions>
{
    public ValidateOptionsResult Validate(string? name, MdmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        if (string.IsNullOrWhiteSpace(options.SecretEncryptionKey))
        {
            if (!relaxed)
            {
                failures.Add($"{MdmOptions.SectionName}:{nameof(MdmOptions.SecretEncryptionKey)} is required outside Development (base64, 32 bytes).");
            }
        }
        else if (!TryDecode(options.SecretEncryptionKey, out var key) || key.Length != 32)
        {
            failures.Add($"{MdmOptions.SectionName}:{nameof(MdmOptions.SecretEncryptionKey)} must be base64 that decodes to exactly 32 bytes.");
        }

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var baseUrl))
        {
            failures.Add($"{MdmOptions.SectionName}:{nameof(MdmOptions.PublicBaseUrl)} must be an absolute URL.");
        }
        else if (!relaxed && baseUrl.Scheme != Uri.UriSchemeHttps && (options.Apple.IsConfigured || options.Android.IsConfigured))
        {
            failures.Add($"{MdmOptions.SectionName}:{nameof(MdmOptions.PublicBaseUrl)} must be https: devices will not enroll over plain http.");
        }

        if (options.Apple.AllowUntrustedDeviceIdentity && !relaxed)
        {
            failures.Add($"{MdmOptions.SectionName}:Apple:{nameof(AppleMdmOptions.AllowUntrustedDeviceIdentity)} must not be enabled outside Development.");
        }

        if (options.Apple.IsConfigured && !relaxed && string.IsNullOrWhiteSpace(options.Apple.IdentityCaCertificatePem))
        {
            failures.Add($"{MdmOptions.SectionName}:Apple:{nameof(AppleMdmOptions.IdentityCaCertificatePem)} is required so device identities can be verified.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
