# Helpdesk

Module: `Solvix.Modules.Helpdesk`. Tickets, categories, priorities, SLA policies, escalation, technicians, comments,
attachments, resolution history, e-mail intake and AI summaries.

## Concepts

* **Priorities:** Low, Medium, High, Critical. **Statuses:** New → Open → In progress ⇄ Waiting on requester → Resolved →
  Closed (Resolved/Closed can reopen). Resolving requires a resolution note.
* **Who sees what.** Members see and follow **only tickets they raised** (another member's ticket answers 404, even for
  search/filter/attachment/history). *Staff* = workspace Admins/Owners and members with an active **technician profile**:
  they see all tickets, internal notes, routing events and statistics. Only Admins manage catalogs.
* **Categories.** Six defaults are created for every workspace (Hardware, Software, Network, Access & Accounts, Security,
  Other). Each can have a default technician and keywords.
* **Technicians.** A member plus capacity (`maxOpenTickets`), skills (categories) and an *on duty* flag. Auto-assignment
  runs when a ticket gets a category: the category's default technician, otherwise the least-loaded available technician
  with the skill and spare capacity; nothing is assigned when nobody qualifies.
* **Linked device.** A ticket can reference a device in the same tenant (database-enforced tenant-safe foreign key).

## Tickets that arrive by e-mail (no category)

E-mail tickets are created **uncategorised** and wait in the **Triage** queue (`GET /helpdesk/tickets?triage=true`,
"Triage" tab). The category keywords produce a *suggestion* (`suggestedCategoryId`); a technician accepts it or picks
another category, which also triggers category-based assignment. Nothing is ever categorised automatically. The SLA clock
runs from the time the mail was received, using the default policy, so triage time counts.

### Connecting a mailbox

Solvix does not poll IMAP/Graph; a mail provider or a small relay **POSTs each message** as JSON. Create a channel in
*Helpdesk settings → E-mail intake* (shows a secret **once**), then forward mail with:

```
POST https://<api-host>/api/v1/helpdesk/inbound-email
X-Channel-Token: svxm_…            ← the channel secret (header only, never in the URL)
Content-Type: application/json

{
  "messageId": "<CAF=abc@mail.example.com>",         // used for de-duplication and threading
  "inReplyTo": "<previous-id@…>",  "references": ["<…>"],
  "from": { "email": "jane@client.com", "name": "Jane" },
  "subject": "Printer offline",
  "text": "plain text body",       "html": "<p>optional, used only if text is empty</p>",
  "headers": { "Auto-Submitted": "no" },              // optional, used to drop auto-replies
  "attachments": [ { "fileName": "screen.png", "contentType": "image/png", "contentBase64": "…" } ]
}
```

Response: `{"outcome":"created|replied|duplicate|ignored","ticketNumber":123}`. Typical relays: a Power Automate flow
("When a new email arrives" → HTTP), an Azure Function / Logic App, or a provider's inbound webhook through a tiny
transform. Always point at the **API**, not the web proxy.

Behaviour and protections (all covered by integration tests):

| Concern | Handling |
|---------|----------|
| Authentication | Per-channel 256-bit secret, only its SHA-256 stored, constant-time compare; the tenant id inside the token binds the request to one tenant *before* any row is read, so a spliced token opens nothing. Rotate or disable a channel at any time. |
| Threading | A reply attaches to a ticket via `[SVX-123]`/`[#123]` in the subject or `In-Reply-To`/`References` — **only if the sender is the ticket's requester**. Anyone else quoting a ticket number opens a *new* ticket. A requester reply reopens a resolved/waiting ticket. |
| Duplicates | Unique `Message-Id` per tenant: provider retries are acknowledged, not duplicated. |
| Mail loops / auto-replies | `Auto-Submitted` ≠ no, `Precedence: bulk/junk/list`, `X-Auto-Response-Suppress`, `List-Id`, and mail from the channel's own address are ignored. |
| Flooding | A sender can open at most 20 tickets per hour per tenant (`Email:MaxTicketsPerSenderPerHour`); excess is dropped. Endpoint rate limit 120/min/IP; body limit 16 MiB. |
| Allow-list | Optional sender-domain allow-list per channel. |
| Content | Plain text only. HTML is reduced to text (scripts/styles dropped); all regex work is bounded (input caps + 250 ms timeouts) so crafted mail cannot burn CPU. The UI renders ticket text as text, never HTML. |
| Attachments | At most 5, same rules as uploads (below). Rejected ones are noted in the ticket history. |

