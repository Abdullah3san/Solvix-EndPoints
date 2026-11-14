namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

public sealed class AssistantReport : TenantEntity
{
    public const int TitleMaxLength = 200;

    private AssistantReport()
    {
    }

    public AssistantReport(Guid tenantId, string title, ReportKind kind, string contentMarkdown, string parametersJson, bool narrativeIncluded, string? model, Guid generatedByUserId, string? generatedByEmail, Guid? conversationId)
        : base(tenantId)
    {
        Title = Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength);
        Kind = kind;
        ContentMarkdown = contentMarkdown;
        ParametersJson = parametersJson;
        NarrativeIncluded = narrativeIncluded;
        Model = model;
        GeneratedByUserId = generatedByUserId;
        GeneratedByEmail = generatedByEmail;
        ConversationId = conversationId;
    }

    public string Title { get; private set; } = string.Empty;

    public ReportKind Kind { get; private set; }

    public string ContentMarkdown { get; private set; } = string.Empty;

    public string ParametersJson { get; private set; } = "{}";

    /// <summary>True when the AI-written narrative section is present (facts tables are always backend-generated).</summary>
    public bool NarrativeIncluded { get; private set; }

    public string? Model { get; private set; }

    public Guid GeneratedByUserId { get; private set; }

    public string? GeneratedByEmail { get; private set; }

    public Guid? ConversationId { get; private set; }
}
