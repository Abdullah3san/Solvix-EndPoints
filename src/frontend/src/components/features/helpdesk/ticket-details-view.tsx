"use client";

import * as React from "react";
import Link from "next/link";
import { Download, Lock, Paperclip, Sparkles } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import { ApiError, problemFromResponse } from "@/lib/api/errors";
import type { TechnicianDto, TicketCategoryDto, TicketDetailsDto, TicketEventDto, TicketPriority, TicketStatus } from "@/lib/api/types";
import { formatBytes, formatRelative } from "@/lib/devices";
import { formatDue, priorityTone, slaLabel, slaTone, statusLabel, statusTone, ticketPriorities, ticketStatuses } from "@/lib/helpdesk";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Select, Textarea } from "@/components/ui/input";
import { useToast } from "@/components/providers/toast-provider";

interface Props {
  initial: TicketDetailsDto;
  history: TicketEventDto[];
  categories: TicketCategoryDto[];
  technicians: TechnicianDto[];
  isStaff: boolean;
  isRequester: boolean;
}

export function TicketDetailsView({ initial, history: initialHistory, categories, technicians, isStaff, isRequester }: Props) {
  const { toast } = useToast();
  const [ticket, setTicket] = React.useState(initial);
  const [history, setHistory] = React.useState(initialHistory);
  const [comment, setComment] = React.useState("");
  const [internal, setInternal] = React.useState(false);
  const [busy, setBusy] = React.useState<string | null>(null);
  const [resolving, setResolving] = React.useState(false);
  const fileInput = React.useRef<HTMLInputElement>(null);
  const s = ticket.summary;

  async function run<T extends TicketDetailsDto>(label: string, action: () => Promise<T>, success?: string) {
    setBusy(label);
    try {
      const updated = await action();
      setTicket(updated);
      setHistory(await api.get<TicketEventDto[]>(`helpdesk/tickets/${s.id}/history`));
      if (success) toast({ tone: "success", title: success });
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(null);
    }
  }

  const post = <T extends TicketDetailsDto>(path: string, body?: unknown) => api.post<T>(`helpdesk/tickets/${s.id}/${path}`, body);

  async function upload(file: File) {
    setBusy("upload");
    try {
      const form = new FormData();
      form.append("file", file);
      const response = await fetch(`${PROXY_PREFIX}/helpdesk/tickets/${s.id}/attachments`, { method: "POST", body: form, credentials: "same-origin" });
      if (!response.ok) throw new ApiError(response.status, await problemFromResponse(response));
      setTicket(await api.get<TicketDetailsDto>(`helpdesk/tickets/${s.id}`));
      toast({ tone: "success", title: "File attached" });
    } catch (e) {
      toast({ tone: "error", title: "Could not attach file", description: messageFrom(e) });
    } finally {
      setBusy(null);
      if (fileInput.current) fileInput.current.value = "";
    }
  }

  const closed = s.status === "Closed";
  const suggested = s.suggestedCategoryId ? categories.find((c) => c.id === s.suggestedCategoryId) : undefined;

  return (
    <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <div className="space-y-6">
        <Card>
          <CardHeader>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-mono text-sm text-muted-foreground">#{s.number}</span>
              <Badge tone={statusTone(s.status)}>{statusLabel(s.status)}</Badge>
              <Badge tone={priorityTone(s.priority)}>{s.priority}</Badge>
              <Badge tone={slaTone(s.slaState)}>SLA: {slaLabel(s.slaState)}</Badge>
              {s.escalationLevel > 0 ? <Badge tone="danger">Escalated ×{s.escalationLevel}</Badge> : null}
              {s.source === "Email" ? <Badge>via e-mail</Badge> : null}
            </div>
            <CardTitle className="mt-2 text-xl">{s.subject}</CardTitle>
            <p className="text-sm text-muted-foreground">
              Raised by {s.requesterName ?? s.requesterEmail ?? "unknown"} {formatRelative(s.createdAtUtc)}
              {ticket.deviceName ? <> · device {ticket.deviceName}</> : null}
            </p>
          </CardHeader>
          <CardContent>
            <p className="whitespace-pre-wrap text-sm">{ticket.description}</p>
          </CardContent>
        </Card>

        {isStaff && s.needsTriage ? (
          <Alert tone="info" title="Needs triage">
            This ticket has no category.{" "}
            {suggested ? (
              <>
                Suggested: <strong>{suggested.name}</strong>.{" "}
                <Button size="sm" variant="outline" loading={busy === "category"} onClick={() => run("category", () => post("category", { categoryId: suggested.id }), "Category set")}>
                  Accept suggestion
                </Button>
              </>
            ) : (
              "Choose a category in the sidebar to route it."
            )}
          </Alert>
        ) : null}

        {ticket.aiSummary ? (
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-base">
                <Sparkles className="h-4 w-4" /> Summary
              </CardTitle>
              <p className="text-xs text-muted-foreground">
                {ticket.aiSummarySource === "extractive" ? "Built-in digest" : `Generated by ${ticket.aiSummarySource}`} · {formatRelative(ticket.aiSummaryGeneratedAtUtc)}
              </p>
            </CardHeader>
            <CardContent>
              <p className="whitespace-pre-wrap text-sm">{ticket.aiSummary}</p>
            </CardContent>
          </Card>
        ) : null}

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Conversation</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {ticket.comments.length === 0 ? <p className="text-sm text-muted-foreground">No replies yet.</p> : null}
            {ticket.comments.map((c) => (
              <div key={c.id} className={`rounded-md border p-3 text-sm ${c.isInternal ? "border-warning/40 bg-warning/10" : "border-border"}`}>
                <div className="mb-1 flex items-center gap-2 text-xs text-muted-foreground">
                  <strong className="text-foreground">{c.authorName ?? c.authorEmail ?? "Requester"}</strong>
                  {c.isFromStaff ? <Badge tone="brand">Technician</Badge> : null}
                  {c.isInternal ? (
                    <Badge tone="warning">
                      <Lock className="mr-1 h-3 w-3" />
                      Internal note
                    </Badge>
                  ) : null}
                  {c.source === "Email" ? <span>via e-mail</span> : null}
                  <span>{formatRelative(c.createdAtUtc)}</span>
                </div>
                {/* Rendered as text, never as HTML: ticket content is untrusted. */}
                <p className="whitespace-pre-wrap">{c.body}</p>
              </div>
            ))}

            {closed ? (
              <Alert tone="info">This ticket is closed. Reopen it to add replies.</Alert>
            ) : (
              <form
                className="space-y-2"
                onSubmit={async (e) => {
                  e.preventDefault();
                  if (!comment.trim()) return;
                  await run("comment", () => post("comments", { body: comment, isInternal: internal }), internal ? "Note added" : "Reply sent");
                  setComment("");
                }}
              >
                <Textarea value={comment} onChange={(e) => setComment(e.target.value)} rows={3} placeholder={isStaff ? "Reply to the requester or write an internal note…" : "Add a reply…"} aria-label="Reply" maxLength={20000} />
                <div className="flex items-center justify-between">
                  {isStaff ? (
                    <label className="flex items-center gap-2 text-sm">
                      <Checkbox checked={internal} onChange={(e) => setInternal(e.target.checked)} /> Internal note (not visible to the requester)
                    </label>
                  ) : (
                    <span />
                  )}
                  <Button type="submit" loading={busy === "comment"} disabled={!comment.trim()}>
                    {internal ? "Add note" : "Send reply"}
                  </Button>
                </div>
              </form>
            )}
          </CardContent>
        </Card>

        {ticket.resolutions.length > 0 ? (
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Resolution history</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3 text-sm">
              {ticket.resolutions.map((r) => (
                <div key={r.id} className="rounded-md border border-border p-3">
                  <p className="whitespace-pre-wrap">{r.notes}</p>
                  <p className="mt-1 text-xs text-muted-foreground">
                    Resolved {formatRelative(r.resolvedAtUtc)}
                    {r.reopenedAtUtc ? ` · reopened ${formatRelative(r.reopenedAtUtc)}` : ""}
                  </p>
                </div>
              ))}
            </CardContent>
          </Card>
        ) : null}

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Activity</CardTitle>
          </CardHeader>
          <CardContent>
            <ol className="space-y-2 text-sm">
              {history.map((e) => (
                <li key={e.id} className="flex flex-wrap gap-x-2 text-muted-foreground">
                  <span className="w-24 shrink-0">{formatRelative(e.occurredAtUtc)}</span>
                  <span className="text-foreground">{e.type.replace(/([A-Z])/g, " $1").trim()}</span>
                  {e.fromValue || e.toValue ? (
                    <span>
                      {e.fromValue ?? "—"} → {e.toValue ?? "—"}
                    </span>
                  ) : null}
                  {e.actorLabel ? <span>by {e.actorLabel}</span> : null}
                  {e.detail ? <span className="w-full pl-24">{e.detail}</span> : null}
                </li>
              ))}
            </ol>
          </CardContent>
        </Card>
      </div>

      <aside className="space-y-4">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Details</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <dl className="grid grid-cols-[6rem_1fr] gap-y-1">
              <dt className="text-muted-foreground">Category</dt>
              <dd>{s.categoryName ?? "—"}</dd>
              <dt className="text-muted-foreground">Assignee</dt>
              <dd>{s.assigneeName ?? "Unassigned"}</dd>
              <dt className="text-muted-foreground">First response</dt>
              <dd>{s.firstResponseBreached ? "Breached" : formatDue(s.firstResponseDueAtUtc)}</dd>
              <dt className="text-muted-foreground">Resolution</dt>
              <dd>{s.resolutionBreached ? "Breached" : formatDue(s.resolutionDueAtUtc)}</dd>
              {ticket.reopenCount > 0 ? (
                <>
                  <dt className="text-muted-foreground">Reopened</dt>
                  <dd>{ticket.reopenCount}×</dd>
                </>
              ) : null}
            </dl>

            {isStaff ? (
              <div className="space-y-3 border-t border-border pt-3">
                <Field label="Status" htmlFor="d-status">
                  <Select
                    id="d-status"
                    value={s.status}
                    disabled={busy !== null}
                    onChange={(e) => {
                      const next = e.target.value as TicketStatus;
                      if (next === "Resolved") setResolving(true);
                      else void run("status", () => post("status", { status: next }), "Status updated");
                    }}
                  >
                    {ticketStatuses
                      .filter((x) => x !== "New" || s.status === "New")
                      .map((x) => (
                        <option key={x} value={x}>
                          {statusLabel(x)}
                        </option>
                      ))}
                  </Select>
                </Field>
                <Field label="Priority" htmlFor="d-priority">
                  <Select id="d-priority" value={s.priority} disabled={busy !== null} onChange={(e) => run("priority", () => post("priority", { priority: e.target.value as TicketPriority }), "Priority updated")}>
                    {ticketPriorities.map((p) => (
                      <option key={p}>{p}</option>
                    ))}
                  </Select>
                </Field>
                <Field label="Category" htmlFor="d-category">
                  <Select id="d-category" value={s.categoryId ?? ""} disabled={busy !== null} onChange={(e) => run("category", () => post("category", { categoryId: emptyToNull(e.target.value) }), "Category updated")}>
                    <option value="">Uncategorised</option>
                    {categories.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Assignee" htmlFor="d-assignee">
                  <Select id="d-assignee" value={s.assigneeTechnicianId ?? ""} disabled={busy !== null} onChange={(e) => run("assign", () => post("assign", { technicianId: emptyToNull(e.target.value) }), "Assignment updated")}>
                    <option value="">Unassigned</option>
                    {technicians
                      .filter((t) => t.isActive)
                      .map((t) => (
                        <option key={t.id} value={t.id}>
                          {t.displayName ?? t.email ?? t.id} ({t.openTickets} open)
                        </option>
                      ))}
                  </Select>
                </Field>
                <Button variant="outline" size="sm" className="w-full" loading={busy === "summary"} onClick={() => run("summary", () => post("summary"), "Summary generated")}>
                  <Sparkles className="h-4 w-4" /> {ticket.aiSummary ? "Regenerate summary" : "Summarise ticket"}
                </Button>
              </div>
            ) : isRequester && s.status === "Resolved" ? (
              <div className="flex gap-2 border-t border-border pt-3">
                <Button size="sm" variant="outline" onClick={() => run("status", () => post("status", { status: "Open" }), "Ticket reopened")}>
                  Not fixed, reopen
                </Button>
                <Button size="sm" onClick={() => run("status", () => post("status", { status: "Closed" }), "Ticket closed")}>
                  Close
                </Button>
              </div>
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <Paperclip className="h-4 w-4" /> Attachments
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            {ticket.attachments.length === 0 ? <p className="text-muted-foreground">No files attached.</p> : null}
            {ticket.attachments.map((a) => (
              <a key={a.id} href={`${PROXY_PREFIX}/helpdesk/tickets/${s.id}/attachments/${a.id}`} download={a.fileName} className="flex items-center justify-between gap-2 rounded-md border border-border px-2 py-1.5 hover:bg-muted/50">
                <span className="truncate">{a.fileName}</span>
                <span className="flex shrink-0 items-center gap-1 text-xs text-muted-foreground">
                  {formatBytes(a.sizeBytes)} <Download className="h-3 w-3" />
                </span>
              </a>
            ))}
            {!closed ? (
              <>
                <input ref={fileInput} type="file" className="sr-only" id="attach" accept=".png,.jpg,.jpeg,.gif,.webp,.pdf,.txt,.log,.csv,.docx,.xlsx" onChange={(e) => e.target.files?.[0] && upload(e.target.files[0])} />
                <Button variant="outline" size="sm" className="w-full" loading={busy === "upload"} onClick={() => fileInput.current?.click()}>
                  Attach a file
                </Button>
                <p className="text-xs text-muted-foreground">Images, PDF, text, CSV, Word, Excel. Up to 10 MB.</p>
              </>
            ) : null}
          </CardContent>
        </Card>
        <Link href="/helpdesk/tickets" className="block text-sm text-primary hover:underline">
          ← All tickets
        </Link>
      </aside>

      <ResolveDialog
        open={resolving}
        onClose={() => setResolving(false)}
        onResolve={async (notes) => {
          setResolving(false);
          await run("status", () => post("status", { status: "Resolved", resolution: notes }), "Ticket resolved");
        }}
      />
    </div>
  );
}

function ResolveDialog({ open, onClose, onResolve }: { open: boolean; onClose: () => void; onResolve: (notes: string) => Promise<void> }) {
  const [notes, setNotes] = React.useState("");
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Resolve ticket"
      description="Record how it was fixed. This stays in the ticket's resolution history."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={!notes.trim()} onClick={() => onResolve(notes.trim())}>
            Resolve
          </Button>
        </>
      }
    >
      <Textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={4} maxLength={5000} aria-label="Resolution" placeholder="What was done?" />
    </Dialog>
  );
}
