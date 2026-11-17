namespace Solvix.Modules.Helpdesk.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public sealed class TicketCategory : TenantEntity
{
    public const int NameMaxLength = 100;

    private TicketCategory()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>When set, new tickets of this category are assigned to this technician.</summary>
    public Guid? DefaultTechnicianId { get; private set; }

    /// <summary>Lower-case words; an inbound e-mail containing one is suggested this category (never auto-applied).</summary>
    public List<string> Keywords { get; private set; } = [];

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static TicketCategory Create(Guid tenantId, string name, string? description, Guid? defaultTechnicianId, IEnumerable<string>? keywords)
    {
        var category = new TicketCategory();
        category.AssignTenant(tenantId);
        category.Update(name, description, defaultTechnicianId, keywords, true);
        return category;
    }

    public void Update(string name, string? description, Guid? defaultTechnicianId, IEnumerable<string>? keywords, bool isActive)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        NormalizedName = Normalize(Name);
        Description = Guard.Optional(description, "description", 500);
        DefaultTechnicianId = defaultTechnicianId;
        IsActive = isActive;
        Keywords = (keywords ?? [])
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length is >= 2 and <= 40)
            .Distinct(StringComparer.Ordinal)
            .Take(30)
            .ToList();
    }
}

/// <summary>A workspace member who works tickets. Admins/owners can work tickets without a profile; a profile adds capacity and skills.</summary>
public sealed class Technician : TenantEntity
{
    private Technician()
    {
    }

    public Guid UserId { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>Available for automatic assignment (on duty).</summary>
    public bool IsAvailable { get; private set; } = true;

    public int? MaxOpenTickets { get; private set; }

    /// <summary>Category ids this technician handles; empty means generalist.</summary>
    public List<Guid> CategoryIds { get; private set; } = [];

    public string? Notes { get; private set; }

    public static Technician Create(Guid tenantId, Guid userId)
    {
        var technician = new Technician { UserId = userId };
        technician.AssignTenant(tenantId);
        return technician;
    }

    public void Update(bool isActive, bool isAvailable, int? maxOpenTickets, IEnumerable<Guid>? categoryIds, string? notes)
    {
        if (maxOpenTickets is < 1 or > 1000)
        {
            throw new ValidationException("maxOpenTickets", "Capacity must be between 1 and 1000.");
        }

        IsActive = isActive;
        IsAvailable = isAvailable;
        MaxOpenTickets = maxOpenTickets;
        CategoryIds = (categoryIds ?? []).Distinct().Take(100).ToList();
        Notes = Guard.Optional(notes, "notes", 500);
    }
}

public sealed class SlaPolicy : TenantEntity
{
    private readonly List<SlaTarget> _targets = [];

    private SlaPolicy()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    public bool IsActive { get; private set; } = true;

    public bool BusinessHoursOnly { get; private set; }

    public string TimeZoneId { get; private set; } = "UTC";

    public int WorkdayStartMinute { get; private set; } = 9 * 60;

    public int WorkdayEndMinute { get; private set; } = 17 * 60;

    /// <summary>Bit 0 = Sunday ... bit 6 = Saturday. Default Monday-Friday.</summary>
    public int WorkDaysMask { get; private set; } = 0b0111110;

    public IReadOnlyList<SlaTarget> Targets => _targets;

    public static SlaPolicy Create(Guid tenantId, string name, bool isDefault, bool businessHoursOnly, string timeZoneId, int startMinute, int endMinute, int workDaysMask, IEnumerable<(TicketPriority Priority, int FirstResponseMinutes, int ResolutionMinutes)> targets)
    {
        var policy = new SlaPolicy();
        policy.AssignTenant(tenantId);
        policy.Update(name, isDefault, true, businessHoursOnly, timeZoneId, startMinute, endMinute, workDaysMask, targets);
        return policy;
    }

    /// <returns>
    /// The newly created targets. Callers updating a TRACKED policy must add them to the context explicitly: EF would
    /// otherwise treat a child with a client-generated key as already existing (Modified) instead of new.
    /// </returns>
    public IReadOnlyList<SlaTarget> Update(string name, bool isDefault, bool isActive, bool businessHoursOnly, string timeZoneId, int startMinute, int endMinute, int workDaysMask, IEnumerable<(TicketPriority Priority, int FirstResponseMinutes, int ResolutionMinutes)> targets)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", 100);
        NormalizedName = Name.ToLowerInvariant();
        IsDefault = isDefault;
        IsActive = isActive;
        BusinessHoursOnly = businessHoursOnly;
        if (businessHoursOnly)
        {
            // Validates zone id and ranges (throws on bad input).
            try
            {
                SlaCalendar.Business(timeZoneId, startMinute, endMinute, (byte)workDaysMask);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ValidationException("businessHours", ex.Message);
            }
        }

        TimeZoneId = timeZoneId;
        WorkdayStartMinute = startMinute;
        WorkdayEndMinute = endMinute;
        WorkDaysMask = workDaysMask;

        var list = targets.ToList();
        if (list.Select(t => t.Priority).Distinct().Count() != list.Count)
        {
            throw new ValidationException("targets", "Each priority may appear once.");
        }

        if (list.Any(t => t.FirstResponseMinutes < 1 || t.ResolutionMinutes < t.FirstResponseMinutes || t.ResolutionMinutes > 60 * 24 * 365))
        {
            throw new ValidationException("targets", "Resolution must be at least as long as first response, and both positive.");
        }

