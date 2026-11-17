namespace Solvix.Agent.Contracts;

using System.Text.Json;
using System.Text.Json.Serialization;

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
[JsonSerializable(typeof(SubmitTicketRequest))]
[JsonSerializable(typeof(AgentTicket))]
[JsonSerializable(typeof(AgentTicketList))]
[JsonSerializable(typeof(SendChatMessageRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatMessageList))]
[JsonSerializable(typeof(PendingRemoteResponse))]
[JsonSerializable(typeof(AcceptRemoteRequest))]
[JsonSerializable(typeof(DenyRemoteRequest))]
[JsonSerializable(typeof(EndRemoteRequest))]
[JsonSerializable(typeof(RemoteOutputRequest))]
[JsonSerializable(typeof(RemoteInputResponse))]
[JsonSerializable(typeof(ScreenInputEvent))]
[JsonSerializable(typeof(PendingFileOperationResponse))]
[JsonSerializable(typeof(BeginFileDownloadRequest))]
[JsonSerializable(typeof(FileOperationResult))]
[JsonSerializable(typeof(AgentProblem))]
[JsonSerializable(typeof(AgentEnrollmentFile))]
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
