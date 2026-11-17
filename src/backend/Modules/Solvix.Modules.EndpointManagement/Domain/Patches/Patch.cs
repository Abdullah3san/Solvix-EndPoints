namespace Solvix.Modules.EndpointManagement.Domain.Patches;

using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public enum PatchApprovalState
{
    NotReviewed = 1,
    Approved = 2,
    Declined = 3,
}

/// <summary>
/// A Windows update as reported by the tenant's agents (WUA identity: update id +
/// revision). Approval is tenant-wide: only approved patches can be deployed.
/// </summary>
public sealed class Patch : TenantEntity
{
    public const int UpdateIdMaxLength = 64;
    public const int TitleMaxLength = 500;
    public const int UrlMaxLength = 1000;
    public const int NoteMaxLength = 1000;

    private Patch()
    {
    }

    private Patch(Guid tenantId, string updateId, int revisionNumber, DateTimeOffset now)
        : base(tenantId)
    {
        UpdateId = updateId;
        RevisionNumber = revisionNumber;
        FirstSeenAtUtc = now;
        LastSeenAtUtc = now;
        ApprovalState = PatchApprovalState.NotReviewed;
    }

    public string UpdateId { get; private set; } = string.Empty;

    public int RevisionNumber { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string[] KbArticleIds { get; private set; } = [];

    public string[] CveIds { get; private set; } = [];

    public UpdateClassification Classification { get; private set; }

    public MsrcSeverity Severity { get; private set; }

    public DateTimeOffset? ReleaseDateUtc { get; private set; }

    public long? MaxDownloadSizeBytes { get; private set; }

    public RebootBehavior RebootBehavior { get; private set; }

    public string? SupportUrl { get; private set; }

    public DateTimeOffset FirstSeenAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public PatchApprovalState ApprovalState { get; private set; }

    public Guid? ApprovalDecidedByUserId { get; private set; }

    public DateTimeOffset? ApprovalDecidedAtUtc { get; private set; }

    public string? ApprovalNote { get; private set; }

    /// <summary>Set when approval came from an auto-approval rule rather than a person.</summary>
    public Guid? AutoApprovalRuleId { get; private set; }

    /// <summary>"Security update" in the broad sense used by compliance reports.</summary>
    public bool IsSecurityRelevant =>
        Classification is UpdateClassification.SecurityUpdates or UpdateClassification.CriticalUpdates
        || Severity is MsrcSeverity.Critical or MsrcSeverity.Important;

    public static Patch Discover(Guid tenantId, UpdateInfo info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        var patch = new Patch(tenantId, Guard.NotNullOrWhiteSpace(info.UpdateId, "updateId", UpdateIdMaxLength).ToLowerInvariant(), info.RevisionNumber, now);
        patch.Refresh(info, now);
        return patch;
    }

    public void Refresh(UpdateInfo info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        Title = Truncate(info.Title, TitleMaxLength) ?? UpdateId;
        KbArticleIds = info.KbArticleIds.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => Truncate(k.Trim(), 32)!).Distinct().Take(20).ToArray();
        CveIds = info.CveIds.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => Truncate(c.Trim().ToUpperInvariant(), 32)!).Distinct().Take(200).ToArray();
        Classification = info.Classification;
        Severity = info.Severity;
        ReleaseDateUtc = info.ReleaseDateUtc;
        MaxDownloadSizeBytes = info.MaxDownloadSizeBytes;
        RebootBehavior = info.RebootBehavior;
        SupportUrl = Truncate(info.SupportUrl, UrlMaxLength);
        LastSeenAtUtc = now;
    }

    public void Approve(Guid userId, string? note, DateTimeOffset now) =>
        Decide(PatchApprovalState.Approved, userId, null, note, now);

    public void Decline(Guid userId, string? note, DateTimeOffset now) =>
        Decide(PatchApprovalState.Declined, userId, null, note, now);

    public void AutoApprove(Guid ruleId, string ruleName, DateTimeOffset now)
    {
        if (ApprovalState != PatchApprovalState.NotReviewed)
        {
            return;
        }

        Decide(PatchApprovalState.Approved, null, ruleId, $"Auto-approved by rule '{ruleName}'.", now);
    }

    /// <summary>Returns the patch to "not reviewed"; allowed only while it is not part of an active deployment (checked by the service).</summary>
    public void ResetApproval(Guid userId, string? note, DateTimeOffset now) =>
        Decide(PatchApprovalState.NotReviewed, userId, null, note, now);

    private void Decide(PatchApprovalState state, Guid? userId, Guid? ruleId, string? note, DateTimeOffset now)
    {
        if (note is { Length: > NoteMaxLength })
        {
            throw new ValidationException("note", $"note must be at most {NoteMaxLength} characters.");
        }

        ApprovalState = state;
        ApprovalDecidedByUserId = userId;
        AutoApprovalRuleId = ruleId;
        ApprovalDecidedAtUtc = now;
        ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
