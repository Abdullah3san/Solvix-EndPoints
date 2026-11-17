"use client";

import * as React from "react";
import Link from "next/link";
import { Plus, Mail } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult, TicketCategoryDto, TicketPriority, TicketStatsDto, TicketSummaryDto } from "@/lib/api/types";
import { formatRelative } from "@/lib/devices";
import { formatDue, priorityTone, slaLabel, slaTone, statusLabel, statusTone, ticketPriorities } from "@/lib/helpdesk";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

type Queue = "all" | "open" | "mine" | "triage" | "unassigned" | "escalated" | "overdue";

const queues: { key: Queue; label: string; staffOnly?: boolean; query: Record<string, string | boolean> }[] = [
  { key: "open", label: "Open", query: { open: true } },
  { key: "all", label: "All", query: {} },
  { key: "mine", label: "Assigned to me", staffOnly: true, query: { mine: true, open: true } },
  { key: "triage", label: "Triage", staffOnly: true, query: { triage: true } },
  { key: "unassigned", label: "Unassigned", staffOnly: true, query: { unassigned: true } },
  { key: "escalated", label: "Escalated", staffOnly: true, query: { escalated: true } },
  { key: "overdue", label: "Overdue", staffOnly: true, query: { overdue: true } },
];

export function TicketsView({
  initial,
  stats,
  categories,
  isStaff,
}: {
  initial: PagedResult<TicketSummaryDto>;
  stats: TicketStatsDto | null;
  categories: TicketCategoryDto[];
  isStaff: boolean;
}) {
  const { toast } = useToast();
  const [data, setData] = React.useState(initial);
  const [queue, setQueue] = React.useState<Queue>("open");
  const [search, setSearch] = React.useState("");
  const [loading, setLoading] = React.useState(false);
  const [creating, setCreating] = React.useState(false);

  const load = React.useCallback(
    async (nextQueue: Queue, nextSearch: string, page = 1) => {
      setLoading(true);
      try {
        const def = queues.find((q) => q.key === nextQueue)!;
        setData(await api.get<PagedResult<TicketSummaryDto>>("helpdesk/tickets", { ...def.query, search: nextSearch, page, pageSize: initial.pageSize }));
      } catch (e) {
        toast({ tone: "error", title: "Could not load tickets", description: messageFrom(e) });
      } finally {
        setLoading(false);
      }
    },
    [initial.pageSize, toast],
  );

  function selectQueue(next: Queue) {
    setQueue(next);
    void load(next, search);
  }

  const counts: Partial<Record<Queue, number>> = stats
    ? { open: stats.open, triage: stats.needsTriage, unassigned: stats.unassigned, escalated: stats.escalated, overdue: stats.overdue }
    : {};

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap gap-1" role="tablist" aria-label="Ticket queues">
          {queues
            .filter((q) => isStaff || !q.staffOnly)
            .map((q) => (
              <Button key={q.key} role="tab" aria-selected={queue === q.key} size="sm" variant={queue === q.key ? "primary" : "outline"} onClick={() => selectQueue(q.key)}>
                {q.label}
                {counts[q.key] !== undefined ? <span className="ml-1.5 rounded-full bg-background/20 px-1.5 text-xs">{counts[q.key]}</span> : null}
              </Button>
            ))}
        </div>
        <div className="flex items-center gap-2">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              void load(queue, search);
            }}
          >
            <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search subject, e-mail or #number" aria-label="Search tickets" className="w-64" />
          </form>
          <Button onClick={() => setCreating(true)}>
            <Plus className="h-4 w-4" /> New ticket
          </Button>
        </div>
      </div>

      {queue === "triage" ? (
        <Alert tone="info" title="Triage queue">
          Tickets here have no category yet, typically because they arrived by e-mail. Open one and confirm the suggested category (or pick another) to route it.
        </Alert>
      ) : null}

      <Card className={loading ? "opacity-60" : undefined}>
        <Table>
          <THead>
            <TR>
              <TH>#</TH>
              <TH>Subject</TH>
              <TH>Status</TH>
              <TH>Priority</TH>
              <TH>Category</TH>
              <TH>Assignee</TH>
              <TH>SLA</TH>
              <TH>Updated</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={8}>No tickets in this view.</EmptyRow>
            ) : (
              data.items.map((t) => (
                <TR key={t.id}>
                  <TD className="font-mono text-xs text-muted-foreground">{t.number}</TD>
                  <TD className="max-w-sm">
                    <Link href={`/helpdesk/tickets/${t.id}`} className="font-medium text-primary hover:underline">
                      {t.subject}
                    </Link>
                    <div className="mt-0.5 flex items-center gap-1 text-xs text-muted-foreground">
                      {t.source === "Email" ? <Mail className="h-3 w-3" aria-label="Received by e-mail" /> : null}
                      {t.requesterName ?? t.requesterEmail ?? "—"}
                    </div>
                  </TD>
                  <TD>
                    <Badge tone={statusTone(t.status)}>{statusLabel(t.status)}</Badge>
                  </TD>
                  <TD>
                    <Badge tone={priorityTone(t.priority)}>{t.priority}</Badge>
                  </TD>
                  <TD>{t.categoryName ?? <Badge tone="warning">Needs triage</Badge>}</TD>
                  <TD className="text-muted-foreground">{t.assigneeName ?? "Unassigned"}</TD>
                  <TD>
                    <Badge tone={slaTone(t.slaState)} title={formatDue(t.resolutionDueAtUtc)}>
                      {slaLabel(t.slaState)}
                    </Badge>
                    {t.escalationLevel > 0 ? <span className="ml-1 text-xs text-destructive">▲{t.escalationLevel}</span> : null}
                  </TD>
                  <TD className="text-muted-foreground">{formatRelative(t.lastActivityAtUtc)}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
        {data.totalPages > 1 ? (
          <CardContent className="flex items-center justify-between pt-4 text-sm text-muted-foreground">
            <span>
              Page {data.page} of {data.totalPages} · {data.totalCount} tickets
            </span>
            <div className="flex gap-2">
              <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => load(queue, search, data.page - 1)}>
                Previous
              </Button>
              <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => load(queue, search, data.page + 1)}>
                Next
              </Button>
            </div>
          </CardContent>
        ) : null}
      </Card>

      {creating ? (
        <NewTicketDialog
          categories={categories}
          isStaff={isStaff}
          onClose={() => setCreating(false)}
          onCreated={async () => {
            setCreating(false);
            toast({ tone: "success", title: "Ticket created" });
            await load(queue, search);
          }}
        />
      ) : null}
    </>
  );
}

