"use client";

import * as React from "react";
import Link from "next/link";
import { Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult, TicketDetailsDto, TicketPriority, TicketStatus, TicketSummaryDto } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatRelative } from "@/lib/devices";
import { priorityTone, statusLabel, statusTone, ticketPriorities, ticketStatuses } from "@/lib/support";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

export function TicketsView({ initial }: { initial: PagedResult<TicketSummaryDto> }) {
  const { toast } = useToast();
  const [data, setData] = React.useState(initial);
  const [status, setStatus] = React.useState<"open" | "all" | TicketStatus>("open");
  const [search, setSearch] = React.useState("");
  const [creating, setCreating] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const load = React.useCallback(async () => {
    try {
      const query: Record<string, string | number | undefined> = { pageSize: 100 };
      if (status === "open") query.open = "true";
      else if (status !== "all") query.status = status;
      if (search.trim()) query.search = search.trim();
      setData(await api.get<PagedResult<TicketSummaryDto>>("tickets", query));
      setError(null);
    } catch (e) {
      setError(messageFrom(e));
    }
  }, [status, search]);

  React.useEffect(() => {
    const timer = setTimeout(() => void load(), 250);
    return () => clearTimeout(timer);
  }, [load]);

  return (
    <>
      <div className="mb-4 flex flex-wrap items-end gap-3">
        <Field label="Show" htmlFor="ticket-status" className="w-44">
          <Select id="ticket-status" value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
            <option value="open">Open and in progress</option>
            <option value="all">All tickets</option>
            {ticketStatuses.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Search" htmlFor="ticket-search" className="w-64">
          <Input id="ticket-search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Subject or requester" />
        </Field>
        <div className="ml-auto">
          <Button onClick={() => setCreating(true)}>
            <Plus className="h-4 w-4" /> New ticket
          </Button>
        </div>
      </div>
      {error ? <Alert tone="error">{error}</Alert> : null}
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>#</TH>
              <TH>Subject</TH>
              <TH>Status</TH>
              <TH>Priority</TH>
              <TH>Requester</TH>
              <TH>Device</TH>
              <TH>Updated</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={7}>No tickets match.</EmptyRow>
            ) : (
              data.items.map((t) => (
                <TR key={t.id}>
                  <TD className="text-muted-foreground">{t.number}</TD>
                  <TD>
                    <Link href={`/helpdesk/tickets/${t.id}`} className="font-medium text-primary hover:underline">
                      {t.subject}
                    </Link>
                    {t.source === "Agent" ? <span className="ml-2 text-xs text-muted-foreground">from laptop</span> : null}
                  </TD>
                  <TD>
                    <Badge tone={statusTone(t.status)}>{statusLabel(t.status)}</Badge>
                  </TD>
                  <TD>
                    <Badge tone={priorityTone(t.priority)}>{t.priority}</Badge>
                  </TD>
                  <TD>{t.requesterName ?? "—"}</TD>
                  <TD>
                    {t.deviceId ? (
                      <Link href={`/endpoints/devices/${t.deviceId}`} className="hover:underline">
                        {t.deviceName}
                      </Link>
                    ) : (
                      "—"
                    )}
                  </TD>
                  <TD className="text-muted-foreground">{formatRelative(t.updatedAtUtc)}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {creating ? (
        <NewTicketDialog
          onClose={() => setCreating(false)}
          onCreated={async () => {
            setCreating(false);
            toast({ tone: "success", title: "Ticket created" });
            await load();
          }}
        />
      ) : null}
    </>
  );
}

function NewTicketDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => Promise<void> }) {
  const [subject, setSubject] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [priority, setPriority] = React.useState<TicketPriority>("Normal");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await api.post<TicketDetailsDto>("tickets", { subject, description: emptyToNull(description), priority, deviceId: null });
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
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="ticket-form" loading={pending}>
            Create
          </Button>
        </>
      }
    >
      <form id="ticket-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Subject" htmlFor="ticket-subject" error={errors.subject} required>
          <Input id="ticket-subject" value={subject} onChange={(e) => setSubject(e.target.value)} required maxLength={200} autoFocus />
        </Field>
        <Field label="Details" htmlFor="ticket-description" error={errors.description}>
          <Textarea id="ticket-description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={8000} />
        </Field>
        <Field label="Priority" htmlFor="ticket-priority">
          <Select id="ticket-priority" value={priority} onChange={(e) => setPriority(e.target.value as TicketPriority)}>
            {ticketPriorities.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </Select>
        </Field>
      </form>
    </Dialog>
  );
}
