namespace Solvix.Modules.Security.Domain;

using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A versioned security baseline. Lifecycle: Draft → PendingApproval → Active (or Rejected), and Active → Retired
/// when a newer version is approved. A submitted policy is immutable; changing it means creating a new draft.
/// The person who approves must be different from the person who wrote or submitted it ("four eyes").
/// </summary>
public sealed class SecurityPolicy : TenantEntity
{
    public const int NameMaxLength = 120;
    public const int DescriptionMaxLength = 500;
    public const int NoteMaxLength = 500;

    private SecurityPolicy()
    {
    }

    public SecurityPolicy(Guid tenantId, int version, string name, string? description, PolicySettings settings, Guid authorUserId, Guid? basedOnPolicyId)
        : base(tenantId)
    {
        Version = version;
        AuthorUserId = authorUserId;
        BasedOnPolicyId = basedOnPolicyId;
        Status = PolicyStatus.Draft;
        Apply(name, description, settings);
    }

    public int Version { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public PolicyStatus Status { get; private set; }

    public PolicySettings Settings { get; private set; } = PolicySettings.Default;

    public Guid AuthorUserId { get; private set; }

    public Guid? BasedOnPolicyId { get; private set; }

    public Guid? SubmittedByUserId { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    public DateTimeOffset? ActivatedAtUtc { get; private set; }

    public void UpdateDraft(string name, string? description, PolicySettings settings)
    {
        if (Status != PolicyStatus.Draft)
        {
            throw new ConflictException("Only a draft policy can be edited. Create a new version to change an approved policy.");
        }

        Apply(name, description, settings);
    }

    public void Submit(Guid userId, DateTimeOffset now)
    {
        if (Status != PolicyStatus.Draft)
        {
            throw new ConflictException($"Only a draft can be submitted for approval (current status: {Status}).");
        }

        Status = PolicyStatus.PendingApproval;
        SubmittedByUserId = userId;
        SubmittedAtUtc = now;
    }

    /// <summary>Throws unless <paramref name="approverUserId"/> may approve this policy right now. Has no side effects.</summary>
    public void EnsureApprovableBy(Guid approverUserId)
    {
        if (Status != PolicyStatus.PendingApproval)
        {
            throw new ConflictException($"Only a policy pending approval can be approved (current status: {Status}).");
        }

        if (approverUserId == AuthorUserId || approverUserId == SubmittedByUserId)
        {
            throw new ForbiddenException("A policy cannot be approved by the person who wrote or submitted it. Ask a different owner.");
        }
    }

    /// <summary>Activates the policy. The approver must be neither the author nor the submitter.</summary>
    public void Approve(Guid approverUserId, string? note, DateTimeOffset now)
    {
        EnsureApprovableBy(approverUserId);
        Status = PolicyStatus.Active;
        DecidedByUserId = approverUserId;
        DecidedAtUtc = now;
        ActivatedAtUtc = now;
        DecisionNote = Guard.Optional(note, "note", NoteMaxLength);
    }

    public void Reject(Guid approverUserId, string note, DateTimeOffset now)
    {
        if (Status != PolicyStatus.PendingApproval)
        {
            throw new ConflictException($"Only a policy pending approval can be rejected (current status: {Status}).");
        }

        Status = PolicyStatus.Rejected;
        DecidedByUserId = approverUserId;
        DecidedAtUtc = now;
        DecisionNote = Guard.NotNullOrWhiteSpace(note, "note", NoteMaxLength);
    }

    /// <summary>The author withdraws a pending submission and returns it to Draft.</summary>
    public void Withdraw(Guid userId)
    {
        if (Status != PolicyStatus.PendingApproval)
        {
            throw new ConflictException("Only a policy pending approval can be withdrawn.");
        }

        if (userId != SubmittedByUserId && userId != AuthorUserId)
        {
            throw new ForbiddenException("Only the author or submitter can withdraw a submission.");
        }

        Status = PolicyStatus.Draft;
        SubmittedByUserId = null;
        SubmittedAtUtc = null;
    }

    public void Retire()
    {
        if (Status != PolicyStatus.Active)
        {
            throw new ConflictException("Only an active policy can be retired.");
        }

        Status = PolicyStatus.Retired;
    }

    private void Apply(string name, string? description, PolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new ValidationException(problems.ToDictionary(p => CamelCase(p.Key), p => new[] { p.Value }));
        }

        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
        Settings = settings;
    }

    private static string CamelCase(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
