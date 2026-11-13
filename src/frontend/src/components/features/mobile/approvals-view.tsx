"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { CommandDto, DeviceDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { commandLabel, deviceTitle, formatDate } from "@/lib/mdm";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan, useSession } from "@/components/providers/session-provider";

type Decision = { command: CommandDto; kind: "approve" | "reject" };

export function ApprovalsView({ initial, devices }: { initial: PagedResult<CommandDto>; devices: DeviceDto[] }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const { user } = useSession();
  const [items, setItems] = React.useState(initial.items);
  const [decision, setDecision] = React.useState<Decision | null>(null);
  const [note, setNote] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const names = React.useMemo(() => new Map(devices.map((d) => [d.id, d])), [devices]);

  async function reload() {
    const r = await api.get<PagedResult<CommandDto>>("mobile-commands", { state: "PendingApproval", pageSize: 100 });
    setItems(r.items);
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (!decision) return;
    setPending(true);
    setError(null);
    try {
      await api.post<CommandDto>(`mobile-commands/${decision.command.id}/${decision.kind}`, { note: note.trim() || null });
      toast({ tone: "success", title: decision.kind === "approve" ? `${commandLabel[decision.command.type]} approved` : "Request rejected" });
      setDecision(null);
      setNote("");
      await reload();
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <>
      <Alert tone="info" className="mb-4">
        Retire and wipe commands run only after a different administrator approves them. Requests expire after 24 hours.
      </Alert>
      <Card>
        <Table>
          <THead>
            <TR><TH>Device</TH><TH>Request</TH><TH>Reason</TH><TH>Requested</TH>{isAdmin ? <TH className="text-right">Decision</TH> : null}</TR>
          </THead>
          <TBody>
            {items.length === 0 ? (
              <EmptyRow colSpan={isAdmin ? 5 : 4}>Nothing is waiting for approval.</EmptyRow>
            ) : (
              items.map((c) => {
                const device = names.get(c.deviceId);
                const own = c.requestedByUserId === user.id;
                return (
                  <TR key={c.id}>
                    <TD>
                      <Link href={`/mobile/devices/${c.deviceId}`} className="font-medium hover:underline">
                        {device ? deviceTitle(device) : `Device ${c.deviceId.slice(0, 8)}`}
                      </Link>
                      {device?.ownership === "PersonallyOwned" ? <Badge tone="warning" className="ml-2">BYOD</Badge> : null}
                    </TD>
                    <TD><Badge tone="danger">{commandLabel[c.type]}</Badge></TD>
                    <TD className="max-w-xs text-sm">{c.reason ?? "—"}</TD>
                    <TD className="text-xs text-muted-foreground">
                      {formatDate(c.createdAtUtc)}
                      {own ? <span className="block">Requested by you</span> : null}
                    </TD>
                    {isAdmin ? (
                      <TD>
                        <div className="flex justify-end gap-1">
                          <Button size="sm" variant="outline" onClick={() => setDecision({ command: c, kind: "reject" })}>Reject</Button>
                          <Button size="sm" variant="destructive" onClick={() => setDecision({ command: c, kind: "approve" })}>Approve</Button>
                        </div>
                      </TD>
                    ) : null}
                  </TR>
                );
              })
            )}
          </TBody>
        </Table>
      </Card>

      {decision ? (
        <Dialog
          open
          onClose={() => setDecision(null)}
          title={decision.kind === "approve" ? `Approve ${commandLabel[decision.command.type]}?` : "Reject request?"}
          description={
            decision.kind === "approve"
              ? decision.command.requestedByUserId === user.id
                ? "You requested this yourself. The server accepts self-approval only when it is enabled for this workspace."
                : "The command is sent to the device immediately."
              : undefined
          }
          footer={
            <>
              <Button variant="outline" onClick={() => setDecision(null)}>Cancel</Button>
              <Button type="submit" form="decision-form" variant={decision.kind === "approve" ? "destructive" : "primary"} loading={pending}>
                {decision.kind === "approve" ? "Approve" : "Reject"}
              </Button>
            </>
          }
        >
          <form id="decision-form" onSubmit={submit} className="space-y-4">
            {error ? <Alert tone="error">{error}</Alert> : null}
            <Field label="Note" htmlFor="decision-note" hint="Optional, recorded in the audit trail">
              <Textarea id="decision-note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
            </Field>
          </form>
        </Dialog>
      ) : null}
    </>
  );
}
