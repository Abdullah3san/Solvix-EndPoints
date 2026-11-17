namespace Solvix.Modules.Helpdesk.Application;

using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Helpdesk.Configuration;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;
using Solvix.SharedKernel.Tenancy;

public interface IEmailIntakeService
{
    Task<InboundEmailResult> ProcessAsync(string? channelToken, InboundEmailRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a provider-delivered e-mail into a ticket (or a comment on an existing one). Mail arrives with no category:
/// the ticket lands in the triage queue, with a keyword-based SUGGESTION a technician can accept.
/// Hardening: per-channel secret, sender-domain allow-list, auto-reply/bulk detection (no loops), de-duplication on
/// Message-Id, per-sender hourly cap, replies attach only when the sender is the ticket's requester, plain-text only.
/// </summary>
internal sealed partial class EmailIntakeService(
    ISolvixDbContext db,
    ITenantDirectory tenants,
    ITenantContextMutator tenantMutator,
    TicketFactory factory,
    IOptions<HelpdeskOptions> options,
    IClock clock,
    ILogger<EmailIntakeService> logger) : IEmailIntakeService
{
    private const string InvalidCode = "channel_token_invalid";

    public async Task<InboundEmailResult> ProcessAsync(string? channelToken, InboundEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ChannelTokenCodec.TryParse(channelToken, out var tenantId, out var channelId, out var secret))
        {
            throw new UnauthorizedException("The channel token is invalid.", InvalidCode);
        }

        try
        {
            var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
            if (tenant is not { IsActive: true })
            {
                throw new UnauthorizedException("The channel token is invalid.", InvalidCode);
            }

            tenantMutator.SetTenant(tenantId, tenant.Slug);
            var channel = await db.Set<InboundEmailChannel>().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
            if (channel is null || !channel.IsEnabled || !ChannelTokenCodec.Matches(channel.SecretHash, secret))
            {
                LogRejected(logger, channelId);
                throw new UnauthorizedException("The channel token is invalid.", InvalidCode);
            }

            return await HandleAsync(tenantId, channel, request, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<InboundEmailResult> HandleAsync(Guid tenantId, InboundEmailChannel channel, InboundEmailRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        var sender = NormalizeAddress(request.From?.Email);
        if (sender is null)
        {
            throw new ValidationException("from", "A valid sender address is required.");
        }

        var messageId = NormalizeMessageId(request.MessageId);
        channel.Touch(now);
        SolvixMetrics.InboundEmails.Add(1);

        if (channel.AllowedSenderDomains.Count > 0 && !channel.AllowedSenderDomains.Contains(sender[(sender.LastIndexOf('@') + 1)..], StringComparer.OrdinalIgnoreCase))
        {
            return await FinishAsync("ignored", null, cancellationToken);
        }

        if (IsAutomated(request.Headers) || (channel.Address is { } own && string.Equals(own, sender, StringComparison.OrdinalIgnoreCase)))
        {
            return await FinishAsync("ignored", null, cancellationToken);
        }

        if (messageId is not null
            && (await db.Set<Ticket>().AnyAsync(t => t.ExternalMessageId == messageId, cancellationToken)
                || await db.Set<TicketComment>().AnyAsync(c => c.ExternalMessageId == messageId, cancellationToken)))
        {
            return await FinishAsync("duplicate", null, cancellationToken);
        }

        var subject = CleanSubject(request.Subject);
        var body = ExtractBody(request, settings.Email.MaxBodyChars);
        var senderName = string.IsNullOrWhiteSpace(request.From?.Name) ? null : request.From!.Name!.Trim();

        var existing = await FindThreadAsync(request, subject, sender, cancellationToken);
        if (existing is not null)
        {
            db.Set<TicketComment>().Add(TicketComment.Create(tenantId, existing.Id, null, senderName, sender, false, false, body, TicketSource.Email, messageId));
            db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, existing.Id, TicketEventType.Commented, null, sender, null, null, "reply by e-mail", now));
            if (existing.Status is TicketStatus.WaitingOnRequester or TicketStatus.Resolved)
            {
                var wasResolved = existing.Status == TicketStatus.Resolved;
                existing.ChangeStatus(TicketStatus.Open, now, null);
                if (wasResolved)
                {
                    foreach (var r in await db.Set<TicketResolution>().Where(r => r.TicketId == existing.Id && r.ReopenedAtUtc == null).ToListAsync(cancellationToken))
                    {
                        r.MarkReopened(now);
                    }

                    db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, existing.Id, TicketEventType.Reopened, null, sender, TicketStatus.Resolved.ToString(), TicketStatus.Open.ToString(), "requester replied by e-mail", now));
                }
            }

            await SaveAttachmentsAsync(tenantId, existing, request.Attachments, cancellationToken);
            return await FinishAsync("replied", existing.Number, cancellationToken);
        }

        // Mail-bomb guard: a single address cannot flood the queue.
        var since = now.AddHours(-1);
        var recent = await db.Set<Ticket>().CountAsync(t => t.Source == TicketSource.Email && t.RequesterEmail == sender && t.CreatedAtUtc > since, cancellationToken);
        if (recent >= settings.Email.MaxTicketsPerSenderPerHour)
        {
            LogThrottled(logger, channel.Id);
            return await FinishAsync("ignored", null, cancellationToken);
        }

        var suggestion = await SuggestCategoryAsync(subject + " " + body, cancellationToken);
        var ticket = await factory.CreateAsync(
            tenantId, subject, body, channel.DefaultPriority, TicketSource.Email, null, null, sender, senderName, null, messageId, suggestion, $"e-mail: {channel.Name}", null, cancellationToken);
        await SaveAttachmentsAsync(tenantId, ticket, request.Attachments, cancellationToken);
        SolvixMetrics.TicketsCreated.Add(1, new KeyValuePair<string, object?>("source", "email"));
        return await FinishAsync("created", ticket.Number, cancellationToken);
    }

