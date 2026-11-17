"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { MemberDto, TicketDetailsDto, TicketPriority, TicketStatus } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { formatRelative } from "@/lib/devices";
import { priorityTone, statusLabel, statusTone, ticketPriorities, ticketStatuses } from "@/lib/support";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Select, Textarea } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/providers/toast-provider";

export function TicketDetailsView({ initial, members }: { initial: TicketDetailsDto; members: MemberDto[] }) {
  const { toast } = useToast();
  const [ticket, setTicket] = React.useState(initial);
  const [body, setBody] = React.useState("");
  const [internal, setInternal] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const s = ticket.summary;

  async function run(action: () => Promise<TicketDetailsDto>, success?: string) {
    setBusy(true);
    setError(null);
    try {
      setTicket(await action());
      if (success) toast({ tone: "success", title: success });
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setBusy(false);
    }
  }

  const update = (patch: Record<string, unknown>) => run(() => api.patch<TicketDetailsDto>(`tickets/${s.id}`, patch), "Ticket updated");

  return (
    <>
      <PageHeader
        title={`#${s.number} ${s.subject}`}
        description={`Raised ${formatRelative(s.createdAtUtc)}${s.requesterName ? ` by ${s.requesterName}` : ""}${s.source === "Agent" ? " from the laptop agent" : ""}`}
        actions={
          <>
            <Badge tone={statusTone(s.status)}>{statusLabel(s.status)}</Badge>
            <Badge tone={priorityTone(s.priority)}>{s.priority}</Badge>
          </>
        }
      />
      {error ? <Alert tone="error">{error}</Alert> : null}
      <div className="grid gap-6 lg:grid-cols-3">
        <div className="space-y-4 lg:col-span-2">
          <Card className="p-4">
            <h2 className="mb-2 text-sm font-medium text-muted-foreground">Description</h2>
            <p className="whitespace-pre-wrap text-sm">{ticket.description ?? "No details provided."}</p>
          </Card>
          <div className="space-y-3" aria-label="Conversation">
            {ticket.comments.map((c) => (
              <Card key={c.id} className={`p-4 ${c.isInternal ? "border-warning/50 bg-warning/5" : ""}`}>
                <div className="mb-1 flex items-center gap-2 text-xs text-muted-foreground">
                  <span className="font-medium text-foreground">{c.authorName ?? c.author}</span>
                  <span>{formatRelative(c.createdAtUtc)}</span>
                  {c.isInternal ? <Badge tone="warning">Internal note</Badge> : null}
                </div>
                <p className="whitespace-pre-wrap text-sm">{c.body}</p>
              </Card>
            ))}
          </div>
          <form
            className="space-y-2"
            onSubmit={(e) => {
              e.preventDefault();
              if (!body.trim()) return;
              void run(() => api.post<TicketDetailsDto>(`tickets/${s.id}/comments`, { body, isInternal: internal }), "Comment added").then(() => setBody(""));
            }}
          >
            <Textarea value={body} onChange={(e) => setBody(e.target.value)} placeholder="Write a reply or an internal note" aria-label="Comment" maxLength={8000} />
            <div className="flex items-center justify-between">
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} /> Internal note (not shown to the requester)
              </label>
              <Button type="submit" loading={busy} disabled={!body.trim()}>
                Add comment
              </Button>
            </div>
          </form>
        </div>
        <Card className="space-y-4 self-start p-4">
          <Field label="Status" htmlFor="t-status">
            <Select id="t-status" value={s.status} disabled={busy} onChange={(e) => void update({ status: e.target.value as TicketStatus })}>
              {ticketStatuses.map((v) => (
                <option key={v} value={v}>
                  {statusLabel(v)}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Priority" htmlFor="t-priority">
            <Select id="t-priority" value={s.priority} disabled={busy} onChange={(e) => void update({ priority: e.target.value as TicketPriority })}>
              {ticketPriorities.map((v) => (
                <option key={v}>{v}</option>
              ))}
            </Select>
          </Field>
          <Field label="Assigned to" htmlFor="t-assignee">
            <Select
              id="t-assignee"
              value={s.assignedUserId ?? ""}
              disabled={busy}
              onChange={(e) => void update(e.target.value ? { assignedUserId: e.target.value } : { unassign: true })}
            >
              <option value="">Unassigned</option>
              {members
                .filter((m) => m.isActive)
                .map((m) => (
                  <option key={m.userId} value={m.userId}>
                    {m.displayName}
                  </option>
                ))}
            </Select>
          </Field>
          {s.deviceId ? (
            <div className="text-sm">
              <div className="text-muted-foreground">Device</div>
              <Link href={`/endpoints/devices/${s.deviceId}`} className="text-primary hover:underline">
                {s.deviceName}
              </Link>
            </div>
          ) : null}
          {ticket.requesterEmail ? (
            <div className="text-sm">
              <div className="text-muted-foreground">Contact</div>
              {ticket.requesterEmail}
            </div>
          ) : null}
        </Card>
      </div>
    </>
  );
}