function NewTicketDialog({ categories, isStaff, onClose, onCreated }: { categories: TicketCategoryDto[]; isStaff: boolean; onClose: () => void; onCreated: () => Promise<void> }) {
  const [subject, setSubject] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [categoryId, setCategoryId] = React.useState("");
  const [priority, setPriority] = React.useState<TicketPriority>("Medium");
  const [requesterEmail, setRequesterEmail] = React.useState("");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      await api.post("helpdesk/tickets", {
        subject,
        description,
        categoryId: emptyToNull(categoryId),
        priority: isStaff ? priority : null,
        requesterEmail: isStaff ? emptyToNull(requesterEmail) : null,
      });
      await onCreated();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="New ticket"
      description="Describe the problem. A technician will pick it up."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="new-ticket-form" loading={pending}>
            Create ticket
          </Button>
        </>
      }
    >
      <form id="new-ticket-form" onSubmit={submit} className="space-y-4">
        {error && Object.keys(errors).length === 0 ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Subject" htmlFor="t-subject" error={errors.subject} required>
          <Input id="t-subject" value={subject} onChange={(e) => setSubject(e.target.value)} maxLength={300} required />
        </Field>
        <Field label="Description" htmlFor="t-description" error={errors.description} required>
          <Textarea id="t-description" value={description} onChange={(e) => setDescription(e.target.value)} rows={5} maxLength={20000} required />
        </Field>
        <Field label="Category" htmlFor="t-category" error={errors.categoryId} hint="Leave empty if you are not sure; a technician will triage it.">
          <Select id="t-category" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">Not sure</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </Select>
        </Field>
        {isStaff ? (
          <>
            <Field label="Priority" htmlFor="t-priority">
              <Select id="t-priority" value={priority} onChange={(e) => setPriority(e.target.value as TicketPriority)}>
                {ticketPriorities.map((p) => (
                  <option key={p}>{p}</option>
                ))}
              </Select>
            </Field>
            <Field label="On behalf of (e-mail)" htmlFor="t-requester" error={errors.requesterEmail} hint="Optional: file the ticket for someone outside the workspace.">
              <Input id="t-requester" type="email" value={requesterEmail} onChange={(e) => setRequesterEmail(e.target.value)} />
            </Field>
          </>
        ) : null}
      </form>
    </Dialog>
  );
}