    private async Task<InboundEmailResult> FinishAsync(string outcome, long? number, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        return new InboundEmailResult(outcome, number);
    }

    private async Task<Ticket?> FindThreadAsync(InboundEmailRequest request, string subject, string sender, CancellationToken cancellationToken)
    {
        Ticket? found = null;
        Match match;
        try
        {
            match = TicketReference().Match((request.Subject ?? string.Empty)[..Math.Min((request.Subject ?? string.Empty).Length, 2000)]);
        }
        catch (RegexMatchTimeoutException)
        {
            match = Match.Empty;
        }

        if (match.Success && long.TryParse(match.Groups[1].Value, out var number))
        {
            found = await db.Set<Ticket>().FirstOrDefaultAsync(t => t.Number == number, cancellationToken);
        }

        if (found is null)
        {
            var refs = (request.References ?? []).Append(request.InReplyTo ?? string.Empty).Select(NormalizeMessageId).Where(r => r is not null).Distinct().Take(20).ToList();
            if (refs.Count > 0)
            {
                found = await db.Set<Ticket>().FirstOrDefaultAsync(t => refs.Contains(t.ExternalMessageId), cancellationToken);
                if (found is null)
                {
                    var ticketId = await db.Set<TicketComment>().Where(c => refs.Contains(c.ExternalMessageId)).Select(c => (Guid?)c.TicketId).FirstOrDefaultAsync(cancellationToken);
                    if (ticketId is not null)
                    {
                        found = await db.Set<Ticket>().FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
                    }
                }
            }
        }

        // Knowing a ticket number must not let a stranger write into it: only the requester's own address attaches.
        return found is not null && string.Equals(found.RequesterEmail, sender, StringComparison.OrdinalIgnoreCase) && found.Status != TicketStatus.Closed ? found : null;
    }

    private async Task<Guid?> SuggestCategoryAsync(string text, CancellationToken cancellationToken)
    {
        var haystack = text.ToLowerInvariant();
        var categories = await db.Set<TicketCategory>().AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken);
        return categories
            .Select(c => (c.Id, Score: c.Keywords.Count(k => haystack.Contains(k, StringComparison.Ordinal))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefault();
    }

    private async Task SaveAttachmentsAsync(Guid tenantId, Ticket ticket, IReadOnlyList<InboundAttachmentDto>? attachments, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        foreach (var item in (attachments ?? []).Take(settings.Email.MaxAttachments))
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(item.ContentBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                continue;
            }

            if (data.Length > settings.Attachments.MaxBytes)
            {
                continue;
            }

            var (name, contentType, error) = AttachmentRules.Validate(item.FileName, data);
            if (error is not null)
            {
                db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.AttachmentAdded, null, "e-mail intake", null, null, $"attachment skipped: {error}", clock.UtcNow));
                continue;
            }

