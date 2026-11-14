namespace Solvix.Agent.Contracts;

/// <summary>
/// Relative routes of the agent API. The agent only ever initiates outbound
/// HTTPS requests to these routes; the server never connects to the agent.
/// </summary>
public static class AgentRoutes
{
    public const string Base = "api/v1/agent";

    /// <summary>Anonymous; authenticated by a short-lived enrollment token in the body.</summary>
    public const string Enroll = Base + "/enroll";

    /// <summary>Device certificate (mutual TLS).</summary>
    public const string Heartbeat = Base + "/heartbeat";

    /// <summary>Device certificate (mutual TLS).</summary>
    public const string Inventory = Base + "/inventory";

    /// <summary>Device certificate (mutual TLS).</summary>
    public const string RenewCertificate = Base + "/certificate";

    /// <summary>Device certificate (mutual TLS).</summary>
    public const string Updates = Base + "/updates";
}

/// <summary>Well-known identifiers embedded in device certificates.</summary>
public static class DeviceIdentityUri
{
    /// <summary>
    /// Device certificates carry a URI subject alternative name of the form
    /// <c>urn:solvix:device:{tenantId}:{deviceId}</c>. Only the platform device
    /// CA can mint such a certificate, so the URI is trustworthy once the chain
    /// has been validated.
    /// </summary>
    public const string Prefix = "urn:solvix:device:";

    public static string Format(Guid tenantId, Guid deviceId) =>
        $"{Prefix}{tenantId:D}:{deviceId:D}";

    public static bool TryParse(string? value, out Guid tenantId, out Guid deviceId)
    {
        tenantId = Guid.Empty;
        deviceId = Guid.Empty;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value[Prefix.Length..].Split(':');
        return parts.Length == 2
            && Guid.TryParseExact(parts[0], "D", out tenantId)
            && Guid.TryParseExact(parts[1], "D", out deviceId)
            && tenantId != Guid.Empty
            && deviceId != Guid.Empty;
    }
}
