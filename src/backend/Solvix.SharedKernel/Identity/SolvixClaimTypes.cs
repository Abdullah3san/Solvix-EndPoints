namespace Solvix.SharedKernel.Identity;

/// <summary>Claim names used in Solvix access tokens.</summary>
public static class SolvixClaimTypes
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string TenantId = "tid";
    public const string TenantSlug = "tslug";
    /// <summary>Id of the authenticated device (device certificates only).</summary>
    public const string DeviceId = "device_id";

    /// <summary>Id of the device credential (certificate) used for this request.</summary>
    public const string DeviceCredentialId = "device_cred";

    /// <summary>Expiry of the presented device certificate (unix seconds).</summary>
    public const string DeviceCredentialExpires = "device_cred_exp";

    /// <summary>Id of the server-side session; a token is only honoured while that session is active.</summary>
    public const string SessionId = "sid";

    /// <summary>Authentication methods used for the session ("pwd", "mfa").</summary>
    public const string AuthenticationMethod = "amr";
}