            var attachment = TicketAttachment.Create(tenantId, ticket.Id, null, name!, contentType!, data.Length, AttachmentRules.Sha256(data), null);
            db.Set<TicketAttachment>().Add(attachment);
            db.Set<TicketAttachmentContent>().Add(TicketAttachmentContent.Create(tenantId, attachment.Id, data));
        }
    }

    internal static bool IsAutomated(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return false;
        }

        string? Get(string name) => headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var autoSubmitted = Get("Auto-Submitted");
        var precedence = Get("Precedence");
        return (autoSubmitted is not null && !string.Equals(autoSubmitted.Trim(), "no", StringComparison.OrdinalIgnoreCase))
            || (precedence is not null && precedence.Trim().ToLowerInvariant() is "bulk" or "junk" or "list" or "auto_reply")
            || Get("X-Auto-Response-Suppress") is not null
            || Get("List-Id") is not null;
    }

    internal static string? NormalizeAddress(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
        {
            return null;
        }

        try
        {
            var address = new MailAddress(email.Trim());
            return address.Address.Contains('@', StringComparison.Ordinal) && address.Address == email.Trim() ? address.Address.ToLowerInvariant() : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string? NormalizeMessageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim().Trim('<', '>');
        return trimmed.Length is > 0 and <= 500 ? trimmed : null;
    }

    internal static string CleanSubject(string? subject)
    {
        var s = (subject ?? string.Empty).Trim();
        s = s.Length > 2000 ? s[..2000] : s;
        try
        {
            s = Prefixes().Replace(s, string.Empty).Trim();
            s = ControlChars().Replace(s, " ");
        }
        catch (RegexMatchTimeoutException)
        {
            s = string.Empty;
        }

        return s.Length == 0 ? "(no subject)" : s.Length <= Ticket.SubjectMaxLength ? s : s[..Ticket.SubjectMaxLength];
    }

    internal static string ExtractBody(InboundEmailRequest request, int maxChars)
    {
        // Mail comes from anyone on the internet: bound the work before any pattern matching, and give up (with a
        // fixed message) rather than ever spending unbounded CPU on a crafted body.
        try
        {
            var text = request.Text is { } t ? (t.Length > 100_000 ? t[..100_000] : t) : null;
            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(request.Html))
            {
                var html = request.Html!.Length > 100_000 ? request.Html[..100_000] : request.Html;
                html = Scripts().Replace(html, " ");
                html = Cells().Replace(html, " ");
                html = Breaks().Replace(html, "\n");
                html = Tags().Replace(html, string.Empty);
                text = WebUtility.HtmlDecode(html);
            }

            text = ControlChars().Replace(text ?? string.Empty, " ");
            text = BlankRuns().Replace(text.Trim(), "\n\n");
            if (text.Length == 0)
            {
                text = "(empty message)";
            }

            return text.Length <= maxChars ? text : text[..maxChars];
        }
        catch (RegexMatchTimeoutException)
        {
            return "(message body could not be displayed)";
        }
    }

    [GeneratedRegex(@"\[(?:SVX-|#)(\d{1,12})\]", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex TicketReference();

    [GeneratedRegex(@"^(\s*(re|fwd?|aw|sv)\s*:\s*)+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex Prefixes();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 250)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h[1-6])\s*/?>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex Cells();

    [GeneratedRegex(@"<[^>]{0,2000}>", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex Tags();

    [GeneratedRegex(@"(\r?\n){3,}", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex BlankRuns();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound e-mail rejected for channel {ChannelId}: bad secret or disabled")]
    private static partial void LogRejected(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound e-mail throttled on channel {ChannelId}: sender exceeded the hourly ticket cap")]
    private static partial void LogThrottled(ILogger logger, Guid channelId);
}
