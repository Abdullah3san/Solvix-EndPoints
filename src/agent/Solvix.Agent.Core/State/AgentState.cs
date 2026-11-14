namespace Solvix.Agent.Core.State;

using System.Text.Json;
using System.Text.Json.Serialization;
using Solvix.Agent.Contracts;

/// <summary>Persistent, non-secret agent state (identity and schedule bookkeeping).</summary>
public sealed record AgentState
{
    public Guid? DeviceId { get; init; }

    public Guid? TenantId { get; init; }

    public Uri? ServerUrl { get; init; }

    public DateTimeOffset? EnrolledAtUtc { get; init; }

    /// <summary>Name of the private key of the active device certificate in the key store.</summary>
    public string? KeyName { get; init; }

    public DateTimeOffset? CertificateNotAfterUtc { get; init; }

    public DateTimeOffset? CertificateIssuedAtUtc { get; init; }

    /// <summary>Key of the previous certificate, deleted once the new one is accepted.</summary>
    public string? RetiredKeyName { get; init; }

    public string? LastInventoryHash { get; init; }

    public DateTimeOffset? LastInventoryAtUtc { get; init; }

    public DateTimeOffset? LastSuccessfulContactAtUtc { get; init; }

    public AgentConfiguration Configuration { get; init; } = new();

    [JsonIgnore]
    public bool IsEnrolled => DeviceId is not null && KeyName is not null;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentState))]
[JsonSerializable(typeof(StagedUpdate))]
internal sealed partial class AgentStateJsonContext : JsonSerializerContext
{
}

/// <summary>A verified package waiting for the privileged updater.</summary>
public sealed record StagedUpdate
{
    public required string Version { get; init; }

    public required string PackagePath { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset StagedAtUtc { get; init; }
}