        _targets.Clear();
        var created = new List<SlaTarget>();
        foreach (var t in list)
        {
            var target = SlaTarget.Create(TenantId, Id, t.Priority, t.FirstResponseMinutes, t.ResolutionMinutes);
            _targets.Add(target);
            created.Add(target);
        }

        return created;
    }

    public void SetDefault(bool value) => IsDefault = value;

    public SlaCalendar Calendar() =>
        BusinessHoursOnly ? SlaCalendar.Business(TimeZoneId, WorkdayStartMinute, WorkdayEndMinute, (byte)WorkDaysMask) : SlaCalendar.AlwaysOn;

    public SlaTarget? TargetFor(TicketPriority priority) => _targets.FirstOrDefault(t => t.Priority == priority);
}

public sealed class SlaTarget : TenantEntity
{
    private SlaTarget()
    {
    }

    public Guid SlaPolicyId { get; private set; }

    public TicketPriority Priority { get; private set; }

    public int FirstResponseMinutes { get; private set; }

    public int ResolutionMinutes { get; private set; }

    internal static SlaTarget Create(Guid tenantId, Guid policyId, TicketPriority priority, int firstResponse, int resolution)
    {
        var target = new SlaTarget { SlaPolicyId = policyId, Priority = priority, FirstResponseMinutes = firstResponse, ResolutionMinutes = resolution };
        target.AssignTenant(tenantId);
        return target;
    }
}

public sealed class EscalationRule : TenantEntity
{
    private EscalationRule()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public EscalationTrigger Trigger { get; private set; }

    /// <summary>1-200: percent of the SLA window that must have elapsed.</summary>
    public int ThresholdPercent { get; private set; }

    public EscalationAction Action { get; private set; }

    public Guid? TargetTechnicianId { get; private set; }

    /// <summary>Only tickets at or above this priority; null = all.</summary>
    public TicketPriority? MinPriority { get; private set; }

    public static EscalationRule Create(Guid tenantId, string name, EscalationTrigger trigger, int thresholdPercent, EscalationAction action, Guid? targetTechnicianId, TicketPriority? minPriority)
    {
        var rule = new EscalationRule();
        rule.AssignTenant(tenantId);
        rule.Update(name, true, trigger, thresholdPercent, action, targetTechnicianId, minPriority);
        return rule;
    }

    public void Update(string name, bool isActive, EscalationTrigger trigger, int thresholdPercent, EscalationAction action, Guid? targetTechnicianId, TicketPriority? minPriority)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", 100);
        if (thresholdPercent is < 1 or > 200)
        {
            throw new ValidationException("thresholdPercent", "Threshold must be between 1 and 200.");
        }

        if (action == EscalationAction.Reassign && targetTechnicianId is null)
        {
            throw new ValidationException("targetTechnicianId", "Reassigning needs a target technician.");
        }

        IsActive = isActive;
        Trigger = trigger;
        ThresholdPercent = thresholdPercent;
        Action = action;
        TargetTechnicianId = action == EscalationAction.Reassign ? targetTechnicianId : null;
        MinPriority = minPriority;
    }
}

/// <summary>Per-tenant gap-free ticket number sequence (atomic raw-SQL increment).</summary>
public sealed class TicketCounter : TenantEntity
{
    private TicketCounter()
    {
    }

    public long LastNumber { get; private set; }
}

/// <summary>
/// An inbound e-mail address delivered to us by a mail provider's webhook. The secret is shown once and only its
/// SHA-256 is stored.
/// </summary>
public sealed class InboundEmailChannel : TenantEntity
{
    public const int SecretHashLength = 64;

    private InboundEmailChannel()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Address { get; private set; }

    public string SecretHash { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; } = true;

    public TicketPriority DefaultPriority { get; private set; } = TicketPriority.Medium;

    /// <summary>When non-empty, only senders from these domains are accepted.</summary>
    public List<string> AllowedSenderDomains { get; private set; } = [];

    public DateTimeOffset? LastReceivedAtUtc { get; private set; }

    public static InboundEmailChannel Create(Guid tenantId, Guid id, string name, string? address, string secretHash, TicketPriority defaultPriority, IEnumerable<string>? allowedDomains)
    {
        var channel = new InboundEmailChannel { Name = Guard.NotNullOrWhiteSpace(name, "name", 100), Address = Guard.Optional(address, "address", 254), SecretHash = secretHash, DefaultPriority = defaultPriority };
        channel.SetId(id);
        channel.AssignTenant(tenantId);
        channel.AllowedSenderDomains = Domains(allowedDomains);
        return channel;
    }

    public void Update(string name, string? address, bool isEnabled, TicketPriority defaultPriority, IEnumerable<string>? allowedDomains)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", 100);
        Address = Guard.Optional(address, "address", 254);
        IsEnabled = isEnabled;
        DefaultPriority = defaultPriority;
        AllowedSenderDomains = Domains(allowedDomains);
    }

    private void SetId(Guid id) => Id = id;

    public void RotateSecret(string secretHash) => SecretHash = secretHash;

    public void Touch(DateTimeOffset now) => LastReceivedAtUtc = now;

    private static List<string> Domains(IEnumerable<string>? domains) =>
        (domains ?? []).Select(d => d.Trim().ToLowerInvariant().TrimStart('@')).Where(d => d.Length is > 3 and <= 100 && d.Contains('.', StringComparison.Ordinal)).Distinct().Take(50).ToList();
}
