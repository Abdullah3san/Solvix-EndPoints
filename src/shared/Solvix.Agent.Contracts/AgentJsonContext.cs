namespace Solvix.Agent.Contracts;

using System.Text.Json;
using System.Text.Json.Serialization;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;

/// <summary>
/// Source-generated serializer metadata matching the API's JSON conventions
/// (camelCase, enums as strings). Used by the agent so it needs no reflection.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EnrollmentRequest))]
[JsonSerializable(typeof(EnrollmentResponse))]
[JsonSerializable(typeof(CertificateRenewalRequest))]
[JsonSerializable(typeof(CertificateRenewalResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(InventoryReport))]
[JsonSerializable(typeof(UpdateCheckResponse))]
[JsonSerializable(typeof(AgentProblem))]
[JsonSerializable(typeof(CommandEnvelope))]
[JsonSerializable(typeof(AgentCommandBatch))]
[JsonSerializable(typeof(CommandAcknowledgement))]
[JsonSerializable(typeof(CommandResultReport))]
[JsonSerializable(typeof(CommandKeySet))]
[JsonSerializable(typeof(DetectionRule))]
[JsonSerializable(typeof(SoftwareOperationResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public sealed partial class AgentJsonContext : JsonSerializerContext
{
}

/// <summary>Subset of an RFC 9457 problem response the agent needs to make decisions.</summary>
public sealed record AgentProblem
{
    public int? Status { get; init; }

    public string? Title { get; init; }

    public string? Detail { get; init; }

    public string? Code { get; init; }
}
