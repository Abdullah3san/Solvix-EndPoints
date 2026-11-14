namespace Solvix.Modules.Assistant.Audit;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Persistence;

/// <summary>Stable audit event kinds.</summary>
public static class AuditKinds
{
    public const string Request = "ai.request";
    public const string Response = "ai.response";
    public const string ToolCall = "ai.tool_call";
    public const string ModelError = "ai.model_error";
    public const string ProposalCreated = "proposal.created";
    public const string ProposalApproved = "proposal.approved";
    public const string ProposalRejected = "proposal.rejected";
    public const string ProposalExpired = "proposal.expired";
    public const string ProposalExecuted = "proposal.executed";
    public const string ProposalDenied = "proposal.denied";
    public const string TicketCreated = "ticket.created";
    public const string ReportGenerated = "report.generated";
    public const string KnowledgeUploaded = "knowledge.uploaded";
    public const string KnowledgeDeleted = "knowledge.deleted";
    public const string KnowledgeReindexed = "knowledge.reindexed";
}

public sealed record AuditRecord(
    string Kind,
    string Name,
    AuditStatus Status,
    Guid? ConversationId = null,
    Guid? ProposalId = null,
    object? Input = null,
    string? Summary = null,
    long DurationMs = 0,
    string? Model = null);

public interface IAssistantAuditLog
{
    /// <summary>Appends an immutable audit entry. Input and summary are redacted before storage.</summary>
    Task RecordAsync(AuditRecord record, CancellationToken cancellationToken);
}

internal sealed class AssistantAuditLog(
    ISolvixDbContext db,
    IAssistantPrincipalAccessor principals,
    IHttpContextAccessor httpContext) : IAssistantAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var principal = principals.Current;
        var input = record.Input is null
            ? null
            : SecretRedactor.RedactText(record.Input as string ?? JsonSerializer.Serialize(record.Input, Json));
        var correlation = httpContext.HttpContext?.Items.TryGetValue("X-Correlation-Id", out var c) == true ? c?.ToString() : null;

        db.Set<AssistantAuditEntry>().Add(new AssistantAuditEntry(
            principal.TenantId,
            record.Kind,
            record.Name,
            record.Status,
            principal.UserId,
            principal.Email,
            record.ConversationId,
            record.ProposalId,
            input,
            SecretRedactor.RedactText(record.Summary),
            record.DurationMs,
            correlation,
            record.Model));
        await db.SaveChangesAsync(cancellationToken);
    }
}
