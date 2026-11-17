namespace Solvix.Modules.Helpdesk.Application;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface ICatalogService
{
    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<CategoryDto> SaveCategoryAsync(Guid? id, SaveCategoryRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<TechnicianDto>> ListTechniciansAsync(CancellationToken cancellationToken);

    Task<TechnicianDto> CreateTechnicianAsync(CreateTechnicianRequest request, CancellationToken cancellationToken);

    Task<TechnicianDto> UpdateTechnicianAsync(Guid id, UpdateTechnicianRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SlaPolicyDto>> ListSlaPoliciesAsync(CancellationToken cancellationToken);

    Task<SlaPolicyDto> SaveSlaPolicyAsync(Guid? id, SaveSlaPolicyRequest request, CancellationToken cancellationToken);

    Task DeleteSlaPolicyAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<EscalationRuleDto>> ListEscalationRulesAsync(CancellationToken cancellationToken);

    Task<EscalationRuleDto> SaveEscalationRuleAsync(Guid? id, SaveEscalationRuleRequest request, CancellationToken cancellationToken);

    Task DeleteEscalationRuleAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<InboundChannelDto>> ListChannelsAsync(CancellationToken cancellationToken);

    Task<CreatedInboundChannelDto> CreateChannelAsync(SaveInboundChannelRequest request, CancellationToken cancellationToken);

    Task<InboundChannelDto> UpdateChannelAsync(Guid id, SaveInboundChannelRequest request, CancellationToken cancellationToken);

    Task<CreatedInboundChannelDto> RotateChannelSecretAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteChannelAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class CatalogService(ISolvixDbContext db, ITenantContext tenantContext, IMemberDirectory members) : ICatalogService
{
    // ----- categories --------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Set<TicketCategory>().AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return (await query.OrderBy(c => c.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();
    }

    public async Task<CategoryDto> SaveCategoryAsync(Guid? id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DefaultTechnicianId is { } techId && !await db.Set<Technician>().AnyAsync(t => t.Id == techId && t.IsActive, cancellationToken))
        {
            throw new ValidationException("defaultTechnicianId", "Unknown technician.");
        }

        var normalized = TicketCategory.Normalize(request.Name!);
        if (await db.Set<TicketCategory>().AnyAsync(c => c.NormalizedName == normalized && c.Id != id, cancellationToken))
        {
            throw new ConflictException("A category with this name already exists.");
        }

        TicketCategory category;
        if (id is null)
        {
            category = TicketCategory.Create(tenantContext.RequireTenantId(), request.Name!, request.Description, request.DefaultTechnicianId, request.Keywords);
            db.Set<TicketCategory>().Add(category);
        }
        else
        {
            category = await db.Set<TicketCategory>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException("Category", id.Value);
            category.Update(request.Name!, request.Description, request.DefaultTechnicianId, request.Keywords, request.IsActive ?? category.IsActive);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(category);
    }

    // ----- technicians -------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TechnicianDto>> ListTechniciansAsync(CancellationToken cancellationToken)
    {
        var technicians = await db.Set<Technician>().AsNoTracking().OrderBy(t => t.CreatedAtUtc).ToListAsync(cancellationToken);
        var names = await members.FindAsync(technicians.Select(t => t.UserId).ToList(), cancellationToken);
        var ids = technicians.Select(t => t.Id).ToList();
        var load = await db.Set<Ticket>().AsNoTracking()
            .Where(t => t.AssigneeTechnicianId != null && ids.Contains(t.AssigneeTechnicianId.Value) && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .GroupBy(t => t.AssigneeTechnicianId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        return technicians.Select(t => ToDto(t, names, load.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<TechnicianDto> CreateTechnicianAsync(CreateTechnicianRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var names = await members.FindAsync([request.UserId], cancellationToken);
        if (!names.TryGetValue(request.UserId, out var member) || !member.IsActive)
        {
            throw new ValidationException("userId", "The user must be an active member of this workspace.");
        }

        if (await db.Set<Technician>().AnyAsync(t => t.UserId == request.UserId, cancellationToken))
        {
            throw new ConflictException("This member is already a technician.");
        }

        var technician = Technician.Create(tenantContext.RequireTenantId(), request.UserId);
        technician.Update(true, true, request.MaxOpenTickets, request.CategoryIds, request.Notes);
        await EnsureCategoriesAsync(technician.CategoryIds, cancellationToken);
        db.Set<Technician>().Add(technician);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(technician, names, 0);
    }

    public async Task<TechnicianDto> UpdateTechnicianAsync(Guid id, UpdateTechnicianRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var technician = await db.Set<Technician>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("Technician", id);
        technician.Update(request.IsActive, request.IsAvailable, request.MaxOpenTickets, request.CategoryIds, request.Notes);
        await EnsureCategoriesAsync(technician.CategoryIds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var names = await members.FindAsync([technician.UserId], cancellationToken);
        var open = await db.Set<Ticket>().CountAsync(t => t.AssigneeTechnicianId == id && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed, cancellationToken);
        return ToDto(technician, names, open);
    }

    // ----- SLA ---------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SlaPolicyDto>> ListSlaPoliciesAsync(CancellationToken cancellationToken) =>
        (await db.Set<SlaPolicy>().Include(p => p.Targets).AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<SlaPolicyDto> SaveSlaPolicyAsync(Guid? id, SaveSlaPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targets = (request.Targets ?? []).Select(t => (t.Priority, t.FirstResponseMinutes, t.ResolutionMinutes)).ToList();
        var tenantId = tenantContext.RequireTenantId();
        var normalized = request.Name!.Trim().ToLowerInvariant();
        if (await db.Set<SlaPolicy>().AnyAsync(p => p.NormalizedName == normalized && p.Id != id, cancellationToken))
        {
            throw new ConflictException("An SLA policy with this name already exists.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                SlaPolicy policy;
                if (id is null)
                {
                    policy = SlaPolicy.Create(tenantId, request.Name!, false, request.BusinessHoursOnly, request.TimeZoneId ?? "UTC", request.WorkdayStartMinute ?? 540, request.WorkdayEndMinute ?? 1020, request.WorkDaysMask ?? 0b0111110, targets);
                    db.Set<SlaPolicy>().Add(policy);
                }
                else
                {
                    policy = await db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("SLA policy", id.Value);
                    // Replace targets in two steps so the unique (policy, priority) index never sees duplicates.
                    await db.Set<SlaTarget>().Where(t => t.SlaPolicyId == id).ExecuteDeleteAsync(ct);
                    var fresh = policy.Update(request.Name!, policy.IsDefault, request.IsActive ?? policy.IsActive, request.BusinessHoursOnly, request.TimeZoneId ?? policy.TimeZoneId, request.WorkdayStartMinute ?? policy.WorkdayStartMinute, request.WorkdayEndMinute ?? policy.WorkdayEndMinute, request.WorkDaysMask ?? policy.WorkDaysMask, targets);
                    db.Set<SlaTarget>().AddRange(fresh);
                }

                if (request.IsDefault && !policy.IsDefault)
                {
                    // One default per tenant: demote the current one first (the partial unique index enforces it).
                    await db.Set<SlaPolicy>().Where(p => p.IsDefault && p.Id != policy.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), ct);
                    policy.SetDefault(true);
                }
                else if (!request.IsDefault && policy.IsDefault && id is not null)
                {
                    throw new ConflictException("Make another policy the default instead of unsetting this one.", "default_required");
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return ToDto(policy);
            },
            cancellationToken);
    }

    public async Task DeleteSlaPolicyAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new NotFoundException("SLA policy", id);
        if (policy.IsDefault)
        {
            throw new ConflictException("The default policy cannot be deleted.", "default_required");
        }

        if (await db.Set<Ticket>().AnyAsync(t => t.SlaPolicyId == id, cancellationToken))
        {
            throw new ConflictException("Tickets reference this policy. Deactivate it instead.", "policy_in_use");
        }

        db.Set<SlaPolicy>().Remove(policy);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ----- escalation --------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<EscalationRuleDto>> ListEscalationRulesAsync(CancellationToken cancellationToken) =>
        (await db.Set<EscalationRule>().AsNoTracking().OrderBy(r => r.ThresholdPercent).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<EscalationRuleDto> SaveEscalationRuleAsync(Guid? id, SaveEscalationRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetTechnicianId is { } target && !await db.Set<Technician>().AnyAsync(t => t.Id == target && t.IsActive, cancellationToken))
        {
            throw new ValidationException("targetTechnicianId", "Unknown technician.");
        }

        EscalationRule rule;
        if (id is null)
        {
            rule = EscalationRule.Create(tenantContext.RequireTenantId(), request.Name!, request.Trigger, request.ThresholdPercent, request.Action, request.TargetTechnicianId, request.MinPriority);
            db.Set<EscalationRule>().Add(rule);
        }
        else
        {
            rule = await db.Set<EscalationRule>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken) ?? throw new NotFoundException("Escalation rule", id.Value);
            rule.Update(request.Name!, request.IsActive ?? rule.IsActive, request.Trigger, request.ThresholdPercent, request.Action, request.TargetTechnicianId, request.MinPriority);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task DeleteEscalationRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.Set<EscalationRule>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken) ?? throw new NotFoundException("Escalation rule", id);
        db.Set<EscalationRule>().Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ----- inbound e-mail channels -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<InboundChannelDto>> ListChannelsAsync(CancellationToken cancellationToken) =>
        (await db.Set<InboundEmailChannel>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<CreatedInboundChannelDto> CreateChannelAsync(SaveInboundChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var id = Guid.CreateVersion7();
        var (token, hash) = ChannelTokenCodec.Generate(tenantId, id);
        var channel = InboundEmailChannel.Create(tenantId, id, request.Name!, request.Address, hash, request.DefaultPriority ?? TicketPriority.Medium, request.AllowedSenderDomains);
        db.Set<InboundEmailChannel>().Add(channel);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedInboundChannelDto(ToDto(channel), token);
    }

    public async Task<InboundChannelDto> UpdateChannelAsync(Guid id, SaveInboundChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = await db.Set<InboundEmailChannel>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException("Channel", id);
        channel.Update(request.Name!, request.Address, request.IsEnabled ?? channel.IsEnabled, request.DefaultPriority ?? channel.DefaultPriority, request.AllowedSenderDomains);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(channel);
    }

    public async Task<CreatedInboundChannelDto> RotateChannelSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        var channel = await db.Set<InboundEmailChannel>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException("Channel", id);
        var (token, hash) = ChannelTokenCodec.Generate(channel.TenantId, channel.Id);
        channel.RotateSecret(hash);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedInboundChannelDto(ToDto(channel), token);
    }

    public async Task DeleteChannelAsync(Guid id, CancellationToken cancellationToken)
    {
        var channel = await db.Set<InboundEmailChannel>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException("Channel", id);
        db.Set<InboundEmailChannel>().Remove(channel);
        await db.SaveChangesAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------------------------------------------

    private async Task EnsureCategoriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await db.Set<TicketCategory>().CountAsync(c => ids.Contains(c.Id), cancellationToken);
        if (found != ids.Count)
        {
            throw new ValidationException("categoryIds", "One or more categories do not exist.");
        }
    }

    private static CategoryDto ToDto(TicketCategory c) => new(c.Id, c.Name, c.Description, c.IsActive, c.DefaultTechnicianId, c.Keywords);

    private static TechnicianDto ToDto(Technician t, IReadOnlyDictionary<Guid, MemberSummary> names, int open)
    {
        names.TryGetValue(t.UserId, out var m);
        return new TechnicianDto(t.Id, t.UserId, m?.DisplayName, m?.Email, t.IsActive, t.IsAvailable, t.MaxOpenTickets, t.CategoryIds, t.Notes, open);
    }

    private static SlaPolicyDto ToDto(SlaPolicy p) =>
        new(p.Id, p.Name, p.IsDefault, p.IsActive, p.BusinessHoursOnly, p.TimeZoneId, p.WorkdayStartMinute, p.WorkdayEndMinute, p.WorkDaysMask,
            p.Targets.OrderBy(t => t.Priority).Select(t => new SlaTargetDto(t.Priority, t.FirstResponseMinutes, t.ResolutionMinutes)).ToList());

    private static EscalationRuleDto ToDto(EscalationRule r) => new(r.Id, r.Name, r.IsActive, r.Trigger, r.ThresholdPercent, r.Action, r.TargetTechnicianId, r.MinPriority);

    private static InboundChannelDto ToDto(InboundEmailChannel c) => new(c.Id, c.Name, c.Address, c.IsEnabled, c.DefaultPriority, c.AllowedSenderDomains, c.LastReceivedAtUtc);
}

/// <summary>
/// Channel credential <c>svxm_</c> + base64url(tenantId ‖ channelId ‖ 256-bit secret). Only SHA-256(secret) is stored.
/// Embedding the tenant lets the anonymous request be bound to that tenant before any row is read, so the channel
/// lookup runs under row-level security; a tampered id finds nothing.
/// </summary>
public static class ChannelTokenCodec
{
    public const string Prefix = "svxm_";

    public static (string Token, string Hash) Generate(Guid tenantId, Guid channelId)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var buffer = new byte[64];
        tenantId.TryWriteBytes(buffer.AsSpan(0, 16), bigEndian: true, out _);
        channelId.TryWriteBytes(buffer.AsSpan(16, 16), bigEndian: true, out _);
        secret.CopyTo(buffer, 32);
        var token = Prefix + Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, HashSecret(secret));
    }

    public static bool TryParse(string? token, out Guid tenantId, out Guid channelId, out byte[] secret)
    {
        tenantId = Guid.Empty;
        channelId = Guid.Empty;
        secret = [];
        if (string.IsNullOrEmpty(token) || token.Length > 200 || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var b64 = token[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != 64)
        {
            return false;
        }

        tenantId = new Guid(raw.AsSpan(0, 16), bigEndian: true);
        channelId = new Guid(raw.AsSpan(16, 16), bigEndian: true);
        secret = raw[32..];
        return tenantId != Guid.Empty && channelId != Guid.Empty;
    }

    public static string HashSecret(byte[] secret) => Convert.ToHexStringLower(SHA256.HashData(secret));

    public static bool Matches(string storedHash, byte[] secret) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(storedHash), System.Text.Encoding.ASCII.GetBytes(HashSecret(secret)));
}
