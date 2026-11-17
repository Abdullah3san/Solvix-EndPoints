namespace Solvix.Modules.Helpdesk.Application;

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

/// <summary>Who is calling: staff (admin/owner or an active technician) sees every ticket, anyone else only their own.</summary>
internal sealed class HelpdeskActor(ISolvixDbContext db, ICurrentUser user)
{
    private bool _loaded;
    private Guid? _technicianId;

    public Guid? UserId => user.UserId;

    public bool IsAdmin => user.HasPermission(Permissions.Helpdesk.Manage);

    public async Task<bool> IsStaffAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return IsAdmin || _technicianId is not null;
    }

    public async Task<Guid?> TechnicianIdAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return _technicianId;
    }

    public async Task RequireStaffAsync(CancellationToken cancellationToken)
    {
        if (!await IsStaffAsync(cancellationToken))
        {
            throw new Solvix.SharedKernel.Exceptions.ForbiddenException("Only technicians and administrators can do this.");
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (user.UserId is { } id)
        {
            _technicianId = await db.Set<Technician>().AsNoTracking().Where(t => t.UserId == id && t.IsActive).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken);
        }
    }
}

/// <summary>Shared ticket creation for the web, API and e-mail intake: numbering, SLA, assignment, audit trail.</summary>
internal sealed class TicketFactory(ISolvixDbContext db, IClock clock)
{
    public async Task<long> NextNumberAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var next = await db.Database
            .SqlQuery<long>($"""
                INSERT INTO ticket_counters (id, tenant_id, last_number, created_at_utc, updated_at_utc)
                VALUES ({Guid.CreateVersion7()}, {tenantId}, 1, {now}, {now})
                ON CONFLICT (tenant_id) DO UPDATE SET last_number = ticket_counters.last_number + 1, updated_at_utc = {now}
                RETURNING last_number AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return next[0];
    }

    public async Task<Ticket> CreateAsync(
        Guid tenantId,
        string subject,
        string description,
        TicketPriority priority,
        TicketSource source,
        Guid? categoryId,
        Guid? requesterUserId,
        string? requesterEmail,
        string? requesterName,
        Guid? deviceId,
        string? externalMessageId,
        Guid? suggestedCategoryId,
        string actorLabel,
        Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var number = await NextNumberAsync(tenantId, cancellationToken);
        var ticket = Ticket.Create(tenantId, number, subject, description, priority, source, categoryId, requesterUserId, requesterEmail, requesterName, deviceId, externalMessageId, now);
        ticket.SetSuggestedCategory(suggestedCategoryId);

        var policy = await db.Set<SlaPolicy>().Include(p => p.Targets).AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, cancellationToken);
        ticket.ApplySla(policy, now);

        if (categoryId is not null)
        {
            var assignee = await new TechnicianAssigner(db).PickAsync(categoryId, cancellationToken);
            if (assignee is not null)
            {
                ticket.Assign(assignee, now);
            }
        }

        db.Set<Ticket>().Add(ticket);
        db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.Created, actorUserId, actorLabel, null, ticket.Status.ToString(), $"{source} ticket, priority {priority}", now));
        if (ticket.AssigneeTechnicianId is { } a)
        {
            db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.Assigned, null, "auto-assignment", null, a.ToString(), "assigned by category rule", now));
        }

        return ticket;
    }
}

/// <summary>Category default technician first, otherwise the least-loaded available technician with the skill and spare capacity.</summary>
internal sealed class TechnicianAssigner(ISolvixDbContext db)
{
    public async Task<Guid?> PickAsync(Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is null)
        {
            return null;
        }

        var category = await db.Set<TicketCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category?.DefaultTechnicianId is { } preferred
            && await db.Set<Technician>().AsNoTracking().AnyAsync(t => t.Id == preferred && t.IsActive, cancellationToken))
        {
            return preferred;
        }

        var technicians = await db.Set<Technician>().AsNoTracking().Where(t => t.IsActive && t.IsAvailable).ToListAsync(cancellationToken);
        var candidates = technicians.Where(t => t.CategoryIds.Count == 0 || t.CategoryIds.Contains(categoryId.Value)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var ids = candidates.Select(t => t.Id).ToList();
        var load = await db.Set<Ticket>().AsNoTracking()
            .Where(t => t.AssigneeTechnicianId != null && ids.Contains(t.AssigneeTechnicianId.Value) && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .GroupBy(t => t.AssigneeTechnicianId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);

        return candidates
            .Select(t => (Technician: t, Open: load.GetValueOrDefault(t.Id)))
            .Where(x => x.Technician.MaxOpenTickets is null || x.Open < x.Technician.MaxOpenTickets)
            .OrderBy(x => x.Open)
            .ThenBy(x => x.Technician.Id)
            .Select(x => (Guid?)x.Technician.Id)
            .FirstOrDefault();
    }
}

/// <summary>Attachment safety rules. The client's file name and content type are treated as hostile.</summary>
public static partial class AttachmentRules
{
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    /// <summary>Returns a safe stored name and the content type derived from the extension, or an error.</summary>
    public static (string? FileName, string? ContentType, string? Error) Validate(string? fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return (null, null, "The file is empty.");
        }

        var name = Sanitize(fileName);
        var extension = Path.GetExtension(name);
        if (!AllowedTypes.TryGetValue(extension, out var contentType))
        {
            return (null, null, $"Files of type '{extension}' are not allowed.");
        }

        if (!MagicMatches(extension.ToLowerInvariant(), data))
        {
            return (null, null, "The file content does not match its extension.");
        }

        return (name, contentType, null);
    }

    public static string Sanitize(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = UnsafeChars().Replace(name, "_").Trim(' ', '.');
        // Neutralise double extensions such as "invoice.exe.png" by keeping the final one only after replacing inner dots.
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name).Replace('.', '_');
        name = stem.Length == 0 ? "file" + ext : stem + ext;
        return name.Length <= 120 ? name : name[^120..];
    }

    private static bool MagicMatches(string extension, byte[] d) => extension switch
    {
        ".png" => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47,
        ".jpg" or ".jpeg" => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8,
        ".gif" => d.Length > 6 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46,
        ".webp" => d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[8] == 0x57 && d[9] == 0x45,
        ".pdf" => d.Length > 4 && d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44 && d[3] == 0x46,
        ".docx" or ".xlsx" => d.Length > 4 && d[0] == 0x50 && d[1] == 0x4B,
        ".txt" or ".log" or ".csv" => !d.AsSpan(0, Math.Min(d.Length, 2048)).Contains((byte)0),
        _ => false,
    };

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [GeneratedRegex(@"[\x00-\x1F\x7F<>:""|?*]")]
    private static partial Regex UnsafeChars();
}
