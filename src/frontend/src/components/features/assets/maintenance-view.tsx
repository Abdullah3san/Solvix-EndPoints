"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { MaintenanceRecordDto, MaintenanceStatus, PagedResult } from "@/lib/api/types";
import { MAINTENANCE_TYPES, formatDay, formatMoney, maintenanceTone } from "@/lib/assets";
import { emptyToNull, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

export function MaintenanceView({ initial }: { initial: PagedResult<MaintenanceRecordDto> }) {
  const { toast } = useToast();
  const [data, setData] = React.useState(initial);
  const [status, setStatus] = React.useState<MaintenanceStatus | "">("InProgress");
  const [closing, setClosing] = React.useState<{ record: MaintenanceRecordDto; complete: boolean } | null>(null);

  const load = React.useCallback(async (s: MaintenanceStatus | "", page = 1) => {
    setData(await api.get<PagedResult<MaintenanceRecordDto>>("asset-maintenance", { status: s, page, pageSize: 50 }));
  }, []);

  async function start(record: MaintenanceRecordDto) {
    try {
      await api.post(`asset-maintenance/${record.id}/start`);
      toast({ tone: "success", title: "Maintenance started" });
      await load(status, data.page);
    } catch (e) {
      toast({ tone: "error", title: "Could not start", description: messageFrom(e) });
    }
  }

  return (
    <>
      <div className="mb-3 flex items-center gap-2">
        <Select
          aria-label="Status"
          className="w-48"
          value={status}
          onChange={(e) => {
            const s = e.target.value as MaintenanceStatus | "";
            setStatus(s);
            void load(s);
          }}
        >
          <option value="InProgress">In progress</option>
          <option value="Scheduled">Scheduled</option>
          <option value="Completed">Completed</option>
          <option value="Cancelled">Cancelled</option>
          <option value="">All</option>
        </Select>
        <span className="text-sm text-muted-foreground">{data.totalCount} records</span>
      </div>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Asset</TH>
              <TH>Work</TH>
              <TH>Status</TH>
              <TH>Vendor</TH>
              <TH>Dates</TH>
              <TH className="text-right">Cost</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={7}>No maintenance records. Log work from an asset&apos;s page.</EmptyRow>
            ) : (
              data.items.map((m) => (
                <TR key={m.id}>
                  <TD>
                    <Link href={`/assets/${m.assetId}`} prefetch={false} className="font-mono text-xs hover:underline">
                      {m.assetTag}
                    </Link>
                    <p className="max-w-48 truncate text-sm">{m.assetName}</p>
                  </TD>
                  <TD>
                    <p className="font-medium">{m.title}</p>
                    <p className="text-xs text-muted-foreground">{MAINTENANCE_TYPES.find((t) => t.value === m.type)?.label}</p>
                  </TD>
                  <TD>
                    <Badge tone={maintenanceTone(m.status)}>{m.status === "InProgress" ? "In progress" : m.status}</Badge>
                  </TD>
                  <TD className="text-muted-foreground">{m.vendor?.name ?? m.performedBy ?? "In-house"}</TD>
                  <TD className="whitespace-nowrap text-xs text-muted-foreground">
                    {m.scheduledFor ? <p>Scheduled {formatDay(m.scheduledFor)}</p> : null}
                    {m.startedAtUtc ? <p>Started {formatDate(m.startedAtUtc)}</p> : null}
                    {m.completedAtUtc ? <p>Closed {formatDate(m.completedAtUtc)}</p> : null}
                  </TD>
                  <TD className="text-right tabular-nums">{formatMoney(m.cost)}</TD>
                  <TD className="whitespace-nowrap text-right">
                    {m.status === "Scheduled" ? (
                      <Button size="sm" variant="ghost" onClick={() => start(m)}>
                        Start
                      </Button>
                    ) : null}
                    {m.status === "Scheduled" || m.status === "InProgress" ? (
                      <>
                        <Button size="sm" variant="ghost" onClick={() => setClosing({ record: m, complete: true })}>
                          Complete
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setClosing({ record: m, complete: false })}>
                          Cancel
                        </Button>
                      </>
                    ) : null}
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {closing ? (
        <CloseDialog
          {...closing}
          onClose={() => setClosing(null)}
          onDone={async () => {
            setClosing(null);
            await load(status, data.page);
          }}
        />
      ) : null}
    </>
  );
}

function CloseDialog({ record, complete, onClose, onDone }: { record: MaintenanceRecordDto; complete: boolean; onClose: () => void; onDone: () => Promise<void> }) {
  const { toast } = useToast();
  const [cost, setCost] = React.useState("");
  const [text, setText] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  async function submit() {
    setBusy(true);
    try {
      await api.post(
        `asset-maintenance/${record.id}/${complete ? "complete" : "cancel"}`,
        complete ? { cost: cost.trim() ? Number(cost) : null, resolution: emptyToNull(text) } : { reason: emptyToNull(text) },
      );
      toast({ tone: "success", title: complete ? "Maintenance completed" : "Maintenance cancelled" });
      await onDone();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
      setBusy(false);
    }
  }
  return (
    <Dialog
      open
      onClose={onClose}
      title={complete ? `Complete “${record.title}”` : `Cancel “${record.title}”`}
      description="The asset returns to the status it had before maintenance."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Back
          </Button>
          <Button variant={complete ? "primary" : "destructive"} loading={busy} onClick={submit}>
            {complete ? "Complete" : "Cancel work"}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {complete ? (
          <Field label="Cost" htmlFor="close-cost">
            <Input id="close-cost" inputMode="decimal" value={cost} onChange={(e) => setCost(e.target.value)} />
          </Field>
        ) : null}
        <Field label={complete ? "Resolution" : "Reason"} htmlFor="close-text">
          <Textarea id="close-text" value={text} onChange={(e) => setText(e.target.value)} maxLength={4000} />
        </Field>
      </div>
    </Dialog>
  );
}
