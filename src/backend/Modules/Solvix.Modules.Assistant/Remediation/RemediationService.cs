namespace Solvix.Modules.Assistant.Remediation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Configuration;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IRemediationService
{
    /// <summary>Validates and records a proposal. NEVER executes anything unless policy allows read-only auto-execution.</summary>
    Task<ProposalDto> ProposeAsync(ProposeRemediationCommand command, CancellationToken cancellationToken);

    Task<PagedResult<ProposalDto>> ListAsync(ProposalStatus? status, PageRequest page, CancellationToken cancellationToken);

    Task<ProposalDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ProposalDto> ApproveAsync(Guid id, ApproveProposalRequest request, CancellationToken cancellationToken);

    Task<ProposalDto> RejectAsync(Guid id, RejectProposalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The single gate between "the AI suggested something" and "something happens on devices".
/// Permissions, targets, parameters and command safety are decided here and by the command
/// gateway, never by the model. Approval is a human REST call; the model has no tool for it.
/// </summary>
internal sealed partial class RemediationService(
    ISolvixDbContext db,
    IAssistantPrincipalAccessor principals,
    IFleetQueryService fleet,
    IDeviceCommandGateway gateway,
    IAssistantAuditLog audit,
    IOptions<AssistantOptions> options,
    IClock clock,
    ILogger<RemediationService> logger) : IRemediationService
{
    public const TenantRole MinProposerRole = TenantRole.Admin;
    private const string AutoApprovedNote = "[auto] approved by policy: read-only action";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RemediationOptions _options = options.Value.Remediation;

    public static TenantRole RequiredApproverRole(RiskLevel risk) => risk == RiskLevel.High ? TenantRole.Owner : TenantRole.Admin;

    public async Task<ProposalDto> ProposeAsync(ProposeRemediationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var principal = principals.Current;

        if (!principal.HasAtLeast(MinProposerRole))
        {
            await DenyAsync(AuditKinds.ProposalDenied, command.Action, command.ConversationId, null, "Proposing actions requires the Admin role.", cancellationToken);
            throw new ForbiddenException("Proposing device actions requires the Admin or Owner role.");
        }

        try
        {
            return await ProposeCoreAsync(principal, command, cancellationToken);
        }
        catch (Exception ex) when (ex is ValidationException or ServiceUnavailableException)
        {
            await DenyAsync(AuditKinds.ProposalDenied, command.Action, command.ConversationId, null, ex.Message + FormatErrors(ex), cancellationToken);
            throw;
        }
    }

    public async Task<PagedResult<ProposalDto>> ListAsync(ProposalStatus? status, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var principal = principals.Current;
        var query = db.Set<RemediationProposal>().AsNoTracking().AsQueryable();
        if (!principal.HasAtLeast(TenantRole.Admin))
        {
            query = query.Where(p => p.ProposedByUserId == principal.UserId);
        }

        if (status is not null)
        {
            query = query.Where(p => p.Status == status);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(p => p.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<ProposalDto>(items.Select(ToDto).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<ProposalDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await FindVisibleAsync(id, cancellationToken));

    public async Task<ProposalDto> ApproveAsync(Guid id, ApproveProposalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var principal = principals.Current;
        var proposal = await FindVisibleAsync(id, cancellationToken);

        var required = RequiredApproverRole(proposal.Risk);
        if (!principal.HasAtLeast(required))
        {
            await DenyAsync(AuditKinds.ProposalDenied, proposal.Action, proposal.ConversationId, proposal.Id, $"Approval requires the {required} role.", cancellationToken);
            throw new ForbiddenException($"Approving a {proposal.Risk}-risk action requires the {required} role.");
        }

        if (!request.Confirm)
        {
            throw new ValidationException("confirm", "Explicit confirmation is required to approve an action.");
        }

        return await ExecuteApprovedAsync(principal, proposal, request.Note, autoApproved: false, cancellationToken);
    }

    public async Task<ProposalDto> RejectAsync(Guid id, RejectProposalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var principal = principals.Current;
        var proposal = await FindVisibleAsync(id, cancellationToken);
        if (!principal.HasAtLeast(TenantRole.Admin))
        {
            await DenyAsync(AuditKinds.ProposalDenied, proposal.Action, proposal.ConversationId, proposal.Id, "Rejecting requires the Admin role.", cancellationToken);
            throw new ForbiddenException("Rejecting a proposal requires the Admin or Owner role.");
        }

        proposal.Reject(principal.UserId, principal.Email, SecretRedactor.RedactText(request.Reason), clock.UtcNow);
        await SaveAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditRecord(AuditKinds.ProposalRejected, proposal.Action, AuditStatus.Success, proposal.ConversationId, proposal.Id, Summary: request.Reason),
            cancellationToken);
        return ToDto(proposal);
    }

    // ------------------------------------------------------------------ propose

    private async Task<ProposalDto> ProposeCoreAsync(AssistantPrincipal principal, ProposeRemediationCommand command, CancellationToken cancellationToken)
    {
        var definition = ActionCatalog.Find(command.Action)
            ?? throw new ValidationException("action", $"'{command.Action}' is not a supported action. Supported: {string.Join(", ", ActionCatalog.All.Select(a => a.Name))}.");

        var parameters = ActionCatalog.NormalizeParameters(definition, command.Parameters);

        var title = SecretRedactor.RedactText(command.Title).Trim();
        var rationale = SecretRedactor.RedactText(command.Rationale).Trim();
        if (title.Length is 0 or > RemediationProposal.TitleMaxLength)
        {
            throw new ValidationException("title", $"title is required (max {RemediationProposal.TitleMaxLength} characters).");
        }

        if (rationale.Length is 0 or > 2000)
        {
            throw new ValidationException("rationale", "rationale is required (max 2000 characters).");
        }

        if (!fleet.IsAvailable || !gateway.IsAvailable)
        {
            throw new ServiceUnavailableException("No device management backend is connected, so actions cannot be proposed.", "fleet_unavailable");
        }

        var targets = await ResolveTargetsAsync(principal, command, cancellationToken);
        var request = BuildRequest(Guid.CreateVersion7(), definition, parameters, targets, rationale);

        var validation = await gateway.ValidateAsync(principal.ToFleetAccess(), request, cancellationToken);
        if (!validation.IsAllowed)
        {
            throw new ValidationException("action", "The device command service rejected this action: " + string.Join(" ", validation.Problems));
        }

        var risk = definition.BaseRisk;
        if (targets.Count > _options.HighRiskTargetThreshold)
        {
            risk = RiskLevel.High;
        }

        var proposal = new RemediationProposal(
            principal.TenantId,
            command.ConversationId,
            principal.UserId,
            principal.Email,
            definition.Name,
            title,
            rationale,
            risk,
            JsonSerializer.Serialize(parameters, Json),
            JsonSerializer.Serialize(targets.Select(t => new ProposalTargetDto(t.Id, t.Name)).ToList(), Json),
            targets.Count,
            Fingerprint(definition.Name, parameters, targets.Select(t => t.Id)),
            clock.UtcNow.AddMinutes(_options.ProposalTtlMinutes));
        db.Set<RemediationProposal>().Add(proposal);
        await SaveAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditRecord(
                AuditKinds.ProposalCreated,
                definition.Name,
                AuditStatus.Success,
                command.ConversationId,
                proposal.Id,
                Input: new { parameters, targets = targets.Select(t => t.Name), risk = risk.ToString() },
                Summary: title),
            cancellationToken);

        if (_options.AutoExecuteReadOnly && definition.ReadOnly && risk == RiskLevel.Low)
        {
            return await ExecuteApprovedAsync(principal, proposal, AutoApprovedNote, autoApproved: true, cancellationToken);
        }

        return ToDto(proposal);
    }

    private async Task<IReadOnlyList<DeviceSummary>> ResolveTargetsAsync(AssistantPrincipal principal, ProposeRemediationCommand command, CancellationToken cancellationToken)
    {
        var hasRefs = command.DeviceRefs is { Count: > 0 };
        var hasScope = command.Scope is { IsEmpty: false };
        if (hasRefs == hasScope)
        {
            throw new ValidationException("targets", "Specify either explicit devices or a branch/department scope (exactly one).");
        }

        var access = principal.ToFleetAccess();
        IReadOnlyList<DeviceSummary> devices;
        if (hasRefs)
        {
            var refs = command.DeviceRefs!.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (refs.Count > _options.MaxTargetsPerProposal)
            {
                throw new ValidationException("targets", $"Too many devices ({refs.Count}); the limit is {_options.MaxTargetsPerProposal} per proposal.");
            }

            var resolution = await fleet.ResolveAsync(access, refs, cancellationToken);
            if (resolution.NotFound.Count > 0 || resolution.Ambiguous.Count > 0)
            {
                var problems = new List<string>();
                if (resolution.NotFound.Count > 0)
                {
                    problems.Add($"Not found or not accessible: {string.Join(", ", resolution.NotFound)}.");
                }

                if (resolution.Ambiguous.Count > 0)
                {
                    problems.Add($"Ambiguous (use the exact name or id): {string.Join(", ", resolution.Ambiguous)}.");
                }

                throw new ValidationException("devices", string.Join(" ", problems));
            }

            devices = resolution.Devices;
        }
        else
        {
            var criteria = new DeviceSearchCriteria { BranchId = command.Scope!.BranchId, DepartmentIds = command.Scope.DepartmentIds };
            var (items, total) = await fleet.SearchAsync(access, criteria, 0, _options.MaxTargetsPerProposal + 1, cancellationToken);
            if (total > _options.MaxTargetsPerProposal)
            {
                throw new ValidationException("targets", $"The scope contains {total} devices; the limit is {_options.MaxTargetsPerProposal} per proposal. Narrow the scope.");
            }

            devices = items;
        }

        var distinct = devices.DistinctBy(d => d.Id).ToList();
        if (distinct.Count == 0)
        {
            throw new ValidationException("targets", "No matching devices were found.");
        }

        return distinct;
    }

    // ------------------------------------------------------------------ approve

    private async Task<ProposalDto> ExecuteApprovedAsync(AssistantPrincipal approver, RemediationProposal proposal, string? note, bool autoApproved, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (proposal.IsExpiredAt(now))
        {
            proposal.Expire();
            await SaveAsync(cancellationToken);
            await audit.RecordAsync(new AuditRecord(AuditKinds.ProposalExpired, proposal.Action, AuditStatus.Denied, proposal.ConversationId, proposal.Id), cancellationToken);
            throw new ConflictException("The proposal has expired. Ask the assistant to propose it again.");
        }

        if (proposal.Status != ProposalStatus.PendingApproval)
        {
            throw new ConflictException($"The proposal is already {proposal.Status}.");
        }

        var definition = ActionCatalog.Find(proposal.Action)
            ?? throw new ConflictException("The proposal references an action that is no longer supported.");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(proposal.ParametersJson, Json) ?? [];
        var targetRefs = JsonSerializer.Deserialize<List<ProposalTargetDto>>(proposal.TargetsJson, Json) ?? [];

        // Integrity: what is about to run must be exactly what was proposed and shown to the human.
        if (!string.Equals(Fingerprint(proposal.Action, parameters, targetRefs.Select(t => t.Id)), proposal.Fingerprint, StringComparison.Ordinal))
        {
            await DenyAsync(AuditKinds.ProposalDenied, proposal.Action, proposal.ConversationId, proposal.Id, "Fingerprint mismatch: proposal contents changed after creation.", cancellationToken);
            throw new ConflictException("The proposal failed its integrity check and cannot be executed.");
        }

        // Re-validate everything with the APPROVER's access, at execution time.
        var access = approver.ToFleetAccess();
        var resolution = await fleet.ResolveAsync(access, targetRefs.Select(t => t.Id.ToString()).ToList(), cancellationToken);
        if (resolution.Devices.Count != targetRefs.Count)
        {
            await DenyAsync(AuditKinds.ProposalDenied, proposal.Action, proposal.ConversationId, proposal.Id, "Target devices are no longer accessible.", cancellationToken);
            throw new ConflictException("Some target devices no longer exist or are no longer accessible to you. Reject this proposal and request a new one.");
        }

        var request = BuildRequest(proposal.Id, definition, parameters, resolution.Devices, proposal.Rationale);
        var validation = await gateway.ValidateAsync(access, request, cancellationToken);
        if (!validation.IsAllowed)
        {
            await DenyAsync(AuditKinds.ProposalDenied, proposal.Action, proposal.ConversationId, proposal.Id, string.Join(" ", validation.Problems), cancellationToken);
            throw new ConflictException("The device command service rejected this action at execution time: " + string.Join(" ", validation.Problems));
        }

        // Claim: a concurrent approval loses on the xmin concurrency token.
        proposal.BeginExecution(approver.UserId, approver.Email, SecretRedactor.RedactText(note), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The proposal was changed by someone else. Reload and try again.");
        }

        // From here on the operation is committed to: do not abandon it because the HTTP client disconnected.
        var ct = CancellationToken.None;
        try
        {
            await audit.RecordAsync(
                new AuditRecord(AuditKinds.ProposalApproved, proposal.Action, AuditStatus.Success, proposal.ConversationId, proposal.Id,
                    Input: new { approvedBy = approver.Email, autoApproved, note },
                    Summary: $"{proposal.Risk} risk, {proposal.TargetCount} device(s)"),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailure(logger, ex);
            proposal.Complete(ProposalStatus.Failed, "[]", clock.UtcNow);
            await db.SaveChangesAsync(ct);
            throw new ServiceUnavailableException("The audit log is unavailable, so the action was not executed.", "audit_unavailable");
        }

        IReadOnlyList<DeviceCommandOutcome> outcomes;
        try
        {
            outcomes = await gateway.ExecuteAsync(access, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogExecutionFailure(logger, ex, proposal.Id);
            outcomes = resolution.Devices.Select(d => new DeviceCommandOutcome(d.Id, DeviceCommandStatus.Failed, "Execution error.")).ToList();
        }

        var failed = outcomes.Count(o => o.Status == DeviceCommandStatus.Failed);
        var outcome = failed == 0 ? ProposalStatus.Succeeded : failed == outcomes.Count ? ProposalStatus.Failed : ProposalStatus.PartiallyFailed;
        var results = outcomes.Select(o => new ProposalResultDto(o.DeviceId, o.Status.ToString(), SecretRedactor.RedactText(o.Message))).ToList();
        proposal.Complete(outcome, JsonSerializer.Serialize(results, Json), clock.UtcNow);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(
            new AuditRecord(AuditKinds.ProposalExecuted, proposal.Action, outcome == ProposalStatus.Failed ? AuditStatus.Failed : AuditStatus.Success, proposal.ConversationId, proposal.Id,
                Summary: $"{outcome}: {outcomes.Count - failed}/{outcomes.Count} device(s) ok"),
            ct);
        return ToDto(proposal);
    }

    // ------------------------------------------------------------------ helpers

    private static DeviceCommandRequest BuildRequest(Guid idempotencyKey, ActionDefinition definition, IReadOnlyDictionary<string, string> parameters, IEnumerable<DeviceSummary> targets, string reason) =>
        new(idempotencyKey, definition.Type, parameters, targets.Select(t => t.Id).ToList(), reason);

    internal static string Fingerprint(string action, IReadOnlyDictionary<string, string> parameters, IEnumerable<Guid> targetIds)
    {
        var sb = new StringBuilder(action).Append('|');
        foreach (var kv in parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        }

        sb.Append('|');
        foreach (var id in targetIds.OrderBy(i => i))
        {
            sb.Append(id).Append(',');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private async Task<RemediationProposal> FindVisibleAsync(Guid id, CancellationToken cancellationToken)
    {
        var principal = principals.Current;
        var proposal = await db.Set<RemediationProposal>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Proposal", id);
        if (!principal.HasAtLeast(TenantRole.Admin) && proposal.ProposedByUserId != principal.UserId)
        {
            throw new NotFoundException("Proposal", id);
        }

        return proposal;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The proposal was changed by someone else. Reload and try again.");
        }
    }

    private async Task DenyAsync(string kind, string name, Guid? conversationId, Guid? proposalId, string reason, CancellationToken cancellationToken) =>
        await audit.RecordAsync(new AuditRecord(kind, name, AuditStatus.Denied, conversationId, proposalId, Summary: reason), cancellationToken);

    private static string FormatErrors(Exception ex) =>
        ex is ValidationException { Errors.Count: > 0 } v ? " " + string.Join(" ", v.Errors.SelectMany(e => e.Value)) : string.Empty;

    private ProposalDto ToDto(RemediationProposal p)
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(p.ParametersJson, Json) ?? [];
        var targets = JsonSerializer.Deserialize<List<ProposalTargetDto>>(p.TargetsJson, Json) ?? [];
        var results = p.ResultJson is null ? [] : JsonSerializer.Deserialize<List<ProposalResultDto>>(p.ResultJson, Json) ?? [];

        // Expiry is evaluated lazily so lists never show a stale "pending" for an expired proposal.
        var status = p.IsExpiredAt(clock.UtcNow) ? ProposalStatus.Expired : p.Status;
        return new ProposalDto(
            p.Id, p.Action, p.Title, p.Rationale, p.Risk, status, parameters, targets, p.TargetCount,
            p.ProposedByEmail, p.ProposedByUserId, p.CreatedAtUtc, p.ExpiresAtUtc, RequiredApproverRole(p.Risk),
            p.DecidedByEmail, p.DecidedAtUtc, p.DecisionNote, p.ExecutedAtUtc, results,
            p.DecisionNote?.StartsWith("[auto]", StringComparison.Ordinal) == true);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit log write failed while approving a proposal")]
    private static partial void LogAuditFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Device command execution failed for proposal {ProposalId}")]
    private static partial void LogExecutionFailure(ILogger logger, Exception exception, Guid proposalId);
}