Not included: **outbound** e-mail (no acknowledgement or reply e-mails are sent; technicians reply in the UI). Wire your
own notifier to the ticket events if you need that.

## SLA policies and escalation

* A policy has a target per priority: *first response* and *resolution* minutes, either **24×7** or **business hours**
  (time zone, start/end, weekdays). Exactly one active default per tenant (database-enforced).
* **Waiting on requester pauses the clock**; on resume the remaining time carries over exactly (sub-second precision).
* `slaState`: `ok`, `at_risk` (≥ 75 % of resolution time elapsed), `breached`, `paused`, `met`, `none`.
* **Escalation rules** (`firstResponse|resolution` elapsed **%** → *flag*, *raise priority one level*, or *reassign*),
  optional minimum priority. Evaluated every 5 minutes (`Helpdesk:Worker:IntervalMinutes`) by a worker holding a PostgreSQL
  advisory lock; **each rule applies at most once per ticket** (`ticket_applied_escalations`). The same pass records SLA
  breaches (`SlaBreached` event, `solvix.helpdesk.sla_breaches`). Raising priority does not recompute due dates.
* Escalation is in-system (flag/reassign/priority); it does not send notifications.

## Attachments

Stored in PostgreSQL (`ticket_attachment_contents`, bytea, RLS-isolated, **included in database backups**; max 10 MiB each,
25 per ticket). Allow-list by extension (png, jpg, gif, webp, pdf, txt, log, csv, docx, xlsx) **and** content signature
check; the client's content type and path are ignored (file name sanitised, inner dots neutralised so `invoice.exe.png`
cannot masquerade). Downloads are `Content-Disposition: attachment` with `nosniff` and `sandbox` CSP, never rendered inline.
No malware scanning is included: add a scanner (ClamAV or your platform's) in front of or behind `AddAttachmentAsync` if
your threat model needs it. For very large volumes move blobs to object storage behind the same service.

## AI-generated summaries

`POST /helpdesk/tickets/{id}/summary` (staff) and automatically on resolution. Default provider `none` produces a built-in
**extractive digest** (issue, activity counts, latest public message, resolution, status): deterministic, nothing leaves the
platform. With `Solvix:Helpdesk:Ai:Provider=anthropic` and an API key the summary is generated by Claude
(`Solvix:Helpdesk:Ai:Model`, default `claude-haiku-5-5`):

* **Data minimisation:** only subject, description and public messages are sent; **internal notes are excluded** unless
  `Ai:IncludeInternalNotes=true`; e-mail addresses, phone-like numbers and long token-like strings are redacted; input is
  capped (`MaxInputChars`).
* **Prompt-injection hardening:** the ticket is sent as untrusted data inside delimiters with an instruction to ignore
  instructions in it; output is stored and rendered as plain text only.
* **Resilience:** any provider failure/timeout (20 s) falls back to the built-in digest; a 30 s per-ticket cooldown limits
  cost. The summary's `source` shows which produced it.
* **Privacy decision for you:** enabling the provider sends (redacted) ticket text to an external AI service. Decide whether
  your customers' terms allow that before turning it on, and consider making it a per-tenant setting.

## API summary (`/api/v1/helpdesk`)

`tickets` (GET list/filters `open|mine|triage|unassigned|escalated|overdue|status|priority|categoryId|search`, POST create),
`tickets/stats`, `tickets/{id}` (GET, PUT), `…/status`, `…/assign`, `…/category`, `…/priority`, `…/device`, `…/comments`,
`…/history`, `…/summary`, `…/attachments` (POST multipart, GET `{attachmentId}`); `categories`, `technicians`,
`sla-policies`, `escalation-rules`, `email-channels` (+ `…/{id}/rotate`); `POST inbound-email` (anonymous, channel token).
OpenAPI is served at `/openapi/v1.json` and the Scalar UI at `/scalar/v1`.
